// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ErrorQueueTests(BrokerFixture broker)
{
    private const string ErrorQueue = BrokerFixture.OrderCancelledQueue + "_error";

    /// <summary>
    /// The consumer fails the first delivery → MassTransit moves it to _error. We peek it (it stays),
    /// republish it (only it), and the consumer then processes it successfully.
    /// </summary>
    [Fact]
    public async Task Faulted_message_can_be_peeked_and_republished_without_loss()
    {
        var orderId = Guid.NewGuid();
        await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Customer request" });
        await broker.WaitForQueueCount(ErrorQueue, 1);

        // Peek: fault details, contract check and target; the message stays in the queue.
        var peek = await broker.Browser.PeekAsync(ErrorQueue, count: 5, force: false, CancellationToken.None);
        var faulted = Assert.Single(peek.Messages);
        Assert.Equal("System.InvalidOperationException", faulted.Fault?.ExceptionType);
        Assert.Equal("Cancellation service unavailable", faulted.Fault?.ExceptionMessage);
        Assert.Equal(BrokerFixture.OrderCancelledQueue, faulted.RepublishTarget);
        Assert.True(faulted.Contract?.Valid);
        await broker.WaitForQueueCount(ErrorQueue, 1);

        // Preview shows an explicit target override (regression: it used to show the resolved one).
        var preview = await broker.Browser.PeekAsync(ErrorQueue, 1, force: false, CancellationToken.None, "some-other-exchange");
        Assert.Equal("some-other-exchange", Assert.Single(preview.Messages).RepublishTarget);

        // Republish only this message; it is acked in _error only after the broker confirmed.
        var result = await broker.Browser.RepublishAsync(ErrorQueue, count: 5, [faulted.MessageId!], null, CancellationToken.None);
        Assert.Equal(1, result.Republished);
        Assert.Equal("republished", Assert.Single(result.Outcomes).Status);

        var received = await broker.WaitForCancelled(orderId);
        Assert.Equal("Customer request", received.Message.Reason);
        Assert.Equal(ErrorQueue, ToText(received.TransportHeaders["X-Republished-From"]));
        Assert.DoesNotContain(received.TransportHeaders.Keys, k => k.StartsWith("MT-Fault-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(received.TransportHeaders.Keys, k => k.Equals("MT-Reason", StringComparison.OrdinalIgnoreCase));
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

    /// <summary>A queue with messages in it, published directly: what a service left there.</summary>
    private async Task<string> ErrorQueueWith(BasicProperties properties, params string[] bodies)
    {
        var owner = "it-" + Guid.NewGuid().ToString("N");
        var queue = owner + "_error";
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(owner, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        foreach (var body in bodies)
            await channel.BasicPublishAsync("", queue, mandatory: false, properties, System.Text.Encoding.UTF8.GetBytes(body));
        await broker.WaitForQueueCount(queue, (uint)bodies.Length);
        return queue;
    }

    [Fact]
    public async Task A_parked_message_outlives_its_ttl_and_gets_it_back_when_republished()
    {
        var queue = await ErrorQueueWith(new BasicProperties { MessageId = "ttl", Expiration = "1500" }, "{}");
        var owner = queue[..^"_error".Length];
        var parked = owner + "_dead";

        var moved = await broker.Browser.DeadLetterAsync(queue, 1, null, null, "keep", CancellationToken.None);
        Assert.Equal(1, moved.Moved);

        await Task.Delay(2500);
        var kept = Assert.Single((await broker.Browser.PeekAsync(parked, 5, false, CancellationToken.None)).Messages);
        Assert.Equal("1500", kept.Headers[HeaderNames.OriginalExpiration]);

        var back = await broker.Browser.RepublishAsync(parked, 1, null, null, CancellationToken.None, targetQueueOverride: owner);
        Assert.Equal(1, back.Republished);

        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        var live = await channel.BasicGetAsync(owner, autoAck: true);
        Assert.Equal("1500", live?.BasicProperties.Expiration);
        Assert.DoesNotContain(HeaderNames.OriginalExpiration, live!.BasicProperties.Headers!.Keys);
    }

    [Fact]
    public async Task A_message_with_a_user_id_can_be_moved()
    {
        var queue = await ErrorQueueWith(new BasicProperties { MessageId = "user", UserId = "rabbitmq" }, "{}");
        var owner = queue[..^"_error".Length];

        var moved = await broker.Browser.DeadLetterAsync(queue, 1, null, null, null, CancellationToken.None);

        Assert.Equal(1, moved.Moved);
        var kept = Assert.Single((await broker.Browser.PeekAsync(owner + "_dead", 5, false, CancellationToken.None)).Messages);
        Assert.Equal("rabbitmq", kept.Headers[HeaderNames.OriginalUserId]);
    }

    [Fact]
    public async Task A_poison_message_hides_nothing_else_in_the_queue()
    {
        var queue = await ErrorQueueWith(new BasicProperties { Timestamp = new AmqpTimestamp(long.MaxValue) },
            """{"a":"\ud800"}""", new string('[', 60) + new string(']', 60), """{"fine":true}""");

        var peek = await broker.Browser.PeekAsync(queue, 5, false, CancellationToken.None);

        Assert.Equal(3, peek.Returned);
        Assert.NotNull(peek.Messages[2].Payload);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(peek, ToolJson.Options);
        Assert.Equal(3, json.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Streams_are_refused_without_closing_the_connection_tools_share()
    {
        var stream = "it-stream-" + Guid.NewGuid().ToString("N");
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(stream, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "stream" });

        var peek = await Assert.ThrowsAsync<StreamQueueException>(() => broker.Browser.PeekAsync(stream, 5, false, CancellationToken.None));
        Assert.Contains("read_stream", peek.Message);
        Assert.True(channel.IsOpen);

        // The browsing connection is back at once, and a move never reaches the connection sends use.
        var error = await ErrorQueueWith(new BasicProperties(), "{}");
        Assert.Equal(1, (await broker.Browser.PeekAsync(error, 5, false, CancellationToken.None)).Returned);
        await Assert.ThrowsAnyAsync<McpException>(() => broker.Browser.DeadLetterAsync(stream, 1, null, "somewhere_dead", null, CancellationToken.None));
        Assert.True(channel.IsOpen);

        var search = new SearchTools(broker.Browser, broker.Connection, broker.Options, new ServiceCollection().BuildServiceProvider());
        var found = await search.FindMessageAsync(contains: "x", queues: [stream, error]);
        Assert.StartsWith("skipped: a stream", found.Queues.Single(q => q.Queue == stream).Status);
        Assert.Equal("searched", found.Queues.Single(q => q.Queue == error).Status);
    }

    [Fact]
    public async Task Sends_end_during_a_memory_alarm_and_reads_keep_working()
    {
        var queue = await ErrorQueueWith(new BasicProperties(), "{}");
        await broker.RabbitmqctlAsync("set_vm_memory_high_watermark", "absolute", "1");
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var message = Accepted();

            await Assert.ThrowsAsync<McpException>(() => broker.Publisher.PublishAsync(message));
            var blocked = await Assert.ThrowsAsync<McpException>(() => broker.Publisher.PublishAsync(message));
            Assert.Contains("blocking publishers", blocked.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");

            Assert.Equal(1, (await broker.Browser.PeekAsync(queue, 5, false, CancellationToken.None)).Returned);
        }
        finally
        {
            await broker.RabbitmqctlAsync("set_vm_memory_high_watermark", "0.4");
        }

        await WaitUntilSendsWork();
    }

    /// <summary>A message the test consumer takes without a fault, so it leaves nothing in the shared error queue.</summary>
    private static OrderSubmitted Accepted() => new()
    {
        OrderId = Guid.NewGuid(),
        CustomerEmail = "alarm@example.com",
        Lines = [new OrderLine { Sku = "ABC-1", Quantity = 1, UnitPrice = 1m }],
    };

    private async Task WaitUntilSendsWork()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await broker.Publisher.PublishAsync(Accepted());
                return;
            }
            catch (McpException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private static string? ToText(object? value) =>
        value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : value?.ToString();

    [Fact]
    public async Task Republish_to_a_missing_exchange_keeps_the_message()
    {
        var orderId = Guid.NewGuid();
        await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Stays in error" });
        await broker.WaitForQueueCount(ErrorQueue, 1);
        var id = Assert.Single((await broker.Browser.PeekAsync(ErrorQueue, 5, false, CancellationToken.None)).Messages).MessageId!;

        var result = await broker.Browser.RepublishAsync(ErrorQueue, 5, [id], "no-such-exchange", CancellationToken.None);

        Assert.Equal(0, result.Republished);
        Assert.Equal("failed", Assert.Single(result.Outcomes).Status);
        await broker.WaitForQueueCount(ErrorQueue, 1);

        // Clean up for other tests: republish to the real target.
        await broker.Browser.RepublishAsync(ErrorQueue, 5, [id], null, CancellationToken.None);
        await broker.WaitForCancelled(orderId);
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

    [Fact]
    public async Task Selected_messages_are_found_behind_the_head_without_count()
    {
        var orderIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var orderId in orderIds)
            await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Behind the head" });
        await broker.WaitForQueueCount(ErrorQueue, 3);
        var ids = (await broker.Browser.PeekAsync(ErrorQueue, 5, false, CancellationToken.None)).Messages
            .Select(m => m.MessageId!).ToArray();
        var tools = new RepublishTools(broker.Browser, broker.Options, TimeProvider.System);

        var byId = await tools.RepublishFromErrorAsync(ErrorQueue, confirm: true, messageIds: [ids[2]]);
        Assert.Equal(1, byId.Republished);
        await broker.WaitForQueueCount(ErrorQueue, 2);

        var byFilter = await tools.RepublishFromErrorAsync(ErrorQueue, confirm: true, contract: nameof(OrderCancelled));
        Assert.Equal(2, byFilter.Republished);

        foreach (var orderId in orderIds)
            await broker.WaitForCancelled(orderId);
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

    [Fact]
    public async Task Peeking_a_queue_with_consumers_requires_force()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.PeekAsync(BrokerFixture.OrderSubmittedQueue, 1, force: false, CancellationToken.None));
        Assert.Contains("active consumer", ex.Message);

        var forced = await broker.Browser.PeekAsync(BrokerFixture.OrderSubmittedQueue, 1, force: true, CancellationToken.None);
        Assert.Equal(BrokerFixture.OrderSubmittedQueue, forced.Queue);
    }

    [Fact]
    public async Task Republishing_from_a_queue_with_consumers_requires_force()
    {
        const string queue = "it-busy_error";
        const string target = "it-busy";
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync(target, durable: true, exclusive: false, autoDelete: false);
        foreach (var id in new[] { "busy-1", "busy-2" })
            await channel.BasicPublishAsync("", queue, mandatory: false, new RabbitMQ.Client.BasicProperties { MessageId = id }, "{}"u8.ToArray());
        await broker.WaitForQueueCount(queue, 2);
        await channel.BasicQosAsync(0, 1, global: false, CancellationToken.None); // the consumer holds one message, the other stays ready
        await channel.BasicConsumeAsync(queue, autoAck: false, new RabbitMQ.Client.Events.AsyncEventingBasicConsumer(channel), CancellationToken.None);
        await broker.WaitForQueueCount(queue, 1);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.RepublishAsync(queue, 1, null, null, CancellationToken.None, targetQueueOverride: target));
        Assert.Contains("active consumer", ex.Message);
        await broker.WaitForQueueCount(target, 0);

        var forced = await broker.Browser.RepublishAsync(queue, 1, null, null, CancellationToken.None, targetQueueOverride: target, force: true);
        Assert.Equal(1, forced.Republished);
        await broker.WaitForQueueCount(target, 1);

        await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
        await channel.QueueDeleteAsync(target, ifUnused: false, ifEmpty: false);
    }

    [Fact]
    public async Task Preview_with_message_ids_shows_those_messages()
    {
        var orderIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var orderId in orderIds)
            await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Previewed by id" });
        await broker.WaitForQueueCount(ErrorQueue, 3);
        var ids = (await broker.Browser.PeekAsync(ErrorQueue, 5, false, CancellationToken.None)).Messages
            .Select(m => m.MessageId!).ToArray();
        var tools = new RepublishTools(broker.Browser, broker.Options, TimeProvider.System);

        var republish = await tools.RepublishFromErrorAsync(ErrorQueue, messageIds: [ids[2]]);
        Assert.Equal(ids[2], Assert.Single(republish.Preview!.Messages).MessageId);
        Assert.Equal(3, republish.Preview.Scanned);

        var deadLetter = await tools.DeadLetterFromErrorAsync(ErrorQueue, messageIds: [ids[1]]);
        Assert.Equal(ids[1], Assert.Single(deadLetter.Preview!.Messages).MessageId);

        // Clean up for other tests.
        await tools.RepublishFromErrorAsync(ErrorQueue, confirm: true, messageIds: ids);
        foreach (var orderId in orderIds)
            await broker.WaitForCancelled(orderId);
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

    [Fact]
    public async Task Spoofed_fault_input_address_is_not_followed_without_an_explicit_target()
    {
        // Anyone who can publish into an error queue controls its headers: this one claims to come from it-order-submitted.
        const string queue = "it-spoof_error";
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            var props = new RabbitMQ.Client.BasicProperties
            {
                MessageId = Guid.NewGuid().ToString(),
                Headers = new Dictionary<string, object?>
                {
                    ["MT-Fault-InputAddress"] = $"rabbitmq://localhost/{BrokerFixture.OrderSubmittedQueue}",
                    ["MT-Reason"] = "fault",
                },
            };
            await channel.BasicPublishAsync("", queue, mandatory: false, props, "{}"u8.ToArray());
        }
        await broker.WaitForQueueCount(queue, 1);

        var preview = Assert.Single((await broker.Browser.PeekAsync(queue, 5, false, CancellationToken.None)).Messages);
        Assert.Contains("targetExchange", preview.RepublishWarning);

        var result = await broker.Browser.RepublishAsync(queue, 5, null, null, CancellationToken.None);

        Assert.Equal(0, result.Republished);
        Assert.Equal("skipped", Assert.Single(result.Outcomes).Status);
        await broker.WaitForQueueCount(queue, 1);
    }

    [Fact]
    public async Task Poison_message_is_parked_with_all_its_headers_and_can_come_back()
    {
        const string deadQueue = BrokerFixture.OrderCancelledQueue + "_dead";
        var orderId = Guid.NewGuid();
        await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Parked" });
        await broker.WaitForQueueCount(ErrorQueue, 1);
        var id = Assert.Single((await broker.Browser.PeekAsync(ErrorQueue, 5, false, CancellationToken.None)).Messages).MessageId!;
        // Other tests park messages here too (e.g. resend_corrected): count relative to what is already there.
        var before = (await broker.Connection.GetQueueAsync(deadQueue, CancellationToken.None))?.MessageCount ?? 0;

        var result = await broker.Browser.DeadLetterAsync(ErrorQueue, 5, [id], null, "cannot be fixed", CancellationToken.None, "it");

        Assert.Equal(1, result.Moved);
        Assert.Equal(deadQueue, result.DeadLetterQueue);
        await broker.WaitForQueueCount(ErrorQueue, 0);
        await broker.WaitForQueueCount(deadQueue, before + 1);

        // Nothing is lost: fault details stay, ours are added, and the target is still the original queue.
        var parked = Assert.Single((await broker.Browser.PeekAsync(deadQueue, 50, false, CancellationToken.None)).Messages,
            m => m.MessageId == id);
        Assert.Equal(id, parked.MessageId);
        Assert.NotNull(parked.Fault);
        Assert.Equal(ErrorQueue, parked.Headers["X-Dead-Lettered-From"]);
        Assert.Equal("cannot be fixed", parked.Headers["X-Dead-Letter-Reason"]);
        Assert.Equal(BrokerFixture.OrderCancelledQueue, parked.RepublishTarget);
        Assert.Null(parked.RepublishWarning);

        var audit = broker.AuditEvents();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "deadletter"
                                    && e.GetProperty("outcome").GetString() == "moved");

        // ... and it can be brought back from the dead-letter queue.
        await broker.Browser.RepublishAsync(deadQueue, 5, [id], null, CancellationToken.None);
        await broker.WaitForCancelled(orderId);
        await broker.WaitForQueueCount(deadQueue, before);
    }

    [Fact]
    public async Task Dead_lettering_refuses_queues_with_consumers()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.DeadLetterAsync(BrokerFixture.OrderSubmittedQueue, 1, null, null, null, CancellationToken.None));
        Assert.Contains("active consumer", ex.Message);
    }
}
