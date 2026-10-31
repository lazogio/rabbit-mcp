// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using RabbitMcp.Server.Contracts;
using RabbitMQ.Client;

namespace RabbitMcp.Server.Messaging;

/// <summary>Request fields of an outgoing raw request (RabbitMQ RPC: reply_to + correlation_id).</summary>
public sealed record RawRequestInfo(Guid RequestId, string ReplyQueue);

/// <summary>
/// Plain-JSON messages (<c>MessageFormat.Raw</c>) for services without MassTransit: the body is the message itself and
/// what the envelope would carry goes into AMQP properties — message_id, correlation_id, type (the contract's URN or
/// its Urn override), app_id, timestamp — plus the same Contract-Version / X-Published-By headers.
/// </summary>
public static class RawMessages
{
    public const string ContentType = "application/json";

    public static byte[] Serialize(ContractDescriptor contract, object message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, contract.Type, MassTransitJson.Options);

    /// <param name="correlationId">
    /// The caller's workflow correlation id. Events carry it in correlation_id; requests use correlation_id for the
    /// request id (RPC convention), so theirs goes into the X-Correlation-Id header.
    /// </param>
    public static BasicProperties CreateProperties(
        ContractDescriptor contract, Guid messageId, string? correlationId, string clientName, DateTimeOffset now,
        RawRequestInfo? request = null, TimeSpan? timeToLive = null)
    {
        var headers = new Dictionary<string, object?>
        {
            [HeaderNames.ContractVersion] = contract.Version,
            [HeaderNames.PublishedBy] = clientName,
        };
        if (request is not null && correlationId is not null)
            headers[HeaderNames.CorrelationId] = correlationId;

        return new BasicProperties
        {
            ContentType = ContentType,
            MessageId = messageId.ToString(),
            CorrelationId = request?.RequestId.ToString() ?? correlationId,
            ReplyTo = request?.ReplyQueue,
            Type = contract.MessageTypeUrn,
            AppId = clientName,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds()),
            Expiration = timeToLive is { } ttl ? ((long)ttl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : null,
            Headers = headers,
        };
    }
}

/// <summary>A reply to a raw request, copied out of the delivery (its buffer is only valid inside the handler).</summary>
/// <param name="Body">The body when it is JSON.</param>
/// <param name="Text">The body as text when it is not JSON (cut at the payload limit).</param>
public sealed record RawReply(string? CorrelationId, string? Type, string? AppId, JsonElement? Body, string? Text, int Bytes)
{
    public static RawReply From(IReadOnlyBasicProperties props, ReadOnlySpan<byte> body, int maxChars)
    {
        var json = body.Length <= maxChars ? MessageInspector.TryParseJson(body) : null;
        string? text = null;
        if (json is null)
            text = Encoding.UTF8.GetString(body.Length > maxChars ? body[..maxChars] : body);

        return new RawReply(props.CorrelationId, props.Type, props.AppId, json, text, body.Length);
    }
}

/// <summary>What came back for a request: a MassTransit envelope or a raw reply.</summary>
public sealed record ReceivedReply(MassTransitEnvelope? Envelope, RawReply? Raw)
{
    public static ReceivedReply Of(MassTransitEnvelope envelope) => new(envelope, null);

    public static ReceivedReply Of(RawReply raw) => new(null, raw);

    /// <summary>The reply's message types: the envelope's messageType, or the raw reply's AMQP type.</summary>
    public IReadOnlyList<string>? MessageTypes =>
        Envelope?.MessageType ?? (Raw?.Type is { Length: > 0 } type ? new[] { type } : null);
}
