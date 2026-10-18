// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
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
        var maxChars = options.Value.MaxPayloadChars;
        var headers = props.Headers;
        var envelope = MassTransitEnvelope.TryParse(body);
        var target = RepublishTargetResolver.Resolve(queue, headers);

        JsonElement? payload = null;
        string? rawBody = null;
        var truncated = false;

        if (body.Length > maxChars)
        {
            // Display only: republishing always uses the original bytes.
            rawBody = Encoding.UTF8.GetString(body[..maxChars]);
            truncated = true;
        }
        else if (envelope is not null)
        {
            payload = envelope.Message;
        }
        else if (TryParseJson(body) is { } json)
        {
            payload = json;
        }
        else
        {
            rawBody = Encoding.UTF8.GetString(body);
        }

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
                             ? DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime).UtcDateTime
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
            Headers: FaultHeaders.OtherHeadersForDisplay(headers));
    }

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
        Validate(contract.Type, json, jsonOptions);

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

    private static JsonElement? TryParseJson(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            return JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
