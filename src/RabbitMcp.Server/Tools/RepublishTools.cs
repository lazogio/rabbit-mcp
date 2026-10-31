// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:EnableRepublish is true.</summary>
[McpServerToolType]
public sealed class RepublishTools(QueueBrowser browser, IOptions<RabbitMcpOptions> options, TimeProvider time)
{
    private const string CountHelp =
        "How many messages from the head of the queue to process. Default 1; with messageIds MaxMessagesPerCall, with a " +
        "filter MaxBulkMessages, so the selected messages are found anywhere in that range. Up to those same limits.";
    private const string TypeHelp = "Filter: only messages whose exception type contains this, e.g. TimeoutException.";
    private const string MessageHelp = "Filter: only messages whose exception message (or dead-lettering reason) contains this.";
    private const string ContractHelp = "Filter: only messages of this contract, e.g. OrderPlaced.";
    private const string SinceHelp = "Filter: only messages that failed after this: 30m, 2h, 7d or an ISO 8601 time.";
    private const string UntilHelp = "Filter: only messages that failed before this (same format).";

    [McpServerTool(Name = "republish_from_error", Title = "Republish faulted messages",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Moves messages from an error queue back to the queue they faulted on, for reprocessing: MassTransit " +
        "_error/_skipped/_dead queues (back to the endpoint's exchange) and RabbitMQ dead-letter queues (x-death header: " +
        "straight back to the queue that rejected the message). " +
        "Two steps: call without confirm to preview (nothing changes), then call with confirm=true, ideally passing the " +
        "messageIds from the preview so only those are moved. Each message is acknowledged in the error queue only after " +
        "the broker confirmed the republish; failures stay in the error queue. Fault, x-death and dead-letter parking headers " +
        "are removed, the body is unchanged. A queue with active consumers is refused unless force=true. For incidents, select " +
        "by a filter (exceptionType, exceptionContains, contract, since, until): " +
        "the preview then groups the matching messages by fault, and one confirmed call moves up to MaxBulkMessages.")]
    public async Task<RepublishResult> RepublishFromErrorAsync(
        [Description("Error queue name, e.g. submit-order_error.")] string errorQueue,
        [Description(CountHelp)] int? count = null,
        [Description("Set to true to execute. Without it, only a preview is returned.")] bool confirm = false,
        [Description("Only republish messages with these MessageIds (from the preview). Others are left in place.")] string[]? messageIds = null,
        [Description("Override the destination exchange. By default it is taken from MT-Fault-InputAddress.")] string? targetExchange = null,
        [Description("Override the destination with a queue, reached directly (default exchange). By default it is taken from x-death.")] string? targetQueue = null,
        [Description("Preview or move even if the error queue has active consumers.")] bool force = false,
        [Description(TypeHelp)] string? exceptionType = null,
        [Description(MessageHelp)] string? exceptionContains = null,
        [Description(ContractHelp)] string? contract = null,
        [Description(SinceHelp)] string? since = null,
        [Description(UntilHelp)] string? until = null,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
    {
        var selection = MessageFilter.From(exceptionType, exceptionContains, contract, since, until, time.GetUtcNow());
        if (!confirm)
        {
            var target = QueueBrowser.Override(targetExchange, targetQueue);
            if (selection is not null)
                return new RepublishResult(errorQueue, Executed: false, null, Outcomes: [], Republished: 0, Requeued: 0)
                {
                    Selection = await browser.SummarizeAsync(errorQueue, selection, options.Value.MaxBulkMessages, force, cancellationToken, messageIds),
                };
            var preview = await browser.PeekAsync(errorQueue, ScanDepth(count, messageIds, null), force, cancellationToken, target?.Display,
                QueueBrowser.HavingIds(messageIds));
            return new RepublishResult(errorQueue, Executed: false, preview, Outcomes: [], Republished: 0, Requeued: 0);
        }

        return await browser.RepublishAsync(errorQueue, ScanDepth(count, messageIds, selection), messageIds, targetExchange, cancellationToken,
            AuditLog.ClientOf(server, user), targetQueue, selection, force);
    }

    [McpServerTool(Name = "dead_letter_from_error", Title = "Park faulted messages in the dead-letter queue",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "For messages that will never succeed (poison messages): moves them from a MassTransit _error/_skipped queue to " +
        "the dead-letter queue (default <queue>_dead, created if missing), so the error queue only holds new faults. " +
        "Nothing is deleted: body and all headers stay, fault details included, and X-Dead-Lettered-By/-From/-At/" +
        "X-Dead-Letter-Reason record who moved it, from where, when and why. A TTL would expire a parked message, so it moves " +
        "to the X-Original-Expiration header. republish_from_error on the dead-letter queue brings messages back, with their " +
        "TTL. A queue that has consumers is never used as the dead-letter queue. " +
        "Two steps: call without confirm to preview, then confirm=true with the messageIds from the preview.")]
    public async Task<DeadLetterResult> DeadLetterFromErrorAsync(
        [Description("Error queue name, e.g. submit-order_error.")] string errorQueue,
        [Description(CountHelp)] int? count = null,
        [Description("Set to true to execute. Without it, only a preview is returned.")] bool confirm = false,
        [Description("Only move messages with these MessageIds (from the preview). Others are left in place.")] string[]? messageIds = null,
        [Description("Why they are parked, at most 1000 characters, e.g. 'customer deleted, cannot be fixed'. Stored as a header and in the audit.")] string? reason = null,
        [Description("Override the dead-letter queue. Default: RabbitMcp:DeadLetterQueue ({queue}_dead).")] string? deadLetterQueue = null,
        [Description(TypeHelp)] string? exceptionType = null,
        [Description(MessageHelp)] string? exceptionContains = null,
        [Description(ContractHelp)] string? contract = null,
        [Description(SinceHelp)] string? since = null,
        [Description(UntilHelp)] string? until = null,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
    {
        var selection = MessageFilter.From(exceptionType, exceptionContains, contract, since, until, time.GetUtcNow());
        if (!confirm)
        {
            QueueBrowser.CheckReason(reason);
            var target = deadLetterQueue ?? DeadLetterNaming.For(errorQueue, options.Value.DeadLetterQueue);
            await browser.CheckDeadLetterTargetAsync(errorQueue, target, cancellationToken);
            if (selection is not null)
                return new DeadLetterResult(errorQueue, target, Executed: false, null, Outcomes: [], Moved: 0, Requeued: 0)
                {
                    Selection = await browser.SummarizeAsync(errorQueue, selection, options.Value.MaxBulkMessages, force: false, cancellationToken, messageIds),
                };
            var preview = await browser.PeekAsync(errorQueue, ScanDepth(count, messageIds, null), force: false, cancellationToken,
                select: QueueBrowser.HavingIds(messageIds));
            return new DeadLetterResult(errorQueue, target, Executed: false, preview, Outcomes: [], Moved: 0, Requeued: 0);
        }

        return await browser.DeadLetterAsync(errorQueue, ScanDepth(count, messageIds, selection), messageIds, deadLetterQueue, reason, cancellationToken,
            AuditLog.ClientOf(server, user), selection);
    }

    /// <summary>A selection must not stop at the first message just because count was left out.</summary>
    private int ScanDepth(int? count, string[]? messageIds, MessageFilter? selection) =>
        count ?? (selection is not null ? options.Value.MaxBulkMessages
            : messageIds is { Length: > 0 } ? options.Value.MaxMessagesPerCall
            : 1);
}
