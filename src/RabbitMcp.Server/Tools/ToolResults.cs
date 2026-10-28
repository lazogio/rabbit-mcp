// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text.Json;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

// Return types of the tools. With UseStructuredContent each of these becomes the
// tool's outputSchema, and the result is returned as structuredContent.

public sealed record PublishResult(
    [property: Description("MessageId of the published message.")] string MessageId,
    [property: Description("MassTransit conversationId; null for raw messages.")] string? ConversationId,
    string? CorrelationId,
    string Contract,
    int ContractVersion,
    [property: Description("masstransit (envelope) or raw (plain JSON).")] string Format,
    [property: Description("Message type URN (MassTransit) or AMQP type property (raw).")] string MessageType,
    [property: Description("Exchange the message was published to; empty for the default exchange.")] string Exchange,
    [property: Description("Routing key; for the default exchange the destination queue. Null when empty.")] string? RoutingKey,
    DateTime SentTimeUtc,
    [property: Description("True when the broker confirmed and routed the message to at least one queue.")] bool Routed);

public sealed record ContractInfo(
    string Name,
    [property: Description("event (publish_*) or request (request_*, waits for a reply)")] string Kind,
    string ToolName,
    int Version,
    [property: Description("masstransit (MassTransit envelope) or raw (plain JSON, AMQP properties; requests use reply_to/correlation_id).")] string Format,
    [property: Description("MassTransit message type URN, or the AMQP type property of raw messages.")] string MessageTypeUrn,
    [property: Description("Exchange messages are published to; empty for the default exchange (RoutingKey = queue).")] string Exchange,
    [property: Description("Routing key of every message; null when empty.")] string? RoutingKey,
    string Description,
    [property: Description("For requests: reply types the consumer may answer with.")] IReadOnlyList<string> Responses,
    [property: Description("Whether you can send it: EnablePublish, PublishAllow/PublishDeny and your roles.")] bool Sendable);

public sealed record ContractList(
    IReadOnlyList<ContractInfo> Contracts,
    [property: Description("Whether publish_* and request_* tools are enabled on this server.")] bool PublishEnabled)
{
    [Description("How many contracts matched nameContains (may be more than returned).")]
    public int Matched { get; init; }

    [Description("True when more contracts matched than were returned: narrow with nameContains or raise limit.")]
    public bool Truncated { get; init; }
}

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
    [property: Description("RabbitMQ dead-lettering history (x-death), newest first: why a queue rejected, expired or dropped it.")] IReadOnlyList<DeathEvent>? DeadLettered,
    [property: Description("Where republish_from_error would send this message: an exchange, or 'queue:<name>' for a queue reached directly (default exchange).")] string? RepublishTarget,
    [property: Description("Why republish_from_error will not use RepublishTarget without an explicit targetExchange.")] string? RepublishWarning,
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
    [property: Description("Exchange, or 'queue:<name>' for a queue reached directly (default exchange).")] string? TargetExchange,
    string? Detail);

public sealed record RepublishResult(
    string ErrorQueue,
    [property: Description("False for a preview (confirm=false): nothing was changed.")] bool Executed,
    PeekResult? Preview,
    IReadOnlyList<RepublishOutcome> Outcomes,
    int Republished,
    int Requeued)
{
    [Description("Preview with a filter: what matches, grouped by fault.")]
    public SelectionSummary? Selection { get; init; }

    [Description("Outcomes not listed (the first 100 are); every one is in the audit.")]
    public int OutcomesOmitted { get; init; }

    [Description("True when count was reached: more matching messages may be left; call again.")]
    public bool MoreMayRemain { get; init; }
}

public sealed record DeclareResult(
    [property: Description("exchange or queue")] string Kind,
    string Name,
    [property: Description("Exchange type (fanout, direct, topic, headers) or queue type (classic, quorum, stream).")] string Type,
    bool Durable,
    [property: Description("Exchanges only: deleted by RabbitMQ when its last binding is removed. Always false for queues.")] bool AutoDelete,
    [property: Description("x-arguments the entity is (or would be) declared with.")] IReadOnlyDictionary<string, string> Arguments,
    [property: Description("Exchange it is (or would be) bound to and receives messages from; null for no binding.")] string? BoundTo,
    [property: Description("Routing key of that binding; null without a binding.")] string? RoutingKey,
    [property: Description("False for a preview (confirm=false): nothing was changed.")] bool Executed,
    [property: Description("Preview: would-create | exists. Executed: created | existed | existed-different (left unchanged).")] string Outcome,
    IReadOnlyList<string> Notes)
{
    [Description("RabbitMcp:TopologyTemplates entry the settings came from; null without a template.")]
    public string? Template { get; init; }
}

public sealed record DeadLetterOutcome(
    string? MessageId,
    [property: Description("moved | skipped | failed")] string Status,
    string? Detail);

public sealed record DeadLetterResult(
    string ErrorQueue,
    [property: Description("Queue the messages are (or would be) moved to. Nothing is ever deleted.")] string DeadLetterQueue,
    [property: Description("False for a preview (confirm=false): nothing was changed.")] bool Executed,
    PeekResult? Preview,
    IReadOnlyList<DeadLetterOutcome> Outcomes,
    int Moved,
    int Requeued)
{
    [Description("Preview with a filter: what matches, grouped by fault.")]
    public SelectionSummary? Selection { get; init; }

    [Description("Outcomes not listed (the first 100 are); every one is in the audit.")]
    public int OutcomesOmitted { get; init; }

    [Description("True when count was reached: more matching messages may be left; call again.")]
    public bool MoreMayRemain { get; init; }
}

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
    [property: Description("True for MassTransit _error / _skipped queues and _dead (dead-letter) queues.")] bool IsErrorQueue,
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
    [property: Description("Exchange the reply is sent to (the requester's temporary reply endpoint).")] string? ReplyTo,
    DateTimeOffset ReceivedAt,
    [property: Description("When the requester gives up; answer before this. Null: no expiration.")] DateTimeOffset? ExpiresAt,
    [property: Description("Seconds left until ExpiresAt.")] int? SecondsLeft);

