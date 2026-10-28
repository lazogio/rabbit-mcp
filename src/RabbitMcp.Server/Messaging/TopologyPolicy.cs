// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Security;
using RabbitMQ.Client;

namespace RabbitMcp.Server.Messaging;

/// <summary>An exchange declare_exchange may create, already checked against <see cref="TopologyPolicy"/>.</summary>
public sealed record ExchangeSpec(
    string Name,
    string Type,
    bool Durable,
    bool AutoDelete,
    IReadOnlyDictionary<string, object?> Arguments,
    string? BindToExchange,
    string RoutingKey);

/// <summary>A queue declare_queue may create, already checked against <see cref="TopologyPolicy"/>.</summary>
public sealed record QueueSpec(
    string Name,
    string QueueType,
    bool Durable,
    IReadOnlyDictionary<string, object?> Arguments,
    string? BindExchange,
    string RoutingKey);

/// <summary>
/// What declare_exchange / declare_queue may create: RabbitMcp:TopologyAllow / TopologyDeny (case-sensitive name
/// patterns) and RabbitMcp:TopologyArguments (allowed x-arguments). Exchanges of MassTransit contracts may only be
/// created with MassTransit's own settings, otherwise the services' bus would fail to start (406 PRECONDITION_FAILED).
/// </summary>
public sealed class TopologyPolicy
{
    /// <summary>
    /// Arguments that cannot drop messages. TTL, max-length and expiry are left out on purpose: they discard messages
    /// (or whole queues) unless a dead-letter exchange is set, so an operator has to allow them explicitly.
    /// </summary>
    public const string DefaultArguments =
        "x-dead-letter-exchange;x-dead-letter-routing-key;alternate-exchange;x-single-active-consumer";

    public static readonly IReadOnlyList<string> ExchangeTypes =
        [ExchangeType.Fanout, ExchangeType.Direct, ExchangeType.Topic, ExchangeType.Headers];

    public static readonly IReadOnlyList<string> QueueTypes = ["classic", "quorum", "stream"];

    private const int MaxNameBytes = 255;

    private readonly Regex[] _allow;
    private readonly Regex[] _deny;
    private readonly HashSet<string> _arguments;
    private readonly bool _anyArgument;
    private readonly HashSet<string> _massTransitExchanges;

