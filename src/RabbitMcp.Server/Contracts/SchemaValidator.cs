// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// JSON Schema validation for contracts loaded from AsyncAPI files — the keywords message schemas use in practice:
/// type (also arrays and OpenAPI's nullable), enum, const, string length/pattern/format (email, uuid, date-time, date,
/// uri), numeric bounds and multipleOf, required/properties/additionalProperties, items and array bounds,
/// allOf/anyOf/oneOf/not. $ref must be inlined first (<see cref="SchemaInliner"/>); a remaining $ref is not checked.
/// Error paths look like the C# contract errors: <c>lines[0].quantity: must be at least 1.</c>
/// </summary>
public static class SchemaValidator
{
    private const int MaxDepth = 64;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Email = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant);

    /// <param name="numericStrings">
    /// Accept numeric strings where a number is expected — MassTransit writes decimals as strings.
    /// </param>
    public static IReadOnlyList<string> Validate(JsonNode? schema, JsonElement instance, bool numericStrings = false)
    {
        var errors = new List<string>();
        Check(schema, instance, "", errors, numericStrings, 0);
        return errors;
    }

    private static void Check(JsonNode? schema, JsonElement value, string path, List<string> errors, bool numericStrings, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (schema is JsonValue literal && literal.TryGetValue<bool>(out var allowed))
        {
            if (!allowed)
                errors.Add($"{Show(path)}: is not allowed.");
            return;
        }
        if (schema is not JsonObject s)
            return;

        if (!TypeMatches(s, value, numericStrings, out var expected))
        {
            errors.Add($"{Show(path)}: must be {expected}, not {Describe(value)}.");
            return;
        }

        if (s["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, ToNode(value))))
            errors.Add($"{Show(path)}: must be one of {string.Join(", ", options.Select(o => o?.ToJsonString() ?? "null"))}.");
        if (s.TryGetPropertyValue("const", out var constant) && !JsonNode.DeepEquals(constant, ToNode(value)))
            errors.Add($"{Show(path)}: must be {constant?.ToJsonString() ?? "null"}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                CheckString(s, value.GetString()!, path, errors, numericStrings);
                break;
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                CheckNumber(s, number, path, errors);
                break;
            case JsonValueKind.Object:
                CheckObject(s, value, path, errors, numericStrings, depth);
                break;
            case JsonValueKind.Array:
                CheckArray(s, value, path, errors, numericStrings, depth);
                break;
        }

        if (s["allOf"] is JsonArray all)
            foreach (var part in all)
                Check(part, value, path, errors, numericStrings, depth + 1);
        if (s["anyOf"] is JsonArray any && !any.Any(part => Validate(part, value, numericStrings).Count == 0))
            errors.Add($"{Show(path)}: matches none of the allowed shapes (anyOf).");
        if (s["oneOf"] is JsonArray one && one.Count(part => Validate(part, value, numericStrings).Count == 0) != 1)
            errors.Add($"{Show(path)}: must match exactly one of the allowed shapes (oneOf).");
        if (s["not"] is { } not && Validate(not, value, numericStrings).Count == 0)
            errors.Add($"{Show(path)}: matches a shape that is not allowed (not).");
    }

    private static bool TypeMatches(JsonObject s, JsonElement value, bool numericStrings, out string expected)
    {
        var types = s["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => new List<string> { t },
            JsonArray a => a.Select(x => x?.GetValue<string>()).OfType<string>().ToList(),
            _ => new List<string>(),
        };
        if (s["nullable"] is JsonValue n && n.TryGetValue<bool>(out var nullable) && nullable)
            types.Add("null");
        expected = string.Join(" or ", types);
        if (types.Count == 0)
            return true;

        return types.Any(type => type switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "string" => value.ValueKind == JsonValueKind.String,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "number" => value.ValueKind == JsonValueKind.Number || (numericStrings && NumericString(value) is not null),
            "integer" => (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && d == decimal.Truncate(d))
                         || (numericStrings && NumericString(value) is { } x && x == decimal.Truncate(x)),
            _ => true,
        });
    }

    private static decimal? NumericString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static void CheckString(JsonObject s, string text, string path, List<string> errors, bool numericStrings)
    {
        // A numeric string standing in for a number gets the number's checks.
        if (numericStrings && Is(s, "number", "integer") && !Is(s, "string") &&
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            CheckNumber(s, number, path, errors);
            return;
        }

        var length = text.EnumerateRunes().Count();
        if (Int(s, "minLength") is { } min && length < min)
            errors.Add($"{Show(path)}: must be at least {min} characters long.");
        if (Int(s, "maxLength") is { } max && length > max)
            errors.Add($"{Show(path)}: must be at most {max} characters long.");
        if (s["pattern"] is JsonValue p && p.TryGetValue<string>(out var pattern))
        {
            try
            {
                if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, PatternTimeout))
                    errors.Add($"{Show(path)}: must match the pattern {pattern}.");
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                errors.Add($"{Show(path)}: the pattern {pattern} cannot be checked.");
            }
        }
        if (s["format"] is JsonValue f && f.TryGetValue<string>(out var format) && !FormatMatches(format, text))
            errors.Add($"{Show(path)}: must be a valid {format}.");
    }

    private static bool FormatMatches(string format, string text) => format switch
    {
        "email" => Email.IsMatch(text),
        "uuid" => Guid.TryParseExact(text, "D", out _),
        "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text.Contains('T', StringComparison.OrdinalIgnoreCase),
        "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
        _ => true, // unknown formats are annotations
    };

    private static void CheckNumber(JsonObject s, decimal number, string path, List<string> errors)
    {
        if (Dec(s, "minimum") is { } min && number < min)
            errors.Add($"{Show(path)}: must be at least {min.ToString(CultureInfo.InvariantCulture)}.");
        if (Dec(s, "maximum") is { } max && number > max)
            errors.Add($"{Show(path)}: must be at most {max.ToString(CultureInfo.InvariantCulture)}.");
        if (Dec(s, "exclusiveMinimum") is { } emin && number <= emin)
            errors.Add($"{Show(path)}: must be greater than {emin.ToString(CultureInfo.InvariantCulture)}.");
        if (Dec(s, "exclusiveMaximum") is { } emax && number >= emax)
            errors.Add($"{Show(path)}: must be less than {emax.ToString(CultureInfo.InvariantCulture)}.");
        if (Dec(s, "multipleOf") is { } step && step > 0 && number % step != 0)
            errors.Add($"{Show(path)}: must be a multiple of {step.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static void CheckObject(JsonObject s, JsonElement value, string path, List<string> errors, bool numericStrings, int depth)
    {
        var properties = s["properties"] as JsonObject;
        if (s["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
            {
                if (!value.TryGetProperty(name, out _))
                    errors.Add($"{Join(path, name)}: is required.");
            }
        }

        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (properties is not null && properties.TryGetPropertyValue(property.Name, out var propertySchema))
                Check(propertySchema, property.Value, Join(path, property.Name), errors, numericStrings, depth + 1);
            else if (s.TryGetPropertyValue("additionalProperties", out var additional) && additional is not null)
                Check(additional, property.Value, Join(path, property.Name), errors, numericStrings, depth + 1);
        }
        if (Int(s, "minProperties") is { } min && count < min)
            errors.Add($"{Show(path)}: must have at least {min} properties.");
        if (Int(s, "maxProperties") is { } max && count > max)
            errors.Add($"{Show(path)}: must have at most {max} properties.");
    }

    private static void CheckArray(JsonObject s, JsonElement value, string path, List<string> errors, bool numericStrings, int depth)
    {
        var length = value.GetArrayLength();
        if (Int(s, "minItems") is { } min && length < min)
            errors.Add($"{Show(path)}: must have at least {min} items.");
        if (Int(s, "maxItems") is { } max && length > max)
            errors.Add($"{Show(path)}: must have at most {max} items.");
        if (s["uniqueItems"] is JsonValue u && u.TryGetValue<bool>(out var unique) && unique &&
            value.EnumerateArray().Select(e => ToNode(e)?.ToJsonString()).Distinct().Count() != length)
            errors.Add($"{Show(path)}: items must be unique.");
        if (s["items"] is JsonObject items)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
                Check(items, item, $"{path}[{i++}]", errors, numericStrings, depth + 1);
        }
    }

    private static bool Is(JsonObject s, params string[] wanted) => s["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var t) => wanted.Contains(t),
        JsonArray a => a.Any(x => x is JsonValue xv && xv.TryGetValue<string>(out var t) && wanted.Contains(t)),
        _ => false,
    };

    private static int? Int(JsonObject s, string key) => Dec(s, key) is { } d ? (int)Math.Min(d, int.MaxValue) : null;

    private static decimal? Dec(JsonObject s, string key) =>
        s[key] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d
        : s[key] is JsonValue w && w.TryGetValue<double>(out var x) && Math.Abs(x) < 7.9e28 ? (decimal)x
        : null;

    private static JsonNode? ToNode(JsonElement value) => JsonNode.Parse(value.GetRawText());

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Number => "a number",
        JsonValueKind.String => "a string",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        _ => "null",
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Show(string path) => path.Length == 0 ? "message" : path;
}

