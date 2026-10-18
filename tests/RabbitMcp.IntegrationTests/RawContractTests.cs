// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using RabbitMcp.IntegrationTests.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMcp.IntegrationTests;

/// <summary>
/// Format = Raw against services written with plain RabbitMQ.Client: no envelope, routing keys, the default
/// exchange, RabbitMQ RPC (reply_to/correlation_id) and RabbitMQ dead-lettering (x-death).
/// </summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RawContractTests(BrokerFixture broker)
{
    private static string Text(object? header) => header is byte[] b ? Encoding.UTF8.GetString(b) : header?.ToString() ?? "";

    /// <summary>An audit field; null fields are not written.</summary>
    private static string? Field(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    [Fact]
    public async Task Raw_event_is_plain_json_routed_by_its_routing_key()
    {
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.ExchangeDeclareAsync(RawTopology.InventoryExchange, ExchangeType.Topic, durable: true, autoDelete: false);
        await channel.QueueDeclareAsync("it-stock-audit", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync("it-stock-audit", RawTopology.InventoryExchange, "stock.#");
        await channel.QueueDeclareAsync("it-price-audit", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync("it-price-audit", RawTopology.InventoryExchange, "price.*");

        var result = await broker.Publisher.PublishAsync(new RawStockAdjusted { Sku = "LAMP-01", Delta = -2 });

        Assert.Equal("raw", result.Format);
        Assert.Equal("stock.adjusted", result.RoutingKey);
        Assert.Null(result.ConversationId);
        await broker.WaitForQueueCount("it-stock-audit", 1);
        await broker.WaitForQueueCount("it-price-audit", 0);

        var delivery = (await channel.BasicGetAsync("it-stock-audit", autoAck: true))!;
        Assert.Equal("""{"sku":"LAMP-01","delta":-2}""", Encoding.UTF8.GetString(delivery.Body.Span));
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal("stock.adjusted", delivery.BasicProperties.Type);
        Assert.Equal(result.MessageId, delivery.BasicProperties.MessageId);
        Assert.Equal("rabbit-mcp-it", delivery.BasicProperties.AppId);
        Assert.Equal("1", Text(delivery.BasicProperties.Headers!["Contract-Version"]));
        Assert.Equal("stock.adjusted", delivery.RoutingKey);

        // contract_consumers follows the topic binding that matches, not the other one.
        var topology = await new TopologyInspector(broker.Management)
            .InspectAsync(broker.Registry.Get(typeof(RawStockAdjusted)), CancellationToken.None);
        Assert.Equal(new[] { "it-stock-audit" }, topology.Queues.Select(q => q.Queue));
        Assert.Equal("no-consumers", topology.Status);
    }

    [Fact]
    public async Task Raw_send_to_a_missing_queue_is_an_error_not_a_lost_message()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => broker.Publisher.PublishAsync(new RawNobody { Id = Guid.NewGuid() }));
        Assert.Contains("Queue 'it-raw-nobody' does not exist", ex.Message);
        Assert.Contains(broker.AuditEvents(), e => Field(e, "contract") == "RawNobody" && Field(e, "outcome") == "error"
                                                   && Field(e, "queue") == "it-raw-nobody");
    }

    [Fact]
    public async Task Raw_request_is_answered_by_a_plain_rpc_server()
    {
        var requests = new ConcurrentQueue<IReadOnlyBasicProperties>();
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await using var replies = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(RawTopology.QuotesQueue, durable: false, exclusive: false, autoDelete: true);

        // A pricing service without MassTransit: reads the plain JSON, answers to reply_to with the correlation_id
        // and names the reply type in the AMQP type property (or forgets to).
        var server = new AsyncEventingBasicConsumer(channel);
        server.ReceivedAsync += async (_, delivery) =>
        {
            requests.Enqueue(new BasicProperties(delivery.BasicProperties));
            var sku = JsonDocument.Parse(delivery.Body).RootElement.GetProperty("sku").GetString();
            (string? Type, string Body) reply = sku switch
            {
                "UNKNOWN" => ("RawNoQuote", $$"""{"sku":"{{sku}}"}"""),
                "NOTYPE" => (null, $$"""{"sku":"{{sku}}"}"""),
                _ => ("RawQuote", $$"""{"sku":"{{sku}}","price":12.5}"""),
            };
            var props = new BasicProperties
            {
                CorrelationId = delivery.BasicProperties.CorrelationId, Type = reply.Type, ContentType = "application/json",
                AppId = "it-pricing",
            };
            await replies.BasicPublishAsync("", delivery.BasicProperties.ReplyTo!, mandatory: false, props,
                Encoding.UTF8.GetBytes(reply.Body));
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        };
        await channel.BasicConsumeAsync(RawTopology.QuotesQueue, autoAck: false, server);

        var workflow = Guid.NewGuid().ToString();
        var quote = await broker.Publisher.RequestAsync(new RawGetQuote { Sku = "LAMP-01" }, timeoutSeconds: 15, correlationId: workflow);

        Assert.Equal("responded", quote.Status);
        Assert.Equal("RawQuote", quote.ResponseType);
        Assert.True(quote.ResponseCheck!.Valid);
        Assert.Equal(12.5m, quote.Response!.Value.GetProperty("price").GetDecimal());
        Assert.Equal("it-pricing", quote.RespondedBy!.ProcessName);
        Assert.Equal(new[] { "RawQuote" }, quote.ResponseMessageTypes);

        var request = Assert.Single(requests);
        Assert.Equal(quote.RequestId, request.CorrelationId); // RPC: correlation_id = request id
        Assert.StartsWith("rabbit-mcp-it_", request.ReplyTo);
        Assert.Equal("urn:message:RabbitMcp.IntegrationTests.Contracts:RawGetQuote", request.Type);
        Assert.Equal(workflow, Text(request.Headers!["X-Correlation-Id"]));
        Assert.Equal("15000", request.Expiration);

        var missing = await broker.Publisher.RequestAsync(new RawGetQuote { Sku = "UNKNOWN" }, timeoutSeconds: 15);
        Assert.Equal("RawNoQuote", missing.ResponseType);
        Assert.True(missing.ResponseCheck!.Valid);

        var untyped = await broker.Publisher.RequestAsync(new RawGetQuote { Sku = "NOTYPE" }, timeoutSeconds: 15);
        Assert.Equal("responded", untyped.Status);
        Assert.Null(untyped.ResponseType);
        Assert.Contains("no AMQP type", untyped.Note);
    }

    [Fact]
    public async Task Message_dead_lettered_by_RabbitMQ_goes_back_to_the_queue_that_rejected_it()
    {
        const string work = "it-raw-work";
        const string dlq = "it-raw-work.dlq";
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync(work, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = dlq,
        });

        // The plain-JSON worker rejects the message; RabbitMQ moves it to the dead-letter queue with x-death.
        var id = Guid.NewGuid().ToString();
        var body = """{"sku":"LAMP-01","delta":1}"""u8.ToArray();
        await channel.BasicPublishAsync("", work, mandatory: false,
            new BasicProperties { MessageId = id, Type = "stock.adjusted", ContentType = "application/json" }, body);
        await broker.WaitForQueueCount(work, 1);
        var rejected = (await channel.BasicGetAsync(work, autoAck: false))!;
        await channel.BasicRejectAsync(rejected.DeliveryTag, requeue: false);
        await broker.WaitForQueueCount(dlq, 1);

        var peeked = Assert.Single((await broker.Browser.PeekAsync(dlq, 5, false, CancellationToken.None)).Messages);
        Assert.Equal(id, peeked.MessageId);
        Assert.Equal("queue:" + work, peeked.RepublishTarget);
        Assert.Null(peeked.RepublishWarning);
        var death = Assert.Single(peeked.DeadLettered!);
        Assert.Equal(work, death.Queue);
        Assert.Equal("rejected", death.Reason);
        Assert.Equal("RawStockAdjusted", peeked.Contract!.Contract);
        Assert.True(peeked.Contract.Valid);
        Assert.DoesNotContain(peeked.Headers.Keys, k => k.StartsWith("x-", StringComparison.Ordinal));

        var result = await broker.Browser.RepublishAsync(dlq, 5, [id], null, CancellationToken.None, client: "it");

        Assert.Equal(1, result.Republished);
        Assert.Equal("queue:" + work, Assert.Single(result.Outcomes).TargetExchange);
        await broker.WaitForQueueCount(dlq, 0);
        await broker.WaitForQueueCount(work, 1);

        var back = (await channel.BasicGetAsync(work, autoAck: true))!;
        Assert.Equal(body, back.Body.ToArray()); // the original bytes
        Assert.Equal(id, back.BasicProperties.MessageId);
        Assert.Equal(dlq, Text(back.BasicProperties.Headers!["X-Republished-From"]));
        Assert.False(back.BasicProperties.Headers.ContainsKey("x-death"));
        Assert.Contains(broker.AuditEvents(), e => Field(e, "action") == "republish" && Field(e, "exchange") == "queue:" + work);
    }
}
