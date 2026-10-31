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
    string RoutingKey)
{
    /// <summary>The RabbitMcp:TopologyTemplates entry it was built from, if any.</summary>
    public string? Template { get; init; }
}

/// <summary>A queue declare_queue may create, already checked against <see cref="TopologyPolicy"/>.</summary>
public sealed record QueueSpec(
    string Name,
    string QueueType,
    bool Durable,
    IReadOnlyDictionary<string, object?> Arguments,
    string? BindExchange,
    string RoutingKey)
{
    /// <summary>The RabbitMcp:TopologyTemplates entry it was built from, if any.</summary>
    public string? Template { get; init; }
}

/// <summary>
/// A named set of settings from RabbitMcp:TopologyTemplates, e.g.
/// "kind=queue;type=quorum;x-dead-letter-exchange=billing.dlx;x-delivery-limit=5". Written by the operator, so its
/// arguments are not limited by TopologyArguments. Values the caller passes explicitly win over the template's.
/// </summary>
public sealed record TopologyTemplate(
    string Name,
    string Kind,
    string? Type,
    bool? Durable,
    bool? AutoDelete,
    IReadOnlyDictionary<string, object?> Arguments,
    string? Bind,
    string? RoutingKey);

/// <summary>
/// What declare_exchange / declare_queue may create: RabbitMcp:TopologyAllow / TopologyDeny (case-sensitive name
/// patterns), TopologyArguments (allowed x-arguments), TopologyTemplates (operator-defined settings) and TopologyRules
/// (the safety rules, all on by default except bind-existing and templates-only).
/// </summary>
public sealed class TopologyPolicy
{
    /// <summary>
    /// Arguments that cannot drop messages. TTL, max-length and expiry are left out on purpose: they discard messages
    /// (or whole queues) unless a dead-letter exchange is set, so an operator has to allow them explicitly.
    /// </summary>
    public const string DefaultArguments =
        "x-dead-letter-exchange;x-dead-letter-routing-key;alternate-exchange;x-single-active-consumer";

    /// <summary>Quorum queues need a dead-letter exchange or x-delivery-limit (RabbitMQ 4 drops after 20 deliveries).</summary>
    public const string RequireDeadLetterRule = "require-dead-letter";
    /// <summary>A dead-letter or alternate exchange in the arguments must exist (messages sent to a missing one are dropped).</summary>
    public const string CheckReferencesRule = "check-references";
    /// <summary>MassTransit exchanges (contracts, responses, endpoint pairs) only with MassTransit's settings, no binding onto contract exchanges.</summary>
    public const string ProtectMassTransitRule = "protect-masstransit";
    /// <summary>Opt-in: also add the requested binding to an exchange or queue that already exists.</summary>
    public const string BindExistingRule = "bind-existing";
    /// <summary>Opt-in: every call must name a template; the caller only chooses name and template.</summary>
    public const string TemplatesOnlyRule = "templates-only";

    public const string DefaultRules = RequireDeadLetterRule + ";" + CheckReferencesRule + ";" + ProtectMassTransitRule;

    private static readonly string[] KnownRules =
        [RequireDeadLetterRule, CheckReferencesRule, ProtectMassTransitRule, BindExistingRule, TemplatesOnlyRule];

    public static readonly IReadOnlyList<string> ExchangeTypes =
        [ExchangeType.Fanout, ExchangeType.Direct, ExchangeType.Topic, ExchangeType.Headers];

    public static readonly IReadOnlyList<string> QueueTypes = ["classic", "quorum", "stream"];

    private const int MaxNameBytes = 255;

    private readonly Regex[] _allow;
    private readonly Regex[] _deny;
    private readonly HashSet<string> _arguments;
    private readonly bool _anyArgument;
    private readonly HashSet<string> _massTransitExchanges;
    private readonly HashSet<string> _rules;
    private readonly Dictionary<string, TopologyTemplate> _templates;

    /// <exception cref="StartupException">Invalid TopologyArguments, TopologyRules or TopologyTemplates.</exception>
    public TopologyPolicy(RabbitMcpOptions options, ContractRegistry contracts)
    {
        _allow = Patterns(options.TopologyAllow);
        _deny = Patterns(options.TopologyDeny);
        var arguments = Split(string.IsNullOrWhiteSpace(options.TopologyArguments) ? DefaultArguments : options.TopologyArguments);
        _anyArgument = arguments.Contains("*");
        _arguments = new HashSet<string>(arguments.Where(a => a is not ("*" or "none")), StringComparer.Ordinal);
        if (_arguments.Contains("x-queue-type"))
            throw new StartupException("RabbitMcp:TopologyArguments: x-queue-type is set with declare_queue's queueType, not as an argument.");

        _rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Split(string.IsNullOrWhiteSpace(options.TopologyRules) ? DefaultRules : options.TopologyRules))
        {
            if (rule.Equals("none", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!KnownRules.Contains(rule, StringComparer.OrdinalIgnoreCase))
                throw new StartupException(
                    $"RabbitMcp:TopologyRules: unknown rule '{rule}'. Use {string.Join(", ", KnownRules)} or none.");
            _rules.Add(rule);
        }

        _templates = new Dictionary<string, TopologyTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in options.TopologyTemplates)
            _templates[name] = ParseTemplate(name, definition);
        if (Has(TemplatesOnlyRule) && _templates.Count == 0)
            throw new StartupException("RabbitMcp:TopologyRules: templates-only needs at least one RabbitMcp:TopologyTemplates entry.");

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

