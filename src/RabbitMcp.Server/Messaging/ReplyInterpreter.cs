// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

public sealed record ReplyInterpretation(
    string Status,
    string? ResponseType,
    JsonElement? Response,
    ContractCheck? ResponseCheck,
    RequestFault? Fault,
    Responder RespondedBy)
{
    /// <summary>Why the reply could not be matched to a declared response, when that is not obvious.</summary>
    public string? Note { get; init; }
}

/// <summary>Classifies a reply: a declared response (validated), an unexpected type, or a MassTransit Fault.</summary>
public static class ReplyInterpreter
{
    public static ReplyInterpretation Interpret(ContractDescriptor request, ReceivedReply reply) =>
        reply.Envelope is { } envelope ? Interpret(request, envelope) : InterpretRaw(request, reply.Raw!);

    /// <summary>
    /// A raw (RPC) reply: the reply type is named by the AMQP type property (a response name or URN); without it the
    /// only declared response is assumed, and with several it stays unknown — the body is never guessed from.
    /// Raw services have no fault convention: errors are declared response types.
    /// </summary>
    public static ReplyInterpretation InterpretRaw(ContractDescriptor request, RawReply reply)
    {
        var responder = new Responder(Address: null, MachineName: null, ProcessName: reply.AppId, Assembly: null);
        var hasType = !string.IsNullOrWhiteSpace(reply.Type);
        var response = hasType
            ? request.FindResponseByType(reply.Type)
            : request.Responses.Count == 1 ? request.Responses[0] : null;

        if (reply.Body is not { } body)
        {
            return new ReplyInterpretation("responded", response?.Name ?? reply.Type,
                JsonSerializer.SerializeToElement(reply.Text ?? ""), null, null, responder)
            {
                Note = $"The reply is not JSON ({reply.Bytes} bytes); Response holds it as text.",
            };
        }

        if (response is null)
        {
            var declared = string.Join(", ", request.Responses.Select(r => r.Name));
            return new ReplyInterpretation("responded", hasType ? reply.Type : null, body, null, null, responder)
            {
                Note = hasType
                    ? $"Reply type '{reply.Type}' is not one of the declared responses ({declared})."
                    : $"The reply has no AMQP type property, so it cannot be matched to one of {declared}. " +
                      "The replier should set type to the response name or URN.",
            };
        }

        var errors = MessageInspector.Validate(response, body, MassTransitJson.Options, numericStrings: false);
        return new ReplyInterpretation(
            "responded", response.Name, body,
            new ContractCheck(response.Name, Version: null, errors.Count == 0, errors),
            null, responder);
    }

    /// <summary>MassTransit sends Fault&lt;TRequest&gt; (urn:message:MassTransit:Fault[[...]]) to the response address.</summary>
    public const string FaultUrnPrefix = "urn:message:MassTransit:Fault";

    public static ReplyInterpretation Interpret(ContractDescriptor request, MassTransitEnvelope reply)
    {
        var types = reply.MessageType ?? Array.Empty<string>();
        var responder = new Responder(
            reply.SourceAddress, reply.Host?.MachineName, reply.Host?.ProcessName, reply.Host?.Assembly);

        if (types.Any(t => t.StartsWith(FaultUrnPrefix, StringComparison.OrdinalIgnoreCase)))
            return new ReplyInterpretation("faulted", "Fault", reply.Message, null, ParseFault(reply.Message), responder);

        var response = request.FindResponse(types);
        if (response is null || reply.Message is not { } message)
            return new ReplyInterpretation("responded", types.FirstOrDefault(), reply.Message, null, null, responder);

        var errors = MessageInspector.Validate(response, message, MassTransitJson.Options, numericStrings: true);
        return new ReplyInterpretation(
            "responded", response.Name, message,
            new ContractCheck(response.Name, Version: null, errors.Count == 0, errors),
            null, responder);
    }

    public static RequestFault? ParseFault(JsonElement? fault)
    {
        if (fault is not { ValueKind: JsonValueKind.Object } f || !MessageInspector.CanShow(f))
            return null;

        var exceptions = new List<ExceptionSummary>();
        if (Get(f, "exceptions") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var ex in list.EnumerateArray())
            {
                exceptions.Add(new ExceptionSummary(
                    GetString(ex, "exceptionType"),
                    GetString(ex, "message"),
                    FaultHeaders.TrimStackTrace(GetString(ex, "stackTrace"))));
            }
        }

        return new RequestFault(GetString(f, "faultedMessageId"), GetString(f, "timestamp"), exceptions);
    }

    private static JsonElement? Get(JsonElement obj, string camelName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        if (obj.TryGetProperty(camelName, out var value))
            return value;
        // Tolerate PascalCase serializers.
        return obj.TryGetProperty(char.ToUpperInvariant(camelName[0]) + camelName[1..], out value) ? value : null;
    }

    private static string? GetString(JsonElement obj, string camelName) =>
        Get(obj, camelName) is { } v && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;
}
