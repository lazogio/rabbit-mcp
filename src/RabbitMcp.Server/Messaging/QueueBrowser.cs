// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

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
    AuditLog audit,
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
                messages.Add(targetExchangeOverride is null ? peeked : peeked with { RepublishTarget = targetExchangeOverride, RepublishWarning = null });
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
        CancellationToken ct,
        string? client = null)
    {
        // Every per-message outcome is audited, including those reached before a failure.
        var outcomes = new List<RepublishOutcome>();
        void AuditOutcomes()
        {
            foreach (var o in outcomes)
                audit.Write(audit.Event(AuditActions.Republish, o.Status, client, exchange: o.TargetExchange,
                    queue: errorQueue, messageId: o.MessageId, detail: o.Detail));
        }

        try
        {
            var result = await RepublishCoreAsync(errorQueue, count, onlyMessageIds, targetExchangeOverride, outcomes, ct);
            AuditOutcomes();
            return result;
        }
        catch (Exception ex)
        {
            // Messages republished before the failure are audited too: they really were moved.
            AuditOutcomes();
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            var moved = outcomes.Count(o => o.Status == "republished");
            var detail = (error?.Message ?? "cancelled") +
                         $" {moved} message(s) were republished before that; the others remain in {errorQueue}.";
            audit.Write(audit.Event(AuditActions.Republish, "error", client, exchange: targetExchangeOverride,
                queue: errorQueue, detail: detail));
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw new McpException(detail, ex);
        }
    }

    private async Task<RepublishResult> RepublishCoreAsync(
        string errorQueue,
        int count,
        IReadOnlyCollection<string>? onlyMessageIds,
        string? targetExchangeOverride,
        List<RepublishOutcome> outcomes,
        CancellationToken ct)
    {
        CheckCount(count);
        await EnsureQueueExistsAsync(errorQueue, ct);

        var filter = onlyMessageIds is { Count: > 0 }
            ? new HashSet<string>(onlyMessageIds, StringComparer.OrdinalIgnoreCase)
            : null;
        var exchangeExists = new Dictionary<string, bool>(StringComparer.Ordinal);
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

                var resolved = targetExchangeOverride is null ? RepublishTargetResolver.Resolve(errorQueue, props.Headers) : null;
                if (resolved?.Conflict is { } conflict)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "skipped", resolved.Exchange, conflict));
                    continue;
                }

                var target = targetExchangeOverride ?? resolved?.Exchange;
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

                // A new span for the retry, linked (not parented) to the original send's trace: the consumer
                // processes it as part of this republish, and the original trace stays one click away.
                using var activity = Telemetry.StartSend("republish", target, linkTo: Telemetry.Extract(props.Headers));
                activity?.SetTag("messaging.message.id", messageId);
                activity?.SetTag("rabbitmcp.source_queue", errorQueue);
                Telemetry.Inject(activity, headers);

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
                catch (OperationInterruptedException ex) when (RabbitConnection.IsNotFound(ex))
                {
                    Telemetry.Fail(activity, "exchange deleted");
                    // The broker closed the channel: every message not yet acked goes back to the error queue.
                    connection.InvalidateExchange(target);
                    throw new McpException(
                        $"Exchange '{target}' was deleted during the republish. {republished} message(s) were republished " +
                        $"before that; all others remain in {errorQueue}.");
                }
                catch (PublishException ex)
                {
                    Telemetry.Fail(activity, ex.IsReturn ? "not routed" : "rejected");
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

    /// <summary>
    /// Moves messages that cannot be processed from an error queue to the dead-letter queue — never deletes them.
    /// Same safety as republish: get (no ack) → publish to the dead-letter queue with confirms + mandatory → ack only
    /// after the broker confirmed. Body and all headers are kept (the MT-Fault-* headers explain why it died) and
    /// X-Dead-Lettered-* headers record who moved it, from where, when and why. Unselected or failed messages stay.
    /// </summary>
    public async Task<DeadLetterResult> DeadLetterAsync(
        string errorQueue,
        int count,
        IReadOnlyCollection<string>? onlyMessageIds,
        string? deadLetterQueue,
        string? reason,
        CancellationToken ct,
        string? client = null)
    {
        var outcomes = new List<DeadLetterOutcome>();
        var target = deadLetterQueue ?? DeadLetterNaming.For(errorQueue, options.Value.DeadLetterQueue);
        void AuditOutcomes()
        {
            foreach (var o in outcomes)
                audit.Write(audit.Event(AuditActions.DeadLetter, o.Status, client, queue: errorQueue,
                    messageId: o.MessageId, detail: $"to {target}{(o.Detail is null ? "" : ": " + o.Detail)}"));
        }

        try
        {
            var result = await DeadLetterCoreAsync(errorQueue, count, onlyMessageIds, target, reason, outcomes, client, ct);
            AuditOutcomes();
            return result;
        }
        catch (Exception ex)
        {
            AuditOutcomes();
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            var moved = outcomes.Count(o => o.Status == "moved");
            var detail = (error?.Message ?? "cancelled") +
                         $" {moved} message(s) were moved to {target} before that; the others remain in {errorQueue}.";
            audit.Write(audit.Event(AuditActions.DeadLetter, "error", client, queue: errorQueue, detail: detail));
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw new McpException(detail, ex);
        }
    }

    private async Task<DeadLetterResult> DeadLetterCoreAsync(
        string errorQueue,
        int count,
        IReadOnlyCollection<string>? onlyMessageIds,
        string target,
        string? reason,
        List<DeadLetterOutcome> outcomes,
        string? client,
        CancellationToken ct)
    {
        CheckCount(count);
        var source = await EnsureQueueExistsAsync(errorQueue, ct);
        DeadLetterNaming.Validate(target);
        if (string.Equals(target, errorQueue, StringComparison.Ordinal))
            throw new McpException($"'{errorQueue}' is already the dead-letter queue; there is nowhere to move its messages.");
        if (source.ConsumerCount > 0)
            throw new McpException(
                $"'{errorQueue}' has {source.ConsumerCount} active consumer(s), so it is not a parked error queue. Nothing was moved.");

        // Created on first use like MassTransit creates _error queues: durable, so parked messages survive restarts.
        if (await connection.GetQueueAsync(target, ct) is null)
        {
            await using var declare = await connection.CreateChannelAsync(publisherConfirms: false, ct);
            await declare.QueueDeclareAsync(target, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            logger.LogInformation("Created dead-letter queue {Queue}", target);
        }

        var filter = onlyMessageIds is { Count: > 0 }
            ? new HashSet<string>(onlyMessageIds, StringComparer.OrdinalIgnoreCase)
            : null;
        var toRequeue = new List<ulong>();
        var moved = 0;
        var scanned = 0;

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
                var messageId = props.MessageId ?? MassTransitEnvelope.TryParse(delivery.Body.Span)?.MessageId;
                if (filter is not null && (messageId is null || !filter.Contains(messageId)))
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    continue;
                }

                // Every original header stays, fault details included; ours are added.
                var headers = props.Headers is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(props.Headers, StringComparer.Ordinal);
                headers[HeaderNames.DeadLetteredBy] = client ?? options.Value.ClientName;
                headers[HeaderNames.DeadLetteredFrom] = errorQueue;
                headers[HeaderNames.DeadLetteredAt] = time.GetUtcNow().ToString("O");
                if (!string.IsNullOrWhiteSpace(reason))
                    headers[HeaderNames.DeadLetterReason] = reason.Trim();

                var newProps = new BasicProperties(props) { DeliveryMode = DeliveryModes.Persistent, Headers = headers };
                try
                {
                    // Default exchange, routing key = queue name: straight into the dead-letter queue. Body bytes unchanged.
                    await channel.BasicPublishAsync("", target, mandatory: true, newProps, delivery.Body, ct);
                }
                catch (PublishException ex)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new DeadLetterOutcome(messageId, "failed",
                        ex.IsReturn ? $"'{target}' did not accept the message. It stays in {errorQueue}."
                                    : $"Broker rejected the publish. Message kept in {errorQueue}."));
                    continue;
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                moved++;
                outcomes.Add(new DeadLetterOutcome(messageId, "moved", null));
                logger.LogInformation("Dead-lettered {MessageId} from {ErrorQueue} to {Queue}", messageId, errorQueue, target);
            }
        }
        finally
        {
            if (channel.IsOpen)
            {
                foreach (var tag in toRequeue)
                    await channel.BasicNackAsync(tag, multiple: false, requeue: true, CancellationToken.None);
            }
        }

        if (filter is not null)
        {
            foreach (var missing in filter.Except(outcomes.Select(o => o.MessageId ?? ""), StringComparer.OrdinalIgnoreCase))
                outcomes.Add(new DeadLetterOutcome(missing, "skipped", $"Not found in the first {scanned} message(s) of {errorQueue}."));
        }

        return new DeadLetterResult(errorQueue, target, Executed: true, Preview: null, outcomes, moved, toRequeue.Count);
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
