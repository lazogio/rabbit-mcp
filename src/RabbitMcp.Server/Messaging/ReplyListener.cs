using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMcp.Server.Messaging;

/// <summary>Requests waiting for a reply, keyed by RequestId.</summary>
public sealed class PendingRequests
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MassTransitEnvelope>> _pending = new();

    public int Count => _pending.Count;

    public Task<MassTransitEnvelope> Register(Guid requestId)
    {
        var tcs = new TaskCompletionSource<MassTransitEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, tcs))
            throw new InvalidOperationException($"Request {requestId} is already pending.");
        return tcs.Task;
    }

    public void Forget(Guid requestId) => _pending.TryRemove(requestId, out _);

    /// <summary>Completes the request the reply belongs to. False for late or unknown replies.</summary>
    public bool TryComplete(MassTransitEnvelope reply) =>
        Guid.TryParse(reply.RequestId, out var requestId)
        && _pending.TryRemove(requestId, out var tcs)
        && tcs.TrySetResult(reply);
}

public sealed record PendingReply(string ResponseAddress, Task<MassTransitEnvelope> Reply);

/// <summary>Naming of the server's temporary reply endpoint.</summary>
public static class ReplyEndpoint
{
    /// <summary>
    /// MassTransit reads "?temporary=true" as durable=false, autoDelete=true and declares the
    /// exchange with exactly those flags before replying, so ours must be declared the same way.
    /// </summary>
    public static string Address(Uri brokerUri, string name) => BrokerAddress.ForEntity(brokerUri, name) + "?temporary=true";

    public static string CreateName(string clientName) =>
        $"{Sanitize(clientName)}_{Sanitize(Environment.MachineName)}_{Guid.NewGuid():N}";

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-');
        return sb.ToString();
    }
}

/// <summary>
/// One temporary reply queue per server process (like a MassTransit bus endpoint).
/// Requests carry its address as responseAddress; replies are matched back by requestId.
/// The exchange/queue/consumer are re-created if the channel was lost.
/// </summary>
public sealed class ReplyListener(
    RabbitConnection connection,
    IOptions<RabbitMcpOptions> options,
    ILogger<ReplyListener> logger) : IAsyncDisposable
{
    private readonly PendingRequests _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private string? _responseAddress;

    /// <summary>Register before publishing, so a fast reply can never arrive unobserved.</summary>
    public async Task<PendingReply> RegisterAsync(Guid requestId, CancellationToken ct)
    {
        var address = await EnsureListeningAsync(ct);
        return new PendingReply(address, _pending.Register(requestId));
    }

    public void Forget(Guid requestId) => _pending.Forget(requestId);

    private async Task<string> EnsureListeningAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } && _responseAddress is { } address)
            return address;

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true } && _responseAddress is { } existing)
                return existing;

            if (_channel is not null)
                await _channel.DisposeAsync();

            var name = ReplyEndpoint.CreateName(options.Value.ClientName);
            var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);

            await channel.ExchangeDeclareAsync(name, ExchangeType.Fanout, durable: false, autoDelete: true, cancellationToken: ct);
            await channel.QueueDeclareAsync(name, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);
            await channel.QueueBindAsync(name, name, routingKey: "", cancellationToken: ct);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnReplyAsync;
            await channel.BasicConsumeAsync(name, autoAck: true, consumer, ct);

            _channel = channel;
            _responseAddress = ReplyEndpoint.Address(connection.BrokerUri, name);
            logger.LogInformation("Listening for replies on {ReplyQueue}", name);
            return _responseAddress;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnReplyAsync(object sender, BasicDeliverEventArgs delivery)
    {
        // The body buffer is only valid during this handler; TryParse copies what it keeps.
        var reply = MassTransitEnvelope.TryParse(delivery.Body.Span);
        if (reply is null)
            logger.LogWarning("Ignoring a reply that is not a MassTransit envelope ({Bytes} bytes)", delivery.Body.Length);
        else if (!_pending.TryComplete(reply))
            logger.LogInformation("Ignoring late or unknown reply for request {RequestId}", reply.RequestId);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        _gate.Dispose();
    }
}
