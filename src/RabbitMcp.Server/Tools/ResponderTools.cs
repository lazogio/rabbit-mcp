using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Responder mode (RabbitMcp:RespondTo): see and answer requests that other services send to this server.</summary>
[McpServerToolType]
public sealed class ResponderTools(ResponderService responder, TimeProvider time)
{
    [McpServerTool(Name = "pending_requests", Title = "Pending requests",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Requests other services sent to this server (responder mode) that are waiting for an answer, oldest first. " +
        "Answer each with one of its replyTools (reply_*) or with reject_request before expiresAt: after that the " +
        "requester has given up and the request disappears. The message is checked against its contract.")]
    public PendingRequestList PendingRequests(
        [Description("Only requests of this contract (name, e.g. GetOrderStatus).")] string? contract = null,
        [Description("Maximum requests to return, 1-100 (default 20).")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var now = time.GetUtcNow();
        var all = responder.Pending()
            .Where(r => contract is null || string.Equals(r.Contract.Name, contract, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var items = all.Take(limit).Select(r =>
        {
            var errors = r.Envelope.Message is { } json
                ? MessageInspector.Validate(r.Contract, json, MassTransitJson.Options)
                : new[] { "The request has no message." };
            return new PendingRequestInfo(
                r.RequestId.ToString(), r.Contract.Name, r.Contract.Version,
                [.. r.Contract.Responses.Select(ResponderContracts.ReplyToolName)],
                r.Envelope.Message,
                new ContractCheck(r.Contract.Name, r.Contract.Version, errors.Count == 0, errors),
                r.Envelope.CorrelationId, r.Envelope.SourceAddress, r.ReceivedAt, r.ExpiresAt,
                r.ExpiresAt is { } at ? Math.Max(0, (int)(at - now).TotalSeconds) : null);
        }).ToArray();

        return new PendingRequestList(items, all.Length,
            [.. responder.Contracts.Requests.Select(c => c.Name)], responder.QueueName);
    }

    [McpServerTool(Name = "reject_request", Title = "Reject a pending request",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Refuses a pending request: the requester receives a MassTransit Fault (as if its consumer had thrown) with " +
        "this reason, so its GetResponse fails at once instead of timing out. Use when no declared reply fits.")]
    public Task<ReplyResult> RejectRequestAsync(
        [Description("requestId from pending_requests.")] string requestId,
        [Description("Why the request is refused; the requester sees it as the exception message.")] string reason,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default) =>
        responder.RejectAsync(ParseId(requestId), reason, AuditLog.ClientOf(server, user), cancellationToken);

    internal static Guid ParseId(string requestId) =>
        Guid.TryParse(requestId, out var id)
            ? id
            : throw new McpException($"'{requestId}' is not a requestId (GUID). See pending_requests.");
}

/// <summary>reply_* tools: one per reply type of the answered request contracts.</summary>
public sealed class ReplyTools(ResponderService responder)
{
    public Task<ReplyResult> ReplyAsync<TResponse>(
        [Description("requestId from pending_requests.")] string requestId,
        [Description("The reply message. Must satisfy the reply contract and its validation rules.")] TResponse response,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
        where TResponse : class =>
        responder.ReplyAsync(ResponderTools.ParseId(requestId), response, AuditLog.ClientOf(server, user), cancellationToken);
}

public static class ReplyToolFactory
{
    private static readonly MethodInfo ReplyMethod = typeof(ReplyTools).GetMethod(nameof(ReplyTools.ReplyAsync))!;

    /// <exception cref="Hosting.StartupException">Two reply types map to the same tool name.</exception>
    public static IReadOnlyList<McpServerTool> CreateTools(ResponderContracts contracts)
    {
        var duplicate = contracts.Responses.GroupBy(ResponderContracts.ReplyToolName).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new RabbitMcp.Server.Hosting.StartupException(
                $"Reply types {string.Join(" and ", duplicate.Select(r => r.Type.FullName))} both map to tool '{duplicate.Key}'. " +
                "Narrow RabbitMcp:RespondTo or rename one of them.");

        return [.. contracts.Responses.Select(r => CreateTool(r, contracts))];
    }

    private static McpServerTool CreateTool(ResponseDescriptor response, ResponderContracts contracts)
    {
        var answers = contracts.Requests.Where(c => c.Responses.Any(x => x.Type == response.Type)).Select(c => c.Name);
        return McpServerTool.Create(
            ReplyMethod.MakeGenericMethod(response.Type),
            ctx => ctx.Services!.GetRequiredService<ReplyTools>(),
            new McpServerToolCreateOptions
            {
                Name = ResponderContracts.ReplyToolName(response),
                Title = $"Reply {response.Name}",
                Description =
                    $"Answers a pending request ({string.Join(", ", answers)}) with a {response.Name} " +
                    $"({response.MessageTypeUrn}). It is sent to the requester's reply address in MassTransit format and " +
                    "completes its GetResponse. The reply is validated first; an invalid reply is not sent and the request stays pending.",
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
                UseStructuredContent = true,
                SerializerOptions = ToolJson.Options,
            });
    }
}
