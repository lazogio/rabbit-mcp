// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered when both EnableRepublish and EnablePublish are on: a correction sends new content.</summary>
[McpServerToolType]
public sealed class CorrectionTools(QueueBrowser browser, PublishPolicy policy, AccessControl access)
{
    [McpServerTool(Name = "resend_corrected", Title = "Fix a failed message and send it again",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "For a message that failed because of its data (wrong e-mail, missing or invalid field): sends a corrected copy " +
        "to the queue it failed on and parks the original, unchanged, in the dead-letter queue (default <queue>_dead) with " +
        "a link to the correction. Nothing is deleted. The correction must satisfy the message's contract. Two steps: call " +
        "without confirm to see the field-by-field changes (nothing changes), then with confirm=true. Take the original " +
        "message from peek_messages, change only what is wrong.")]
    public Task<CorrectionResult> ResendCorrectedAsync(
        [Description("Error queue the message is in, e.g. order-placed_error (or a _dead queue).")] string errorQueue,
        [Description("MessageId of the failed message (from peek_messages).")] string messageId,
        [Description("The whole corrected message (the payload, not an envelope).")] JsonElement corrected,
        [Description("Set to true to send the correction. Without it, only the changes are shown.")] bool confirm = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
    {
        var permissions = access.For(user);
        string? Authorize(ContractDescriptor contract) =>
            !policy.IsAllowed(contract)
                ? $"Sending {contract.Name} is not allowed by this server's configuration (RabbitMcp:PublishAllow/PublishDeny). Nothing was changed."
                : !permissions.Allows(Permission.Publish, contract)
                    ? $"Permission 'publish' for {contract.Name} is required to send a corrected copy. Nothing was changed."
                    : null;

        return browser.ResendCorrectedAsync(errorQueue, messageId, corrected, confirm, Authorize, cancellationToken,
            AuditLog.ClientOf(server, user));
    }
}
