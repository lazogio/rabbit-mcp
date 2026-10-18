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
    /// <param name="targetExchangeOverride">Shown as each message's RepublishTarget (republish preview): an exchange or "queue:name".</param>
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

    /// <summary>The explicit destination of a republish: <paramref name="targetQueue"/> wins over <paramref name="targetExchange"/>.</summary>
    public static RepublishTarget? Override(string? targetExchange, string? targetQueue)
    {
        if (!string.IsNullOrWhiteSpace(targetQueue))
        {
            if (!string.IsNullOrWhiteSpace(targetExchange))
                throw new McpException("Pass targetExchange or targetQueue, not both.");
            DeadLetterNaming.Validate(targetQueue.Trim());
            return RepublishTarget.ToQueue(targetQueue.Trim(), "targetQueue");
        }
        return string.IsNullOrWhiteSpace(targetExchange) ? null : new RepublishTarget(targetExchange.Trim(), "targetExchange");
    }

    /// <summary>
    /// For each message: get (no ack) → publish to its original queue (its exchange, or the queue itself through the
    /// default exchange) with publisher confirms → ack only after the broker confirmed. Anything that is
    /// skipped or fails stays unacked and is requeued at the end, so no message is lost.
    /// A crash between confirm and ack can produce a duplicate (at-least-once).
    /// </summary>
    public async Task<RepublishResult> RepublishAsync(
        string errorQueue,
        int count,
        IReadOnlyCollection<string>? onlyMessageIds,
        string? targetExchangeOverride,
        CancellationToken ct,
        string? client = null,
        string? targetQueueOverride = null)
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
            var target = Override(targetExchangeOverride, targetQueueOverride);
            var result = await RepublishCoreAsync(errorQueue, count, onlyMessageIds, target, outcomes, ct);
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
            audit.Write(audit.Event(AuditActions.Republish, "error", client,
                exchange: targetQueueOverride is { Length: > 0 } q ? RepublishTarget.QueuePrefix + q : targetExchangeOverride,
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
        RepublishTarget? targetOverride,
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

                var resolved = targetOverride is null ? RepublishTargetResolver.Resolve(errorQueue, props.Headers) : null;
                if (resolved?.Conflict is { } conflict)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "skipped", resolved.Display, conflict));
                    continue;
                }

                var destination = targetOverride ?? resolved;
                if (destination is null)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "skipped", null,
                        "Cannot determine the original queue (no MT-Fault-InputAddress or x-death header and no " +
                        "_error/_skipped/_dead suffix). Pass targetExchange or targetQueue."));
                    continue;
                }

                var target = destination.Display;
                // Back into the error queue itself (its own queue or, for MassTransit, the exchange of the same name):
                // the message would only lose its fault headers.
                if (string.Equals(destination.IsQueue ? destination.RoutingKey : destination.Exchange, errorQueue, StringComparison.Ordinal))
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "skipped", target,
                        $"The destination is {errorQueue} itself: the message would only lose its fault details. Pass targetQueue or targetExchange."));
                    continue;
                }

                if (!exchangeExists.TryGetValue(target, out var exists))
                    exchangeExists[target] = exists = destination.IsQueue
                        ? await connection.QueueExistsAsync(destination.RoutingKey, ct)
                        : await connection.ExchangeExistsAsync(destination.Exchange, ct);
                if (!exists)
                {
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "failed", target, destination.IsQueue
                        ? $"Queue '{destination.RoutingKey}' does not exist."
                        : $"Exchange '{destination.Exchange}' does not exist."));
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
                    await channel.BasicPublishAsync(destination.Exchange, destination.RoutingKey, mandatory: true, newProps,
                        delivery.Body, ct);
                }
                catch (OperationInterruptedException ex) when (RabbitConnection.IsNotFound(ex))
                {
                    Telemetry.Fail(activity, "exchange deleted");
                    // The broker closed the channel: every message not yet acked goes back to the error queue.
                    connection.InvalidateExchange(destination.Exchange);
                    throw new McpException(
                        $"Exchange '{destination.Exchange}' was deleted during the republish. {republished} message(s) were " +
                        $"republished before that; all others remain in {errorQueue}.");
                }
                catch (PublishException ex)
                {
                    Telemetry.Fail(activity, ex.IsReturn ? "not routed" : "rejected");
                    toRequeue.Add(delivery.DeliveryTag);
                    outcomes.Add(new RepublishOutcome(messageId, "failed", target, ex.IsReturn
                        ? destination.IsQueue
                            ? $"Not routed: queue '{destination.RoutingKey}' no longer exists. Message kept in {errorQueue}."
                            : $"Not routed: no queue is bound to exchange '{destination.Exchange}'. Message kept in {errorQueue}."
                        : $"Broker rejected the publish. Message kept in {errorQueue}."));
                    continue;
                }

                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                republished++;
                outcomes.Add(new RepublishOutcome(messageId, "republished", target, null));
                // messageId and target come from the message (properties, fault/x-death headers): one line each.
                logger.LogInformation("Republished {MessageId} from {ErrorQueue} to {Target}",
                    AuditLog.OneLine(messageId), errorQueue, AuditLog.OneLine(target));
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
        if (source.ConsumerCount > 0)
            throw new McpException(
                $"'{errorQueue}' has {source.ConsumerCount} active consumer(s), so it is not a parked error queue. Nothing was moved.");

        // Created on first use like MassTransit creates _error queues: durable, so parked messages survive restarts.
        if (await CheckDeadLetterTargetAsync(errorQueue, target, ct) is null)
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
                logger.LogInformation("Dead-lettered {MessageId} from {ErrorQueue} to {Queue}", AuditLog.OneLine(messageId), errorQueue, target);
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

    /// <summary>
    /// Fix and resend one message whose data made it fail. The correction is validated against the message's contract;
    /// without <paramref name="execute"/> only the field-by-field changes are returned. Executing: get (no ack) → publish
    /// the corrected copy (new messageId, X-Corrected-From) to the original queue with confirms → move the original,
    /// unchanged, to the dead-letter queue (X-Corrected-To/-By/-At) with confirms → ack. Nothing is deleted: if parking
    /// the original fails, it stays in the error queue and the result says so.
    /// </summary>
    /// <param name="authorize">Null when the caller may send this contract, otherwise why not.</param>
    public async Task<CorrectionResult> ResendCorrectedAsync(
        string errorQueue, string messageId, System.Text.Json.JsonElement corrected, bool execute,
        Func<Contracts.ContractDescriptor, string?> authorize, CancellationToken ct, string? client = null)
    {
        try
        {
            var result = await ResendCorrectedCoreAsync(errorQueue, messageId, corrected, execute, authorize, client, ct);
            if (result.Executed)
            {
                audit.Write(audit.Event(AuditActions.Correct, result.OriginalParked ? "sent" : "sent-original-kept", client,
                    result.Contract, exchange: result.Target, queue: errorQueue, messageId: result.NewMessageId,
                    detail: $"correction of {result.OriginalMessageId}; changed {string.Join(", ", result.Changes.Select(c => c.Path))}; " +
                            (result.OriginalParked ? $"original parked in {result.DeadLetterQueue}" : "original kept in the error queue"),
                    message: corrected, messageType: typeof(System.Text.Json.JsonElement)));
            }
            return result;
        }
        catch (Exception ex) when (execute)
        {
            var error = ex is OperationCanceledException ? null : BrokerErrors.ToMcp(ex);
            audit.Write(audit.Event(AuditActions.Correct, "error", client, queue: errorQueue, messageId: messageId,
                detail: error?.Message ?? "cancelled"));
            if (error is null || ReferenceEquals(error, ex))
                throw;
            throw error;
        }
    }

    private async Task<CorrectionResult> ResendCorrectedCoreAsync(
        string errorQueue, string messageId, System.Text.Json.JsonElement corrected, bool execute,
        Func<Contracts.ContractDescriptor, string?> authorize, string? client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new McpException("messageId is required (see peek_messages).");
        var source = await EnsureQueueExistsAsync(errorQueue, ct);
        if (source.ConsumerCount > 0)
            throw new McpException(
                $"'{errorQueue}' has {source.ConsumerCount} active consumer(s), so it is not a parked error queue. Nothing was changed.");

        var deadLetterQueue = DeadLetterNaming.For(errorQueue, options.Value.DeadLetterQueue);
        var toRequeue = new List<ulong>();
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
        try
        {
            BasicGetResult? found = null;
            for (var scanned = 0; scanned < options.Value.MaxMessagesPerCall && found is null; scanned++)
            {
                var delivery = await channel.BasicGetAsync(errorQueue, autoAck: false, ct);
                if (delivery is null)
                    break;
                var id = delivery.BasicProperties.MessageId ?? MassTransitEnvelope.TryParse(delivery.Body.Span)?.MessageId;
                if (string.Equals(id, messageId, StringComparison.OrdinalIgnoreCase))
                    found = delivery;
                else
                    toRequeue.Add(delivery.DeliveryTag);
            }
            if (found is null)
                throw new McpException(
                    $"Message {messageId} is not among the first {options.Value.MaxMessagesPerCall} messages of {errorQueue}. Nothing was changed.");
            toRequeue.Add(found.DeliveryTag); // until everything is confirmed

            var props = found.BasicProperties;
            var envelope = MassTransitEnvelope.TryParse(found.Body.Span);
            var contract = inspector.ContractOf(props, envelope)
                ?? throw new McpException("No registered contract matches this message, so the correction cannot be validated. Nothing was changed.");
            if (contract.IsRequest)
                throw new McpException($"{contract.Name} is a request: its requester stopped waiting long ago, so a corrected copy would get no one an answer.");
            if (authorize(contract) is { } denied)
                throw new McpException(denied);

            var errors = MessageInspector.Validate(contract, corrected, MassTransitJson.Options);
            if (errors.Count > 0)
                throw new McpException($"The correction violates {contract.Name} v{contract.Version}; nothing was changed. " + string.Join("; ", errors));

            System.Text.Json.JsonElement original;
            if (envelope?.Message is { } message)
                original = message;
            else
            {
                try { original = System.Text.Json.JsonDocument.Parse(found.Body).RootElement.Clone(); }
                catch (System.Text.Json.JsonException) { throw new McpException("The original message is not JSON; it cannot be corrected here."); }
            }
            var changes = JsonDiff.Compare(original, corrected);
            if (changes.Count == 0)
                throw new McpException("The correction is identical to the original. Use republish_from_error to retry it unchanged.");

            var target = RepublishTargetResolver.Resolve(errorQueue, props.Headers)
                ?? throw new McpException("Cannot determine the original queue (no MT-Fault-InputAddress or x-death header, no _error/_skipped/_dead suffix).");
            if (target.Conflict is { } conflict)
                throw new McpException(conflict + " Nothing was changed.");
            if (string.Equals(target.IsQueue ? target.RoutingKey : target.Exchange, errorQueue, StringComparison.Ordinal))
                throw new McpException($"The destination is {errorQueue} itself. Nothing was changed.");
            // Correcting a message that is already parked (an _dead queue): the original goes back to the same queue,
            // annotated, so it is not corrected twice by accident.
            var parking = string.Equals(deadLetterQueue, errorQueue, StringComparison.Ordinal)
                ? source
                : await CheckDeadLetterTargetAsync(errorQueue, deadLetterQueue, ct);

            if (!execute)
                return new CorrectionResult(errorQueue, messageId, contract.Name, false, changes, target.Display, deadLetterQueue,
                    null, false, "Preview: call again with confirm=true to send the corrected copy and park the original.");

            if (!(target.IsQueue ? await connection.QueueExistsAsync(target.RoutingKey, ct) : await connection.ExchangeExistsAsync(target.Exchange, ct)))
                throw new McpException($"{target.Display} does not exist. Nothing was changed.");

            // 1. The corrected copy: a new message of the same conversation; its body is the corrected message.
            var now = time.GetUtcNow().ToString("O");
            var newId = Guid.NewGuid().ToString();
            var headers = FaultHeaders.WithoutFaultHeaders(props.Headers);
            headers[HeaderNames.CorrectedFrom] = messageId;
            headers[HeaderNames.CorrectedBy] = client ?? options.Value.ClientName;
            headers[HeaderNames.CorrectedAt] = now;
            using var activity = Telemetry.StartSend("correct", target.Display, linkTo: Telemetry.Extract(props.Headers));
            Telemetry.Inject(activity, headers);
            var copyProps = new BasicProperties(props) { MessageId = newId, DeliveryMode = DeliveryModes.Persistent, Headers = headers };
            var copyBody = envelope is not null
                ? CorrectedEnvelope(found.Body.Span, newId, corrected)
                : System.Text.Encoding.UTF8.GetBytes(corrected.GetRawText());
            try
            {
                await channel.BasicPublishAsync(target.Exchange, target.RoutingKey, mandatory: true, copyProps, copyBody, ct);
            }
            catch (PublishException ex)
            {
                Telemetry.Fail(activity, ex.IsReturn ? "not routed" : "rejected");
                throw new McpException(ex.IsReturn
                    ? $"Not routed: nothing is bound behind {target.Display}. Nothing was changed."
                    : "The broker rejected the corrected message. Nothing was changed.");
            }

            // 2. The original, unchanged, into the dead-letter queue — linked to its correction.
            var parked = false;
            string? note = null;
            try
            {
                if (parking is null)
                {
                    await using var declare = await connection.CreateChannelAsync(publisherConfirms: false, ct);
                    await declare.QueueDeclareAsync(deadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
                }
                var originalHeaders = props.Headers is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(props.Headers, StringComparer.Ordinal);
                originalHeaders[HeaderNames.CorrectedTo] = newId;
                originalHeaders[HeaderNames.CorrectedBy] = client ?? options.Value.ClientName;
                originalHeaders[HeaderNames.CorrectedAt] = now;
                originalHeaders[HeaderNames.DeadLetteredBy] = client ?? options.Value.ClientName;
                originalHeaders[HeaderNames.DeadLetteredFrom] = errorQueue;
                originalHeaders[HeaderNames.DeadLetteredAt] = now;
                originalHeaders[HeaderNames.DeadLetterReason] = $"corrected and resent as {newId}";
                await channel.BasicPublishAsync("", deadLetterQueue, mandatory: true,
                    new BasicProperties(props) { DeliveryMode = DeliveryModes.Persistent, Headers = originalHeaders }, found.Body, ct);
                await channel.BasicAckAsync(found.DeliveryTag, multiple: false, ct);
                toRequeue.Remove(found.DeliveryTag);
                parked = true;
            }
            catch (Exception ex) when (ex is PublishException or OperationInterruptedException or McpException)
            {
                note = $"The corrected copy {newId} was sent, but the original could not be parked ({BrokerErrors.ToMcp(ex).Message}); " +
                       $"it stays in {errorQueue}. Do not resend it.";
            }

            logger.LogInformation("Corrected {MessageId} from {ErrorQueue} as {NewId} to {Target}",
                AuditLog.OneLine(messageId), errorQueue, newId, AuditLog.OneLine(target.Display));
            return new CorrectionResult(errorQueue, messageId, contract.Name, true, changes, target.Display, deadLetterQueue,
                newId, parked, note);
        }
        finally
        {
            if (channel.IsOpen)
            {
                foreach (var tag in toRequeue)
                    await channel.BasicNackAsync(tag, multiple: false, requeue: true, CancellationToken.None);
            }
        }
    }

    /// <summary>The original envelope with a new messageId and the corrected message; every other field kept.</summary>
    public static byte[] CorrectedEnvelope(ReadOnlySpan<byte> original, string newMessageId, System.Text.Json.JsonElement corrected)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(original) as System.Text.Json.Nodes.JsonObject
                   ?? throw new McpException("The original envelope is not a JSON object.");
        string Key(string name) => node.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        node[Key("messageId")] = newMessageId;
        node[Key("message")] = System.Text.Json.Nodes.JsonNode.Parse(corrected.GetRawText());
        return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    /// <summary>
    /// The dead-letter queue must be a parking place: a valid name, not the error queue itself, and nothing consuming
    /// it — otherwise "parking" would hand the messages to a live consumer. Returns the queue, or null if it does not
    /// exist yet (it is created on first use).
    /// </summary>
    public async Task<QueueDeclareOk?> CheckDeadLetterTargetAsync(string errorQueue, string target, CancellationToken ct)
    {
        DeadLetterNaming.Validate(target);
        if (string.Equals(target, errorQueue, StringComparison.Ordinal))
            throw new McpException($"'{errorQueue}' is already the dead-letter queue; there is nowhere to move its messages.");

        var existing = await connection.GetQueueAsync(target, ct);
        if (existing is { ConsumerCount: > 0 })
            throw new McpException(
                $"'{target}' has {existing.ConsumerCount} active consumer(s): it is a live queue, not a dead-letter queue. " +
                "Nothing was moved.");
        return existing;
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
