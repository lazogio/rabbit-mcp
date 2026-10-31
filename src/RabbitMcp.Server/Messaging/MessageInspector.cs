// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;

namespace RabbitMcp.Server.Messaging;

/// <summary>Turns a raw AMQP delivery into a <see cref="PeekedMessage"/> for the model.</summary>
public sealed class MessageInspector(ContractRegistry contracts, IOptions<RabbitMcpOptions> options)
{
    public PeekedMessage Inspect(int index, string queue, BasicGetResult delivery) =>
        Inspect(index, queue, delivery.BasicProperties, delivery.Body.Span, delivery.Redelivered);

    public PeekedMessage Inspect(
        int index, string queue, IReadOnlyBasicProperties props, ReadOnlySpan<byte> body, bool redelivered)
    {
        try
        {
            return Interpret(index, queue, props, body, redelivered);
        }
        catch (Exception ex)
        {
            // Whoever can publish to a queue decides what is in it: one message must never hide the others.
            var shortened = Shortened(body, options.Value.MaxPayloadChars);
            return new PeekedMessage(index, props.MessageId, props.CorrelationId, redelivered, props.ContentType, null, null, null,
                null, null, null, null, null, shortened ?? Encoding.UTF8.GetString(body), shortened is not null, body.Length,
                new Dictionary<string, string?>())
            {
                Problem = $"The message could not be interpreted ({ex.GetType().Name}); only its body is shown.",
            };
        }
    }

    private PeekedMessage Interpret(
        int index, string queue, IReadOnlyBasicProperties props, ReadOnlySpan<byte> body, bool redelivered)
    {
        var maxChars = options.Value.MaxPayloadChars;
        var headers = props.Headers;
        var envelope = MassTransitEnvelope.TryParse(body);
        var target = RepublishTargetResolver.Resolve(queue, headers);

        JsonElement? payload = null;
        string? rawBody = null;
        string? problem = null;
        var truncated = false;

        if (Shortened(body, maxChars) is { } shortened)
        {
            // Display only: republishing always uses the original bytes.
            rawBody = shortened;
            truncated = true;
        }
        else if ((envelope is not null ? envelope.Message : TryParseJson(body)) is { ValueKind: not JsonValueKind.Null } json)
        {
            if (CanShow(json))
                payload = json;
            else
                problem = "Its JSON has broken text or is nested too deeply for a tool result; RawBody holds it as text.";
        }

        if (payload is null && !truncated)
            rawBody = Encoding.UTF8.GetString(body);

        // Raw (plain JSON) messages name their type in the AMQP type property instead of the envelope.
        var rawType = envelope is null && props.IsTypePresent() && !string.IsNullOrWhiteSpace(props.Type) ? props.Type : null;

        return new PeekedMessage(
            Index: index,
            MessageId: props.MessageId ?? envelope?.MessageId,
            CorrelationId: props.CorrelationId ?? envelope?.CorrelationId,
            Redelivered: redelivered,
            ContentType: props.ContentType,
            MessageTypes: envelope?.MessageType ?? (rawType is null ? null : new[] { rawType }),
            SentTimeUtc: envelope?.SentTime
                         ?? (props.IsTimestampPresent() && props.Timestamp.UnixTime > 0
                             ? FaultHeaders.TimeOf(props.Timestamp.UnixTime)?.UtcDateTime
                             : null),
            Contract: envelope is not null ? CheckContract(envelope) : CheckRaw(rawType, payload),
            Fault: FaultHeaders.Parse(headers),
            DeadLettered: FaultHeaders.ParseDeaths(headers),
            RepublishTarget: target?.Display,
            RepublishWarning: target?.Conflict,
            Payload: payload,
            RawBody: rawBody,
            Truncated: truncated,
            BodyBytes: body.Length,
            Headers: FaultHeaders.OtherHeadersForDisplay(headers))
        {
            Problem = problem,
        };
    }

    /// <summary>Levels of nesting a message may have in a tool result: a result wraps it in about ten, and JSON serializers stop at 64.</summary>
    public const int MaxShownDepth = 50;

