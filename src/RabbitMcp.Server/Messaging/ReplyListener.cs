// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMcp.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Requests waiting for a reply, keyed by RequestId. A MassTransit request is completed by an envelope with that
/// requestId; a raw request by a reply whose correlation_id is the request id — never the other way round.
/// </summary>
public sealed class PendingRequests
{
    private sealed record Entry(TaskCompletionSource<ReceivedReply> Reply, bool Raw);

    private readonly ConcurrentDictionary<Guid, Entry> _pending = new();

    public int Count => _pending.Count;

    public Task<ReceivedReply> Register(Guid requestId, MessageFormat format = MessageFormat.MassTransit)
    {
        var entry = new Entry(new TaskCompletionSource<ReceivedReply>(TaskCreationOptions.RunContinuationsAsynchronously),
            format == MessageFormat.Raw);
        if (!_pending.TryAdd(requestId, entry))
            throw new InvalidOperationException($"Request {requestId} is already pending.");
        return entry.Reply.Task;
    }

    public void Forget(Guid requestId) => _pending.TryRemove(requestId, out _);

    /// <summary>Completes the MassTransit request the reply belongs to. False for late or unknown replies.</summary>
    public bool TryComplete(MassTransitEnvelope reply) =>
        Guid.TryParse(reply.RequestId, out var requestId) && Complete(requestId, raw: false, ReceivedReply.Of(reply));

    /// <summary>Completes the raw request whose id is the reply's correlation_id. False for late or unknown replies.</summary>
    public bool TryComplete(RawReply reply) =>
        Guid.TryParse(reply.CorrelationId, out var requestId) && Complete(requestId, raw: true, ReceivedReply.Of(reply));

    private bool Complete(Guid requestId, bool raw, ReceivedReply reply) =>
        _pending.TryGetValue(requestId, out var entry) && entry.Raw == raw
        && _pending.TryRemove(new KeyValuePair<Guid, Entry>(requestId, entry))
        && entry.Reply.TrySetResult(reply);
}

/// <param name="ResponseAddress">MassTransit responseAddress of the reply endpoint.</param>
/// <param name="QueueName">The same endpoint's queue, the reply_to of raw requests.</param>
public sealed record PendingReply(string ResponseAddress, string QueueName, Task<ReceivedReply> Reply);

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

    /// <summary>
    /// The responder's queue: the same name for every instance with this ClientName, declared exclusive, so only one
    /// instance consumes at a time and the others stand by (RESOURCE_LOCKED) until it goes away.
    /// </summary>
    public static string ResponderQueueName(string clientName) => $"{Sanitize(clientName)}-responder";

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
/// MassTransit requests carry its address as responseAddress and replies are matched back by requestId; raw requests
/// carry its queue name as reply_to and replies are matched by correlation_id.
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
    private Endpoint? _endpoint;

    private sealed record Endpoint(string Address, string Queue);

    /// <summary>Register before publishing, so a fast reply can never arrive unobserved.</summary>
    public async Task<PendingReply> RegisterAsync(Guid requestId, CancellationToken ct,
        MessageFormat format = MessageFormat.MassTransit)
    {
        var endpoint = await EnsureListeningAsync(ct);
        return new PendingReply(endpoint.Address, endpoint.Queue, _pending.Register(requestId, format));
    }

    public void Forget(Guid requestId) => _pending.Forget(requestId);

    private async Task<Endpoint> EnsureListeningAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } && _endpoint is { } endpoint)
            return endpoint;

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true } && _endpoint is { } existing)
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
            _endpoint = new Endpoint(ReplyEndpoint.Address(connection.BrokerUri, name), name);
            logger.LogInformation("Listening for replies on {ReplyQueue}", name);
            return _endpoint;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnReplyAsync(object sender, BasicDeliverEventArgs delivery)
    {
        // The body buffer is only valid during this handler; TryParse/From copy what they keep.
        var envelope = MassTransitEnvelope.TryParse(delivery.Body.Span);
        if (envelope is not null)
        {
            if (!_pending.TryComplete(envelope))
                logger.LogInformation("Ignoring late or unknown reply for request {RequestId}", envelope.RequestId);
        }
        else if (!string.IsNullOrEmpty(delivery.BasicProperties.CorrelationId))
        {
            var raw = RawReply.From(delivery.BasicProperties, delivery.Body.Span, options.Value.MaxPayloadChars);
            if (!_pending.TryComplete(raw))
                logger.LogInformation("Ignoring late or unknown raw reply with correlation id {CorrelationId}", raw.CorrelationId);
        }
        else
        {
            logger.LogWarning("Ignoring a reply that is neither a MassTransit envelope nor has a correlation_id ({Bytes} bytes)",
                delivery.Body.Length);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        _gate.Dispose();
    }
}
