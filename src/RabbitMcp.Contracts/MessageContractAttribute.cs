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
/// existing message type that lives in another namespace.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MessageContractAttribute(string description) : Attribute
{
    /// <summary>What the message means. Shown to the model as the tool description.</summary>
    public string Description { get; } = description;

    /// <summary>Contract version, sent in the <c>Contract-Version</c> header.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Overrides the message type URN (e.g. <c>urn:message:Shop.Messages:OrderSubmitted</c>).</summary>
    public string? Urn { get; init; }

    /// <summary>Overrides the exchange the message is published to.</summary>
    public string? Exchange { get; init; }

    /// <summary>
    /// Overrides the MCP tool name. Default: <c>publish_{snake_case_type_name}</c>, or
    /// <c>request_{snake_case_type_name}</c> when <see cref="Responses"/> is set.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Makes this contract a request (MassTransit request/response). The consumer replies
    /// with one of these types via <c>context.RespondAsync(...)</c>. Response types are plain
    /// classes/records; their URN is <c>urn:message:{Namespace}:{TypeName}</c>.
    /// </summary>
    /// <example><c>Responses = new[] { typeof(OrderStatus), typeof(OrderNotFound) }</c></example>
    public Type[] Responses { get; init; } = [];
}
