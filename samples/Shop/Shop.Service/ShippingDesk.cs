// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Shop.Contracts;

namespace Shop.Service;

/// <summary>
/// A service that does NOT use MassTransit, only RabbitMQ.Client — like a legacy or non-.NET service. It answers
/// GetShippingQuote requests the RabbitMQ RPC way: plain JSON in, a reply to reply_to with the same correlation_id and
/// the reply type in the AMQP type property. rabbit-mcp talks to it through the contract's Format = Raw.
/// Messages it cannot read are rejected into shipping-quotes.dlq (RabbitMQ dead-lettering), never dropped.
/// </summary>
public sealed class ShippingDesk(IConfiguration configuration, ILogger<ShippingDesk> logger) : BackgroundService
{
    public const string Queue = "shipping-quotes";
    public const string DeadLetterQueue = Queue + ".dlq";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration["RABBITMQ_URI"] ?? "amqp://guest:guest@localhost:5672/"),
            ClientProvidedName = "shop-shipping-desk",
            AutomaticRecoveryEnabled = true,
        };

        IConnection? connection = null;
        while (connection is null)
        {
            try
            {
                connection = await factory.CreateConnectionAsync(stoppingToken);
            }
            catch (BrokerUnreachableException ex)
            {
                logger.LogWarning("Shipping desk: RabbitMQ not reachable yet ({Message}), retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        await using (connection)
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = DeadLetterQueue,
                },
                cancellationToken: stoppingToken);
            await channel.BasicQosAsync(0, 10, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                GetShippingQuote? request;
                try
                {
                    request = JsonSerializer.Deserialize<GetShippingQuote>(delivery.Body.Span, Json);
                }
                catch (JsonException)
                {
                    request = null;
                }

                if (request is null || string.IsNullOrEmpty(delivery.BasicProperties.ReplyTo))
                {
                    logger.LogWarning("Shipping desk: unreadable request, rejected to {Queue}", DeadLetterQueue);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                    return;
                }

                object reply = request.Country == "AQ"
                    ? new ShippingUnavailable { Country = request.Country, Reason = "We do not ship to Antarctica." }
                    : new ShippingQuote
                    {
                        Country = request.Country,
                        Carrier = request.Country == "GE" ? "Georgian Post" : "DHL",
                        Price = Math.Round(5m + (decimal)request.WeightKg * (request.Country == "GE" ? 1.5m : 4m), 2),
                        Days = request.Country == "GE" ? 2 : 6,
                    };

                var props = new BasicProperties
                {
                    CorrelationId = delivery.BasicProperties.CorrelationId,
                    Type = reply.GetType().Name,
                    ContentType = "application/json",
                    AppId = "shop-shipping-desk",
                };
                await channel.BasicPublishAsync("", delivery.BasicProperties.ReplyTo, mandatory: false, props,
                    JsonSerializer.SerializeToUtf8Bytes(reply, reply.GetType(), Json));
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                logger.LogInformation("Shipping desk: {Reply} for {Country}", props.Type, request.Country);
            };

            await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Shipping desk listening on {Queue} (plain RabbitMQ, no MassTransit)", Queue);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
