// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Security;

/// <summary>
/// What a tool needs: a permission, optionally for specific contracts (publish_*/request_*/reply_*), for the error
/// queue named in an argument (republish_from_error) or for the contract of the pending request it answers.
/// <see cref="OverrideArguments"/> redirect messages elsewhere (another exchange or queue): they need the permission
/// without patterns, otherwise a role limited to some error queues could move their messages into any queue.
/// </summary>
public sealed record ToolRule(
    Permission Permission,
    IReadOnlyList<ContractDescriptor> Contracts,
    string? QueueArgument = null,
    bool ByRequestId = false,
    IReadOnlyList<string>? OverrideArguments = null);

/// <summary>
/// Maps every tool to the permission it needs and decides, per caller, which tools are listed (tools/list) and
/// which calls are allowed (tools/call). A tool without a rule is denied whenever access control is on.
/// </summary>
public sealed class ToolAccess(AccessControl control, ContractRegistry contracts, IServiceProvider services)
{
    private static readonly Dictionary<string, ToolRule> Fixed = new(StringComparer.Ordinal)
    {
        ["list_contracts"] = new(Permission.Read, []),
        ["validate_message"] = new(Permission.Read, []),
        ["queue_stats"] = new(Permission.Read, []),
        ["peek_messages"] = new(Permission.Read, []),
        ["list_queues"] = new(Permission.Read, []),
        ["contract_consumers"] = new(Permission.Read, []),
        ["find_message"] = new(Permission.Read, []),
        ["recent_activity"] = new(Permission.Read, []),
        ["republish_from_error"] = new(Permission.Republish, [], QueueArgument: "errorQueue",
            OverrideArguments: ["targetExchange", "targetQueue"]),
        // Moving to the dead-letter queue is a move out of the error queue like republish (nothing is deleted).
        ["dead_letter_from_error"] = new(Permission.Republish, [], QueueArgument: "errorQueue",
            OverrideArguments: ["deadLetterQueue"]),
        // The contract's publish permission is checked inside the tool, once the message's contract is known.
        ["resend_corrected"] = new(Permission.Republish, [], QueueArgument: "errorQueue"),
        ["pending_requests"] = new(Permission.Respond, []),
        ["reject_request"] = new(Permission.Respond, [], ByRequestId: true),
    };

    public AccessControl Control => control;

    public ToolRule? RuleFor(string tool)
    {
        if (Fixed.TryGetValue(tool, out var rule))
            return rule;

        if (contracts.FindByName(tool) is { } contract && contract.ToolName == tool)
            return new ToolRule(contract.IsRequest ? Permission.Request : Permission.Publish, [contract]);

        if (services.GetService<ResponderContracts>() is { } responder &&
            responder.Responses.FirstOrDefault(r => ResponderContracts.ReplyToolName(r) == tool) is { } response)
        {
            return new ToolRule(Permission.Respond,
                [.. responder.Requests.Where(q => q.Responses.Any(x => x.Type == response.Type))], ByRequestId: true);
        }

        return null;
    }

    /// <summary>Listed in tools/list: the caller has the permission for at least one of the tool's subjects.</summary>
    public bool IsVisible(string tool, UserPermissions user)
    {
        if (user.IsUnrestricted)
            return true;
        return RuleFor(tool) is { } rule &&
               (rule.Contracts.Count == 0 ? user.Has(rule.Permission) : rule.Contracts.Any(c => user.Allows(rule.Permission, c)));
    }

    /// <summary>Null when the call is allowed; otherwise the reason (shown to the caller and audited).</summary>
    public AccessDenied? Check(string tool, IDictionary<string, JsonElement>? arguments, UserPermissions user)
    {
        if (user.IsUnrestricted)
            return null;

        var rule = RuleFor(tool);
        if (rule is null)
            return new AccessDenied($"'{tool}' is not covered by any permission.", null);

        AccessDenied Need(string subject, ContractDescriptor? contract) => new(
            $"Permission '{rule.Permission.ToString().ToLowerInvariant()}'{subject} is required for {tool}. " +
            $"Your roles: {(user.Roles.Count == 0 ? "none" : string.Join(", ", user.Roles))}.", contract);

        if (!IsVisible(tool, user))
            return Need("", rule.Contracts.Count == 1 ? rule.Contracts[0] : null);

        if (rule.QueueArgument is { } argument && Text(arguments, argument) is { } queue &&
            !user.AllowsQueue(rule.Permission, queue))
            return Need($" on queue '{queue}'", null);

        if (rule.OverrideArguments?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(Text(arguments, a))) is { } redirect &&
            !user.HasUnrestricted(rule.Permission))
            return Need($" without patterns (to choose {redirect})", null);

        if (rule.ByRequestId && Text(arguments, "requestId") is { } id && Guid.TryParse(id, out var requestId) &&
            services.GetService<ResponderService>()?.Pending().FirstOrDefault(r => r.RequestId == requestId) is { } pending &&
            !user.Allows(rule.Permission, pending.Contract))
            return Need($" for {pending.Contract.Name}", pending.Contract);

        return null;
    }

    private static string? Text(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record AccessDenied(string Reason, ContractDescriptor? Contract);
