// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Security;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;

namespace RabbitMcp.IntegrationTests;

/// <summary>Where messages may (not) be moved to, and queues the server cannot inspect.</summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HardeningTests(BrokerFixture broker)
{
    private async Task<IChannel> QueueWithOneMessageAsync(string queue, IDictionary<string, object?>? headers = null)
    {
        var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.BasicPublishAsync("", queue, mandatory: false,
            new BasicProperties { MessageId = Guid.NewGuid().ToString(), Headers = headers }, """{"orderId":1}"""u8.ToArray());
        await broker.WaitForQueueCount(queue, 1);
        return channel;
    }

    [Fact]
    public async Task A_queue_with_consumers_is_never_used_as_the_dead_letter_queue()
    {
        const string source = "it-hardening-parked";
        await using var channel = await QueueWithOneMessageAsync(source);

        // it-order-submitted is consumed by a MassTransit endpoint: "parking" there would hand the message to it.
        var ex = await Assert.ThrowsAsync<McpException>(() => broker.Browser.DeadLetterAsync(
            source, 5, null, BrokerFixture.OrderSubmittedQueue, "nope", CancellationToken.None, "it"));
        Assert.Contains("live queue", ex.Message);
        await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.CheckDeadLetterTargetAsync(source, BrokerFixture.OrderSubmittedQueue, CancellationToken.None));

        await broker.WaitForQueueCount(source, 1); // nothing moved
    }

    [Fact]
    public async Task Republishing_into_the_error_queue_itself_is_skipped_and_keeps_the_fault()
    {
        const string queue = "it-hardening-loop";
        await using var channel = await QueueWithOneMessageAsync(queue,
            new Dictionary<string, object?> { ["MT-Reason"] = "fault", ["MT-Fault-Message"] = "boom" });

        var result = await broker.Browser.RepublishAsync(queue, 5, null, null, CancellationToken.None,
            client: "it", targetQueueOverride: queue);

        Assert.Equal(0, result.Republished);
        Assert.Contains("itself", Assert.Single(result.Outcomes).Detail);
        await broker.WaitForQueueCount(queue, 1);
        var kept = Assert.Single((await broker.Browser.PeekAsync(queue, 5, false, CancellationToken.None)).Messages);
        Assert.Equal("boom", kept.Fault!.ExceptionMessage);
    }

    [Fact]
    public async Task Queues_exclusive_to_another_connection_do_not_break_search_stats_or_sending()
    {
        // Another application's exclusive queue: passive declares from our connection fail with 405 RESOURCE_LOCKED.
        await using var other = await new ConnectionFactory { Uri = new Uri(broker.Options.Value.Uri) }.CreateConnectionAsync();
        await using var otherChannel = await other.CreateChannelAsync();
        const string queue = "it-hardening-exclusive";
        await otherChannel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await otherChannel.BasicPublishAsync("", queue, mandatory: false, new BasicProperties(), """{"x":1}"""u8.ToArray());

        Assert.True(await broker.Connection.QueueExistsAsync(queue, CancellationToken.None));

        var tools = new BrokerTools(broker.Registry, PublishPolicy.AllowAll, broker.Connection, broker.Browser,
            new AccessControl(new RabbitMcpOptions()), broker.Options);
        var stat = Assert.Single((await tools.QueueStatsAsync([queue], CancellationToken.None)).Queues);
        Assert.True(stat.Exists);
        Assert.Null(stat.ReadyMessages);

        var search = new SearchTools(broker.Browser, broker.Connection, broker.Options, new ServiceCollection().BuildServiceProvider());
        var found = await search.FindMessageAsync(contains: "x", queues: [queue, "it-hardening-missing"]);
        Assert.Equal("skipped: exclusive to another connection", found.Queues.Single(q => q.Queue == queue).Status);
        Assert.Equal("missing", found.Queues.Single(q => q.Queue == "it-hardening-missing").Status);
    }
}