    public bool RequireDeadLetter => Has(RequireDeadLetterRule);

    public bool CheckReferences => Has(CheckReferencesRule);

    public bool ProtectMassTransit => Has(ProtectMassTransitRule);

    public bool BindExisting => Has(BindExistingRule);

    public IReadOnlyCollection<TopologyTemplate> Templates => _templates.Values;

    /// <summary>Whether MassTransit declares this exchange itself (a contract's or response's "Namespace:Type").</summary>
    public bool IsMassTransitExchange(string name) => _massTransitExchanges.Contains(name);

    /// <exception cref="McpException">The exchange may not be created like this; the message says why.</exception>
    public ExchangeSpec Exchange(string name, string? template, string? type, bool? durable, bool? autoDelete,
        IDictionary<string, JsonElement>? arguments, string? bindToExchange, string? routingKey)
    {
        CheckName("exchange", name);
        var t = Template(template, "exchange", type, durable, autoDelete, arguments, bindToExchange, routingKey);
        var kind = (type ?? t?.Type ?? ExchangeType.Fanout).Trim().ToLowerInvariant();
        if (!ExchangeTypes.Contains(kind))
            throw new McpException($"Unknown exchange type '{type ?? t?.Type}'. Use {string.Join(", ", ExchangeTypes)}.");
        var args = Merge(t, arguments);
        var source = Source(bindToExchange ?? t?.Bind, nameof(bindToExchange));
        if (source == name)
            throw new McpException("An exchange cannot be bound to itself.");
        var spec = new ExchangeSpec(name, kind, durable ?? t?.Durable ?? true, autoDelete ?? t?.AutoDelete ?? false, args,
            source, routingKey ?? t?.RoutingKey ?? "") { Template = t?.Name };

        if (ProtectMassTransit && IsMassTransitExchange(name))
        {
            RequireMassTransitSettings(spec, $"'{name}' is the exchange of a MassTransit contract");
            if (source is not null)
                throw new McpException(
                    $"'{name}' is the exchange of a MassTransit contract: binding another exchange to it would deliver " +
                    "foreign messages to its consumers. Create it without bindToExchange.");
        }
        return spec;
    }

