// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Contracts;

/// <summary>breaking: existing publishers/consumers stop working · warning: may break some · info: compatible.</summary>
public sealed record ContractChange(string Severity, string Contract, string Path, string Message);

/// <summary>
/// Compares two versions of a contracts assembly on the wire, the way consumers see them: format, message type URNs,
/// exchange and routing key, and the JSON shape (camelCase, as serialized). DataAnnotations rules are not compared —
/// only the structure.
/// </summary>
public static class ContractDiff
{
    public const string Breaking = "breaking";
    public const string Warning = "warning";
    public const string Info = "info";

    private static readonly JsonSerializerOptions SchemaJson = CreateSchemaOptions();

    /// <summary>MassTransit naming, but without custom converters: they would turn a type's schema into "any".</summary>
    private static JsonSerializerOptions CreateSchemaOptions()
    {
        var options = new JsonSerializerOptions(MassTransitJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Clear();
        return options;
    }

    private static readonly JsonSchemaExporterOptions ExporterOptions = new() { TreatNullObliviousAsNonNullable = true };

    public static IReadOnlyList<ContractChange> Compare(ContractRegistry before, ContractRegistry after)
    {
        var changes = new List<ContractChange>();
        var matched = new HashSet<ContractDescriptor>();

        foreach (var old in before.Contracts)
        {
            var current = after.Contracts.FirstOrDefault(c => Same(c.MessageTypeUrn, old.MessageTypeUrn))
                          ?? after.Contracts.FirstOrDefault(c => c.Name == old.Name);
            if (current is null)
            {
                changes.Add(new(Breaking, old.Name, "", $"Contract removed ({old.MessageTypeUrn}): its messages are no longer understood."));
                continue;
            }
            matched.Add(current);

            var own = new List<ContractChange>();
            if (!Same(current.MessageTypeUrn, old.MessageTypeUrn))
                own.Add(new(Breaking, old.Name, "", $"Message type changed from {old.MessageTypeUrn} to {current.MessageTypeUrn} " +
                                                    "(namespace or name): consumers of the old type no longer receive it."));
            if (current.Format != old.Format)
                own.Add(new(Breaking, old.Name, "", $"Wire format changed from {old.FormatName} to {current.FormatName}: " +
                                                    "existing consumers cannot read the new messages."));
            if (current.Exchange != old.Exchange)
                own.Add(new(Breaking, old.Name, "", $"Exchange changed from '{old.Exchange}' to '{current.Exchange}'."));
            if (current.RoutingKey != old.RoutingKey)
                own.Add(new(Breaking, old.Name, "", $"Routing key changed from '{old.RoutingKey}' to '{current.RoutingKey}': " +
                                                    "queues bound for the old key no longer receive it."));

            CompareSchemas(old.Name, "", Schema(old.Type), Schema(current.Type), own);
            CompareResponses(old, current, own);

            if (current.Version < old.Version)
                own.Add(new(Warning, old.Name, "", $"Version went down from {old.Version} to {current.Version}."));
            else if (own.Any(c => c.Severity == Breaking) && current.Version == old.Version)
                own.Add(new(Warning, old.Name, "", $"Breaking change without a version bump (still v{old.Version})."));

            changes.AddRange(own);
        }

        foreach (var added in after.Contracts.Where(c => !matched.Contains(c)))
            changes.Add(new(Info, added.Name, "", $"New contract ({added.MessageTypeUrn})."));

        return changes;
    }

    private static void CompareResponses(ContractDescriptor old, ContractDescriptor current, List<ContractChange> changes)
    {
        foreach (var r in old.Responses)
        {
            var match = current.Responses.FirstOrDefault(x => Same(x.MessageTypeUrn, r.MessageTypeUrn));
            if (match is null)
                changes.Add(new(Breaking, old.Name, $"→{r.Name}", $"Reply type {r.Name} removed: requesters waiting for it get no answer."));
            else
                CompareSchemas(old.Name, $"→{r.Name}", Schema(r.Type), Schema(match.Type), changes);
        }
        foreach (var r in current.Responses.Where(x => !old.Responses.Any(o => Same(o.MessageTypeUrn, x.MessageTypeUrn))))
            changes.Add(new(Warning, old.Name, $"→{r.Name}",
                $"New reply type {r.Name}: existing requesters do not expect it and will time out when it is sent."));
    }

    public static JsonNode Schema(Type type) => SchemaJson.GetJsonSchemaAsNode(type, ExporterOptions);

    /// <summary>Compares two JSON schemas produced by <see cref="Schema"/>, recursively.</summary>
    public static void CompareSchemas(string contract, string path, JsonNode? old, JsonNode? current, List<ContractChange> changes)
    {
        if (old is not JsonObject o || current is not JsonObject n)
            return;

        var (oldTypes, oldNull) = Types(o);
        var (newTypes, newNull) = Types(n);
        if (oldTypes.Count > 0 && newTypes.Count > 0 && !oldTypes.SetEquals(newTypes))
        {
            changes.Add(new(Breaking, contract, Show(path), $"Type changed from {string.Join("|", oldTypes)} to {string.Join("|", newTypes)}."));
            return;
        }
        if (!oldNull && newNull)
            changes.Add(new(Warning, contract, Show(path), "May now be null: consumers that assume a value can fail."));

        if (o["properties"] is JsonObject oldProps && n["properties"] is JsonObject newProps)
        {
            var oldRequired = Required(o);
            var newRequired = Required(n);
            foreach (var (name, oldProp) in oldProps)
            {
                var propPath = path.Length == 0 ? name : $"{path}.{name}";
                if (!newProps.TryGetPropertyValue(name, out var newProp))
                {
                    changes.Add(oldRequired.Contains(name)
                        ? new(Breaking, contract, propPath, "Required property removed: consumers that need it fail.")
                        : new(Warning, contract, propPath, "Property removed: consumers still reading it get null."));
                    continue;
                }
                if (!oldRequired.Contains(name) && newRequired.Contains(name))
                    changes.Add(new(Breaking, contract, propPath, "Became required: existing publishers do not send it."));
                else if (oldRequired.Contains(name) && !newRequired.Contains(name))
                    changes.Add(new(Warning, contract, propPath, "No longer required: consumers may now receive it missing."));
                CompareSchemas(contract, propPath, oldProp, newProp, changes);
            }
            foreach (var (name, _) in newProps.Where(p => !oldProps.ContainsKey(p.Key)))
            {
                var propPath = path.Length == 0 ? name : $"{path}.{name}";
                changes.Add(newRequired.Contains(name)
                    ? new(Breaking, contract, propPath, "New required property: existing publishers do not send it.")
                    : new(Info, contract, propPath, "New optional property."));
            }
        }

        CompareSchemas(contract, path + "[]", o["items"], n["items"], changes);

        if (o["enum"] is JsonArray oldEnum && n["enum"] is JsonArray newEnum)
        {
            var before = oldEnum.Select(v => v?.ToJsonString()).ToHashSet();
            var after = newEnum.Select(v => v?.ToJsonString()).ToHashSet();
            foreach (var removed in before.Except(after))
                changes.Add(new(Breaking, contract, Show(path), $"Enum value {removed} removed."));
            foreach (var added in after.Except(before))
                changes.Add(new(Warning, contract, Show(path), $"Enum value {added} added: older consumers do not know it."));
        }
    }

    private static (HashSet<string> Types, bool Nullable) Types(JsonObject node)
    {
        var all = node["type"] switch
        {
            JsonValue v => new HashSet<string> { v.GetValue<string>() },
            JsonArray a => a.Select(x => x!.GetValue<string>()).ToHashSet(),
            _ => new HashSet<string>(),
        };
        var nullable = all.Remove("null");
        // integer is a subset of number: treat as the same kind so int -> long or decimal is not "breaking" by itself.
        return (all.Select(t => t == "integer" ? "number" : t).ToHashSet(), nullable);
    }

    private static HashSet<string> Required(JsonObject node) =>
        node["required"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToHashSet() : [];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Show(string path) => path.Length == 0 ? "(message)" : path;
}
