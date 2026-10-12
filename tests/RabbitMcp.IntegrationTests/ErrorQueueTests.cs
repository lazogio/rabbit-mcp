using ModelContextProtocol;
using RabbitMcp.Contracts.Orders;

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
        Assert.Equal(ErrorQueue, received.Headers["X-Republished-From"]?.ToString());
        Assert.DoesNotContain(received.Headers.Keys, k => k.StartsWith("MT-Fault-", StringComparison.OrdinalIgnoreCase));
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }

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
}
