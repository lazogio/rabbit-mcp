// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

/// <summary>The request contracts this server answers (RabbitMcp:RespondTo).</summary>
public sealed record ResponderContracts(IReadOnlyList<ContractDescriptor> Requests)
{
    /// <summary>Every reply type of every answered request, once.</summary>
    public IReadOnlyList<ResponseDescriptor> Responses { get; } =
        Requests.SelectMany(r => r.Responses).DistinctBy(r => r.Type).ToArray();

    public static ResponderContracts From(ContractRegistry registry, string? respondTo)
    {
        var policy = new PublishPolicy(respondTo, null);
        var requests = registry.Contracts.Where(c => c.IsRequest && policy.IsAllowed(c)).ToArray();
        if (requests.Where(c => c.IsRaw).Select(c => c.Name).ToArray() is { Length: > 0 } raw)
            throw new RabbitMcp.Server.Hosting.StartupException(
                $"RabbitMcp:RespondTo matches raw contract(s) {string.Join(", ", raw)}: responder mode answers MassTransit " +
                "requests only. Narrow RespondTo to MassTransit request contracts.");
        if (requests.Where(c => c.IsSchemaBased).Select(c => c.Name).ToArray() is { Length: > 0 } fromFiles)
            throw new RabbitMcp.Server.Hosting.StartupException(
                $"RabbitMcp:RespondTo matches contract(s) from AsyncAPI files ({string.Join(", ", fromFiles)}): responder mode " +
                "needs C# contracts (its reply tools are typed).");
        // The responder binds its queue to the contract's fanout exchange, as MassTransit endpoints do.
        if (requests.Where(c => c.RoutingKey.Length > 0).Select(c => c.Name).ToArray() is { Length: > 0 } routed)
            throw new RabbitMcp.Server.Hosting.StartupException(
                $"RabbitMcp:RespondTo matches contract(s) with a RoutingKey ({string.Join(", ", routed)}): responder mode " +
                "listens on MassTransit's fanout exchange of a contract and does not support routing keys.");
        if (requests.Length == 0)
            throw new RabbitMcp.Server.Hosting.StartupException(
                $"RabbitMcp:RespondTo '{respondTo}' matches no request contract (contracts with Responses).");
        return new ResponderContracts(requests);
    }

    public static string ReplyToolName(ResponseDescriptor response) => "reply_" + ContractRegistry.ToSnakeCase(response.Name);
}

