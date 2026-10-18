// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using ModelContextProtocol;
using RabbitMcp.Samples.Orders;

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
    public async Task Peeking_a_queue_with_consumers_requires_force()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.PeekAsync(BrokerFixture.OrderSubmittedQueue, 1, force: false, CancellationToken.None));
        Assert.Contains("active consumer", ex.Message);

        var forced = await broker.Browser.PeekAsync(BrokerFixture.OrderSubmittedQueue, 1, force: true, CancellationToken.None);
        Assert.Equal(BrokerFixture.OrderSubmittedQueue, forced.Queue);
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

        var result = await broker.Browser.DeadLetterAsync(ErrorQueue, 5, [id], null, "cannot be fixed", CancellationToken.None, "it");

        Assert.Equal(1, result.Moved);
        Assert.Equal(deadQueue, result.DeadLetterQueue);
        await broker.WaitForQueueCount(ErrorQueue, 0);
        await broker.WaitForQueueCount(deadQueue, 1);

        // Nothing is lost: fault details stay, ours are added, and the target is still the original queue.
        var parked = Assert.Single((await broker.Browser.PeekAsync(deadQueue, 5, false, CancellationToken.None)).Messages);
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
        await broker.WaitForQueueCount(deadQueue, 0);
    }

    [Fact]
    public async Task Dead_lettering_refuses_queues_with_consumers()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.DeadLetterAsync(BrokerFixture.OrderSubmittedQueue, 1, null, null, null, CancellationToken.None));
        Assert.Contains("active consumer", ex.Message);
    }
}
