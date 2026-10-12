using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

/// <summary>Non-destructive peeking and at-least-once republishing of queued messages.</summary>
public sealed class QueueBrowser(
    RabbitConnection connection,
    MessageInspector inspector,
    TimeProvider time,
    IOptions<RabbitMcpOptions> options,
    ILogger<QueueBrowser> logger)
{
    /// <summary>
    /// Fetches up to <paramref name="count"/> messages without acknowledging them, then
    /// returns all of them to the queue with one multiple-nack. Messages are never removed.
    /// Refuses queues with active consumers unless <paramref name="force"/> is set: while
    /// peeked messages are held they are invisible to those consumers, and they come back
    /// redelivered and possibly out of order.
    /// </summary>
    /// <param name="targetExchangeOverride">Shown as each message's RepublishTarget (republish preview).</param>
    public async Task<PeekResult> PeekAsync(
        string queue, int count, bool force, CancellationToken ct, string? targetExchangeOverride = null)
    {
        CheckCount(count);
        var info = await EnsureQueueExistsAsync(queue, ct);
        if (info.ConsumerCount > 0 && !force)
            throw new McpException(
                $"Queue '{queue}' has {info.ConsumerCount} active consumer(s). Peeking holds messages away from them and " +
                "returns them redelivered, possibly out of order. Nothing was read. Pass force=true to peek anyway.");

        var messages = new List<PeekedMessage>(count);
        ulong lastTag = 0;
        uint remaining = 0;

        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            while (messages.Count < count)
            {
                var delivery = await channel.BasicGetAsync(queue, autoAck: false, ct);
                if (delivery is null)
                    break;

                lastTag = delivery.DeliveryTag;
                remaining = delivery.MessageCount;
                var peeked = inspector.Inspect(messages.Count, queue, delivery);
                messages.Add(targetExchangeOverride is null ? peeked : peeked with { RepublishTarget = targetExchangeOverride });
            }
        }
        finally
        {
            // Even without this nack, closing the channel would requeue everything unacked.
            if (lastTag > 0 && channel.IsOpen)
                await channel.BasicNackAsync(lastTag, multiple: true, requeue: true, CancellationToken.None);
        }

        return new PeekResult(queue, messages.Count, remaining, messages);
    }

    /// <summary>
    /// For each message: get (no ack) → publish to its original queue's exchange with
    /// publisher confirms → ack only after the broker confirmed. Anything that is
    /// skipped or fails stays unacked and is requeued at the end, so no message is lost.
    /// A crash between confirm and ack can produce a duplicate (at-least-once).
    /// </summary>
    public async Task<RepublishResult> RepublishAsync(
        string errorQueue,
        int count,
        IReadOnlyCollection<string>? onlyMessageIds,
        string? targetExchangeOverride,
        CancellationToken ct)
    {
        CheckCount(count);
        await EnsureQueueExistsAsync(errorQueue, ct);

        var filter = onlyMessageIds is { Count: > 0 }
            ? new HashSet<string>(onlyMessageIds, StringComparer.OrdinalIgnoreCase)
            : null;
        var exchangeExists = new Dictionary<string, bool>(StringComparer.Ordinal);
        var outcomes = new List<RepublishOutcome>();
        var toRequeue = new List<ulong>();
        var republished = 0;
        var scanned = 0;
        var clientName = options.Value.ClientName;

        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
        try
        {
            while (scanned < count)
            {
                var delivery = await channel.BasicGetAsync(errorQueue, autoAck: false, ct);
                if (delivery is null)
                    break;
                scanned++;

                var props = delivery.BasicProperties;
                var messageId = props.MessageId
                    ?? MassTransitEnvelope.TryParse(delivery.Body.Span)?.MessageId;

                if (filter is not null && (messageId is null || !filter.Contains(messageId)))
                {
                    // Not selected: keep it unacked so BasicGet doesn't hand it out again.
                    toRequeue.Add(delivery.DeliveryTag);
                    continue;
                }

                var target = targetExchangeOverride
                    ?? RepublishTargetResolver.Resolve(errorQueue, props.Headers)?.Exchange;
                if (target is null)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "skipped", null,
                        "Cannot determine the original queue (no MT-Fault-InputAddress header and no _error/_skipped suffix). Pass targetExchange."));
                    continue;
                }

                if (!exchangeExists.TryGetValue(target, out var exists))
                    exchangeExists[target] = exists = await connection.ExchangeExistsAsync(target, ct);
                if (!exists)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "failed", target, $"Exchange '{target}' does not exist."));
                    continue;
                }

                var headers = FaultHeaders.WithoutFaultHeaders(props.Headers);
                headers[HeaderNames.RepublishedBy] = clientName;
                headers[HeaderNames.RepublishedFrom] = errorQueue;
                headers[HeaderNames.RepublishedAt] = time.GetUtcNow().ToString("O");

                var newProps = new BasicProperties(props)
                {
                    DeliveryMode = DeliveryModes.Persistent,
                    Headers = headers,
                };

                try
                {
                    // Original body bytes, never re-serialized or truncated.
                    await channel.BasicPublishAsync(target, routingKey: "", mandatory: true, newProps, delivery.Body, ct);
                }
                catch (PublishException ex)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "failed", target, ex.IsReturn
                        ? $"Not routed: no queue is bound to exchange '{target}'. Message kept in {errorQueue}."
                        : $"Broker rejected the publish. Message kept in {errorQueue}."));
                    continue;
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                republished++;
                outcomes.Add(new RepublishOutcome(messageId, "republished", target, null));
                logger.LogInformation("Republished {MessageId} from {ErrorQueue} to {Exchange}", messageId, errorQueue, target);
            }
        }
        finally
        {
            if (channel.IsOpen)
            {
                foreach (var tag in toRequeue)
                    await channel.BasicNackAsync(tag, multiple: false, requeue: true, CancellationToken.None);
            }
            // If the channel is already closed, the broker has requeued every unacked message.
        }

        if (filter is not null)
        {
            foreach (var missing in filter.Except(outcomes.Select(o => o.MessageId ?? ""), StringComparer.OrdinalIgnoreCase))
                outcomes.Add(new RepublishOutcome(missing, "skipped", null, $"Not found in the first {scanned} message(s) of {errorQueue}."));
        }

        return new RepublishResult(errorQueue, Executed: true, Preview: null, outcomes, republished, toRequeue.Count);
    }

    private async Task<QueueDeclareOk> EnsureQueueExistsAsync(string queue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queue))
            throw new McpException("Queue name is required.");
        return await connection.GetQueueAsync(queue, ct)
            ?? throw new McpException($"Queue '{queue}' does not exist.");
    }

    private void CheckCount(int count)
    {
        var max = options.Value.MaxMessagesPerCall;
        if (count < 1 || count > max)
            throw new McpException($"count must be between 1 and {max}.");
    }
}
