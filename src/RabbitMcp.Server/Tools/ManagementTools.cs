// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:ManagementUri is configured.</summary>
[McpServerToolType]
public sealed class ManagementTools(ManagementClient management, TopologyInspector topology, ContractRegistry contracts)
{
    public const int MaxLimit = 500;

    [McpServerTool(Name = "list_queues", Title = "List queues",
        ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Lists the queues of the configured vhost with ready/unacked message counts, consumers and type, sorted by name. " +
        "MassTransit _error/_skipped queues and _dead (dead-letter) queues are flagged with the queue they belong to. Use it to find queue names for " +
        "queue_stats, peek_messages and republish_from_error. Counts come from management statistics and can lag a few seconds.")]
    public Task<QueueListing> ListQueuesAsync(
        [Description("Case-insensitive substring of the queue name, e.g. 'order'.")] string? nameContains = null,
        [Description("Only MassTransit error queues (_error, _skipped) and dead-letter queues (_dead).")] bool onlyErrorQueues = false,
        [Description("Only queues that hold messages (ready or unacknowledged).")] bool onlyNonEmpty = false,
        [Description("Maximum queues to return, 1-500 (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxLimit)
            throw new McpException($"limit must be between 1 and {MaxLimit}.");
        return management.ListQueuesAsync(nameContains, onlyErrorQueues, onlyNonEmpty, limit, cancellationToken);
    }

    [McpServerTool(Name = "contract_consumers", Title = "Who consumes each contract",
        ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "For each contract (or one): follows the broker bindings from its exchange to the queues that receive it (matching " +
        "the contract's routing key on direct and topic exchanges; the default exchange reaches the queue it names), with " +
        "consumer counts and the backlog of each queue's _error and _skipped queue. Status shows problems at a glance: " +
        "no-exchange (nobody consumes or publishes it), no-queue (sending it now is unroutable), no-consumers (messages pile " +
        "up / requests time out). Use before publishing, or to find where messages of a contract end up. With many " +
        "contracts, narrow it with nameContains.")]
    public async Task<ContractConsumersReport> ContractConsumersAsync(
        [Description("Contract name, e.g. OrderSubmitted. Empty: all contracts.")] string? contract = null,
        [Description("Only return contracts whose status is not ok.")] bool onlyProblems = false,
        [Description("Case-insensitive part of the contract name, tool name, .NET type or URN, e.g. 'order'.")] string? nameContains = null,
        [Description("Maximum contracts to inspect, 1-1000 (default 100); each costs a few management API calls.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ContractDescriptor> selected;
        var matched = 1;
        if (!string.IsNullOrWhiteSpace(contract))
        {
            selected = [contracts.FindByName(contract)
                ?? throw new McpException($"Unknown contract '{contract}'. See list_contracts.")];
        }
        else
        {
            (selected, matched) = BrokerTools.Select(contracts.Contracts, nameContains, limit);
        }

        var results = new List<ContractConsumers>();
        foreach (var c in selected)
            results.Add(await topology.InspectAsync(c, cancellationToken));

        var ok = results.Count(r => r.Status == "ok");
        return new ContractConsumersReport(
            [.. results.Where(r => !onlyProblems || r.Status != "ok")], ok, results.Count - ok,
            "Consumer and message counts come from management statistics and can lag a few seconds." +
            (matched > selected.Count ? $" Only {selected.Count} of {matched} contracts were inspected: narrow with nameContains or raise limit." : ""))
        {
            Truncated = matched > selected.Count,
        };
    }
}
