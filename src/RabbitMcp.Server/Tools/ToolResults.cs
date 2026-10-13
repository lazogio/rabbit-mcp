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
    [property: Description("event (publish_*) or request (request_*, waits for a reply)")] string Kind,
    string ToolName,
    int Version,
    string MessageTypeUrn,
    string Exchange,
    string Description,
    [property: Description("For requests: reply types the consumer may answer with.")] IReadOnlyList<string> Responses,
    [property: Description("Whether this server exposes its publish_/request_ tool (EnablePublish and PublishAllow/PublishDeny).")] bool Sendable);

public sealed record ContractList(
    IReadOnlyList<ContractInfo> Contracts,
    [property: Description("Whether publish_* and request_* tools are enabled on this server.")] bool PublishEnabled);

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
    [property: Description("Contract version; null for response types, which are not versioned.")] int? Version,
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

public sealed record ExceptionSummary(string? ExceptionType, string? Message, string? StackTrace);

public sealed record RequestFault(
    string? FaultedMessageId,
    string? Timestamp,
    IReadOnlyList<ExceptionSummary> Exceptions);

public sealed record Responder(
    [property: Description("Receive endpoint of the consumer that replied (envelope sourceAddress).")] string? Address,
    string? MachineName,
    string? ProcessName,
    string? Assembly);

public sealed record RequestResult(
    string RequestId,
    string MessageId,
    string Contract,
    [property: Description("responded | faulted | timeout")] string Status,
    [property: Description("Name of the reply type, e.g. OrderStatus.")] string? ResponseType,
    [property: Description("Message types of the reply, as sent by the consumer.")] IReadOnlyList<string>? ResponseMessageTypes,
    [property: Description("The reply message.")] JsonElement? Response,
    [property: Description("Whether the reply satisfies its response contract. Null for unknown reply types.")] ContractCheck? ResponseCheck,
    [property: Description("Exceptions thrown by the consumer when Status is faulted.")] RequestFault? Fault,
    Responder? RespondedBy,
    long ElapsedMs,
    [property: Description("Hint for the next step, e.g. after a timeout.")] string? Note);

public sealed record QueueInfo(
    string Name,
    [property: Description("classic, quorum or stream.")] string? Type,
    string? State,
    long? MessagesReady,
    long? MessagesUnacknowledged,
    long? Consumers,
    [property: Description("True for MassTransit _error / _skipped queues.")] bool IsErrorQueue,
    [property: Description("For error queues: the queue the messages faulted on (republish target).")] string? SourceQueue);

public sealed record QueueListing(
    string Vhost,
    IReadOnlyList<QueueInfo> Queues,
    [property: Description("How many queues matched the filters (may be more than returned).")] int Matched,
    int TotalInVhost,
    [property: Description("True when more queues matched than were returned.")] bool Truncated,
    string Note);

public sealed record PendingRequestInfo(
    [property: Description("Pass this to reply_* or reject_request.")] string RequestId,
    string Contract,
    int ContractVersion,
    [property: Description("Tools that answer this request (one per declared reply type).")] IReadOnlyList<string> ReplyTools,
    [property: Description("The request message.")] JsonElement? Message,
    [property: Description("Whether the request satisfies its contract.")] ContractCheck Check,
    string? CorrelationId,
    [property: Description("Requester's endpoint (envelope sourceAddress).")] string? From,
    DateTimeOffset ReceivedAt,
    [property: Description("When the requester gives up; answer before this. Null: no expiration.")] DateTimeOffset? ExpiresAt,
    [property: Description("Seconds left until ExpiresAt.")] int? SecondsLeft);

public sealed record PendingRequestList(
    IReadOnlyList<PendingRequestInfo> Requests,
    [property: Description("All pending requests (may be more than returned).")] int Total,
    [property: Description("Request contracts this server answers.")] IReadOnlyList<string> AnsweredContracts,
    [property: Description("Exclusive queue the responder consumes from; null while not connected.")] string? Queue);

public sealed record ReplyResult(
    string RequestId,
    string Contract,
    [property: Description("replied | rejected")] string Status,
    [property: Description("Reply type sent, or Fault for a rejection.")] string ResponseType,
    string MessageId,
    [property: Description("The requester's reply exchange.")] string Exchange,
    [property: Description("Time between receiving the request and answering it.")] long ElapsedMs);
