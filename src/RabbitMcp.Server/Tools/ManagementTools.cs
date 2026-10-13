using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:ManagementUri is configured.</summary>
[McpServerToolType]
public sealed class ManagementTools(ManagementClient management)
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
}
