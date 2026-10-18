// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

/// <summary>Field-by-field differences between two JSON values (for showing a correction to the person).</summary>
public static class JsonDiff
{
    public const int MaxChanges = 50;

    public static IReadOnlyList<FieldChange> Compare(JsonElement before, JsonElement after)
    {
        var changes = new List<FieldChange>();
        Walk("", before, after, changes);
        return changes;
    }

    private static void Walk(string path, JsonElement? before, JsonElement? after, List<FieldChange> changes)
    {
        if (changes.Count >= MaxChanges)
            return;

        if (before is { ValueKind: JsonValueKind.Object } b && after is { ValueKind: JsonValueKind.Object } a)
        {
            var names = b.EnumerateObject().Select(p => p.Name)
                .Concat(a.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                Walk(path.Length == 0 ? name : $"{path}.{name}",
                    b.TryGetProperty(name, out var bv) ? bv : null,
                    a.TryGetProperty(name, out var av) ? av : null, changes);
            }
            return;
        }

        if (before is { ValueKind: JsonValueKind.Array } ba && after is { ValueKind: JsonValueKind.Array } aa
            && ba.GetArrayLength() == aa.GetArrayLength())
        {
            for (var i = 0; i < ba.GetArrayLength(); i++)
                Walk($"{path}[{i}]", ba[i], aa[i], changes);
            return;
        }

        if (!SameValue(before, after))
            changes.Add(new FieldChange(path.Length == 0 ? "(message)" : path, before?.Clone(), after?.Clone()));
    }

    private static bool SameValue(JsonElement? a, JsonElement? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Value.ValueKind == JsonValueKind.Number && b.Value.ValueKind == JsonValueKind.Number
            && a.Value.TryGetDecimal(out var x) && b.Value.TryGetDecimal(out var y))
            return x == y; // 1.50 and 1.5 are the same number
        return a.Value.ValueKind == b.Value.ValueKind && a.Value.GetRawText() == b.Value.GetRawText();
    }
}
