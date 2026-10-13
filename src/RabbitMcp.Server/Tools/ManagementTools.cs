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
        "MassTransit _error/_skipped queues are flagged with the queue they belong to. Use it to find queue names for " +
        "queue_stats, peek_messages and republish_from_error. Counts come from management statistics and can lag a few seconds.")]
    public Task<QueueListing> ListQueuesAsync(
        [Description("Case-insensitive substring of the queue name, e.g. 'order'.")] string? nameContains = null,
        [Description("Only MassTransit error queues (_error, _skipped).")] bool onlyErrorQueues = false,
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
        "For each contract (or one): follows the broker bindings from its exchange to the queues that receive it, with " +
        "consumer counts and the backlog of each queue's _error and _skipped queue. Status shows problems at a glance: " +
        "no-exchange (nobody consumes or publishes it), no-queue (sending it now is unroutable), no-consumers (messages pile " +
        "up / requests time out). Use before publishing, or to find where messages of a contract end up.")]
    public async Task<ContractConsumersReport> ContractConsumersAsync(
        [Description("Contract name, e.g. OrderSubmitted. Empty: all contracts.")] string? contract = null,
        [Description("Only return contracts whose status is not ok.")] bool onlyProblems = false,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ContractDescriptor> selected = contracts.Contracts;
        if (!string.IsNullOrWhiteSpace(contract))
        {
            selected = [contracts.FindByName(contract)
                ?? throw new McpException($"Unknown contract '{contract}'. See list_contracts.")];
        }

        var results = new List<ContractConsumers>();
        foreach (var c in selected)
            results.Add(await topology.InspectAsync(c, cancellationToken));

        var ok = results.Count(r => r.Status == "ok");
        return new ContractConsumersReport(
            [.. results.Where(r => !onlyProblems || r.Status != "ok")], ok, results.Count - ok,
            "Consumer and message counts come from management statistics and can lag a few seconds.");
    }
}