/// <summary>
/// Replaces local <c>$ref</c>s ("#/components/schemas/Order", "#/$defs/Line") with the schemas they point to, so a
/// contract's schema stands alone (tool inputSchema, contract:// resources, validation). Recursive references stay as
/// <c>$ref</c> after the first level.
/// </summary>
public static class SchemaInliner
{
    public static JsonNode? Inline(JsonNode? node, JsonNode root) => Inline(node, root, [], 0);

    private static JsonNode? Inline(JsonNode? node, JsonNode root, HashSet<string> active, int depth)
    {
        if (depth > 64)
            return node?.DeepClone();
        switch (node)
        {
            case JsonObject obj when obj["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference):
                if (active.Contains(reference))
                    return new JsonObject { ["$ref"] = reference }; // recursion: leave it
                var target = Resolve(root, reference)
                             ?? throw new InvalidOperationException($"$ref '{reference}' cannot be resolved (only local '#/...' references are supported).");
                active.Add(reference);
                var inlined = Inline(target, root, active, depth + 1);
                active.Remove(reference);
                return inlined;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                    copy[key] = Inline(value, root, active, depth + 1);
                return copy;
            case JsonArray array:
                return new JsonArray([.. array.Select(item => Inline(item, root, active, depth + 1))]);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>A local JSON pointer reference: "#/components/messages/Order~1Line" (RFC 6901 escapes).</summary>
    public static JsonNode? Resolve(JsonNode root, string reference)
    {
        if (!reference.StartsWith('#'))
            return null;
        JsonNode? current = root;
        foreach (var raw in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (current is null)
                return null;
        }
        return current;
    }
}
