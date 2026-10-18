// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Envelopes a responder sends back to a MassTransit request client: a reply (what RespondAsync would send)
/// or a Fault&lt;TRequest&gt; (what a throwing consumer would send). Field names follow MassTransit's
/// JsonMessageEnvelope and Fault/ExceptionInfo contracts.
/// </summary>
public static class ResponderEnvelopes
{
    public const string FaultUrn = "urn:message:MassTransit:Fault";
    public const string RejectedExceptionType = "RabbitMcp.RequestRejectedException";

    private const string UrnPrefix = "urn:message:";

    /// <summary>urn:message:MassTransit:Fault[[Ns:Type]] — MassTransit's URN for Fault&lt;Ns.Type&gt;.</summary>
    /// <remarks>A custom Urn that does not start with urn:message: is used as it is (it must not crash a rejection).</remarks>
    public static string FaultUrnFor(ContractDescriptor request) =>
        $"{FaultUrn}[[{(request.MessageTypeUrn.StartsWith(UrnPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.MessageTypeUrn[UrnPrefix.Length..]
            : request.MessageTypeUrn)}]]";

    public static MassTransitEnvelope Reply(
        PendingRequest request, ResponseDescriptor response, object message,
        Uri brokerUri, string sourceEntity, Guid messageId, DateTime nowUtc) =>
        Base(request, brokerUri, sourceEntity, messageId, nowUtc, request.ResponseAddress,
            [response.MessageTypeUrn],
            JsonSerializer.SerializeToElement(message, response.Type, MassTransitJson.Options));

    public static MassTransitEnvelope Fault(
        PendingRequest request, string reason,
        Uri brokerUri, string sourceEntity, Guid messageId, DateTime nowUtc)
    {
        var fault = new
        {
            faultId = Guid.NewGuid(),
            faultedMessageId = request.Envelope.MessageId,
            timestamp = nowUtc,
            exceptions = new[]
            {
                new { exceptionType = RejectedExceptionType, message = reason, stackTrace = "", source = "rabbit-mcp" },
            },
            host = EnvelopeHost.Current,
            faultMessageTypes = request.Envelope.MessageType ?? new[] { request.Contract.MessageTypeUrn },
            message = request.Envelope.Message,
        };

        return Base(request, brokerUri, sourceEntity, messageId, nowUtc, request.FaultAddress,
            [FaultUrnFor(request.Contract), FaultUrn],
            JsonSerializer.SerializeToElement(fault, MassTransitJson.Options));
    }

    private static MassTransitEnvelope Base(
        PendingRequest request, Uri brokerUri, string sourceEntity, Guid messageId, DateTime nowUtc,
        string destination, string[] messageTypes, JsonElement message) => new()
    {
        MessageId = messageId.ToString(),
        RequestId = request.RequestId.ToString(),
        CorrelationId = request.Envelope.CorrelationId,
        ConversationId = request.Envelope.ConversationId ?? request.Envelope.MessageId,
        InitiatorId = request.Envelope.MessageId,
        SourceAddress = BrokerAddress.ForEntity(brokerUri, sourceEntity),
        DestinationAddress = destination,
        MessageType = messageTypes,
        Message = message,
        SentTime = nowUtc,
        Headers = new Dictionary<string, object?>(),
        Host = EnvelopeHost.Current,
    };
}
