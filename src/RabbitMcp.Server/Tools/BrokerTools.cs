// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Tools;

/// <summary>Tools that are always available. None of them removes or sends messages.</summary>
[McpServerToolType]
public sealed class BrokerTools(
    ContractRegistry contracts,
    PublishPolicy policy,
    RabbitConnection connection,
    QueueBrowser browser,
    AccessControl access,
    IOptions<RabbitMcpOptions> options)
{
    [McpServerTool(Name = "list_contracts", Title = "List message contracts",
        ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the message contracts this server knows: events (publish_*) and requests (request_*, with their possible replies), with version, wire format (masstransit or raw plain JSON), message type, exchange and routing key. With many contracts, narrow it with nameContains.")]
    public ContractList ListContracts(
        [Description("Case-insensitive part of the contract name, tool name, .NET type or message URN, e.g. 'order'.")] string? nameContains = null,
        [Description("Maximum contracts to return, 1-1000 (default 200).")] int limit = 200,
        ClaimsPrincipal? user = null)
    {
        var (selected, matched) = Select(contracts.Contracts, nameContains, limit);
        var permissions = access.For(user);
        return new(
            [.. selected.Select(c =>
                new ContractInfo(
                    c.Name, c.IsRequest ? "request" : "event", c.ToolName, c.Version, c.FormatName, c.MessageTypeUrn,
                    c.Exchange, c.RoutingKey.Length == 0 ? null : c.RoutingKey, c.Description, [.. c.Responses.Select(r => r.Name)],
                    Sendable: options.Value.EnablePublish && policy.IsAllowed(c) &&
                              permissions.Allows(c.IsRequest ? Permission.Request : Permission.Publish, c)))],
            options.Value.EnablePublish)
        {
            Matched = matched,
            Truncated = matched > selected.Count,
        };
    }

    public const int MaxContractLimit = 1000;

    /// <summary>Contracts matching <paramref name="nameContains"/> (name, tool, type, URN), at most <paramref name="limit"/>.</summary>
    internal static (IReadOnlyList<ContractDescriptor> Selected, int Matched) Select(
        IEnumerable<ContractDescriptor> all, string? nameContains, int limit)
    {
        if (limit is < 1 or > MaxContractLimit)
            throw new McpException($"limit must be between 1 and {MaxContractLimit}.");
        var text = nameContains?.Trim();
        var matching = string.IsNullOrEmpty(text)
            ? all.ToList()
            : all.Where(c => new[] { c.Name, c.ToolName, c.TypeName, c.MessageTypeUrn }
                .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        return (matching.Take(limit).ToList(), matching.Count);
    }

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
            QueueDeclareOk? ok;
            try
            {
                ok = await connection.GetQueueAsync(queue, cancellationToken);
            }
            catch (OperationInterruptedException ex) when (RabbitConnection.IsResourceLocked(ex))
            {
                // Exclusive to another connection: it exists, but AMQP does not tell us its counts.
                stats.Add(new QueueStat(queue, Exists: true, null, null));
                continue;
            }
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
        "MassTransit fault details (exception, consumer, retry count) for _error/_skipped queues, and RabbitMQ dead-lettering " +
        "history (x-death) for dead-letter queues. " +
        "Messages are fetched unacknowledged and immediately requeued, so they get the redelivered flag and may change order; " +
        "on RabbitMQ 3.x quorum queues each peek counts towards the delivery limit, so a queue with one can drop the message " +
        "(RabbitMQ 4 does not count peeks). Queues with active consumers are refused unless force=true. For a stream use read_stream.")]
    public Task<PeekResult> PeekMessagesAsync(
        [Description("Queue name, e.g. submit-order_error.")] string queue,
        [Description("How many messages to show (default 5).")] int count = 5,
        [Description("Peek even if the queue has active consumers (they may get the messages later, redelivered and out of order).")] bool force = false,
        CancellationToken cancellationToken = default) =>
        browser.PeekAsync(queue, count, force, cancellationToken);
}
