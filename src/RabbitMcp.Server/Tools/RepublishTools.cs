using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:EnableRepublish is true.</summary>
[McpServerToolType]
public sealed class RepublishTools(QueueBrowser browser)
{
    [McpServerTool(Name = "republish_from_error", Title = "Republish faulted messages",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Moves messages from a MassTransit _error/_skipped queue back to the queue they faulted on, for reprocessing. " +
        "Two steps: call without confirm to preview (nothing changes), then call with confirm=true, ideally passing the " +
        "messageIds from the preview so only those are moved. Each message is acknowledged in the error queue only after " +
        "the broker confirmed the republish; failures stay in the error queue. Fault headers are removed, the body is unchanged.")]
    public async Task<RepublishResult> RepublishFromErrorAsync(
        [Description("Error queue name, e.g. submit-order_error.")] string errorQueue,
        [Description("How many messages from the head of the queue to process (default 1).")] int count = 1,
        [Description("Set to true to execute. Without it, only a preview is returned.")] bool confirm = false,
        [Description("Only republish messages with these MessageIds (from the preview). Others are left in place.")] string[]? messageIds = null,
        [Description("Override the destination exchange. By default it is taken from MT-Fault-InputAddress.")] string? targetExchange = null,
        [Description("Preview even if the error queue has active consumers.")] bool force = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            var preview = await browser.PeekAsync(errorQueue, count, force, cancellationToken, targetExchange);
            return new RepublishResult(errorQueue, Executed: false, preview, Outcomes: [], Republished: 0, Requeued: 0);
        }

        return await browser.RepublishAsync(errorQueue, count, messageIds, targetExchange, cancellationToken,
            AuditLog.ClientOf(server, user));
    }
}
