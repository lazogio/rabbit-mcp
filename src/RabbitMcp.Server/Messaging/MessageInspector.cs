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

        return new PeekedMessage(
            Index: index,
            MessageId: props.MessageId ?? envelope?.MessageId,
            CorrelationId: props.CorrelationId ?? envelope?.CorrelationId,
            Redelivered: redelivered,
            ContentType: props.ContentType,
            MessageTypes: envelope?.MessageType,
            SentTimeUtc: envelope?.SentTime,
            Contract: CheckContract(envelope),
            Fault: FaultHeaders.Parse(headers),
            RepublishTarget: RepublishTargetResolver.Resolve(queue, headers)?.Exchange,
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

    /// <summary>Deserializes JSON into the contract type and runs its validation rules.</summary>
    public static IReadOnlyList<string> Validate(ContractDescriptor contract, JsonElement json, JsonSerializerOptions jsonOptions)
    {
        object? instance;
        try
        {
            instance = json.Deserialize(contract.Type, jsonOptions);
        }
        catch (JsonException ex)
        {
            return [$"{(string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "message" : ex.Path.TrimStart('$', '.'))}: {ex.Message}"];
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
