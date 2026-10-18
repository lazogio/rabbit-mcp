// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RabbitMcp.Contracts;

namespace RabbitMcp.Server.Contracts;

/// <summary>Everything the server needs to know about one message contract.</summary>
public sealed record ContractDescriptor(
    Type Type,
    string Name,
    string ToolName,
    string MessageTypeUrn,
    string Exchange,
    int Version,
    string Description)
{
    /// <summary>Possible replies. Non-empty means this contract is a request.</summary>
    public IReadOnlyList<ResponseDescriptor> Responses { get; init; } = [];

    /// <summary>MassTransit envelope (default) or plain JSON.</summary>
    public MessageFormat Format { get; init; } = MessageFormat.MassTransit;

    /// <summary>
    /// For contracts from AsyncAPI files: the message's JSON Schema (refs inlined). <see cref="Type"/> is then
    /// <see cref="JsonElement"/> and validation uses this schema instead of DataAnnotations.
    /// </summary>
    public JsonNode? Schema { get; init; }

    /// <summary>Where the contract was defined: the C# type's full name, or "&lt;document&gt;.&lt;message&gt;" for AsyncAPI.</summary>
    public string TypeName { get; init; } = Type.FullName ?? Name;

    public bool IsSchemaBased => Schema is not null;

    /// <summary>Routing key of every message; with the default exchange (<see cref="Exchange"/> "") a queue name.</summary>
    public string RoutingKey { get; init; } = "";

    public bool IsRequest => Responses.Count > 0;

    public bool IsRaw => Format == MessageFormat.Raw;

    /// <summary>Sent through the default exchange, straight to the queue named <see cref="RoutingKey"/>.</summary>
    public bool UsesDefaultExchange => Exchange.Length == 0;

    /// <summary>"masstransit" or "raw", as shown to the model.</summary>
    public string FormatName => IsRaw ? "raw" : "masstransit";

    /// <summary>Where messages go, for descriptions and errors: "exchange 'X'", "... with routing key 'k'" or "queue 'q'".</summary>
    public string Destination =>
        UsesDefaultExchange ? $"queue '{RoutingKey}'"
        : RoutingKey.Length == 0 ? $"exchange '{Exchange}'"
        : $"exchange '{Exchange}' with routing key '{RoutingKey}'";

    /// <summary>Name for spans and audit: the exchange, or the queue for the default exchange.</summary>
    public string DestinationName => UsesDefaultExchange ? RoutingKey : Exchange;

    public ResponseDescriptor? FindResponse(IEnumerable<string>? urns) =>
        urns?.Select(u => Responses.FirstOrDefault(r => string.Equals(r.MessageTypeUrn, u, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(r => r is not null);

    /// <summary>A raw reply's AMQP type: a response URN or name (case-insensitive).</summary>
    public ResponseDescriptor? FindResponseByType(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? null
            : FindResponse([type.Trim()])
              ?? Responses.FirstOrDefault(r => string.Equals(r.Name, type.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A reply type of a request contract.</summary>
public sealed record ResponseDescriptor(Type Type, string Name, string MessageTypeUrn)
{
    /// <summary>For replies declared in AsyncAPI files: their JSON Schema (<see cref="Type"/> is JsonElement).</summary>
    public JsonNode? Schema { get; init; }
}

/// <summary>Discovers [MessageContract] types and resolves them by type, name or URN.</summary>
public sealed partial class ContractRegistry
{
    private readonly Dictionary<Type, ContractDescriptor> _byType = [];
    private readonly Dictionary<string, ContractDescriptor> _byUrn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContractDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ContractRegistry(IEnumerable<Type> contractTypes, IEnumerable<ContractDescriptor>? schemaContracts = null)
    {
        var all = new List<ContractDescriptor>();
        foreach (var d in contractTypes.Select(Describe).Concat(schemaContracts ?? []))
        {
            if (!_byUrn.TryAdd(d.MessageTypeUrn, d))
                throw new InvalidOperationException($"Two contracts share the message type '{d.MessageTypeUrn}'.");
            if (!_byName.TryAdd(d.ToolName, d))
                throw new InvalidOperationException($"Two contracts share the tool name '{d.ToolName}'.");
            if (!_byName.TryAdd(d.Name, d) && !ReferenceEquals(_byName[d.Name], d))
                throw new InvalidOperationException($"Two contracts share the name '{d.Name}'. Use distinct type names.");

            if (!d.IsSchemaBased)
                _byType.Add(d.Type, d);
            all.Add(d);
        }

        Contracts = [.. all.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ContractDescriptor> Contracts { get; }

    public static ContractRegistry FromAssemblies(params Assembly[] assemblies) =>
        new(assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.GetCustomAttribute<MessageContractAttribute>() is not null));

    public ContractDescriptor Get(Type type) =>
        _byType.TryGetValue(type, out var d)
            ? d
            : throw new InvalidOperationException($"'{type.FullName}' is not a registered message contract.");

    /// <summary>Finds a contract by type name (OrderSubmitted) or tool name (publish_order_submitted).</summary>
    public ContractDescriptor? FindByName(string name) => _byName.GetValueOrDefault(name.Trim());

    /// <summary>Returns the first registered contract among MassTransit messageType URNs.</summary>
    public ContractDescriptor? FindByUrns(IEnumerable<string>? urns) =>
        urns?.Select(u => _byUrn.GetValueOrDefault(u)).FirstOrDefault(d => d is not null);

    public static ContractDescriptor Describe(Type type)
    {
        var attr = type.GetCustomAttribute<MessageContractAttribute>()
            ?? throw new InvalidOperationException($"'{type.FullName}' has no [MessageContract] attribute.");

        if (type.IsGenericType || type.IsNested || type.IsAbstract || !type.IsClass)
            throw new InvalidOperationException(
                $"Contract '{type.FullName}' must be a non-generic, non-nested, non-abstract class or record.");
        if (string.IsNullOrEmpty(type.Namespace))
            throw new InvalidOperationException($"Contract '{type.Name}' must be declared in a namespace.");
        if (attr.Version < 1)
            throw new InvalidOperationException($"Contract '{type.FullName}' has an invalid version {attr.Version}.");

        if (!Enum.IsDefined(attr.Format))
            throw new InvalidOperationException($"Contract '{type.FullName}' has an unknown Format {(int)attr.Format}.");

        var responses = attr.Responses.Distinct().Select(DescribeResponse).ToArray();

        // MassTransit defaults: urn:message:Namespace:Type and exchange Namespace:Type.
        var messageName = $"{type.Namespace}:{type.Name}";
        var toolName = attr.ToolName ?? (responses.Length > 0 ? "request_" : "publish_") + ToSnakeCase(type.Name);

        if (!ToolNameRegex().IsMatch(toolName))
            throw new InvalidOperationException(
                $"Tool name '{toolName}' for '{type.FullName}' must match {ToolNameRegex()}.");

        var routingKey = attr.RoutingKey ?? "";
        var urn = attr.Urn ?? "urn:message:" + messageName;
        string exchange;
        if (attr.Format == MessageFormat.Raw)
        {
            // A plain-JSON service has its own topology: there is no naming convention to fall back on.
            if (attr.Exchange is null && routingKey.Length == 0)
                throw new InvalidOperationException(
                    $"Raw contract '{type.FullName}' needs Exchange and/or RoutingKey (RoutingKey alone sends to that queue " +
                    "through the default exchange).");
            exchange = attr.Exchange ?? "";
        }
        else
        {
            exchange = attr.Exchange ?? messageName;
        }

        if (exchange.Length == 0 && routingKey.Length == 0)
            throw new InvalidOperationException(
                $"Contract '{type.FullName}' uses the default exchange (Exchange = \"\") and needs RoutingKey = the queue name.");
        CheckName(type, "Exchange", exchange);
        CheckName(type, "RoutingKey", routingKey);
        CheckName(type, "Urn", urn);
        if (urn.Trim().Length == 0)
            throw new InvalidOperationException($"Contract '{type.FullName}' has an empty Urn.");

        return new ContractDescriptor(
            Type: type,
            Name: type.Name,
            ToolName: toolName,
            MessageTypeUrn: urn,
            Exchange: exchange,
            Version: attr.Version,
            Description: attr.Description)
        {
            Responses = responses,
            Format = attr.Format,
            RoutingKey = routingKey,
        };
    }

    /// <summary>AMQP short strings: at most 255 bytes.</summary>
    private static void CheckName(Type type, string property, string value) => CheckName(type.FullName ?? type.Name, property, value);

    private static void CheckName(string contract, string property, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 255)
            throw new InvalidOperationException($"Contract '{contract}': {property} is longer than 255 bytes.");
    }

    /// <summary>A contract defined by a JSON Schema (AsyncAPI) instead of a C# type; same rules as <see cref="Describe"/>.</summary>
    public static ContractDescriptor FromSchema(
        string typeName, string name, string? toolName, string urn, string exchange, string routingKey, MessageFormat format,
        int version, string description, JsonNode schema, IReadOnlyList<ResponseDescriptor> responses)
    {
        if (!IdentifierRegex().IsMatch(name))
            throw new InvalidOperationException($"'{typeName}': '{name}' is not a usable message name (letters, digits, '_').");
        var tool = toolName ?? (responses.Count > 0 ? "request_" : "publish_") + ToSnakeCase(name);
        if (!ToolNameRegex().IsMatch(tool))
            throw new InvalidOperationException($"Tool name '{tool}' for '{typeName}' must match {ToolNameRegex()}.");
        if (version < 1)
            throw new InvalidOperationException($"'{typeName}' has an invalid version {version}.");
        if (string.IsNullOrWhiteSpace(urn))
            throw new InvalidOperationException($"'{typeName}' has an empty message type.");
        if (exchange.Length == 0 && routingKey.Length == 0)
            throw new InvalidOperationException($"'{typeName}' has no destination: give its channel an AMQP exchange or queue binding, or an address.");
        CheckName(typeName, "exchange", exchange);
        CheckName(typeName, "routing key", routingKey);
        CheckName(typeName, "message type", urn);

        return new ContractDescriptor(typeof(JsonElement), name, tool, urn, exchange, version, description)
        {
            Responses = responses,
            Format = format,
            RoutingKey = routingKey,
            Schema = schema,
            TypeName = typeName,
        };
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex IdentifierRegex();

    private static ResponseDescriptor DescribeResponse(Type type)
    {
        if (type.IsGenericType || type.IsNested || type.IsAbstract || !type.IsClass || string.IsNullOrEmpty(type.Namespace))
            throw new InvalidOperationException(
                $"Response type '{type.FullName}' must be a non-generic, non-nested, non-abstract class or record in a namespace.");

        return new ResponseDescriptor(type, type.Name, $"urn:message:{type.Namespace}:{type.Name}");
    }

    /// <summary>OrderSubmitted -> order_submitted, HTTPRequestSent -> http_request_sent.</summary>
    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                var prev = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNameRegex();
}
