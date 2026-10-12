using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Tools;

/// <summary>
/// Publishes one contract message. <see cref="PublishAsync{T}"/> is closed over each
/// contract type to create one typed MCP tool per contract (see <see cref="ContractToolFactory"/>),
/// so the tool's inputSchema is exactly the contract's JSON Schema.
/// </summary>
public sealed class ContractPublisher(
    RabbitConnection connection,
    ContractRegistry contracts,
    EnvelopeFactory envelopes,
    IOptions<RabbitMcpOptions> options,
    ILogger<ContractPublisher> logger)
{
    public async Task<PublishResult> PublishAsync<T>(
        [Description("The message. Must satisfy the contract schema and its validation rules.")]
        T message,
        [Description("Optional correlation id (GUID) linking this message to an existing workflow.")]
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var contract = contracts.Get(typeof(T));

        // 1. Contract rules (DataAnnotations). Nothing is sent when they fail.
        var errors = ContractValidator.Validate(message);
        if (errors.Count > 0)
            throw new McpException(
                $"Message violates contract {contract.Name} v{contract.Version}; nothing was published. " +
                string.Join("; ", errors));

        if (correlationId is not null && !Guid.TryParse(correlationId, out _))
            throw new McpException("correlationId must be a GUID.");

        // 2. Publishing to a missing exchange closes the channel and loses the message.
        if (!await connection.ExchangeExistsAsync(contract.Exchange, cancellationToken))
            throw new McpException(
                $"Exchange '{contract.Exchange}' does not exist, so no service consumes {contract.Name} yet " +
                "(MassTransit creates it when a consumer starts). Nothing was published.");

        // 3. MassTransit envelope + AMQP properties.
        var messageId = Guid.NewGuid();
        var envelope = envelopes.Create(
            contract, message, connection.BrokerUri, options.Value.ClientName, messageId, correlationId);

        var props = new BasicProperties
        {
            ContentType = MassTransitJson.ContentType,
            MessageId = envelope.MessageId,
            CorrelationId = correlationId,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                [HeaderNames.ContractVersion] = contract.Version,
                [HeaderNames.PublishedBy] = options.Value.ClientName,
            },
        };

        // 4. Publisher confirms + mandatory: an unroutable message is an error, not a silent drop.
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, cancellationToken);
        try
        {
            await channel.BasicPublishAsync(
                contract.Exchange, routingKey: "", mandatory: true, props,
                EnvelopeFactory.Serialize(envelope), cancellationToken);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            throw new McpException(
                $"Exchange '{contract.Exchange}' exists but no queue is bound to it; the broker returned the message. Nothing was delivered.");
        }
        catch (PublishException)
        {
            throw new McpException("The broker rejected the message (nack). Nothing was delivered.");
        }

        logger.LogInformation("Published {Contract} {MessageId} to {Exchange}", contract.Name, messageId, contract.Exchange);

        return new PublishResult(
            MessageId: envelope.MessageId!,
            ConversationId: envelope.ConversationId!,
            CorrelationId: correlationId,
            Contract: contract.Name,
            ContractVersion: contract.Version,
            MessageType: contract.MessageTypeUrn,
            Exchange: contract.Exchange,
            SentTimeUtc: envelope.SentTime!.Value,
            Routed: true);
    }
}

/// <summary>Creates one publish_* tool per registered contract.</summary>
public static class ContractToolFactory
{
    private static readonly MethodInfo PublishMethod =
        typeof(ContractPublisher).GetMethod(nameof(ContractPublisher.PublishAsync))!;

    public static IEnumerable<McpServerTool> CreatePublishTools(ContractRegistry registry) =>
        registry.Contracts.Select(CreatePublishTool);

    public static McpServerTool CreatePublishTool(ContractDescriptor contract) =>
        McpServerTool.Create(
            PublishMethod.MakeGenericMethod(contract.Type),
            ctx => ctx.Services!.GetRequiredService<ContractPublisher>(),
            new McpServerToolCreateOptions
            {
                Name = contract.ToolName,
                Title = $"Publish {contract.Name} (v{contract.Version})",
                Description =
                    $"{contract.Description}\n\n" +
                    $"Publishes a {contract.MessageTypeUrn} message (contract v{contract.Version}) to exchange '{contract.Exchange}' " +
                    "in MassTransit format. The message is validated against the contract first; an invalid message is rejected " +
                    "and nothing is sent. Use validate_message to check a message without publishing.",
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
                UseStructuredContent = true,
                SerializerOptions = ToolJson.Options,
            });
}
