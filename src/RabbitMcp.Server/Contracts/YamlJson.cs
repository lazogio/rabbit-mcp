// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// Reads JSON or YAML into a <see cref="JsonNode"/>. YAML plain scalars follow the YAML 1.2 core schema (true/false,
/// null/~, integers, floats); quoted scalars stay strings — so "minimum: 1" is a number and "version: '1.0'" a string.
/// </summary>
public static partial class YamlJson
{
    public static JsonNode? Parse(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return JsonNode.Parse(trimmed, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"YAML error at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode, 0);
    }

    private static JsonNode? Convert(YamlNode node, int depth)
    {
        if (depth > 128)
            throw new InvalidOperationException("YAML is nested too deeply.");
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                    obj[(key as YamlScalarNode)?.Value ?? key.ToString()] = Convert(value, depth + 1);
                return obj;
            case YamlSequenceNode sequence:
                return new JsonArray([.. sequence.Children.Select(child => Convert(child, depth + 1))]);
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null; // aliases are resolved by YamlStream
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any))
            return JsonValue.Create(value);

        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }
        if (IntegerRegex().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (FloatRegex().IsMatch(value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(value);
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex("^[-+]?(\\.[0-9]+|[0-9]+(\\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();
}
