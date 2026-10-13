// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RabbitMcp.Server.Messaging;

/// <summary>JSON settings for MCP tool arguments and results (what the model sees).</summary>
public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = Create();

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
/// (camelCase, case-insensitive reads, enums as numbers).
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
        o.MakeReadOnly();
        return o;
    }
}
