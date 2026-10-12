using System.Reflection;
using System.Text;
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

    public bool IsRequest => Responses.Count > 0;

    public ResponseDescriptor? FindResponse(IEnumerable<string>? urns) =>
        urns?.Select(u => Responses.FirstOrDefault(r => string.Equals(r.MessageTypeUrn, u, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(r => r is not null);
}

/// <summary>A reply type of a request contract.</summary>
public sealed record ResponseDescriptor(Type Type, string Name, string MessageTypeUrn);

/// <summary>Discovers [MessageContract] types and resolves them by type, name or URN.</summary>
public sealed partial class ContractRegistry
{
    private readonly Dictionary<Type, ContractDescriptor> _byType = [];
    private readonly Dictionary<string, ContractDescriptor> _byUrn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContractDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ContractRegistry(IEnumerable<Type> contractTypes)
    {
        foreach (var type in contractTypes)
        {
            var d = Describe(type);

            if (!_byUrn.TryAdd(d.MessageTypeUrn, d))
                throw new InvalidOperationException($"Two contracts share the message type '{d.MessageTypeUrn}'.");
            if (!_byName.TryAdd(d.ToolName, d))
                throw new InvalidOperationException($"Two contracts share the tool name '{d.ToolName}'.");
            if (!_byName.TryAdd(d.Name, d) && !ReferenceEquals(_byName[d.Name], d))
                throw new InvalidOperationException($"Two contracts share the name '{d.Name}'. Use distinct type names.");

            _byType.Add(type, d);
        }

        Contracts = [.. _byType.Values.OrderBy(c => c.Name, StringComparer.Ordinal)];
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

        var responses = attr.Responses.Distinct().Select(DescribeResponse).ToArray();

        // MassTransit defaults: urn:message:Namespace:Type and exchange Namespace:Type.
        var messageName = $"{type.Namespace}:{type.Name}";
        var toolName = attr.ToolName ?? (responses.Length > 0 ? "request_" : "publish_") + ToSnakeCase(type.Name);

        if (!ToolNameRegex().IsMatch(toolName))
            throw new InvalidOperationException(
                $"Tool name '{toolName}' for '{type.FullName}' must match {ToolNameRegex()}.");

        return new ContractDescriptor(
            Type: type,
            Name: type.Name,
            ToolName: toolName,
            MessageTypeUrn: attr.Urn ?? "urn:message:" + messageName,
            Exchange: attr.Exchange ?? messageName,
            Version: attr.Version,
            Description: attr.Description)
        {
            Responses = responses,
        };
    }

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
