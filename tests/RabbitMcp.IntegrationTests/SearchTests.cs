// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SearchTests(BrokerFixture broker)
{
    private const string ErrorQueue = BrokerFixture.OrderCancelledQueue + "_error";

    private SearchTools Search() => new(broker.Browser, broker.Connection, broker.Options,
        new ServiceCollection().AddSingleton(broker.Management).BuildServiceProvider());

    private static async Task<T> Eventually<T>(Func<Task<T>> get, Func<T, bool> ok)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
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
    public async Task Faulted_message_is_found_by_its_content_id_and_audit()
    {
        var orderId = Guid.NewGuid();
        var published = await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Find me" });
        await broker.WaitForQueueCount(ErrorQueue, 1);
        var search = Search();

        // By text, in named queues: one is consumed (skipped), one does not exist.
        var byText = await search.FindMessageAsync(contains: orderId.ToString(),
            queues: [ErrorQueue, BrokerFixture.OrderSubmittedQueue, "it-no-such-queue"]);
        var found = Assert.Single(byText.Matches);
        Assert.Equal(ErrorQueue, found.Queue);
        Assert.Equal(published.MessageId, found.Message.MessageId);
        Assert.Contains("Cancellation service unavailable", found.Message.Fault!.ExceptionMessage);
        Assert.Equal("skipped: has consumers", byText.Queues.Single(q => q.Queue == BrokerFixture.OrderSubmittedQueue).Status);
        Assert.Equal("missing", byText.Queues.Single(q => q.Queue == "it-no-such-queue").Status);

        Assert.Single((await search.FindMessageAsync(messageId: published.MessageId, queues: [ErrorQueue])).Matches);

        // Without a list: every error queue the Management API knows (its statistics lag, so poll).
        var everywhere = await Eventually(() => search.FindMessageAsync(contains: orderId.ToString()), r => r.Matches.Count == 1);
        Assert.Equal(ErrorQueue, Assert.Single(everywhere.Matches).Queue);

        // The audit says who sent it.
        var activity = AuditReader.Read(broker.AuditPath, new AuditQuery(Id: published.MessageId));
        var sent = Assert.Single(activity.Events);
        Assert.Equal((AuditActions.Publish, "sent", "OrderCancelled"), (sent.Action, sent.Outcome, sent.Contract));

        // Clean up for other tests.
        await broker.Browser.RepublishAsync(ErrorQueue, 5, [published.MessageId], null, CancellationToken.None);
        await broker.WaitForCancelled(orderId);
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

    [Fact]
    public async Task Text_past_the_display_limit_is_found()
    {
        const string queue = "it-long_error";
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        var body = System.Text.Encoding.UTF8.GetBytes(new string('x', broker.Options.Value.MaxPayloadChars + 1000) + "needle-at-the-end");
        await channel.BasicPublishAsync("", queue, mandatory: false, new RabbitMQ.Client.BasicProperties { MessageId = "long-1" }, body);
        await broker.WaitForQueueCount(queue, 1);

        var found = await Search().FindMessageAsync(contains: "NEEDLE-AT-THE-END", queues: [queue]);

        Assert.True(Assert.Single(found.Matches).Message.Truncated);
        await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
    }
}
