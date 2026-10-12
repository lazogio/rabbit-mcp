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
    Responder RespondedBy);

/// <summary>Classifies a reply: a declared response (validated), an unexpected type, or a MassTransit Fault.</summary>
public static class ReplyInterpreter
{
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

        var errors = MessageInspector.Validate(response.Type, message, MassTransitJson.Options);
        return new ReplyInterpretation(
            "responded", response.Name, message,
            new ContractCheck(response.Name, request.Version, errors.Count == 0, errors),
            null, responder);
    }

    public static RequestFault? ParseFault(JsonElement? fault)
    {
        if (fault is not { ValueKind: JsonValueKind.Object } f)
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
