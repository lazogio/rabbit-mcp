// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using RabbitMcp.Server.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMcp.IntegrationTests;

/// <summary>Contracts from the AsyncAPI example against a real broker and a plain RabbitMQ.Client "warehouse".</summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AsyncApiTests(BrokerFixture broker)
{
    private static readonly ContractRegistry Warehouse =
        new([], AsyncApiLoader.Load(Path.Combine(AppContext.BaseDirectory, "asyncapi", "warehouse.yaml")));

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Events_from_the_file_are_validated_and_routed_by_its_bindings()
    {
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.ExchangeDeclareAsync("warehouse", ExchangeType.Topic, durable: true, autoDelete: false);
        await channel.QueueDeclareAsync("it-warehouse-stock", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync("it-warehouse-stock", "warehouse", "stock.*");
        var publisher = broker.CreatePublisher(Warehouse);
        var contract = Warehouse.FindByName("StockAdjusted")!;

        var invalid = await Assert.ThrowsAsync<McpException>(() =>
            publisher.PublishJsonAsync(contract, J("""{"sku":"lamp","delta":1}""")));
        Assert.Contains("sku: must match the pattern", invalid.Message);

        var result = await publisher.PublishJsonAsync(contract, J("""{"sku":"LAMP-01","delta":-3,"reason":"damaged"}"""));
        await broker.WaitForQueueCount("it-warehouse-stock", 1);
        var delivery = (await channel.BasicGetAsync("it-warehouse-stock", autoAck: true))!;

        Assert.Equal("""{"sku":"LAMP-01","delta":-3,"reason":"damaged"}""", Encoding.UTF8.GetString(delivery.Body.Span));
        Assert.Equal("StockAdjusted", delivery.BasicProperties.Type);
        Assert.Equal(result.MessageId, delivery.BasicProperties.MessageId);
        Assert.Equal("stock.adjusted", delivery.RoutingKey);
    }

    [Fact]
    public async Task Requests_from_the_file_are_answered_over_rabbitmq_rpc()
    {
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await using var replies = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync("warehouse-reservations", durable: true, exclusive: false, autoDelete: false);
        var server = new AsyncEventingBasicConsumer(channel);
        server.ReceivedAsync += async (_, delivery) =>
        {
            var request = JsonDocument.Parse(delivery.Body).RootElement;
            var orderId = request.GetProperty("orderId").GetString();
            var (type, body) = request.GetProperty("quantity").GetInt32() > 10
                ? ("OutOfStock", $$"""{"orderId":"{{orderId}}","available":10}""")
                : ("StockReserved", $$"""{"orderId":"{{orderId}}","reservedUntil":"2026-10-19T12:00:00Z"}""");
            await replies.BasicPublishAsync("", delivery.BasicProperties.ReplyTo!, mandatory: false,
                new BasicProperties { CorrelationId = delivery.BasicProperties.CorrelationId, Type = type }, Encoding.UTF8.GetBytes(body));
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        };
        await channel.BasicConsumeAsync("warehouse-reservations", autoAck: false, server);
        var publisher = broker.CreatePublisher(Warehouse);
        var reserve = Warehouse.FindByName("ReserveStock")!;

        var reserved = await publisher.RequestJsonAsync(reserve,
            J($$"""{"orderId":"{{Guid.NewGuid()}}","sku":"LAMP-01","quantity":2}"""), timeoutSeconds: 15);
        Assert.Equal("StockReserved", reserved.ResponseType);
        Assert.True(reserved.ResponseCheck!.Valid);

        var missing = await publisher.RequestJsonAsync(reserve,
            J($$"""{"orderId":"{{Guid.NewGuid()}}","sku":"LAMP-01","quantity":50}"""), timeoutSeconds: 15);
        Assert.Equal("OutOfStock", missing.ResponseType);
        Assert.Equal(10, missing.Response!.Value.GetProperty("available").GetInt32());
    }
}
