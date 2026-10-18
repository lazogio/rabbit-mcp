// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Tools;

/// <summary>
/// Sends contract messages. <see cref="PublishAsync{T}"/> (events) and <see cref="RequestAsync{T}"/>
/// (request/response) are closed over each contract type to create one typed MCP tool per contract
/// (see <see cref="ContractToolFactory"/>), so the tool's inputSchema is the contract's JSON Schema.
/// </summary>
public sealed class ContractPublisher(
    RabbitConnection connection,
    ContractRegistry contracts,
    EnvelopeFactory envelopes,
    ReplyListener replies,
    PublishPolicy policy,
    AuditLog audit,
    TimeProvider time,
    IOptions<RabbitMcpOptions> options,
    ILogger<ContractPublisher> logger)
{
    public const int MaxTimeoutSeconds = 120;

    public async Task<PublishResult> PublishAsync<T>(
        [Description("The message. Must satisfy the contract schema and its validation rules.")]
        T message,
        [Description("Optional correlation id (GUID) linking this message to an existing workflow.")]
        string? correlationId = null,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var contract = contracts.Get(typeof(T));
        var client = AuditLog.ClientOf(server, user);
        var messageId = Guid.NewGuid();
        using var activity = Telemetry.StartSend("publish", contract.DestinationName);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("rabbitmcp.contract", contract.Name);
        activity?.SetTag("rabbitmcp.client", client);
        Outgoing outgoing;
        try
        {
            await EnsureSendableAsync(contract, message, correlationId, cancellationToken);
            outgoing = Build(contract, message, messageId, correlationId, request: null);
            await SendAsync(contract, outgoing, cancellationToken);
        }
        catch (Exception ex)
        {
            // Every failed attempt is audited — broker errors (403, lost connection) and cancellation included.
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            var detail = error?.Message ?? "cancelled (the message may or may not have been sent)";
            Telemetry.Fail(activity, detail);
            audit.Write(audit.Event(AuditActions.Publish, "error", client, contract.Name, AuditExchange(contract),
                AuditQueue(contract), correlationId: correlationId, detail: detail, message: message, messageType: typeof(T)));
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw error;
        }

        logger.LogInformation("Published {Contract} {MessageId} to {Destination}", contract.Name, messageId, contract.Destination);
        audit.Write(audit.Event(AuditActions.Publish, "sent", client, contract.Name, AuditExchange(contract),
            AuditQueue(contract), messageId: outgoing.MessageId, correlationId: correlationId, message: message,
            messageType: typeof(T)));

        return new PublishResult(
            MessageId: outgoing.MessageId,
            ConversationId: outgoing.ConversationId,
            CorrelationId: correlationId,
            Contract: contract.Name,
            ContractVersion: contract.Version,
            Format: contract.FormatName,
            MessageType: contract.MessageTypeUrn,
            Exchange: contract.Exchange,
            RoutingKey: contract.RoutingKey.Length == 0 ? null : contract.RoutingKey,
            SentTimeUtc: outgoing.SentTimeUtc,
            Routed: true);
    }

    /// <summary>
    /// MassTransit request client semantics: the request carries requestId + responseAddress
    /// (this server's temporary reply queue) and a TTL equal to the timeout; the consumer's
    /// RespondAsync (or its Fault) comes back to that queue and is matched by requestId.
    /// Raw contracts use RabbitMQ RPC instead: reply_to = the same queue, correlation_id = requestId.
    /// </summary>
    public async Task<RequestResult> RequestAsync<T>(
        [Description("The request message. Must satisfy the contract schema and its validation rules.")]
        T message,
        [Description("Seconds to wait for the reply, 1-120. The request expires in the broker after the same time.")]
        int timeoutSeconds = 30,
        [Description("Optional correlation id (GUID) linking this request to an existing workflow.")]
        string? correlationId = null,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var contract = contracts.Get(typeof(T));
        var client = AuditLog.ClientOf(server, user);
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        void Audit(string outcome, string? detail = null) =>
            audit.Write(audit.Event(AuditActions.Request, outcome, client, contract.Name, AuditExchange(contract),
                AuditQueue(contract), messageId: messageId.ToString(), requestId: requestId.ToString(),
                correlationId: correlationId, detail: detail, message: message, messageType: typeof(T)));

        using var activity = Telemetry.StartSend("request", contract.DestinationName, ActivityKind.Client);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("rabbitmcp.request_id", requestId.ToString());
        activity?.SetTag("rabbitmcp.contract", contract.Name);
        activity?.SetTag("rabbitmcp.client", client);
        var started = time.GetTimestamp();

        try
        {
            var result = await SendRequestAsync(contract, message, timeoutSeconds, correlationId, messageId, requestId, cancellationToken);
            activity?.SetTag("rabbitmcp.outcome", result.Status);
            activity?.SetTag("rabbitmcp.response_type", result.ResponseType);
            if (result.Status != "responded")
                Telemetry.Fail(activity, result.Status);
            Telemetry.RecordRequestDuration(time.GetElapsedTime(started), contract.Name, result.Status);
            Audit(result.Status, result.ResponseType is null ? result.Note : $"reply {result.ResponseType}");
            return result;
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            var detail = error?.Message ?? "cancelled (the request may or may not have been sent)";
            Telemetry.Fail(activity, detail);
            Telemetry.RecordRequestDuration(time.GetElapsedTime(started), contract.Name, "error");
            Audit("error", detail);
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw error;
        }
    }

    private async Task<RequestResult> SendRequestAsync<T>(
        ContractDescriptor contract, T message, int timeoutSeconds, string? correlationId,
        Guid messageId, Guid requestId, CancellationToken cancellationToken)
        where T : class
    {
        if (timeoutSeconds is < 1 or > MaxTimeoutSeconds)
            throw new McpException($"timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");

        await EnsureSendableAsync(contract, message, correlationId, cancellationToken);

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // Listen first, then send: a fast reply can never be missed.
        var pending = await replies.RegisterAsync(requestId, cancellationToken, contract.Format);
        try
        {
            var outgoing = Build(contract, message, messageId, correlationId,
                new RequestTarget(requestId, pending, time.GetUtcNow().UtcDateTime + timeout, timeout));

            var started = time.GetTimestamp();
            await SendAsync(contract, outgoing, cancellationToken);
            logger.LogInformation("Sent request {Contract} {RequestId} to {Destination}", contract.Name, requestId, contract.Destination);

            ReceivedReply reply;
            try
            {
                reply = await pending.Reply.WaitAsync(timeout, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new RequestResult(
                    requestId.ToString(), messageId.ToString(), contract.Name, "timeout",
                    null, null, null, null, null, null,
                    (long)time.GetElapsedTime(started).TotalMilliseconds,
                    $"No reply within {timeoutSeconds}s. The request had the same TTL, so the broker drops it if no consumer " +
                    (contract.IsRaw
                        ? "picked it up. Check the consumer's queue with contract_consumers / queue_stats, and that the replier " +
                          "answers to reply_to with the request's correlation_id."
                        : "picked it up. Check the consumer's queue and its _error queue with queue_stats / peek_messages."));
            }

            var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
            var r = ReplyInterpreter.Interpret(contract, reply);

            return new RequestResult(
                requestId.ToString(), messageId.ToString(), contract.Name, r.Status,
                r.ResponseType, reply.MessageTypes, r.Response, r.ResponseCheck, r.Fault, r.RespondedBy, elapsed,
                r.ResponseCheck is { Valid: false }
                    ? $"The consumer replied with a {r.ResponseType} that violates its contract."
                    : r.Note
                      ?? (r.Status == "responded" && r.ResponseCheck is null
                          ? $"Reply type is not one of the declared responses ({string.Join(", ", contract.Responses.Select(x => x.Name))})."
                          : null));
        }
        finally
        {
            replies.Forget(requestId);
        }
    }

    /// <summary>What a request adds to the outgoing message: where the reply goes and when the request expires.</summary>
    private sealed record RequestTarget(Guid RequestId, PendingReply Reply, DateTime ExpiresUtc, TimeSpan TimeToLive);

    /// <summary>The message as it goes on the wire.</summary>
    private sealed record Outgoing(BasicProperties Properties, ReadOnlyMemory<byte> Body, string MessageId,
        string? ConversationId, DateTime SentTimeUtc);

    /// <summary>MassTransit envelope, or plain JSON with AMQP properties for raw contracts.</summary>
    private Outgoing Build(ContractDescriptor contract, object message, Guid messageId, string? correlationId, RequestTarget? request)
    {
        var now = time.GetUtcNow();
        var clientName = options.Value.ClientName;

        if (contract.IsRaw)
        {
            var props = RawMessages.CreateProperties(contract, messageId, correlationId, clientName, now,
                request is null ? null : new RawRequestInfo(request.RequestId, request.Reply.QueueName), request?.TimeToLive);
            return new Outgoing(props, RawMessages.Serialize(contract, message), messageId.ToString(), null, now.UtcDateTime);
        }

        var envelope = envelopes.Create(contract, message, connection.BrokerUri, clientName, messageId, correlationId,
            request is null ? null : new RequestEnvelopeInfo(request.RequestId, request.Reply.ResponseAddress, request.ExpiresUtc));
        var properties = new BasicProperties
        {
            ContentType = MassTransitJson.ContentType,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds()),
            Expiration = request is null
                ? null
                : ((long)request.TimeToLive.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            Headers = new Dictionary<string, object?>
            {
                [HeaderNames.ContractVersion] = contract.Version,
                [HeaderNames.PublishedBy] = clientName,
            },
        };
        return new Outgoing(properties, EnvelopeFactory.Serialize(envelope), envelope.MessageId!, envelope.ConversationId,
            envelope.SentTime!.Value);
    }

    private static string? AuditExchange(ContractDescriptor contract) => contract.UsesDefaultExchange ? null : contract.Exchange;

    private static string? AuditQueue(ContractDescriptor contract) => contract.UsesDefaultExchange ? contract.RoutingKey : null;

    private async Task EnsureSendableAsync(ContractDescriptor contract, object message, string? correlationId, CancellationToken ct)
    {
        // Normally the tool is not even registered; this also covers direct callers.
        if (!policy.IsAllowed(contract))
            throw new McpException($"Sending {contract.Name} is not allowed by this server's configuration (RabbitMcp:PublishAllow/PublishDeny).");

        // Contract rules (DataAnnotations). Nothing is sent when they fail.
        var errors = ContractValidator.Validate(message);
        if (errors.Count > 0)
            throw new McpException(
                $"Message violates contract {contract.Name} v{contract.Version}; nothing was sent. " +
                string.Join("; ", errors));

        if (correlationId is not null && !Guid.TryParse(correlationId, out _))
            throw new McpException("correlationId must be a GUID.");

        // The default exchange always exists (and cannot be declared, not even passively): check the queue instead.
        if (contract.UsesDefaultExchange)
        {
            if (await connection.GetQueueAsync(contract.RoutingKey, ct) is null)
                throw new McpException(
                    $"Queue '{contract.RoutingKey}' does not exist, so no service consumes {contract.Name} yet. Nothing was sent.");
            return;
        }

        // Publishing to a missing exchange closes the channel and loses the message.
        if (!await connection.ExchangeExistsAsync(contract.Exchange, ct))
            throw new McpException(
                $"Exchange '{contract.Exchange}' does not exist, so no service consumes {contract.Name} yet" +
                (contract.IsRaw ? "." : " (MassTransit creates it when a consumer starts).") + " Nothing was sent.");
    }

    /// <summary>Publisher confirms + mandatory: an unroutable message is an error, not a silent drop.</summary>
    private async Task SendAsync(ContractDescriptor contract, Outgoing outgoing, CancellationToken ct)
    {
        // The consumer's span becomes a child of ours (traceparent + MassTransit's MT-Activity-Id).
        Telemetry.Inject(Activity.Current, outgoing.Properties.Headers!);

        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
        try
        {
            await channel.BasicPublishAsync(
                contract.Exchange, contract.RoutingKey, mandatory: true, outgoing.Properties, outgoing.Body, ct);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            throw new McpException(contract.UsesDefaultExchange
                ? $"Queue '{contract.RoutingKey}' no longer exists; the broker returned the message. Nothing was delivered."
                : contract.RoutingKey.Length == 0
                    ? $"Exchange '{contract.Exchange}' exists but no queue is bound to it; the broker returned the message. Nothing was delivered."
                    : $"No queue is bound to exchange '{contract.Exchange}' for routing key '{contract.RoutingKey}'; the broker " +
                      "returned the message. Nothing was delivered.");
        }
        catch (OperationInterruptedException ex) when (RabbitConnection.IsNotFound(ex))
        {
            // The exchange was deleted after it was last seen (cached check). Forget it.
            connection.InvalidateExchange(contract.Exchange);
            throw new McpException(
                $"Exchange '{contract.Exchange}' no longer exists, so no service consumes {contract.Name} right now. Nothing was delivered.");
        }
        catch (PublishException)
        {
            throw new McpException("The broker rejected the message (nack). Nothing was delivered.");
        }
    }
}

/// <summary>Creates one publish_* (event) or request_* (request/response) tool per registered contract.</summary>
public static class ContractToolFactory
{
    private static readonly MethodInfo PublishMethod =
        typeof(ContractPublisher).GetMethod(nameof(ContractPublisher.PublishAsync))!;

    private static readonly MethodInfo RequestMethod =
        typeof(ContractPublisher).GetMethod(nameof(ContractPublisher.RequestAsync))!;

    public static IEnumerable<McpServerTool> CreateTools(ContractRegistry registry, PublishPolicy? policy = null) =>
        registry.Contracts.Where(c => (policy ?? PublishPolicy.AllowAll).IsAllowed(c)).Select(CreateTool);

    public static McpServerTool CreateTool(ContractDescriptor contract) =>
        McpServerTool.Create(
            (contract.IsRequest ? RequestMethod : PublishMethod).MakeGenericMethod(contract.Type),
            ctx => ctx.Services!.GetRequiredService<ContractPublisher>(),
            new McpServerToolCreateOptions
            {
                Name = contract.ToolName,
                Title = $"{(contract.IsRequest ? "Request" : "Publish")} {contract.Name} (v{contract.Version})",
                Description = contract.IsRequest ? RequestDescription(contract) : PublishDescription(contract),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
                UseStructuredContent = true,
                SerializerOptions = ToolJson.Options,
            });

    private static string PublishDescription(ContractDescriptor c) =>
        $"{c.Description}\n\n" +
        (c.IsRaw
            ? $"Publishes the message as plain JSON (AMQP type {c.MessageTypeUrn}, contract v{c.Version}) to {c.Destination}. "
            : $"Publishes a {c.MessageTypeUrn} message (contract v{c.Version}) to {c.Destination} in MassTransit format. ") +
        "The message is validated against the contract first; an invalid message is rejected and nothing is sent. " +
        "Use validate_message to check a message without publishing.";

    private static string RequestDescription(ContractDescriptor c) =>
        $"{c.Description}\n\n" +
        (c.IsRaw
            ? $"Sends the request as plain JSON (AMQP type {c.MessageTypeUrn}, contract v{c.Version}) to {c.Destination} with " +
              "reply_to and correlation_id (RabbitMQ RPC) and waits for the reply. " +
              $"Possible replies: {string.Join(", ", c.Responses.Select(r => r.Name))}, named by the reply's AMQP type; " +
              "the reply is validated against its contract. "
            : $"Sends a {c.MessageTypeUrn} request (contract v{c.Version}) to {c.Destination} and waits for the consumer's reply " +
              $"(MassTransit request/response). Possible replies: {string.Join(", ", c.Responses.Select(r => r.Name))}; " +
              "the reply is validated against its contract. If the consumer throws, Status is 'faulted' with the exception details. ") +
        "If nobody answers in time, Status is 'timeout'.";
}
