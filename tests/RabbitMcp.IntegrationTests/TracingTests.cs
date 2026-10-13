using System.Diagnostics;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.IntegrationTests;

/// <summary>Proves that MassTransit 8 continues rabbit-mcp's traces (MT-Activity-Id / traceparent).</summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TracingTests(BrokerFixture broker)
{
    private const string ErrorQueue = BrokerFixture.OrderCancelledQueue + "_error";

    private static string? Tag(Activity a, string key) => a.GetTagItem(key)?.ToString();

    [Fact]
    public async Task Consumer_span_joins_the_publish_trace()
    {
        var id = Guid.NewGuid();
        var result = await broker.Publisher.PublishAsync(new OrderSubmitted
        {
            OrderId = id, CustomerEmail = "trace@example.ge",
            Lines = [new OrderLine { Sku = "ABC-1", Quantity = 1, UnitPrice = 1m }],
        });
        var received = await broker.WaitForSubmitted(id);

        var publish = broker.Span(Telemetry.Name, a => Tag(a, "messaging.message.id") == result.MessageId);
        Assert.Equal(ActivityKind.Producer, publish.Kind);
        Assert.Equal($"publish {result.Exchange}", publish.DisplayName);
        Assert.Equal("rabbitmq", Tag(publish, "messaging.system"));
        Assert.Equal(publish.TraceId, received.TraceId);
        Assert.Equal(publish.Id, received.TransportHeaders["MT-Activity-Id"]?.ToString());
    }

    [Fact]
    public async Task Request_consumer_joins_the_request_trace()
    {
        var id = Guid.NewGuid(); // unknown order → OrderNotFound, consume trace recorded per id
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = id }, timeoutSeconds: 15);

        var request = broker.Span(Telemetry.Name, a => Tag(a, "rabbitmcp.request_id") == r.RequestId);
        Assert.Equal(ActivityKind.Client, request.Kind);
        Assert.Equal("responded", Tag(request, "rabbitmcp.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, request.Status);
        Assert.Equal(request.TraceId, broker.StatusTraces[id]);
    }

    [Fact]
    public async Task Republish_starts_a_new_trace_linked_to_the_original()
    {
        var orderId = Guid.NewGuid();
        var original = await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Trace me" });
        var originalSpan = broker.Span(Telemetry.Name, a => Tag(a, "messaging.message.id") == original.MessageId);
        await broker.WaitForQueueCount(ErrorQueue, 1);

        await broker.Browser.RepublishAsync(ErrorQueue, 5, [original.MessageId], null, CancellationToken.None);
        var received = await broker.WaitForCancelled(orderId);

        var republish = broker.Span(Telemetry.Name, a =>
            a.DisplayName.StartsWith("republish ") && Tag(a, "messaging.message.id") == original.MessageId);
        Assert.NotEqual(originalSpan.TraceId, republish.TraceId);
        Assert.Contains(republish.Links, l => l.Context.TraceId == originalSpan.TraceId);
        Assert.Equal(republish.TraceId, received.TraceId);
        Assert.Equal(ErrorQueue, Tag(republish, "rabbitmcp.source_queue"));
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }
}