public sealed record PendingRequestList(
    IReadOnlyList<PendingRequestInfo> Requests,
    [property: Description("All pending requests (may be more than returned).")] int Total,
    [property: Description("Request contracts this server answers.")] IReadOnlyList<string> AnsweredContracts,
    [property: Description("Exclusive queue the responder consumes from; null while not listening.")] string? Queue,
    [property: Description("listening | standby (another instance answers) | disconnected | starting")] string Status);

public sealed record ReplyResult(
    string RequestId,
    string Contract,
    [property: Description("replied | rejected")] string Status,
    [property: Description("Reply type sent, or Fault for a rejection.")] string ResponseType,
    string MessageId,
    [property: Description("The requester's reply exchange.")] string Exchange,
    [property: Description("Time between receiving the request and answering it.")] long ElapsedMs);

public sealed record ConsumerQueue(
    string Queue,
    [property: Description("Exchanges the message passes through, from the contract exchange to this queue.")] IReadOnlyList<string> Via,
    long? Consumers,
    long? MessagesReady,
    [property: Description("Ready messages in <queue>_error (faults); null if that queue does not exist.")] long? ErrorQueueMessages,
    [property: Description("Ready messages in <queue>_skipped (no consumer for the type); null if it does not exist.")] long? SkippedQueueMessages);

public sealed record ContractConsumers(
    string Contract,
    [property: Description("event or request")] string Kind,
    string Exchange,
    bool ExchangeExists,
    [property: Description("ok | no-consumers (queues but nobody consuming) | no-queue (unroutable) | no-exchange (never consumed or published)")] string Status,
    IReadOnlyList<ConsumerQueue> Queues,
    long TotalConsumers,
    IReadOnlyList<string> Warnings);

public sealed record ContractConsumersReport(
    IReadOnlyList<ContractConsumers> Contracts,
    int Ok,
    [property: Description("Contracts whose status is not ok.")] int WithProblems,
    string Note)
{
    [Description("True when not every matching contract was inspected (limit).")]
    public bool Truncated { get; init; }
}

public sealed record FoundMessage(
    [property: Description("Queue the message is in.")] string Queue,
    PeekedMessage Message);

public sealed record QueueSearch(
    string Queue,
    [property: Description("searched | searched the first N of M | empty | missing | skipped: has consumers | skipped: exclusive to another connection | error: <reason>")] string Status,
    int Scanned,
    [property: Description("Ready messages in the queue.")] long? Ready);

public sealed record FindMessageResult(
    IReadOnlyList<FoundMessage> Matches,
    IReadOnlyList<QueueSearch> Queues,
    string? Note);

public sealed record RecentActivity(
    [property: Description("Audit events, newest first.")] IReadOnlyList<AuditEvent> Events,
    [property: Description("True when more events matched than were returned.")] bool Truncated,
    [property: Description("Audit lines read (from the end of the file).")] int Scanned);

public sealed record FieldChange(
    [property: Description("Field path, e.g. customerEmail or lines[0].quantity.")] string Path,
    [property: Description("Value in the original message; null if the field was added.")] JsonElement? Before,
    [property: Description("Value in the correction; null if the field was removed.")] JsonElement? After);

public sealed record CorrectionResult(
    string ErrorQueue,
    string OriginalMessageId,
    string Contract,
    [property: Description("False for a preview (confirm=false): nothing was changed.")] bool Executed,
    [property: Description("What the correction changes, field by field.")] IReadOnlyList<FieldChange> Changes,
    [property: Description("Where the corrected message goes: an exchange, or 'queue:<name>'.")] string Target,
    [property: Description("Where the original is parked, unchanged. Nothing is deleted.")] string DeadLetterQueue,
    [property: Description("MessageId of the corrected message, once sent.")] string? NewMessageId,
    [property: Description("True when the original was moved to the dead-letter queue (false: it is still in the error queue).")] bool OriginalParked,
    string? Note);

public sealed record FaultGroup(
    string? ExceptionType,
    [property: Description("Exception message (first 200 characters), or the dead-lettering reason.")] string? ExceptionMessage,
    string? Contract,
    int Count,
    [property: Description("A few messageIds of this group.")] IReadOnlyList<string> SampleMessageIds);

public sealed record SelectionSummary(
    [property: Description("Messages read from the head of the queue (nothing was changed).")] int Scanned,
    [property: Description("How many of them match the filter.")] int Matched,
    [property: Description("True when the scan stopped at its limit: the queue holds more.")] bool MoreMayRemain,
    IReadOnlyList<FaultGroup> Groups);

public sealed record StreamReadResult(
    string Queue,
    [property: Description("Where reading started: first, last, next, an offset or a time.")] string From,
    int Returned,
    [property: Description("Stream offset of the first returned message.")] long? FirstOffset,
    [property: Description("Stream offset of the last returned message: pass LastOffset+1 as from to continue.")] long? LastOffset,
    IReadOnlyList<PeekedMessage> Messages,
    string? Note);