/// <summary>
/// Responder mode: listens for requests of <see cref="ResponderContracts"/> on an exclusive queue bound to their
/// exchanges (declared like MassTransit does: fanout, durable). Each request is acknowledged on arrival and kept
/// in the <see cref="RequestInbox"/> until the model answers (reply_*), rejects it (a Fault, like a throwing
/// consumer), or it expires. Keeps retrying when the broker is unreachable.
/// </summary>
public sealed class ResponderService(
    RabbitConnection connection,
    ResponderContracts contracts,
    RequestInbox inbox,
    AuditLog audit,
    TimeProvider time,
    IOptions<RabbitMcpOptions> options,
    ILogger<ResponderService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const ushort ResourceLocked = 405;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listens;

    public ResponderContracts Contracts => contracts;

    /// <summary>Completes the first time the queue is bound and consuming.</summary>
    public Task Ready => _ready.Task;

    /// <summary>The queue being consumed; null while not listening.</summary>
    public string? QueueName { get; private set; }

    /// <summary>starting | listening | standby (another instance holds the queue) | disconnected</summary>
    public string Status { get; private set; } = "starting";

    /// <summary>How many times listening (re)started — grows after every reconnect.</summary>
    public int Listens => Volatile.Read(ref _listens);

    public IReadOnlyList<PendingRequest> Pending() => inbox.List();

    /// <summary>Long poll for a pending request (see <see cref="RequestInbox.WaitForAsync"/>).</summary>
    public Task<bool> WaitForPendingAsync(Func<PendingRequest, bool> match, TimeSpan timeout, CancellationToken ct) =>
        inbox.WaitForAsync(match, timeout, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastProblem = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                QueueName = null;
                var standby = ex is OperationInterruptedException oie && oie.ShutdownReason?.ReplyCode == ResourceLocked;
                Status = standby ? "standby" : "disconnected";
                var problem = standby
                    ? "another rabbit-mcp instance with the same ClientName is responding; standing by"
                    : ex.Message;
                // Log a changed situation once, not every retry.
                if (problem != lastProblem)
                    logger.LogWarning("Responder is not listening ({Reason}); retrying every {Delay}s", AuditLog.OneLine(problem), RetryDelay.TotalSeconds);
                lastProblem = problem;
            }

            try { await Task.Delay(RetryDelay, time, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Consumes until the channel is lost (then throws, and ExecuteAsync starts over) or the host stops.</summary>
    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, e) =>
        {
            lost.TrySetResult($"channel closed: {e.ReplyCode} {e.ReplyText}");
            return Task.CompletedTask;
        };

        var queue = ReplyEndpoint.ResponderQueueName(options.Value.ClientName);
        // Exclusive: a second instance gets RESOURCE_LOCKED and stands by, so each request is answered once.
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: stoppingToken);
        foreach (var contract in contracts.Requests)
        {
            // Same flags MassTransit uses for publish exchanges, so either side may create it first.
            await channel.ExchangeDeclareAsync(contract.Exchange, ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: stoppingToken);
            await channel.QueueBindAsync(queue, contract.Exchange, routingKey: "", cancellationToken: stoppingToken);
        }
        await channel.BasicQosAsync(0, 50, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnRequestAsync(channel, delivery);
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);

        QueueName = queue;
        Status = "listening";
        Interlocked.Increment(ref _listens);
        _ready.TrySetResult();
        logger.LogInformation("Responder listening on {Queue} for {Contracts}",
            queue, string.Join(", ", contracts.Requests.Select(c => c.Name)));

        string reason;
        try { reason = await lost.Task.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        QueueName = null;
        Status = "disconnected";
        throw new InvalidOperationException(reason);
    }

    private async Task OnRequestAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        try
        {
            var envelope = MassTransitEnvelope.TryParse(delivery.Body.Span);
            var contract = envelope?.MessageType is null ? null : contracts.Requests.FirstOrDefault(c =>
                envelope.MessageType.Contains(c.MessageTypeUrn, StringComparer.OrdinalIgnoreCase));

            if (envelope is null || contract is null)
            {
                logger.LogWarning("Responder ignored a message that is not a known request ({Bytes} bytes)", delivery.Body.Length);
                return;
            }
            if (!Guid.TryParse(envelope.RequestId, out var requestId) || string.IsNullOrEmpty(envelope.ResponseAddress))
            {
                logger.LogInformation("Responder ignored a {Contract} without requestId/responseAddress (published, not requested)", contract.Name);
                return;
            }

            var expires = envelope.ExpirationTime is { } exp
                ? new DateTimeOffset(exp.Kind == DateTimeKind.Local ? exp.ToUniversalTime() : DateTime.SpecifyKind(exp, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            if (expires <= time.GetUtcNow())
                return; // the requester has already given up

            var request = new PendingRequest(requestId, contract, envelope, time.GetUtcNow(), expires,
                Telemetry.Extract(delivery.BasicProperties.Headers));

            // responseAddress/faultAddress come from the message: whoever can publish the request chooses them. Only
            // MassTransit-style temporary reply endpoints are answered (or faulted, e.g. "busy"), never other exchanges.
            if (await UnsafeReplyAddressAsync(request) is { } refusal)
            {
                logger.LogWarning("Responder refused {Contract} {RequestId}: {Reason}", contract.Name, requestId, AuditLog.OneLine(refusal));
                audit.Write(audit.Event(AuditActions.Receive, "refused", envelope.SourceAddress, contract.Name, contract.Exchange,
                    queue: QueueName, messageId: envelope.MessageId, requestId: envelope.RequestId,
                    correlationId: envelope.CorrelationId, detail: refusal));
                return;
            }

            // pending_requests shows every request: one that a tool result cannot carry would hide the others.
            if (envelope.Message is { } message && !MessageInspector.CanShow(message))
            {
                logger.LogWarning("Responder refused {Contract} {RequestId}: its message cannot be shown", contract.Name, requestId);
                await SendFaultAsync(request, "The request has broken text or is nested too deeply to be answered.",
                    null, keepOnFailure: false, CancellationToken.None);
                return;
            }

            switch (inbox.TryAdd(request))
            {
                case InboxAddResult.Duplicate:
                    logger.LogInformation("Responder ignored a duplicate delivery of request {RequestId}", requestId);
                    return;
                case InboxAddResult.Full:
                    await SendFaultAsync(request, $"rabbit-mcp responder is busy ({inbox.Capacity} requests pending).",
                        null, keepOnFailure: false, CancellationToken.None);
                    return;
            }

            audit.Write(audit.Event(AuditActions.Receive, "pending", envelope.SourceAddress, contract.Name, contract.Exchange,
                queue: QueueName, messageId: envelope.MessageId, requestId: envelope.RequestId,
                correlationId: envelope.CorrelationId, message: envelope.Message, messageType: typeof(System.Text.Json.JsonElement)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Responder failed to handle an incoming message");
        }
        finally
        {
            if (channel.IsOpen)
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
    }

    /// <summary>Null when every reply address of the request is a temporary reply endpoint; otherwise why not.</summary>
    private async Task<string?> UnsafeReplyAddressAsync(PendingRequest request)
    {
        foreach (var address in new[] { request.ResponseAddress, request.FaultAddress }.Distinct(StringComparer.Ordinal))
        {
            var exchange = BrokerAddress.GetEntityName(address);
            if (exchange is null)
                return $"reply address '{address}' cannot be parsed";

            switch (await connection.CheckTemporaryExchangeAsync(exchange, CancellationToken.None))
            {
                case ReplyEndpointCheck.Missing:
                    return $"reply exchange '{exchange}' does not exist (the requester is gone)";
                case ReplyEndpointCheck.NotTemporary:
                    return $"reply address '{exchange}' is not a temporary reply endpoint (fanout, durable:false, autoDelete:true); " +
                           "answering it would publish into a regular exchange";
            }
        }
        return null;
    }

    /// <summary>Answers a pending request with one of its declared reply types.</summary>
    public async Task<ReplyResult> ReplyAsync<TResponse>(Guid requestId, TResponse response, string? client, CancellationToken ct)
        where TResponse : class
    {
        var request = Take(requestId);
        var descriptor = request.Contract.Responses.FirstOrDefault(r => r.Type == typeof(TResponse));
        if (descriptor is null)
        {
            inbox.Return(request);
            throw new McpException(
                $"{typeof(TResponse).Name} is not a reply to {request.Contract.Name}. Use one of: " +
                string.Join(", ", request.Contract.Responses.Select(ResponderContracts.ReplyToolName)) + " (or reject_request).");
        }

        var errors = ContractValidator.Validate(response);
        if (errors.Count > 0)
        {
            inbox.Return(request);
            throw new McpException($"Reply violates {descriptor.Name}; nothing was sent. " + string.Join("; ", errors));
        }

        var envelope = ResponderEnvelopes.Reply(request, descriptor, response, connection.BrokerUri,
            QueueName ?? options.Value.ClientName, Guid.NewGuid(), time.GetUtcNow().UtcDateTime);
        await SendAsync(request, envelope, request.ResponseAddress, "reply", $"reply {descriptor.Name}", client, keepOnFailure: true, ct);

        return new ReplyResult(request.RequestId.ToString(), request.Contract.Name, "replied", descriptor.Name,
            envelope.MessageId!, BrokerAddress.GetEntityName(request.ResponseAddress)!,
            (long)(time.GetUtcNow() - request.ReceivedAt).TotalMilliseconds);
    }

    /// <summary>Sends a Fault&lt;TRequest&gt;: the requester's GetResponse fails at once with this reason.</summary>
    public async Task<ReplyResult> RejectAsync(Guid requestId, string reason, string? client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new McpException("reason is required: the requester sees it as the fault message.");

        var request = Take(requestId);
        var envelope = await SendFaultAsync(request, reason.Trim(), client, keepOnFailure: true, ct);
        return new ReplyResult(request.RequestId.ToString(), request.Contract.Name, "rejected", "Fault",
            envelope.MessageId!, BrokerAddress.GetEntityName(request.FaultAddress)!,
            (long)(time.GetUtcNow() - request.ReceivedAt).TotalMilliseconds);
    }

    private PendingRequest Take(Guid requestId) =>
        inbox.TryTake(requestId) ?? throw new McpException(
            $"No pending request {requestId}: it was already answered, has expired (the requester gave up), or never arrived. " +
            "See pending_requests.");

    private async Task<MassTransitEnvelope> SendFaultAsync(
        PendingRequest request, string reason, string? client, bool keepOnFailure, CancellationToken ct)
    {
        var envelope = ResponderEnvelopes.Fault(request, reason, connection.BrokerUri,
            QueueName ?? options.Value.ClientName, Guid.NewGuid(), time.GetUtcNow().UtcDateTime);
        await SendAsync(request, envelope, request.FaultAddress, "reject", $"rejected: {reason}", client, keepOnFailure, ct);
        return envelope;
    }

    private async Task SendAsync(
        PendingRequest request, MassTransitEnvelope envelope, string destination, string operation, string detail,
        string? client, bool keepOnFailure, CancellationToken ct)
    {
        var exchange = BrokerAddress.GetEntityName(destination)
            ?? throw new McpException($"The request's reply address '{destination}' cannot be parsed.");

        using var activity = Telemetry.StartSend(operation, exchange, linkTo: request.TraceContext);
        activity?.SetTag("rabbitmcp.request_id", request.RequestId.ToString());
        activity?.SetTag("rabbitmcp.contract", request.Contract.Name);

        void Audit(string outcome, string text) =>
            audit.Write(audit.Event(AuditActions.Respond, outcome, client, request.Contract.Name, exchange,
                messageId: envelope.MessageId, requestId: request.RequestId.ToString(),
                correlationId: envelope.CorrelationId, detail: text, message: envelope.Message,
                messageType: typeof(System.Text.Json.JsonElement)));

        var props = new BasicProperties
        {
            ContentType = MassTransitJson.ContentType,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?> { [HeaderNames.PublishedBy] = options.Value.ClientName },
        };
        Telemetry.Inject(Activity.Current, props.Headers);

        try
        {
            await using var channel = await connection.CreatePublishChannelAsync(ct);
            await channel.PublishAsync(exchange, routingKey: "", props, EnvelopeFactory.Serialize(envelope), ct);
        }
        catch (Exception ex) when (ex is PublishException { IsReturn: true }
                                   || (ex is OperationInterruptedException oie && RabbitConnection.IsNotFound(oie)))
        {
            // The requester's temporary reply queue is gone: it stopped or timed out. Nothing to retry.
            Telemetry.Fail(activity, "requester gone");
            Audit("error", "requester gone");
            throw new McpException($"The requester of {request.RequestId} is no longer listening (it stopped or timed out). Nothing was delivered.");
        }
        catch (OperationCanceledException)
        {
            // The tool call was cancelled: the request was not answered, so it stays pending.
            if (keepOnFailure)
                inbox.Return(request);
            Telemetry.Fail(activity, "cancelled");
            Audit("error", "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Broker hiccup: keep the request so the model can try again.
            if (keepOnFailure)
                inbox.Return(request);
            Telemetry.Fail(activity, ex.Message);
            Audit("error", ex.Message);
            throw ex as McpException ?? new McpException($"Could not send the {operation}: {ex.Message}. The request is still pending.");
        }

        Audit(operation == "reject" ? "rejected" : "sent", detail);
        logger.LogInformation("Responder {Operation} for {Contract} {RequestId}", operation, request.Contract.Name, request.RequestId);
    }
}
