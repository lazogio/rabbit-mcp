// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// MassTransit's JSON envelope (application/vnd.masstransit+json). Field names
/// match MassTransit's JsonMessageEnvelope, so it is both written and read here.
/// </summary>
public sealed class MassTransitEnvelope
{
    public string? MessageId { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ConversationId { get; set; }
    public string? InitiatorId { get; set; }
    public string? SourceAddress { get; set; }
    public string? DestinationAddress { get; set; }
    public string? ResponseAddress { get; set; }
    public string? FaultAddress { get; set; }
    public string[]? MessageType { get; set; }
    public JsonElement? Message { get; set; }
    public DateTime? ExpirationTime { get; set; }
    public DateTime? SentTime { get; set; }
    public Dictionary<string, object?>? Headers { get; set; }
    public EnvelopeHost? Host { get; set; }

    /// <summary>Parses a body as an envelope; null if it is not JSON or has no messageType/message.</summary>
    public static MassTransitEnvelope? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<MassTransitEnvelope>(body, MassTransitJson.Options);
            return envelope is { MessageType.Length: > 0, Message: not null } ? envelope : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed class EnvelopeHost
{
    public string? MachineName { get; set; }
    public string? ProcessName { get; set; }
    public int? ProcessId { get; set; }
    public string? Assembly { get; set; }
    public string? AssemblyVersion { get; set; }
    public string? FrameworkVersion { get; set; }
    public string? MassTransitVersion { get; set; }
    public string? OperatingSystemVersion { get; set; }

    public static EnvelopeHost Current { get; } = CreateCurrent();

    private static EnvelopeHost CreateCurrent()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(EnvelopeHost).Assembly;
        using var process = Process.GetCurrentProcess();
        return new EnvelopeHost
        {
            MachineName = Environment.MachineName,
            ProcessName = process.ProcessName,
            ProcessId = Environment.ProcessId,
            Assembly = entry.GetName().Name,
            AssemblyVersion = entry.GetName().Version?.ToString(),
            FrameworkVersion = Environment.Version.ToString(),
            OperatingSystemVersion = RuntimeInformation.OSDescription,
        };
    }
}

/// <summary>Request/response fields of an outgoing request (MassTransit request client semantics).</summary>
public sealed record RequestEnvelopeInfo(Guid RequestId, string ResponseAddress, DateTime ExpirationTimeUtc);

/// <summary>Builds the envelope for an outgoing contract message.</summary>
public sealed class EnvelopeFactory(TimeProvider time)
{
    public MassTransitEnvelope Create(
        ContractDescriptor contract,
        object message,
        Uri brokerUri,
        string sourceName,
        Guid messageId,
        string? correlationId,
        RequestEnvelopeInfo? request = null)
    {
        return new MassTransitEnvelope
        {
            MessageId = messageId.ToString(),
            ConversationId = messageId.ToString(),
            CorrelationId = correlationId,
            RequestId = request?.RequestId.ToString(),
            ResponseAddress = request?.ResponseAddress,
            ExpirationTime = request?.ExpirationTimeUtc,
            SourceAddress = BrokerAddress.ForEntity(brokerUri, sourceName),
            // The exchange, or for the default exchange the queue it routes to (MassTransit's queue:name address).
            DestinationAddress = BrokerAddress.ForEntity(brokerUri, contract.DestinationName),
            MessageType = [contract.MessageTypeUrn],
            Message = JsonSerializer.SerializeToElement(message, contract.Type, MassTransitJson.Options),
            SentTime = time.GetUtcNow().UtcDateTime,
            Headers = new Dictionary<string, object?>
            {
                [HeaderNames.ContractVersion] = contract.Version,
                [HeaderNames.PublishedBy] = sourceName,
            },
            Host = EnvelopeHost.Current,
        };
    }

    public static byte[] Serialize(MassTransitEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, MassTransitJson.Options);
}

public static class HeaderNames
{
    public const string ContractVersion = "Contract-Version";
    public const string PublishedBy = "X-Published-By";
    public const string RepublishedBy = "X-Republished-By";
    public const string RepublishedFrom = "X-Republished-From";
    public const string RepublishedAt = "X-Republished-At";
    public const string DeadLetteredBy = "X-Dead-Lettered-By";
    public const string DeadLetteredFrom = "X-Dead-Lettered-From";
    public const string DeadLetteredAt = "X-Dead-Lettered-At";
    public const string DeadLetterReason = "X-Dead-Letter-Reason";

    public const string OriginalExpiration = "X-Original-Expiration"; // a parked message's TTL; a republish restores it
    public const string OriginalUserId = "X-Original-User-Id";        // user_id of a moved message: only its sender may publish it

    public const string CorrectedFrom = "X-Corrected-From"; // on the corrected copy: the original's messageId
    public const string CorrectedTo = "X-Corrected-To";     // on the parked original: the corrected copy's messageId
    public const string CorrectedBy = "X-Corrected-By";
    public const string CorrectedAt = "X-Corrected-At";

    /// <summary>Workflow correlation id of a raw request (its correlation_id property is the request id).</summary>
    public const string CorrelationId = "X-Correlation-Id";

    // RabbitMQ dead-lettering (DLX): history of a message a queue rejected, expired or dropped.
    public const string Death = "x-death";
    public const string FirstDeathPrefix = "x-first-death-";
    public const string LastDeathPrefix = "x-last-death-";

    // MassTransit
    public const string Reason = "MT-Reason";
    public const string FaultPrefix = "MT-Fault-";
    public const string HostPrefix = "MT-Host-";
    public const string FaultInputAddress = "MT-Fault-InputAddress";
    public const string FaultExceptionType = "MT-Fault-ExceptionType";
    public const string FaultMessage = "MT-Fault-Message";
    public const string FaultMessageType = "MT-Fault-MessageType";
    public const string FaultConsumerType = "MT-Fault-ConsumerType";
    public const string FaultTimestamp = "MT-Fault-Timestamp";
    public const string FaultStackTrace = "MT-Fault-StackTrace";
    public const string FaultRetryCount = "MT-Fault-RetryCount";
    public const string FaultRedeliveryCount = "MT-Fault-RedeliveryCount";
    public const string HostMachineName = "MT-Host-MachineName";
    public const string HostProcessName = "MT-Host-ProcessName";
}
