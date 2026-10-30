// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace RabbitMcp.Server.Messaging;

/// <summary>JSON settings for MCP tool arguments and results (what the model sees).</summary>
public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Schema generation for everything built from a C# contract, so tools and resources show the same schema.</summary>
    public static AIJsonSchemaCreateOptions SchemaOptions { get; } = new() { TransformSchemaNode = NumericLimits };

    /// <summary>
    /// [Range(typeof(decimal), "0.01", "100")] comes out with its limits as strings, which is not valid JSON Schema
    /// (minimum and maximum must be numbers). The limits are invariant-culture text, see ContractValidator.
    /// </summary>
    private static JsonNode NumericLimits(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (schema is JsonObject keywords)
        {
            foreach (var name in (string[])["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"])
            {
                if (keywords[name] is JsonValue limit && limit.TryGetValue<string>(out var text) &&
                    decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    keywords[name] = number;
            }
        }
        return schema;
    }

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Non-nullable reference properties reject null and `required` members must be present,
            // and both are reflected in the generated input schema.
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly();
        return o;
    }
}

/// <summary>
/// JSON settings compatible with MassTransit's System.Text.Json serializer
/// (camelCase, case-insensitive reads, enums as numbers, decimals read from numbers or strings).
/// </summary>
public static class MassTransitJson
{
    public const string ContentType = "application/vnd.masstransit+json";

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            RespectNullableAnnotations = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        o.Converters.Add(new MassTransitDecimalConverter());
        o.MakeReadOnly();
        return o;
    }
}

/// <summary>
/// MassTransit writes decimals as JSON strings ("1800", its StringDecimalJsonConverter) and reads numbers or strings.
/// Reading both is required, or every MassTransit message with a decimal looks invalid. Written as numbers, which
/// MassTransit and every other JSON consumer read.
/// </summary>
public sealed class MassTransitDecimalConverter : JsonConverter<decimal>
{
    private const NumberStyles Style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var number))
            return number;
        if (reader.TokenType == JsonTokenType.String &&
            decimal.TryParse(reader.GetString(), Style, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new JsonException($"Expected a decimal number or numeric string, found {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