    /// <summary>
    /// Whether a tool result can carry this JSON. A reader accepts text that nothing can write back (a lone surrogate,
    /// invalid UTF-8), and a value of the deepest kind no longer fits inside a result; either would fail the whole call,
    /// after the tool did its work.
    /// </summary>
    public static bool CanShow(JsonElement json)
    {
        try
        {
            return Walk(json, 1);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        static bool Walk(JsonElement value, int depth)
        {
            if (depth > MaxShownDepth)
                return false;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        _ = property.Name;
                        if (!Walk(property.Value, depth + 1))
                            return false;
                    }
                    return true;
                case JsonValueKind.Array:
                    return value.EnumerateArray().All(item => Walk(item, depth + 1));
                case JsonValueKind.String:
                    _ = value.GetString();
                    return true;
                default:
                    return true;
            }
        }
    }

    /// <summary>A reply for a tool result: itself, or as text when a result cannot carry it as JSON (see <see cref="CanShow"/>).</summary>
    public static JsonElement? Showable(JsonElement? json) =>
        json is { } value && !CanShow(value)
            ? JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8Value(value)))
            : json;

    /// <summary>The contract a delivered message belongs to: by the envelope's messageType, or a raw message's AMQP type.</summary>
    public ContractDescriptor? ContractOf(IReadOnlyBasicProperties props, MassTransitEnvelope? envelope) =>
        envelope is not null
            ? contracts.FindByUrns(envelope.MessageType)
            : props.IsTypePresent() && !string.IsNullOrWhiteSpace(props.Type) ? contracts.FindByUrns([props.Type!]) : null;

    /// <summary>Validates an envelope's message against the contract its messageType names.</summary>
    public ContractCheck? CheckContract(MassTransitEnvelope? envelope)
    {
        var contract = contracts.FindByUrns(envelope?.MessageType);
        if (contract is null || envelope?.Message is not { } message)
            return null;

        var errors = Validate(contract, message, MassTransitJson.Options);
        return new ContractCheck(contract.Name, contract.Version, errors.Count == 0, errors);
    }

    /// <summary>Validates a plain-JSON body against the contract its AMQP type names (raw contracts and their Urn).</summary>
    public ContractCheck? CheckRaw(string? type, JsonElement? body)
    {
        if (type is null || body is not { } json || contracts.FindByUrns([type]) is not { } contract)
            return null;

        var errors = Validate(contract, json, MassTransitJson.Options);
        return new ContractCheck(contract.Name, contract.Version, errors.Count == 0, errors);
    }

    /// <summary>Deserializes JSON into the contract type and runs its validation rules.</summary>
    public static IReadOnlyList<string> Validate(ContractDescriptor contract, JsonElement json, JsonSerializerOptions jsonOptions) =>
        contract.Schema is { } schema
            ? AgainstSchema(schema, json, numericStrings: !contract.IsRaw)
            : Validate(contract.Type, json, jsonOptions);

    /// <summary>A reply against its type, or its JSON Schema for replies declared in AsyncAPI files.</summary>
    public static IReadOnlyList<string> Validate(ResponseDescriptor response, JsonElement json, JsonSerializerOptions jsonOptions,
        bool numericStrings) =>
        response.Schema is { } schema
            ? AgainstSchema(schema, json, numericStrings)
            : Validate(response.Type, json, jsonOptions);

    private static IReadOnlyList<string> AgainstSchema(JsonNode schema, JsonElement json, bool numericStrings) =>
        CanShow(json) ? SchemaValidator.Validate(schema, json, numericStrings) : ["message: has broken text or is nested too deeply."];

    /// <summary>Deserializes JSON into <paramref name="type"/> and runs its validation rules.</summary>
    public static IReadOnlyList<string> Validate(Type type, JsonElement json, JsonSerializerOptions jsonOptions)
    {
        object? instance;
        try
        {
            instance = json.Deserialize(type, jsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"{(string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "message" : ex.Path.TrimStart('$', '.'))}: {ex.Message}"];
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A contract System.Text.Json cannot handle (e.g. an interface-typed property) must not break peeking.
            return [$"message: cannot be read as {type.Name} ({ex.Message})"];
        }

        return ContractValidator.Validate(instance);
    }

    /// <summary>
    /// The first <paramref name="maxChars"/> characters when the body has more, else null. Only the head of the body
    /// is decoded (one UTF-16 character takes at most 3 UTF-8 bytes), and a character is never cut in half.
    /// </summary>
    private static string? Shortened(ReadOnlySpan<byte> body, int maxChars)
    {
        if (body.Length <= maxChars)
            return null;

        var headBytes = (int)Math.Min(body.Length, maxChars * 3L);
        var head = Encoding.UTF8.GetString(body[..headBytes]);
        if (headBytes == body.Length && head.Length <= maxChars)
            return null;

        var end = Math.Min(maxChars, head.Length);
        if (char.IsHighSurrogate(head[end - 1]))
            end--;
        return head[..end];
    }

    /// <summary>The body when all of it is one JSON value; text that only starts like JSON (42 is the answer) is not.</summary>
    public static JsonElement? TryParseJson(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            var json = JsonElement.ParseValue(ref reader); // copies the data
            return reader.Read() ? null : json;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions EnumNames = CreateEnumNames();

    private static JsonSerializerOptions CreateEnumNames()
    {
        var o = new JsonSerializerOptions(MassTransitJson.Options);
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly();
        return o;
    }

    /// <summary>
    /// A message with enum names replaced by numbers. Tool schemas show names, but the wire format has numbers (see
    /// <see cref="MassTransitJson"/>). Everything else stays as given, fields the contract does not define included.
    /// </summary>
    public static JsonElement WithEnumNumbers(ContractDescriptor contract, JsonElement json)
    {
        if (contract.IsSchemaBased)
            return json;

        JsonElement wire;
        try
        {
            wire = JsonSerializer.SerializeToElement(json.Deserialize(contract.Type, EnumNames), contract.Type, MassTransitJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return json; // validation reports what is wrong
        }

        var node = JsonNode.Parse(json.GetRawText());
        NumberEnums(node, wire);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void NumberEnums(JsonNode? given, JsonElement wire)
    {
        switch (given)
        {
            case JsonObject fields when wire.ValueKind == JsonValueKind.Object:
                foreach (var (name, value) in fields.ToList())
                {
                    var known = wire.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                    if (IsEnumName(value, known))
                        fields[name] = JsonNode.Parse(known.GetRawText());
                    else
                        NumberEnums(value, known);
                }
                break;
            case JsonArray items when wire.ValueKind == JsonValueKind.Array:
                for (var i = 0; i < Math.Min(items.Count, wire.GetArrayLength()); i++)
                {
                    if (IsEnumName(items[i], wire[i]))
                        items[i] = JsonNode.Parse(wire[i].GetRawText());
                    else
                        NumberEnums(items[i], wire[i]);
                }
                break;
        }
    }

    /// <summary>A name where the wire has a number. A numeric string is a decimal as MassTransit writes it, and stays.</summary>
    private static bool IsEnumName(JsonNode? given, JsonElement wire) =>
        wire.ValueKind == JsonValueKind.Number && given is JsonValue value && value.TryGetValue<string>(out var text)
        && !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
