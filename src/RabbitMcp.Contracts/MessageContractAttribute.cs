// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

namespace RabbitMcp.Contracts;

/// <summary>
/// Marks a type as a message contract that the MCP server exposes as a typed
/// <c>publish_*</c> tool. The type must be a non-generic, non-nested class or record.
/// </summary>
/// <remarks>
/// Defaults follow MassTransit conventions, so MassTransit consumers of the same
/// type receive the message without any extra configuration:
/// <list type="bullet">
///   <item>message type URN: <c>urn:message:{Namespace}:{TypeName}</c></item>
///   <item>exchange: <c>{Namespace}:{TypeName}</c> (fanout, created by the consumers)</item>
/// </list>
/// Use <see cref="Urn"/> and <see cref="Exchange"/> to map a contract onto an
/// existing message type that lives in another namespace. For services that do not use MassTransit, set
/// <see cref="Format"/> to <see cref="MessageFormat.Raw"/>: the message is sent as plain JSON, routed with
/// <see cref="Exchange"/> and <see cref="RoutingKey"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MessageContractAttribute(string description) : Attribute
{
    /// <summary>What the message means. Shown to the model as the tool description.</summary>
    public string Description { get; } = description;

    /// <summary>Contract version, sent in the <c>Contract-Version</c> header.</summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// Overrides the message type URN (e.g. <c>urn:message:Shop.Messages:OrderSubmitted</c>). For
    /// <see cref="MessageFormat.Raw"/> contracts it is the AMQP <c>type</c> property (any string, e.g. <c>order.created</c>).
    /// </summary>
    public string? Urn { get; init; }

    /// <summary>
    /// Overrides the exchange the message is published to. For <see cref="MessageFormat.Raw"/> contracts there is no
    /// default: set this, <see cref="RoutingKey"/>, or both (<c>""</c> or no exchange = the default exchange, where the
    /// routing key is a queue name).
    /// </summary>
    public string? Exchange { get; init; }

    /// <summary>
    /// Routing key of every message (default: empty). Needed for direct and topic exchanges; with the default exchange
    /// it names the destination queue. MassTransit's own fanout exchanges ignore it.
    /// </summary>
    public string? RoutingKey { get; init; }

    /// <summary>
    /// Wire format: <see cref="MessageFormat.MassTransit"/> (default) or <see cref="MessageFormat.Raw"/> for services
    /// that read plain JSON (any language or library).
    /// </summary>
    public MessageFormat Format { get; init; } = MessageFormat.MassTransit;

    /// <summary>
    /// Overrides the MCP tool name. Default: <c>publish_{snake_case_type_name}</c>, or
    /// <c>request_{snake_case_type_name}</c> when <see cref="Responses"/> is set.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Makes this contract a request (MassTransit request/response). The consumer replies
    /// with one of these types via <c>context.RespondAsync(...)</c>. Response types are plain
    /// classes/records; their URN is <c>urn:message:{Namespace}:{TypeName}</c>.
    /// For <see cref="MessageFormat.Raw"/> contracts this is RabbitMQ RPC: the request carries <c>reply_to</c> and
    /// <c>correlation_id</c>, and the replier names the reply type in the AMQP <c>type</c> property (type name or URN;
    /// optional when there is only one response type).
    /// </summary>
    /// <example><c>Responses = new[] { typeof(OrderStatus), typeof(OrderNotFound) }</c></example>
    public Type[] Responses { get; init; } = [];
}

/// <summary>How the messages of a contract look on the wire.</summary>
public enum MessageFormat
{
    /// <summary>
    /// MassTransit JSON envelope (<c>application/vnd.masstransit+json</c>): MassTransit consumers receive it as is.
    /// </summary>
    MassTransit = 0,

    /// <summary>
    /// The message itself as plain JSON (<c>application/json</c>, camelCase), with message id, correlation id and the
    /// message type in AMQP properties — for services that use RabbitMQ without MassTransit.
    /// </summary>
    Raw = 1,
}