    /// <exception cref="McpException">The queue may not be created like this; the message says why.</exception>
    public QueueSpec Queue(string name, string? template, string? queueType, bool? durable,
        IDictionary<string, JsonElement>? arguments, string? bindExchange, string? routingKey)
    {
        CheckName("queue", name);
        var t = Template(template, "queue", queueType, durable, null, arguments, bindExchange, routingKey);
        var kind = (queueType ?? t?.Type ?? "classic").Trim().ToLowerInvariant();
        if (!QueueTypes.Contains(kind))
            throw new McpException($"Unknown queue type '{queueType ?? t?.Type}'. Use {string.Join(", ", QueueTypes)}.");
        var isDurable = durable ?? t?.Durable ?? true;
        if (!isDurable)
            throw new McpException(
                $"{kind} queues are always durable: a transient queue loses its messages on restart, and RabbitMQ 4 refuses " +
                "it by closing the whole connection. Call again with durable=true.");

        var args = Merge(t, arguments);
        // Always explicit: RabbitMQ accepts a declare without x-queue-type for a queue of any type, so only an explicit
        // type makes the "same settings" check of an existing queue compare it.
        args["x-queue-type"] = kind;
        if (RequireDeadLetter && kind == "quorum" && !args.ContainsKey("x-dead-letter-exchange") && !args.ContainsKey("x-delivery-limit"))
            throw new McpException(
                "Quorum queues drop a message after 20 failed deliveries (RabbitMQ 4's default delivery limit) unless it " +
                "can be dead-lettered. Pass arguments {\"x-dead-letter-exchange\": \"<existing exchange>\"} (or " +
                "x-delivery-limit, if RabbitMcp:TopologyArguments allows it), or use a template that sets one.");
        return new QueueSpec(name, kind, isDurable, args, Source(bindExchange ?? t?.Bind, nameof(bindExchange)),
            routingKey ?? t?.RoutingKey ?? "") { Template = t?.Name };
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

    /// <summary>
    /// "kind=queue;type=quorum;durable;x-dead-letter-exchange=billing.dlx;bind=billing.events;routingKey=#".
    /// Keys: kind (queue|exchange, required), type, durable, autoDelete, bind, routingKey; any other key containing
    /// '-' is an argument. A bare key means true; whole numbers and true/false are typed, everything else is a string.
    /// </summary>
    /// <exception cref="StartupException">Malformed definition.</exception>
    public static TopologyTemplate ParseTemplate(string name, string? definition)
    {
        string? kind = null, type = null, bind = null, routingKey = null;
        bool? durable = null, autoDelete = null;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in Split(definition))
        {
            var eq = entry.IndexOf('=');
            var key = (eq < 0 ? entry : entry[..eq]).Trim();
            var text = eq < 0 ? "true" : entry[(eq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "kind": kind = text.ToLowerInvariant(); break;
                case "type": type = text.ToLowerInvariant(); break;
                case "bind": bind = text; break;
                case "routingkey": routingKey = text; break;
                case "durable": durable = Bool(name, key, text); break;
                case "autodelete": autoDelete = Bool(name, key, text); break;
                case "x-queue-type":
                    throw new StartupException($"RabbitMcp:TopologyTemplates:{name}: use type=quorum|stream|classic, not x-queue-type.");
                default:
                    if (!key.Contains('-'))
                        throw new StartupException(
                            $"RabbitMcp:TopologyTemplates:{name}: unknown key '{key}'. Use kind, type, durable, autoDelete, " +
                            "bind, routingKey or an x-argument such as x-dead-letter-exchange.");
                    arguments[key] = text switch
                    {
                        "true" => true,
                        "false" => false,
                        _ when long.TryParse(text, out var n) => n,
                        _ => text,
                    };
                    break;
            }
        }

        if (kind is not ("queue" or "exchange"))
            throw new StartupException($"RabbitMcp:TopologyTemplates:{name}: kind=queue or kind=exchange is required.");
        if (type is not null && !(kind == "queue" ? QueueTypes : ExchangeTypes).Contains(type))
            throw new StartupException($"RabbitMcp:TopologyTemplates:{name}: unknown {kind} type '{type}'.");
        if (kind == "queue" && autoDelete is not null)
            throw new StartupException($"RabbitMcp:TopologyTemplates:{name}: queues are never auto-delete.");
        if (kind == "queue" && durable == false)
            throw new StartupException($"RabbitMcp:TopologyTemplates:{name}: queues are always durable.");
        return new TopologyTemplate(name, kind, type, durable, autoDelete, arguments, bind, routingKey);
    }

    private bool Has(string rule) => _rules.Contains(rule);

    private TopologyTemplate? Template(string? name, string kind, string? type, bool? durable, bool? autoDelete,
        IDictionary<string, JsonElement>? arguments, string? bind, string? routingKey)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (Has(TemplatesOnlyRule))
                throw new McpException($"RabbitMcp:TopologyRules has templates-only: pass template ({TemplateList(kind)}).");
            return null;
        }

        if (!_templates.TryGetValue(name.Trim(), out var template) || template.Kind != kind)
            throw new McpException($"Unknown {kind} template '{AuditLog.OneLine(name)}'. Available: {TemplateList(kind)}.");
        if (Has(TemplatesOnlyRule) && (type is not null || durable is not null || autoDelete is not null ||
                                       arguments is { Count: > 0 } || bind is not null || routingKey is not null))
            throw new McpException("RabbitMcp:TopologyRules has templates-only: pass only name and template.");
        return template;
    }

    private string TemplateList(string kind)
    {
        var names = _templates.Values.Where(t => t.Kind == kind).Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length == 0 ? "none configured" : string.Join(", ", names);
    }

    // Template arguments are the operator's; only what the caller adds is limited by TopologyArguments.
    private Dictionary<string, object?> Merge(TopologyTemplate? template, IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, object?>(template?.Arguments ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        foreach (var (key, value) in Arguments(arguments))
            result[key] = value;
        return result;
    }

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
                    (_arguments.Count == 0 ? "none" : string.Join(", ", _arguments.Order(StringComparer.Ordinal))) +
                    ". A template (RabbitMcp:TopologyTemplates) can set others.");
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

    private static bool Bool(string template, string key, string text) =>
        bool.TryParse(text, out var value)
            ? value
            : throw new StartupException($"RabbitMcp:TopologyTemplates:{template}: {key} must be true or false, not '{text}'.");

    private static string[] Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // RabbitMQ names are case-sensitive, like the queue patterns of roles.
    private static Regex[] Patterns(string? value) => [.. Split(value).Select(p => Glob.ToRegex(p, ignoreCase: false))];
}
