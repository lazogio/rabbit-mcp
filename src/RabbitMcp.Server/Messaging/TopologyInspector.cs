using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Who receives a contract: follows bindings from the contract's exchange (exchange-to-exchange, as MassTransit
/// binds "Ns:Type" → endpoint exchange → queue) down to queues, with their consumers and _error/_skipped backlogs.
/// </summary>
public sealed class TopologyInspector(ManagementClient management)
{
    public const int MaxExchanges = 50;
    public const int MaxQueues = 50;

    public async Task<ContractConsumers> InspectAsync(ContractDescriptor contract, CancellationToken ct)
    {
        var kind = contract.IsRequest ? "request" : "event";
        var root = await management.GetSourceBindingsAsync(contract.Exchange, ct);
        if (root is null)
        {
            return new ContractConsumers(contract.Name, kind, contract.Exchange, ExchangeExists: false, "no-exchange",
                [], 0, [$"Exchange '{contract.Exchange}' does not exist: no MassTransit endpoint consumes {contract.Name} " +
                       "(MassTransit creates it when a consumer starts or the first message is published)."]);
        }

        // Breadth-first through exchange-to-exchange bindings; remember the path to each queue.
        var queues = new List<(string Queue, IReadOnlyList<string> Via)>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { contract.Exchange };
        var frontier = new Queue<(string Exchange, IReadOnlyList<ManagementBinding> Bindings, IReadOnlyList<string> Via)>();
        frontier.Enqueue((contract.Exchange, root, new[] { contract.Exchange }));
        var truncated = false;

        while (frontier.Count > 0)
        {
            var (_, bindings, via) = frontier.Dequeue();
            foreach (var b in bindings)
            {
                if (b.DestinationType == "queue")
                {
                    if (seenQueues.Count >= MaxQueues) { truncated = true; continue; }
                    if (seenQueues.Add(b.Destination))
                        queues.Add((b.Destination, via));
                }
                else if (b.DestinationType == "exchange" && visited.Add(b.Destination))
                {
                    if (visited.Count > MaxExchanges) { truncated = true; continue; }
                    var next = await management.GetSourceBindingsAsync(b.Destination, ct);
                    if (next is not null)
                        frontier.Enqueue((b.Destination, next, via.Append(b.Destination).ToArray()));
                }
            }
        }

        var reached = new List<ConsumerQueue>();
        var warnings = new List<string>();
        foreach (var (name, via) in queues)
        {
            var queue = await management.GetQueueAsync(name, ct);
            var error = await management.GetQueueAsync(name + "_error", ct);
            var skipped = await management.GetQueueAsync(name + "_skipped", ct);
            reached.Add(new ConsumerQueue(name, via, queue?.Consumers, queue?.MessagesReady,
                error?.MessagesReady, skipped?.MessagesReady));

            if (queue?.Consumers is 0)
                warnings.Add($"Queue '{name}' has no consumers ({queue.MessagesReady ?? 0} messages waiting).");
            if (error?.MessagesReady is > 0)
                warnings.Add($"'{name}_error' holds {error.MessagesReady} faulted messages (peek_messages / republish_from_error).");
            if (skipped?.MessagesReady is > 0)
                warnings.Add($"'{name}_skipped' holds {skipped.MessagesReady} messages that endpoint has no consumer for.");
        }

        var consumers = reached.Sum(q => q.Consumers ?? 0);
        var status = reached.Count == 0 ? "no-queue" : consumers == 0 ? "no-consumers" : "ok";
        if (reached.Count == 0)
            warnings.Insert(0, $"No queue is bound to '{contract.Exchange}': a {contract.Name} sent now is unroutable" +
                               (contract.IsRequest ? " and the request would time out." : " and is not delivered."));
        else if (consumers == 0)
            warnings.Insert(0, $"Queues receive {contract.Name}, but nothing consumes them right now" +
                               (contract.IsRequest ? ": requests will time out." : ": messages pile up."));
        if (truncated)
            warnings.Add($"Topology larger than {MaxExchanges} exchanges / {MaxQueues} queues; the list is incomplete.");

        return new ContractConsumers(contract.Name, kind, contract.Exchange, ExchangeExists: true, status,
            reached, consumers, warnings);
    }
}
