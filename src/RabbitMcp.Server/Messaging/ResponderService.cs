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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ResponderContracts Contracts => contracts;

    /// <summary>Completes once the queue is bound and consuming.</summary>
    public Task Ready => _ready.Task;

    public string? QueueName { get; private set; }

    public IReadOnlyList<PendingRequest> Pending() => inbox.List();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Responder is not listening yet ({Reason}); retrying in {Delay}s",
                    ex.Message, RetryDelay.TotalSeconds);
                try { await Task.Delay(RetryDelay, time, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        var queue = ReplyEndpoint.CreateName(options.Value.ClientName + "-responder");

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
        _ready.TrySetResult();
        logger.LogInformation("Responder listening on {Queue} for {Contracts}",
            queue, string.Join(", ", contracts.Requests.Select(c => c.Name)));

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task OnRequestAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        try
        {
            var envelope = MassTransitEnvelope.TryParse(delivery.Body.Span);
            var contract = envelope is null ? null : contracts.Requests.FirstOrDefault(c =>
                envelope.MessageType!.Contains(c.MessageTypeUrn, StringComparer.OrdinalIgnoreCase));

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
                ? new DateTimeOffset(DateTime.SpecifyKind(exp, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            if (expires <= time.GetUtcNow())
                return; // the requester has already given up

            var request = new PendingRequest(requestId, contract, envelope, time.GetUtcNow(), expires,
                Telemetry.Extract(delivery.BasicProperties.Headers));

            if (!inbox.TryAdd(request))
            {
                await SendFaultAsync(request, $"rabbit-mcp responder is busy ({inbox.Capacity} requests pending).", null, CancellationToken.None);
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
        await SendAsync(request, envelope, request.ResponseAddress, "reply", $"reply {descriptor.Name}", client, ct);

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
        var envelope = await SendFaultAsync(request, reason.Trim(), client, ct);
        return new ReplyResult(request.RequestId.ToString(), request.Contract.Name, "rejected", "Fault",
            envelope.MessageId!, BrokerAddress.GetEntityName(request.FaultAddress)!,
            (long)(time.GetUtcNow() - request.ReceivedAt).TotalMilliseconds);
    }

    private PendingRequest Take(Guid requestId) =>
        inbox.TryTake(requestId) ?? throw new McpException(
            $"No pending request {requestId}: it was already answered, has expired (the requester gave up), or never arrived. " +
            "See pending_requests.");

    private async Task<MassTransitEnvelope> SendFaultAsync(PendingRequest request, string reason, string? client, CancellationToken ct)
    {
        var envelope = ResponderEnvelopes.Fault(request, reason, connection.BrokerUri,
            QueueName ?? options.Value.ClientName, Guid.NewGuid(), time.GetUtcNow().UtcDateTime);
        await SendAsync(request, envelope, request.FaultAddress, "reject", $"rejected: {reason}", client, ct);
        return envelope;
    }

    private async Task SendAsync(
        PendingRequest request, MassTransitEnvelope envelope, string destination, string operation, string detail,
        string? client, CancellationToken ct)
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
            await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
            await channel.BasicPublishAsync(exchange, routingKey: "", mandatory: true, props,
                EnvelopeFactory.Serialize(envelope), ct);
        }
        catch (Exception ex) when (ex is PublishException { IsReturn: true }
                                   || (ex is OperationInterruptedException oie && RabbitConnection.IsNotFound(oie)))
        {
            // The requester's temporary reply queue is gone: it stopped or timed out. Nothing to retry.
            Telemetry.Fail(activity, "requester gone");
            Audit("error", "requester gone");
            throw new McpException($"The requester of {request.RequestId} is no longer listening (it stopped or timed out). Nothing was delivered.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broker hiccup: keep the request so the model can try again.
            inbox.Return(request);
            Telemetry.Fail(activity, ex.Message);
            Audit("error", ex.Message);
            throw ex as McpException ?? new McpException($"Could not send the {operation}: {ex.Message}. The request is still pending.");
        }

        Audit(operation == "reject" ? "rejected" : "sent", detail);
        logger.LogInformation("Responder {Operation} for {Contract} {RequestId}", operation, request.Contract.Name, request.RequestId);
    }
}
