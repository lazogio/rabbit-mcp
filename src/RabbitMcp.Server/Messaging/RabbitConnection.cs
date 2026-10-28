// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

public enum ReplyEndpointCheck { Temporary, Missing, NotTemporary }

/// <summary>
/// One lazily opened, auto-recovering AMQP connection shared by all tools.
/// Channels are short-lived: one per tool call.
/// </summary>
public sealed class RabbitConnection(IOptions<RabbitMcpOptions> options, TimeProvider time, ILogger<RabbitConnection> logger)
    : IAsyncDisposable
{
    private const ushort NotFound = 404;
    private const ushort ResourceLocked = 405;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ExistenceCache _exchanges = new(time, TimeSpan.FromSeconds(Math.Max(0, options.Value.ExchangeCacheSeconds)));
    private IConnection? _connection;

    /// <summary>True when the broker closed a channel because an entity does not exist.</summary>
    public static bool IsNotFound(OperationInterruptedException ex) => ex.ShutdownReason?.ReplyCode == NotFound;

    /// <summary>True for 405 RESOURCE_LOCKED: the queue is exclusive to another connection.</summary>
    public static bool IsResourceLocked(OperationInterruptedException ex) => ex.ShutdownReason?.ReplyCode == ResourceLocked;

    /// <summary>Forget a cached "exists" result, e.g. after a publish failed with 404.</summary>
    public void InvalidateExchange(string exchange) => _exchanges.Invalidate(exchange);

    public Uri BrokerUri { get; } = options.Value.BrokerUri;

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        var channelOptions = publisherConfirms
            ? new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)
            : null;
        return await connection.CreateChannelAsync(channelOptions, ct);
    }

    /// <summary>
    /// Passive declare on a throwaway channel: the broker closes a channel when
    /// the entity is missing (404), so this must never share a working channel.
    /// Positive results are cached for RabbitMcp:ExchangeCacheSeconds unless <paramref name="useCache"/> is false.
    /// </summary>
    public async Task<bool> ExchangeExistsAsync(string exchange, CancellationToken ct, bool useCache = true)
    {
        if (useCache && _exchanges.IsKnownToExist(exchange))
            return true;

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

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open)
            return open;

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } opened)
                return opened;

            if (_connection is not null)
                await _connection.DisposeAsync();

            var factory = new ConnectionFactory
            {
                Uri = BrokerUri,
                ClientProvidedName = options.Value.ClientName,
                AutomaticRecoveryEnabled = true,
            };

            try
            {
                _connection = await factory.CreateConnectionAsync(ct);
            }
            catch (BrokerUnreachableException ex)
            {
                // Never surface the URI itself: it contains credentials.
                logger.LogWarning(ex, "Cannot connect to RabbitMQ at {Broker}", BrokerAddress.Describe(BrokerUri));
                throw new McpException(
                    $"Cannot connect to RabbitMQ at {BrokerAddress.Describe(BrokerUri)}. Is the broker running and are the credentials correct?");
            }

            logger.LogInformation("Connected to RabbitMQ at {Broker}", BrokerAddress.Describe(BrokerUri));
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
