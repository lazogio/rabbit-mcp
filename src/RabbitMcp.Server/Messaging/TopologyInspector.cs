// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Who receives a contract: follows bindings from the contract's exchange (exchange-to-exchange, as MassTransit
/// binds "Ns:Type" → endpoint exchange → queue) down to queues, with their consumers and _error/_skipped backlogs.
/// Bindings are matched against the contract's routing key the way the broker does for fanout, direct and topic
/// exchanges; other exchange types (headers, plugins) are followed without evaluating their bindings, with a warning.
/// A contract on the default exchange reaches exactly the queue named by its routing key.
/// </summary>
public sealed class TopologyInspector(ManagementClient management)
{
    public const int MaxExchanges = 50;
    public const int MaxQueues = 50;
    public const string DefaultExchange = "(default exchange)";

    public async Task<ContractConsumers> InspectAsync(ContractDescriptor contract, CancellationToken ct)
    {
        var kind = contract.IsRequest ? "request" : "event";
        var queues = new List<(string Queue, IReadOnlyList<string> Via)>();
        var warnings = new List<string>();
        var truncated = false;

        if (contract.UsesDefaultExchange)
        {
            // The default exchange routes to the queue whose name is the routing key, if it exists.
            queues.Add((contract.RoutingKey, new[] { DefaultExchange }));
        }
        else
        {
            var root = await management.GetExchangeAsync(contract.Exchange, ct);
            if (root is null)
            {
                return new ContractConsumers(contract.Name, kind, contract.Exchange, ExchangeExists: false, "no-exchange",
                    [], 0, [contract.IsRaw
                        ? $"Exchange '{contract.Exchange}' does not exist: nothing receives {contract.Name}."
                        : $"Exchange '{contract.Exchange}' does not exist: no MassTransit endpoint consumes {contract.Name} " +
                          "(MassTransit creates it when a consumer starts or the first message is published)."]);
            }

            // Breadth-first through exchange-to-exchange bindings (the routing key passes through unchanged);
            // remember the path to each queue.
            var seenQueues = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { contract.Exchange };
            var frontier = new Queue<(ManagementExchange Exchange, IReadOnlyList<string> Via)>();
            frontier.Enqueue((root, new[] { contract.Exchange }));

            while (frontier.Count > 0)
            {
                var (exchange, via) = frontier.Dequeue();
                var matches = RoutingKeys.Matches(exchange.Type, contract.RoutingKey);
                if (matches is null && exchange.Bindings.Count > 0)
                    warnings.Add($"Exchange '{exchange.Name}' is a {exchange.Type} exchange: its bindings were not evaluated, " +
                                 $"so some queues listed behind it may not receive {contract.Name}.");

                foreach (var b in exchange.Bindings)
                {
                    if (matches is not null && !matches(b.RoutingKey ?? ""))
                        continue;

                    if (b.DestinationType == "queue")
                    {
                        if (seenQueues.Count >= MaxQueues) { truncated = true; continue; }
                        if (seenQueues.Add(b.Destination))
                            queues.Add((b.Destination, via));
                    }
                    else if (b.DestinationType == "exchange" && visited.Add(b.Destination))
                    {
                        if (visited.Count > MaxExchanges) { truncated = true; continue; }
                        var next = await management.GetExchangeAsync(b.Destination, ct);
                        if (next is not null)
                            frontier.Enqueue((next, via.Append(b.Destination).ToArray()));
                    }
                }
            }
        }

        var reached = new List<ConsumerQueue>();
        foreach (var (name, via) in queues)
        {
            var queue = await management.GetQueueAsync(name, ct);
            if (queue is null && contract.UsesDefaultExchange)
                continue; // the default exchange has nowhere to route to
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
        var outcome = contract.IsRequest ? " and the request would time out." : " and is not delivered.";
        if (reached.Count == 0)
            warnings.Insert(0, contract.UsesDefaultExchange
                ? $"Queue '{contract.RoutingKey}' does not exist: a {contract.Name} sent now is unroutable" + outcome
                : contract.RoutingKey.Length == 0
                    ? $"No queue is bound to '{contract.Exchange}': a {contract.Name} sent now is unroutable" + outcome
                    : $"No queue is bound to '{contract.Exchange}' for routing key '{contract.RoutingKey}': a {contract.Name} " +
                      "sent now is unroutable" + outcome);
        else if (consumers == 0)
            warnings.Insert(0, $"Queues receive {contract.Name}, but nothing consumes them right now" +
                               (contract.IsRequest ? ": requests will time out." : ": messages pile up."));
        if (truncated)
            warnings.Add($"Topology larger than {MaxExchanges} exchanges / {MaxQueues} queues; the list is incomplete.");

        return new ContractConsumers(contract.Name, kind, contract.Exchange, ExchangeExists: true, status,
            reached, consumers, warnings);
    }
}

/// <summary>How the broker matches a binding key against a message's routing key, per exchange type.</summary>
public static class RoutingKeys
{
    /// <summary>A binding-key predicate for <paramref name="routingKey"/>; null when the type cannot be evaluated.</summary>
    public static Func<string, bool>? Matches(string exchangeType, string routingKey) => exchangeType switch
    {
        "fanout" => _ => true,
        "direct" => key => string.Equals(key, routingKey, StringComparison.Ordinal),
        "topic" => key => TopicMatches(key, routingKey),
        _ => null, // headers, x-consistent-hash, x-delayed-message, ...
    };

    /// <summary>AMQP topic matching: words separated by '.', '*' = exactly one word, '#' = zero or more words.</summary>
    public static bool TopicMatches(string bindingKey, string routingKey)
    {
        var pattern = Words(bindingKey);
        var words = Words(routingKey);
        var memo = new Dictionary<(int, int), bool>();

        bool Match(int p, int w)
        {
            if (memo.TryGetValue((p, w), out var known))
                return known;
            bool result;
            if (p == pattern.Length)
                result = w == words.Length;
            else if (pattern[p] == "#")
                result = Match(p + 1, w) || (w < words.Length && Match(p, w + 1));
            else
                result = w < words.Length && (pattern[p] == "*" || pattern[p] == words[w]) && Match(p + 1, w + 1);
            return memo[(p, w)] = result;
        }

        return Match(0, 0);
    }

    // RabbitMQ treats an empty key as zero words (so "#" matches it and "*" does not).
    private static string[] Words(string key) => key.Length == 0 ? [] : key.Split('.');
}
