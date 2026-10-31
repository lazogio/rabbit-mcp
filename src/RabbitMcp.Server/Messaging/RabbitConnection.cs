// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

public enum ReplyEndpointCheck { Temporary, Missing, NotTemporary }

/// <summary>
/// Lazily opened, auto-recovering AMQP connections shared by all tools. Channels are short-lived: one per tool call.
/// Two kinds of work get a connection of their own. Publishing: during a broker alarm RabbitMQ stops reading from any
/// connection that publishes, and everything else must keep working meanwhile. Browsing (basic.get): on a stream it
/// closes the whole connection (540 NOT_IMPLEMENTED), and AMQP cannot tell a stream beforehand, so consumers and
/// sends must not share it.
/// </summary>
public sealed class RabbitConnection(IOptions<RabbitMcpOptions> options, TimeProvider time, ILogger<RabbitConnection> logger)
    : IAsyncDisposable
{
    private const ushort NotFound = 404;
    private const ushort ResourceLocked = 405;
    private const ushort NotImplemented = 540;
    private const int MaxNameBytes = 255;

    private enum Purpose { General, Browsing, Publishing }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ExistenceCache _exchanges = new(time, TimeSpan.FromSeconds(Math.Max(0, options.Value.ExchangeCacheSeconds)));
    private readonly IConnection?[] _connections = new IConnection?[Enum.GetValues<Purpose>().Length];
    private volatile string? _blockedReason;
    private volatile CancellationTokenSource _blocked = new();

    /// <summary>True when the broker closed a channel because an entity does not exist.</summary>
    public static bool IsNotFound(OperationInterruptedException ex) => ex.ShutdownReason?.ReplyCode == NotFound;

    /// <summary>True for 405 RESOURCE_LOCKED: the queue is exclusive to another connection.</summary>
    public static bool IsResourceLocked(OperationInterruptedException ex) => ex.ShutdownReason?.ReplyCode == ResourceLocked;

    /// <summary>
    /// True when basic.get on <paramref name="queue"/> was refused because it is a stream. The refusal closes the
    /// connection, so a call on another queue sees the same code; the reply text names the queue that caused it.
    /// </summary>
    public static bool IsStreamRefusal(OperationInterruptedException ex, string queue) =>
        ex.ShutdownReason is { ReplyCode: NotImplemented } reason && reason.ReplyText.Contains($"'{queue}'", StringComparison.Ordinal);

    /// <summary>Forget a cached "exists" result, e.g. after a publish failed with 404.</summary>
    public void InvalidateExchange(string exchange) => _exchanges.Invalidate(exchange);

    public Uri BrokerUri { get; } = options.Value.BrokerUri;

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(publisherConfirms ? Purpose.Publishing : Purpose.General, ct);
        return await connection.CreateChannelAsync(publisherConfirms ? Confirming : null, ct);
    }

    /// <summary>A channel for basic.get, which may close its whole connection (see the class summary).</summary>
    public async Task<IChannel> CreateBrowsingChannelAsync(CancellationToken ct)
    {
        var connection = await GetConnectionAsync(Purpose.Browsing, ct);
        return await connection.CreateChannelAsync(null, ct);
    }

    private static readonly CreateChannelOptions Confirming =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    /// <summary>
    /// A channel for publishing, or a refusal while the broker blocks publishers: a publish would only wait for the
    /// alarm to end, and then deliver a message whose sender gave up.
    /// </summary>
    /// <param name="limit">Waits at most this long, when that is less than RabbitMcp:PublishTimeoutSeconds.</param>
    public async Task<PublishChannel> CreatePublishChannelAsync(CancellationToken ct, TimeSpan? limit = null)
    {
        ThrowIfBlocked();
        var connection = await GetConnectionAsync(Purpose.Publishing, ct);
        using var deadline = PublishDeadline(ct, limit);
        try
        {
            return new PublishChannel(await connection.CreateChannelAsync(Confirming, deadline.Token), this);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ThrowIfBlocked();
            throw new McpException("RabbitMQ did not open a channel in time. Nothing was sent. Try again when the broker recovers.");
        }
    }

    internal void ThrowIfBlocked()
    {
        if (_blockedReason is { } reason)
            throw new McpException($"RabbitMQ is blocking publishers ({reason}): a memory or disk alarm is on. Nothing was sent. " +
                                   "Reading still works; try again when the alarm clears.");
    }

    /// <summary>Ends at the time limit, or as soon as the broker blocks publishers: only the alarm's end would release the wait.</summary>
    internal CancellationTokenSource PublishDeadline(CancellationToken ct, TimeSpan? limit)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _blocked.Token);
        var configured = TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds);
        deadline.CancelAfter(limit < configured ? limit.Value : configured);
        return deadline;
    }

    /// <summary>Why a publish got no confirm. The broker may have the message already, so it must not read as a failed send.</summary>
    internal string Unconfirmed() =>
        $"RabbitMQ did not confirm the message ({(_blockedReason is { } reason ? $"it is blocking publishers: {reason}" : "no answer in time")}). " +
        "It may still be delivered when the broker recovers, so check before sending it again.";

    /// <summary>
    /// Passive declare on a throwaway channel: the broker closes a channel when
    /// the entity is missing (404), so this must never share a working channel.
    /// Positive results are cached for RabbitMcp:ExchangeCacheSeconds unless <paramref name="useCache"/> is false.
    /// </summary>
    public async Task<bool> ExchangeExistsAsync(string exchange, CancellationToken ct, bool useCache = true)
    {
        if (useCache && _exchanges.IsKnownToExist(exchange))
            return true;
        if (TooLong(exchange))
            return false;

        await using var channel = await CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclarePassiveAsync(exchange, ct);
            _exchanges.MarkExists(exchange);
            return true;
        }
        catch (OperationInterruptedException ex) when (IsNotFound(ex))
        {
            _exchanges.Invalidate(exchange);
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="exchange"/> is a temporary reply endpoint as MassTransit creates them (fanout,
    /// durable:false, autoDelete:true — the bus / request-client endpoint). Re-declaring it with those flags is a no-op
    /// for such an exchange and fails with 406 for any other one, so an address taken from an incoming message cannot
    /// make this server publish into a regular application exchange. Not cached: the answer guards a send.
    /// </summary>
    public async Task<ReplyEndpointCheck> CheckTemporaryExchangeAsync(string exchange, CancellationToken ct)
    {
        await using (var probe = await CreateChannelAsync(publisherConfirms: false, ct))
        {
            try
            {
                await probe.ExchangeDeclarePassiveAsync(exchange, ct);
            }
            catch (OperationInterruptedException ex) when (IsNotFound(ex))
            {
                return ReplyEndpointCheck.Missing;
            }
        }

        await using var channel = await CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: false, autoDelete: true, cancellationToken: ct);
            return ReplyEndpointCheck.Temporary;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode is 406 or 403)
        {
            return ReplyEndpointCheck.NotTemporary; // different flags/type (406) or a reserved amq.* name (403)
        }
    }

    /// <summary>
    /// Whether the queue exists. A queue exclusive to another connection exists too: its passive declare fails with
    /// 405 RESOURCE_LOCKED, but messages can still be published to it.
    /// </summary>
    public async Task<bool> QueueExistsAsync(string queue, CancellationToken ct)
    {
        try
        {
            return await GetQueueAsync(queue, ct) is not null;
        }
        catch (OperationInterruptedException ex) when (IsResourceLocked(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Ready-message and consumer counts, or null if the queue does not exist. Throws 405 RESOURCE_LOCKED for a queue
    /// exclusive to another connection.
    /// </summary>
    public async Task<QueueDeclareOk?> GetQueueAsync(string queue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new McpException("Queue name is required.");
        if (TooLong(queue))
            return null;

        await using var channel = await CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            return await channel.QueueDeclarePassiveAsync(queue, ct);
        }
        catch (OperationInterruptedException ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    /// <summary>No such entity can exist, and the client library throws on the name instead of asking the broker.</summary>
    private static bool TooLong(string name) => Encoding.UTF8.GetByteCount(name) > MaxNameBytes;

    private async Task<IConnection> GetConnectionAsync(Purpose purpose, CancellationToken ct)
    {
        if (_connections[(int)purpose] is { IsOpen: true } open)
            return open;

        await _gate.WaitAsync(ct);
        try
        {
            var existing = _connections[(int)purpose];
            if (existing is { IsOpen: true })
                return existing;

            if (existing is not null)
                await existing.DisposeAsync();

            var factory = new ConnectionFactory
            {
                Uri = BrokerUri,
                ClientProvidedName = options.Value.ClientName + purpose switch
                {
                    Purpose.Publishing => "-publish",
                    Purpose.Browsing => "-browse",
                    _ => "",
                },
                AutomaticRecoveryEnabled = true,
            };

            IConnection connection;
            try
            {
                connection = await factory.CreateConnectionAsync(ct);
            }
            catch (BrokerUnreachableException ex)
            {
                // Never surface the URI itself: it contains credentials.
                logger.LogWarning(ex, "Cannot connect to RabbitMQ at {Broker}", BrokerAddress.Describe(BrokerUri));
                throw new McpException(
                    $"Cannot connect to RabbitMQ at {BrokerAddress.Describe(BrokerUri)}. Is the broker running and are the credentials correct?");
            }

            if (purpose == Purpose.Publishing)
            {
                await UnblockAsync();
                connection.ConnectionBlockedAsync += (_, e) => BlockAsync(e.Reason);
                connection.ConnectionUnblockedAsync += (_, _) => UnblockAsync();
                connection.RecoverySucceededAsync += (_, _) => UnblockAsync();
            }

            _connections[(int)purpose] = connection;
            logger.LogInformation("Connected to RabbitMQ at {Broker}", BrokerAddress.Describe(BrokerUri));
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BlockAsync(string reason)
    {
        logger.LogWarning("RabbitMQ is blocking publishers: {Reason}", AuditLog.OneLine(reason));
        _blockedReason = reason;
        await _blocked.CancelAsync();
    }

    private Task UnblockAsync()
    {
        if (_blockedReason is not null)
            logger.LogInformation("RabbitMQ stopped blocking publishers");
        _blockedReason = null;
        if (_blocked.IsCancellationRequested)
            _blocked = new CancellationTokenSource();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }
        _blocked.Dispose();
        _gate.Dispose();
    }
}

/// <summary>A channel with publisher confirms whose publishes always end, also when the broker never confirms.</summary>
public sealed class PublishChannel : IAsyncDisposable
{
    private readonly RabbitConnection _connection;
    private bool _stalled;

    internal PublishChannel(IChannel channel, RabbitConnection connection)
    {
        Channel = channel;
        _connection = connection;
    }

    public IChannel Channel { get; }

    /// <summary>Publishes with mandatory set and waits for the broker's confirm.</summary>
    /// <param name="limit">Waits at most this long, when that is less than RabbitMcp:PublishTimeoutSeconds.</param>
    /// <exception cref="McpException">The broker blocks publishers, or gave no confirm in time.</exception>
    public async Task PublishAsync(
        string exchange, string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken ct, TimeSpan? limit = null)
    {
        _connection.ThrowIfBlocked();
        using var deadline = _connection.PublishDeadline(ct, limit);
        try
        {
            await Channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _stalled = true;
            throw new McpException(_connection.Unconfirmed());
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A blocked broker answers channel.close only after the alarm, or the client gives up after 20 seconds: no call waits for that.
        if (_stalled)
            _ = Channel.DisposeAsync().AsTask();
        else
            await Channel.DisposeAsync();
    }
}
