using System.ComponentModel;
using System.Text.Json;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

// Return types of the tools. With UseStructuredContent each of these becomes the
// tool's outputSchema, and the result is returned as structuredContent.

public sealed record PublishResult(
    [property: Description("MessageId of the published message.")] string MessageId,
    string ConversationId,
    string? CorrelationId,
    string Contract,
    int ContractVersion,
    string MessageType,
    string Exchange,
    DateTime SentTimeUtc,
    [property: Description("True when the broker confirmed and routed the message to at least one queue.")] bool Routed);

public sealed record ContractInfo(
    string Name,
    string ToolName,
    int Version,
    string MessageTypeUrn,
    string Exchange,
    string Description);

public sealed record ContractList(
    IReadOnlyList<ContractInfo> Contracts,
    [property: Description("Whether publish_* tools are enabled on this server.")] bool PublishEnabled);

public sealed record ContractValidation(
    string Contract,
    int Version,
    bool Valid,
    IReadOnlyList<string> Errors);

public sealed record QueueStat(
    string Queue,
    bool Exists,
    [property: Description("Messages ready for delivery (unacknowledged ones are not counted).")] uint? ReadyMessages,
    uint? Consumers);

public sealed record QueueStats(IReadOnlyList<QueueStat> Queues);

public sealed record ContractCheck(
    string Contract,
    int Version,
    bool Valid,
    IReadOnlyList<string> Errors);

public sealed record PeekedMessage(
    int Index,
    string? MessageId,
    string? CorrelationId,
    bool Redelivered,
    string? ContentType,
    IReadOnlyList<string>? MessageTypes,
    DateTime? SentTimeUtc,
    [property: Description("Contract matched by message type, with validation result. Null if no registered contract matches.")] ContractCheck? Contract,
    [property: Description("MassTransit fault details, present for messages in _error/_skipped queues.")] FaultInfo? Fault,
    [property: Description("Exchange republish_from_error would send this message to.")] string? RepublishTarget,
    [property: Description("The message (envelope 'message' field, or the whole JSON body).")] JsonElement? Payload,
    [property: Description("Body as text when it is not JSON or is too large (truncated for display only).")] string? RawBody,
    bool Truncated,
    int BodyBytes,
    IReadOnlyDictionary<string, string?> Headers);

public sealed record PeekResult(
    string Queue,
    int Returned,
    [property: Description("Ready messages left in the queue after the last fetched one.")] uint RemainingAfterLast,
    IReadOnlyList<PeekedMessage> Messages);

public sealed record RepublishOutcome(
    string? MessageId,
    [property: Description("republished | skipped | failed")] string Status,
    string? TargetExchange,
    string? Detail);

public sealed record RepublishResult(
    string ErrorQueue,
    [property: Description("False for a preview (confirm=false): nothing was changed.")] bool Executed,
    PeekResult? Preview,
    IReadOnlyList<RepublishOutcome> Outcomes,
    int Republished,
    int Requeued);
