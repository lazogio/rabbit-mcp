// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using RabbitMcp.Samples.Orders;
using RabbitMcp.IntegrationTests.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ManagementTests(BrokerFixture broker)
{
    /// <summary>Management statistics are refreshed every few seconds; poll instead of asserting once.</summary>
    private async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        T last;
        do
        {
            last = await get();
            if (ok(last))
                return last;
            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    [Fact]
    public async Task Lists_MassTransit_queues_with_consumers_case_insensitive()
    {
        var result = await Eventually(
            () => broker.Management.ListQueuesAsync("IT-ORDER", false, false, 100, CancellationToken.None),
            r => r.Queues.Any(q => q.Name == BrokerFixture.OrderSubmittedQueue && q.Consumers >= 1));

        var queue = Assert.Single(result.Queues, q => q.Name == BrokerFixture.OrderSubmittedQueue);
        Assert.Equal(1, queue.Consumers);
        Assert.False(queue.IsErrorQueue);
        Assert.Equal("/", result.Vhost);
        Assert.All(result.Queues, q => Assert.Contains("it-order", q.Name));
    }

    [Fact]
    public async Task Error_queues_are_flagged_with_their_source_queue()
    {
        const string errorQueue = "it-manual_error";
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None))
            await channel.QueueDeclareAsync(errorQueue, durable: true, exclusive: false, autoDelete: false);

        var result = await Eventually(
            () => broker.Management.ListQueuesAsync(null, onlyErrorQueues: true, onlyNonEmpty: false, 100, CancellationToken.None),
            r => r.Queues.Any(q => q.Name == errorQueue));

        var queue = Assert.Single(result.Queues, q => q.Name == errorQueue);
        Assert.True(queue.IsErrorQueue);
        Assert.Equal("it-manual", queue.SourceQueue);
        Assert.All(result.Queues, q => Assert.True(q.IsErrorQueue));
        Assert.DoesNotContain(result.Queues, q => q.Name == BrokerFixture.OrderSubmittedQueue);
    }

    private Task<RabbitMcp.Server.Tools.ContractConsumers> Consumers(Type contract) =>
        new TopologyInspector(broker.Management).InspectAsync(broker.Registry.Get(contract), CancellationToken.None);

    [Fact]
    public async Task MassTransit_consumer_is_found_through_the_endpoint_exchange()
    {
        var result = await Eventually(() => Consumers(typeof(OrderSubmitted)), r => r.Status == "ok");

        Assert.Equal("ok", result.Status);
        var queue = Assert.Single(result.Queues, q => q.Queue == BrokerFixture.OrderSubmittedQueue);
        Assert.Equal(new[] { broker.Registry.Get(typeof(OrderSubmitted)).Exchange, BrokerFixture.OrderSubmittedQueue }, queue.Via);
        Assert.True(queue.Consumers >= 1);
    }

    [Fact]
    public async Task Problems_are_classified()
    {
        Assert.Equal("no-exchange", (await Consumers(typeof(NobodyListens))).Status);
        Assert.Equal("no-queue", (await Consumers(typeof(UnboundEvent))).Status);

        var ping = await Eventually(() => Consumers(typeof(PingNobody)), r => r.Status == "no-consumers");
        Assert.Equal("no-consumers", ping.Status);
        Assert.Contains(ping.Warnings, w => w.Contains("time out"));
    }
}