    public TopologyPolicy(RabbitMcpOptions options, ContractRegistry contracts)
    {
        _allow = Patterns(options.TopologyAllow);
        _deny = Patterns(options.TopologyDeny);
        var arguments = Split(string.IsNullOrWhiteSpace(options.TopologyArguments) ? DefaultArguments : options.TopologyArguments);
        _anyArgument = arguments.Contains("*");
        _arguments = new HashSet<string>(arguments.Where(a => a is not ("*" or "none")), StringComparer.Ordinal);
        if (_arguments.Contains("x-queue-type"))
            throw new StartupException("RabbitMcp:TopologyArguments: x-queue-type is set with declare_queue's queueType, not as an argument.");

        // Exchanges MassTransit declares itself (publish exchanges of events, requests and their responses).
        _massTransitExchanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts.Contracts.Where(c => !c.IsRaw && !c.UsesDefaultExchange))
        {
            _massTransitExchanges.Add(contract.Exchange);
            foreach (var response in contract.Responses)
                if (ExchangeOfUrn(response.MessageTypeUrn) is { } exchange)
                    _massTransitExchanges.Add(exchange);
        }
    }

    /// <summary>Whether MassTransit declares this exchange itself (a contract's or response's "Namespace:Type").</summary>
    public bool IsMassTransitExchange(string name) => _massTransitExchanges.Contains(name);

    /// <exception cref="McpException">The exchange may not be created like this; the message says why.</exception>
    public ExchangeSpec Exchange(string name, string? type, bool durable, bool autoDelete,
        IDictionary<string, JsonElement>? arguments, string? bindToExchange, string? routingKey)
    {
        CheckName("exchange", name);
        var kind = (type ?? ExchangeType.Fanout).Trim().ToLowerInvariant();
        if (!ExchangeTypes.Contains(kind))
            throw new McpException($"Unknown exchange type '{type}'. Use {string.Join(", ", ExchangeTypes)}.");
        var args = Arguments(arguments);
        var source = Source(bindToExchange, nameof(bindToExchange));
        if (source == name)
            throw new McpException("An exchange cannot be bound to itself.");
        var spec = new ExchangeSpec(name, kind, durable, autoDelete, args, source, routingKey ?? "");

        if (IsMassTransitExchange(name))
        {
            RequireMassTransitSettings(spec, $"'{name}' is the exchange of a MassTransit contract");
            if (source is not null)
                throw new McpException(
                    $"'{name}' is the exchange of a MassTransit contract: binding another exchange to it would deliver " +
                    "foreign messages to its consumers. Create it without bindToExchange.");
        }
        return spec;
    }

    /// <summary>
    /// MassTransit declares its exchanges fanout, durable, not auto-delete and without arguments; an exchange declared
    /// differently makes its services fail to start (406 PRECONDITION_FAILED).
    /// </summary>
    /// <exception cref="McpException">The spec has other settings; <paramref name="why"/> says why it matters.</exception>
    public static void RequireMassTransitSettings(ExchangeSpec spec, string why)
    {
        if (spec.Type != ExchangeType.Fanout || !spec.Durable || spec.AutoDelete || spec.Arguments.Count > 0)
            throw new McpException(
                $"{why}. MassTransit declares such exchanges as fanout, durable, not auto-delete and without arguments; " +
                "any other settings make its services fail to start (406 PRECONDITION_FAILED). Call again with " +
                "type=fanout, durable=true, autoDelete=false and no arguments.");
    }

    /// <exception cref="McpException">The queue may not be created like this; the message says why.</exception>
    public QueueSpec Queue(string name, string? queueType, bool durable,
        IDictionary<string, JsonElement>? arguments, string? bindExchange, string? routingKey)
    {
        CheckName("queue", name);
        var kind = (queueType ?? "classic").Trim().ToLowerInvariant();
        if (!QueueTypes.Contains(kind))
            throw new McpException($"Unknown queue type '{queueType}'. Use {string.Join(", ", QueueTypes)}.");
        if (kind != "classic" && !durable)
            throw new McpException($"{kind} queues are always durable: call again with durable=true.");

        var args = Arguments(arguments);
        // Always explicit: RabbitMQ accepts a declare without x-queue-type for a queue of any type, so only an explicit
        // type makes the "same settings" check of an existing queue compare it.
        args["x-queue-type"] = kind;
        if (kind == "quorum" && !args.ContainsKey("x-dead-letter-exchange") && !args.ContainsKey("x-delivery-limit"))
            throw new McpException(
                "Quorum queues drop a message after 20 failed deliveries (RabbitMQ 4's default delivery limit) unless it " +
                "can be dead-lettered. Pass arguments {\"x-dead-letter-exchange\": \"<existing exchange>\"} (or " +
                "x-delivery-limit, if RabbitMcp:TopologyArguments allows it).");
        return new QueueSpec(name, kind, durable, args, Source(bindExchange, nameof(bindExchange)), routingKey ?? "");
    }

    /// <summary>
    /// Exchanges the arguments point to (dead-letter exchange, alternate exchange): messages sent to a missing one are
    /// dropped by RabbitMQ, so they must exist before the entity is created. "" (the default exchange) always exists.
    /// </summary>
    public static IEnumerable<(string Argument, string Exchange)> ReferencedExchanges(IReadOnlyDictionary<string, object?> arguments) =>
        new[] { "x-dead-letter-exchange", "alternate-exchange" }
            .Where(a => arguments.TryGetValue(a, out var v) && v is string { Length: > 0 })
            .Select(a => (a, (string)arguments[a]!));

    /// <summary>"urn:message:Billing.Contracts:PaymentCaptured" → "Billing.Contracts:PaymentCaptured".</summary>
    public static string? ExchangeOfUrn(string urn) =>
        urn.StartsWith("urn:message:", StringComparison.OrdinalIgnoreCase) && urn.Length > "urn:message:".Length
            ? urn["urn:message:".Length..]
            : null;

    private void CheckName(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException($"The {kind} name is required (server-named {kind}s are not supported).");
        if (name != name.Trim() || name.Any(char.IsControl))
            throw new McpException($"The {kind} name '{AuditLog.OneLine(name)}' has leading/trailing spaces or control characters.");
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            throw new McpException($"The {kind} name is longer than {MaxNameBytes} bytes.");
        if (name.StartsWith("amq.", StringComparison.Ordinal))
            throw new McpException($"Names starting with 'amq.' are reserved by RabbitMQ; choose another {kind} name.");
        if (_deny.Any(r => r.IsMatch(name)))
            throw new McpException($"RabbitMcp:TopologyDeny does not allow creating the {kind} '{name}'.");
        if (_allow.Length > 0 && !_allow.Any(r => r.IsMatch(name)))
            throw new McpException($"RabbitMcp:TopologyAllow does not cover the {kind} '{name}'. Nothing was created.");
    }

    private static string? Source(string? exchange, string argument)
    {
        if (exchange is null)
            return null;
        if (exchange.Length == 0 || exchange == "amq.default")
            throw new McpException($"{argument}: the default exchange cannot be bound to (it reaches every queue by name already).");
        return exchange;
    }

    private Dictionary<string, object?> Arguments(IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            if (key == "x-queue-type")
                throw new McpException("Set the queue type with queueType, not as the x-queue-type argument.");
            if (!_anyArgument && !_arguments.Contains(key))
                throw new McpException(
                    $"Argument '{AuditLog.OneLine(key)}' is not allowed (RabbitMcp:TopologyArguments). Allowed: " +
                    (_arguments.Count == 0 ? "none" : string.Join(", ", _arguments.Order(StringComparer.Ordinal))) + ".");
            result[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                _ => throw new McpException($"Argument '{AuditLog.OneLine(key)}' must be a string, a whole number or true/false."),
            };
        }
        return result;
    }

    private static string[] Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // RabbitMQ names are case-sensitive, like the queue patterns of roles.
    private static Regex[] Patterns(string? value) => [.. Split(value).Select(p => Glob.ToRegex(p, ignoreCase: false))];
}
