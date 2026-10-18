// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ControlTests(BrokerFixture broker)
{
    private const string ErrorQueue = BrokerFixture.OrderCancelledQueue + "_error";

    private static string? S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static OrderSubmitted Order(Guid id, string email = "ana@example.ge") => new()
    {
        OrderId = id,
        CustomerEmail = email,
        Lines = [new OrderLine { Sku = "ABC-1", Quantity = 1, UnitPrice = 5m }],
    };

    [Fact]
    public async Task Successful_publish_is_audited_with_ids_and_payload()
    {
        var id = Guid.NewGuid();
        var result = await broker.Publisher.PublishAsync(Order(id));

        var e = Assert.Single(broker.AuditEvents(), x => S(x, "messageId") == result.MessageId);
        Assert.Equal("publish", S(e, "action"));
        Assert.Equal("sent", S(e, "outcome"));
        Assert.Equal("OrderSubmitted", S(e, "contract"));
        Assert.Equal("RabbitMcp.Samples.Orders:OrderSubmitted", S(e, "exchange"));
        Assert.Equal(id, e.GetProperty("message").GetProperty("orderId").GetGuid()); // AuditIncludePayload = true
    }

    [Fact]
    public async Task Rejected_publish_is_audited_as_error()
    {
        var marker = $"bad-{Guid.NewGuid():N}";
        await Assert.ThrowsAsync<McpException>(() => broker.Publisher.PublishAsync(Order(Guid.NewGuid(), marker)));

        var e = Assert.Single(broker.AuditEvents(), x =>
            x.TryGetProperty("message", out var m) && S(m, "customerEmail") == marker);
        Assert.Equal("error", S(e, "outcome"));
        Assert.Contains("customerEmail", S(e, "detail"));
    }

    [Fact]
    public async Task Request_is_audited_with_its_outcome()
    {
        var r = await broker.Publisher.RequestAsync(new GetOrderStatus { OrderId = BrokerFixture.KnownOrderId }, timeoutSeconds: 15);

        var e = Assert.Single(broker.AuditEvents(), x => S(x, "requestId") == r.RequestId);
        Assert.Equal("request", S(e, "action"));
        Assert.Equal("responded", S(e, "outcome"));
        Assert.Equal("reply OrderStatus", S(e, "detail"));
    }

    [Fact]
    public async Task Denied_contract_cannot_be_sent_and_the_attempt_is_audited()
    {
        var restricted = broker.CreatePublisher(new PublishPolicy(allow: null, deny: "Order*"));
        var marker = $"denied-{Guid.NewGuid():N}@example.ge";

        var ex = await Assert.ThrowsAsync<McpException>(() => restricted.PublishAsync(Order(Guid.NewGuid(), marker)));

        Assert.Contains("not allowed", ex.Message);
        var e = Assert.Single(broker.AuditEvents(), x =>
            x.TryGetProperty("message", out var m) && S(m, "customerEmail") == marker);
        Assert.Equal("error", S(e, "outcome"));
    }

    [Fact]
    public async Task Republish_is_audited_per_message()
    {
        var orderId = Guid.NewGuid();
        await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "Audit me" });
        await broker.WaitForQueueCount(ErrorQueue, 1);
        var messageId = Assert.Single((await broker.Browser.PeekAsync(ErrorQueue, 5, false, CancellationToken.None)).Messages).MessageId!;

        await broker.Browser.RepublishAsync(ErrorQueue, 5, [messageId], null, CancellationToken.None, client: "it-client 1.0");
        await broker.WaitForCancelled(orderId);

        var e = Assert.Single(broker.AuditEvents(), x => S(x, "action") == "republish" && S(x, "messageId") == messageId);
        Assert.Equal("republished", S(e, "outcome"));
        Assert.Equal(ErrorQueue, S(e, "queue"));
        Assert.Equal(BrokerFixture.OrderCancelledQueue, S(e, "exchange"));
        Assert.Equal("it-client 1.0", S(e, "client"));
        await broker.WaitForQueueCount(ErrorQueue, 0);
    }
}
