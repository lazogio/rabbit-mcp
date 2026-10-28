// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:AuditLogPath is set.</summary>
[McpServerToolType]
public sealed class AuditTools(AuditLog audit, TimeProvider time)
{
    [McpServerTool(Name = "recent_activity", Title = "Recent activity (audit log)",
        ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "What was done through this server, newest first: publishes, requests, republishes, dead-lettering, responder " +
        "replies and refused calls, with outcome, ids, contract, queue and who did it. Answers questions like " +
        "'who republished from order-placed_error today?' or 'what happened to message X?'.")]
    public RecentActivity GetRecentActivity(
        [Description("publish, request, republish, deadletter, correct, declare, receive, respond, confirm or access.")] string? action = null,
        [Description("e.g. sent, error, republished, moved, denied, refused.")] string? outcome = null,
        [Description("Contract name, e.g. OrderPlaced.")] string? contract = null,
        [Description("Part of the client: user (API key name, token user) or MCP client name.")] string? client = null,
        [Description("Part of the queue name, e.g. order-placed_error.")] string? queue = null,
        [Description("A messageId, requestId or correlationId.")] string? id = null,
        [Description("Only events after this: 30m, 2h, 7d or an ISO 8601 time.")] string? since = null,
        [Description("Maximum events, 1-500 (default 50).")] int limit = 50,
        [Description("Include message bodies (only recorded with AuditIncludePayload).")] bool includePayload = false)
    {
        var path = audit.FilePath ?? throw new McpException("No audit file is configured (RabbitMcp:AuditLogPath).");
        return AuditReader.Read(path, new AuditQuery(action, outcome, contract, client, queue, id,
            AuditReader.ParseSince(since, time.GetUtcNow()), limit, includePayload));
    }
}
