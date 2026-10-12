using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// One lazily opened, auto-recovering AMQP connection shared by all tools.
/// Channels are short-lived: one per tool call.
/// </summary>
public sealed class RabbitConnection(IOptions<RabbitMcpOptions> options, ILogger<RabbitConnection> logger)
    : IAsyncDisposable
{
    private const ushort NotFound = 404;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

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
    /// </summary>
    public async Task<bool> ExchangeExistsAsync(string exchange, CancellationToken ct)
    {
        await using var channel = await CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclarePassiveAsync(exchange, ct);
            return true;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == NotFound)
        {
            return false;
        }
    }

    /// <summary>Ready-message and consumer counts, or null if the queue does not exist.</summary>
    public async Task<QueueDeclareOk?> GetQueueAsync(string queue, CancellationToken ct)
    {
        await using var channel = await CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            return await channel.QueueDeclarePassiveAsync(queue, ct);
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == NotFound)
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
