// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.IntegrationTests.Contracts;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PublishTests(BrokerFixture broker)
{
    private static OrderSubmitted Order(Guid id) => new()
    {
        OrderId = id,
        CustomerEmail = "ana@example.ge",
        Lines = [new OrderLine { Sku = "ABC-1", Quantity = 2, UnitPrice = 9.99m }],
    };

    [Fact]
    public async Task Published_event_reaches_a_MassTransit_consumer_with_the_same_message_id()
    {
        var id = Guid.NewGuid();

        var result = await broker.Publisher.PublishAsync(Order(id));
        var received = await broker.WaitForSubmitted(id);

        Assert.True(result.Routed);
        Assert.Equal(Guid.Parse(result.MessageId), received.MessageId);
        Assert.Equal("ana@example.ge", received.Message.CustomerEmail);
        Assert.Equal(9.99m, Assert.Single(received.Message.Lines).UnitPrice);
        Assert.Equal("1", received.Headers["Contract-Version"]?.ToString());
    }

    [Fact]
    public async Task Invalid_message_is_rejected_and_nothing_is_sent()
    {
        var id = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Publisher.PublishAsync(Order(id) with { CustomerEmail = "not-an-email" }));

        Assert.Contains("customerEmail", ex.Message);
        await Task.Delay(1000);
        Assert.False(broker.WasSubmitted(id));
    }

    [Fact]
    public async Task Missing_exchange_is_an_error_not_a_lost_message()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Publisher.PublishAsync(new NobodyListens { Id = Guid.NewGuid() }));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Exchange_deleted_after_a_cached_check_is_reported_and_forgotten()
    {
        var exchange = broker.Registry.Get(typeof(VolatileEvent)).Exchange;
        const string queue = "it-volatile";
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);

        async Task Recreate()
        {
            await channel.ExchangeDeclareAsync(exchange, RabbitMQ.Client.ExchangeType.Fanout, durable: true, autoDelete: false);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, exchange, routingKey: "");
        }

        await Recreate();
        Assert.True((await broker.Publisher.PublishAsync(new VolatileEvent { Id = Guid.NewGuid() })).Routed); // caches "exists"

        await channel.ExchangeDeleteAsync(exchange);
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Publisher.PublishAsync(new VolatileEvent { Id = Guid.NewGuid() }));
        Assert.Contains("no longer exists", ex.Message);

        // Invalidated: the next publish checks again and fails cleanly before sending.
        var again = await Assert.ThrowsAsync<McpException>(() =>
            broker.Publisher.PublishAsync(new VolatileEvent { Id = Guid.NewGuid() }));
        Assert.Contains("does not exist", again.Message);

        await Recreate();
        Assert.True((await broker.Publisher.PublishAsync(new VolatileEvent { Id = Guid.NewGuid() })).Routed);
        await broker.WaitForQueueCount(queue, 2);
    }

    [Fact]
    public async Task Unbound_exchange_is_reported_as_returned()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Publisher.PublishAsync(new UnboundEvent { Id = Guid.NewGuid() }));

        Assert.Contains("no queue is bound", ex.Message);
    }
}

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RequestResponseTests(BrokerFixture broker)
{
    [Fact]
    public async Task Request_gets_the_consumers_reply_validated_and_attributed()
    {
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = BrokerFixture.KnownOrderId }, timeoutSeconds: 15);

        Assert.Equal("responded", r.Status);
        Assert.Equal("OrderStatus", r.ResponseType);
        Assert.True(r.ResponseCheck!.Valid);
        Assert.Equal("Shipped", r.Response!.Value.GetProperty("status").GetString());
        Assert.Contains(BrokerFixture.OrderStatusQueue, r.RespondedBy!.Address);
    }

    [Fact]
    public async Task Alternative_reply_type_is_recognised()
    {
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = Guid.NewGuid() }, timeoutSeconds: 15);

        Assert.Equal("responded", r.Status);
        Assert.Equal("OrderNotFound", r.ResponseType);
        Assert.True(r.ResponseCheck!.Valid);
    }

    [Fact]
    public async Task Reply_that_violates_its_contract_is_flagged()
    {
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = BrokerFixture.BadReplyOrderId }, timeoutSeconds: 15);

        Assert.Equal("OrderStatus", r.ResponseType);
        Assert.False(r.ResponseCheck!.Valid);
        Assert.Contains(r.ResponseCheck.Errors, e => e.StartsWith("status:"));
    }

    [Fact]
    public async Task Consumer_exception_comes_back_as_fault()
    {
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = BrokerFixture.FailingOrderId }, timeoutSeconds: 15);

        Assert.Equal("faulted", r.Status);
        var ex = Assert.Single(r.Fault!.Exceptions);
        Assert.Equal("System.InvalidOperationException", ex.ExceptionType);
        Assert.Equal("Order database unavailable", ex.Message);
    }

    [Fact]
    public async Task No_consumer_means_timeout()
    {
        var r = await broker.Publisher.RequestAsync(new PingNobody { Id = Guid.NewGuid() }, timeoutSeconds: 1);

        Assert.Equal("timeout", r.Status);
        Assert.Null(r.Response);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public async Task Concurrent_requests_are_correlated_to_their_own_replies()
    {
        var known = Enumerable.Range(0, 5).Select(_ => BrokerFixture.KnownOrderId);
        var unknown = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid());
        var ids = known.Concat(unknown).ToArray();

        var results = await Task.WhenAll(ids.Select(id =>
            broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = id }, timeoutSeconds: 15)));

        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i] == BrokerFixture.KnownOrderId ? "OrderStatus" : "OrderNotFound", results[i].ResponseType);
            Assert.Equal(ids[i], results[i].Response!.Value.GetProperty("orderId").GetGuid());
        }
        Assert.Equal(ids.Length, results.Select(r => r.RequestId).Distinct().Count());
    }
}
