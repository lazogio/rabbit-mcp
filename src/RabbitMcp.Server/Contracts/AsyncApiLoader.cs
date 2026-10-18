// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RabbitMcp.Contracts;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// Contracts from AsyncAPI 3.0 / 2.x documents (YAML or JSON), for teams that do not write C#. Every message of an
/// operation becomes a contract; an operation with a reply (3.0) becomes a request whose replies are the reply's
/// messages. Routing comes from the channel's AMQP binding: <c>is: queue</c> → that queue through the default exchange;
/// otherwise the binding's exchange with the channel address as routing key. Messages are plain JSON (raw) unless
/// <c>x-rabbit-mcp.format: masstransit</c>. Optional <c>x-rabbit-mcp</c> on a message: <c>format</c>, <c>urn</c>
/// (AMQP type / MassTransit URN), <c>version</c>, <c>toolName</c>.
/// </summary>
public static class AsyncApiLoader
{
    public const string Extension = "x-rabbit-mcp";

    /// <summary>";"-separated paths (RabbitMcp:ContractFiles), relative to <paramref name="baseDirectory"/>.</summary>
    public static IReadOnlyList<ContractDescriptor> LoadFiles(string? configured, string baseDirectory) =>
        [.. (configured ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p), baseDirectory))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(Load)];

    public static IReadOnlyList<ContractDescriptor> Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"RabbitMcp:ContractFiles: '{path}' does not exist.");
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException($"AsyncAPI file '{path}': {ex.Message}", ex);
        }
    }

    public static IReadOnlyList<ContractDescriptor> Parse(string text, string fallbackTitle = "asyncapi")
    {
        var root = YamlJson.Parse(text) as JsonObject ?? throw new InvalidOperationException("the document is not an object.");
        var version = Str(root["asyncapi"]) ?? throw new InvalidOperationException("'asyncapi' (the spec version) is missing.");
        var document = Identifier(Str(root["info"]?["title"]) ?? fallbackTitle);

        var found = version.StartsWith("3.", StringComparison.Ordinal) ? FromV3(root)
            : version.StartsWith("2.", StringComparison.Ordinal) ? FromV2(root)
            : throw new InvalidOperationException($"AsyncAPI {version} is not supported (2.x and 3.x are).");

        var contracts = new List<ContractDescriptor>();
        foreach (var (key, message, channel, address, replies) in found.DistinctBy(f => f.Key))
            contracts.Add(Describe(root, document, key, message, channel, address, replies));
        return contracts;
    }

    private sealed record Found(string Key, JsonObject Message, JsonObject? Channel, string? Address, IReadOnlyList<(string Key, JsonObject Message)> Replies);

    private static IEnumerable<Found> FromV3(JsonObject root)
    {
        var operations = root["operations"] as JsonObject;
        if (operations is null || operations.Count == 0)
        {
            // No operations: every message of every channel is an event.
            foreach (var (_, channelNode) in root["channels"] as JsonObject ?? new JsonObject())
            {
                var channel = Deref(root, channelNode) as JsonObject;
                foreach (var (key, message) in Messages(root, channel?["messages"]))
                    yield return new Found(key, message, channel, Str(channel?["address"]), []);
            }
            yield break;
        }

        foreach (var (_, operationNode) in operations)
        {
            if (Deref(root, operationNode) is not JsonObject operation)
                continue;
            var channel = Deref(root, operation["channel"]) as JsonObject;
            var messages = operation["messages"] is JsonArray listed ? Messages(root, listed) : Messages(root, channel?["messages"]);
            var replies = operation["reply"] is JsonObject reply
                ? (reply["messages"] is JsonArray replyMessages
                    ? Messages(root, replyMessages)
                    : Messages(root, (Deref(root, reply["channel"]) as JsonObject)?["messages"])).ToList()
                : [];
            foreach (var (key, message) in messages)
                yield return new Found(key, message, channel, Str(channel?["address"]), replies);
        }
    }

    private static IEnumerable<Found> FromV2(JsonObject root)
    {
        foreach (var (address, channelNode) in root["channels"] as JsonObject ?? new JsonObject())
        {
            if (Deref(root, channelNode) is not JsonObject channel)
                continue;
            foreach (var kind in new[] { "publish", "subscribe" })
            {
                if (channel[kind] is not JsonObject operation || operation["message"] is not { } messageNode)
                    continue;
                var messages = Deref(root, messageNode) is JsonObject m && m["oneOf"] is JsonArray oneOf
                    ? Messages(root, oneOf)
                    : Messages(root, new JsonArray(messageNode.DeepClone()));
                foreach (var (key, message) in messages)
                    yield return new Found(key, message, channel, address, []);
            }
        }
    }

    /// <summary>Messages of a "messages" map (3.0 channel) or list of refs (operation / oneOf), keyed by their name.</summary>
    private static IEnumerable<(string Key, JsonObject Message)> Messages(JsonObject root, JsonNode? node)
    {
        var entries = node switch
        {
            JsonObject map => map.Select(p => (Fallback: p.Key, Node: p.Value)),
            JsonArray list => list.Select(n => (Fallback: (string)null!, Node: n)),
            _ => Enumerable.Empty<(string Fallback, JsonNode? Node)>(),
        };
        foreach (var (fallback, entry) in entries)
        {
            var reference = Str(entry?["$ref"]);
            if (Deref(root, entry) is not JsonObject message)
                continue;
            var key = Str(message["name"]) is { } name && IsIdentifier(name) ? name
                : reference is not null ? reference[(reference.LastIndexOf('/') + 1)..]
                : fallback ?? Str(message["name"]) ?? Str(message["title"])
                ?? throw new InvalidOperationException("a message has no name, key or $ref.");
            yield return (Identifier(key), message);
        }
    }

    private static ContractDescriptor Describe(JsonObject root, string document, string key, JsonObject message,
        JsonObject? channel, string? address, IReadOnlyList<(string Key, JsonObject Message)> replies)
    {
        var x = message[Extension] as JsonObject;
        var formatName = Str(x?["format"]) ?? "raw";
        var format = formatName.ToLowerInvariant() switch
        {
            "raw" or "json" => MessageFormat.Raw,
            "masstransit" => MessageFormat.MassTransit,
            _ => throw new InvalidOperationException($"message {key}: {Extension}.format must be raw or masstransit, not '{formatName}'."),
        };
        var urn = Str(x?["urn"]) ?? (format == MessageFormat.MassTransit
            ? throw new InvalidOperationException($"message {key}: MassTransit messages need {Extension}.urn (urn:message:Namespace:Type).")
            : Str(message["name"]) ?? key);

        var amqp = channel?["bindings"]?["amqp"] as JsonObject;
        string exchange, routingKey;
        if (Str(amqp?["is"]) == "queue")
        {
            exchange = "";
            routingKey = Str(amqp?["queue"]?["name"]) ?? address ?? "";
        }
        else
        {
            exchange = Str(amqp?["exchange"]?["name"])
                       ?? (format == MessageFormat.MassTransit && urn.StartsWith("urn:message:", StringComparison.OrdinalIgnoreCase)
                           ? urn["urn:message:".Length..] : "");
            routingKey = format == MessageFormat.MassTransit && Str(amqp?["exchange"]?["name"]) is null ? "" : address ?? "";
        }

        var version = x?["version"] is JsonValue v && v.TryGetValue<long>(out var number) ? (int)number
            : x?["version"] is JsonValue s && int.TryParse(s.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed
            : 1;
        var description = Str(message["description"]) ?? Str(message["summary"]) ?? Str(message["title"]) ?? key;

        var responses = replies.Select(r => new ResponseDescriptor(typeof(JsonElement), r.Key,
                Str((r.Message[Extension] as JsonObject)?["urn"]) ?? Str(r.Message["name"]) ?? r.Key)
            {
                Schema = Payload(root, r.Message),
            })
            .DistinctBy(r => r.Name)
            .ToArray();

        return ContractRegistry.FromSchema($"{document}.{key}", key, Str(x?["toolName"]), urn, exchange, routingKey, format,
            version, description, Payload(root, message), responses);
    }

    private static JsonNode Payload(JsonObject root, JsonObject message) =>
        SchemaInliner.Inline(message["payload"], root) ?? new JsonObject();

    /// <summary>Follows $refs (a channel's message entry is often itself a $ref to components).</summary>
    private static JsonNode? Deref(JsonObject root, JsonNode? node)
    {
        for (var hops = 0; hops < 16 && node is JsonObject o && Str(o["$ref"]) is { } reference; hops++)
            node = SchemaInliner.Resolve(root, reference) ?? throw new InvalidOperationException($"$ref '{reference}' cannot be resolved.");
        return node;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    private static bool IsIdentifier(string name) =>
        name.Length is > 0 and <= 100 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>"stock.adjusted" / "stock-adjusted" → StockAdjusted; identifiers stay as they are.</summary>
    public static string Identifier(string name)
    {
        if (IsIdentifier(name))
            return name;
        var parts = System.Text.RegularExpressions.Regex.Split(name, "[^A-Za-z0-9]+").Where(p => p.Length > 0);
        var joined = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return joined.Length == 0 ? "Message" : char.IsAsciiDigit(joined[0]) ? "M" + joined : joined;
    }
}
