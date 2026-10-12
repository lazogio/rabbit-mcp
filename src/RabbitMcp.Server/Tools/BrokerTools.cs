using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Tools that are always available. None of them removes or sends messages.</summary>
[McpServerToolType]
public sealed class BrokerTools(
    ContractRegistry contracts,
    RabbitConnection connection,
    QueueBrowser browser,
    IOptions<RabbitMcpOptions> options)
{
    [McpServerTool(Name = "list_contracts", Title = "List message contracts",
        ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the message contracts this server knows: events (publish_*) and requests (request_*, with their possible replies), with version, MassTransit message type and exchange.")]
    public ContractList ListContracts() =>
        new(
            [.. contracts.Contracts.Select(c =>
                new ContractInfo(
                    c.Name, c.IsRequest ? "request" : "event", c.ToolName, c.Version, c.MessageTypeUrn, c.Exchange,
                    c.Description, [.. c.Responses.Select(r => r.Name)]))],
            options.Value.EnablePublish);

    [McpServerTool(Name = "validate_message", Title = "Validate a message against a contract",
        ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks a JSON message against a contract's schema and validation rules without publishing anything.")]
    public ContractValidation ValidateMessage(
        [Description("Contract name (e.g. OrderSubmitted) or its publish tool name.")] string contract,
        [Description("The message JSON to check.")] JsonElement message)
    {
        var descriptor = contracts.FindByName(contract)
            ?? throw new McpException(
                $"Unknown contract '{contract}'. Known: {string.Join(", ", contracts.Contracts.Select(c => c.Name))}.");

        var errors = MessageInspector.Validate(descriptor, message, ToolJson.Options);
        return new ContractValidation(descriptor.Name, descriptor.Version, errors.Count == 0, errors);
    }

    [McpServerTool(Name = "queue_stats", Title = "Queue statistics",
        ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Ready-message and consumer counts for the given queues (e.g. submit-order, submit-order_error).")]
    public async Task<QueueStats> QueueStatsAsync(
        [Description("Queue names, 1-50.")] string[] queues,
        CancellationToken cancellationToken)
    {
        if (queues is not { Length: > 0 and <= 50 })
            throw new McpException("Pass between 1 and 50 queue names.");

        var stats = new List<QueueStat>(queues.Length);
        foreach (var queue in queues.Distinct(StringComparer.Ordinal))
        {
            var ok = await connection.GetQueueAsync(queue, cancellationToken);
            stats.Add(ok is null
                ? new QueueStat(queue, Exists: false, null, null)
                : new QueueStat(queue, Exists: true, ok.MessageCount, ok.ConsumerCount));
        }
        return new QueueStats(stats);
    }

    [McpServerTool(Name = "peek_messages", Title = "Peek at queued messages",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Shows the first messages of a queue without removing them: payload, matching contract with validation result, " +
        "and MassTransit fault details (exception, consumer, retry count) for _error/_skipped queues. " +
        "Messages are fetched unacknowledged and immediately requeued, so they get the redelivered flag and may change order; " +
        "on quorum queues each peek counts towards the delivery limit. Queues with active consumers are refused unless force=true.")]
    public Task<PeekResult> PeekMessagesAsync(
        [Description("Queue name, e.g. submit-order_error.")] string queue,
        [Description("How many messages to show (default 5).")] int count = 5,
        [Description("Peek even if the queue has active consumers (they may get the messages later, redelivered and out of order).")] bool force = false,
        CancellationToken cancellationToken = default) =>
        browser.PeekAsync(queue, count, force, cancellationToken);
}
