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
        using var activity = Telemetry.StartSend("publish", contract.Exchange);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("rabbitmcp.contract", contract.Name);
        activity?.SetTag("rabbitmcp.client", client);
        MassTransitEnvelope envelope;
        try
        {
            await EnsureSendableAsync(contract, message, correlationId, cancellationToken);

            envelope = envelopes.Create(
                contract, message, connection.BrokerUri, options.Value.ClientName, messageId, correlationId);

            await SendAsync(contract, envelope, timeToLive: null, cancellationToken);
        }
        catch (Exception ex)
        {
            // Every failed attempt is audited — broker errors (403, lost connection) and cancellation included.
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            var detail = error?.Message ?? "cancelled (the message may or may not have been sent)";
            Telemetry.Fail(activity, detail);
            audit.Write(audit.Event(AuditActions.Publish, "error", client, contract.Name, contract.Exchange,
                correlationId: correlationId, detail: detail, message: message, messageType: typeof(T)));
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw error;
        }

        logger.LogInformation("Published {Contract} {MessageId} to {Exchange}", contract.Name, messageId, contract.Exchange);
        audit.Write(audit.Event(AuditActions.Publish, "sent", client, contract.Name, contract.Exchange,
            messageId: envelope.MessageId, correlationId: correlationId, message: message, messageType: typeof(T)));

        return new PublishResult(
            MessageId: envelope.MessageId!,
            ConversationId: envelope.ConversationId!,
            CorrelationId: correlationId,
            Contract: contract.Name,
            ContractVersion: contract.Version,
            MessageType: contract.MessageTypeUrn,
            Exchange: contract.Exchange,
            SentTimeUtc: envelope.SentTime!.Value,
            Routed: true);
    }

    /// <summary>
    /// MassTransit request client semantics: the request carries requestId + responseAddress
    /// (this server's temporary reply queue) and a TTL equal to the timeout; the consumer's
    /// RespondAsync (or its Fault) comes back to that queue and is matched by requestId.
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
            audit.Write(audit.Event(AuditActions.Request, outcome, client, contract.Name, contract.Exchange,
                messageId: messageId.ToString(), requestId: requestId.ToString(), correlationId: correlationId,
                detail: detail, message: message, messageType: typeof(T)));

        using var activity = Telemetry.StartSend("request", contract.Exchange, ActivityKind.Client);
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
        var pending = await replies.RegisterAsync(requestId, cancellationToken);
        try
        {
            var envelope = envelopes.Create(
                contract, message, connection.BrokerUri, options.Value.ClientName, messageId, correlationId,
                new RequestEnvelopeInfo(requestId, pending.ResponseAddress, time.GetUtcNow().UtcDateTime + timeout));

            var started = time.GetTimestamp();
            await SendAsync(contract, envelope, timeToLive: timeout, cancellationToken);
            logger.LogInformation("Sent request {Contract} {RequestId} to {Exchange}", contract.Name, requestId, contract.Exchange);

            MassTransitEnvelope reply;
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
                    "picked it up. Check the consumer's queue and its _error queue with queue_stats / peek_messages.");
            }

            var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
            var r = ReplyInterpreter.Interpret(contract, reply);

            return new RequestResult(
                requestId.ToString(), messageId.ToString(), contract.Name, r.Status,
                r.ResponseType, reply.MessageType, r.Response, r.ResponseCheck, r.Fault, r.RespondedBy, elapsed,
                r.ResponseCheck is { Valid: false }
                    ? $"The consumer replied with a {r.ResponseType} that violates its contract."
                    : r.Status == "responded" && r.ResponseCheck is null
                        ? $"Reply type is not one of the declared responses ({string.Join(", ", contract.Responses.Select(x => x.Name))})."
                        : null);
        }
        finally
        {
            replies.Forget(requestId);
        }
    }

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

        // Publishing to a missing exchange closes the channel and loses the message.
        if (!await connection.ExchangeExistsAsync(contract.Exchange, ct))
            throw new McpException(
                $"Exchange '{contract.Exchange}' does not exist, so no service consumes {contract.Name} yet " +
                "(MassTransit creates it when a consumer starts). Nothing was sent.");
    }

    /// <summary>Publisher confirms + mandatory: an unroutable message is an error, not a silent drop.</summary>
    private async Task SendAsync(ContractDescriptor contract, MassTransitEnvelope envelope, TimeSpan? timeToLive, CancellationToken ct)
    {
        var props = new BasicProperties
        {
            ContentType = MassTransitJson.ContentType,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(time.GetUtcNow().ToUnixTimeSeconds()),
            Expiration = timeToLive is { } ttl
                ? ((long)ttl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : null,
            Headers = new Dictionary<string, object?>
            {
                [HeaderNames.ContractVersion] = contract.Version,
                [HeaderNames.PublishedBy] = options.Value.ClientName,
            },
        };
        // The consumer's span becomes a child of ours (traceparent + MassTransit's MT-Activity-Id).
        Telemetry.Inject(Activity.Current, props.Headers!);

        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
        try
        {
            await channel.BasicPublishAsync(
                contract.Exchange, routingKey: "", mandatory: true, props,
                EnvelopeFactory.Serialize(envelope), ct);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            throw new McpException(
                $"Exchange '{contract.Exchange}' exists but no queue is bound to it; the broker returned the message. Nothing was delivered.");
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
        $"Publishes a {c.MessageTypeUrn} message (contract v{c.Version}) to exchange '{c.Exchange}' in MassTransit format. " +
        "The message is validated against the contract first; an invalid message is rejected and nothing is sent. " +
        "Use validate_message to check a message without publishing.";

    private static string RequestDescription(ContractDescriptor c) =>
        $"{c.Description}\n\n" +
        $"Sends a {c.MessageTypeUrn} request (contract v{c.Version}) to exchange '{c.Exchange}' and waits for the consumer's reply " +
        $"(MassTransit request/response). Possible replies: {string.Join(", ", c.Responses.Select(r => r.Name))}; " +
        "the reply is validated against its contract. If the consumer throws, Status is 'faulted' with the exception details. " +
        "If nobody answers in time, Status is 'timeout'.";
}
