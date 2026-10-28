// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.IntegrationTests;

[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyTests(BrokerFixture broker)
{
    private TopologyManager Manager(RabbitMcpOptions? options = null) =>
        new(broker.Connection, new TopologyPolicy(options ?? new RabbitMcpOptions(), broker.Registry), broker.Audit,
            NullLogger<TopologyManager>.Instance);

    private string SubmittedExchange => broker.Registry.Get(typeof(OrderSubmitted)).Exchange;

    private static string? S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public async Task Queue_bound_to_a_contract_exchange_receives_its_events()
    {
        var queue = $"it-topology-{Guid.NewGuid():N}";
        var manager = Manager();

        // Preview: nothing is created.
        var preview = await manager.DeclareQueueAsync(queue, null, true, null, SubmittedExchange, null, confirm: false, "it", default);
        Assert.False(preview.Executed);
        Assert.Equal("would-create", preview.Outcome);
        Assert.False(await broker.Connection.QueueExistsAsync(queue, default));

        var created = await manager.DeclareQueueAsync(queue, null, true, null, SubmittedExchange, null, confirm: true, "it", default);
        Assert.True(created.Executed);
        Assert.Equal("created", created.Outcome);
        Assert.Equal(SubmittedExchange, created.BoundTo);

        await broker.Publisher.PublishAsync(new OrderSubmitted
        {
            OrderId = Guid.NewGuid(),
            CustomerEmail = "nino@example.ge",
            Lines = [new OrderLine { Sku = "TOPO-1", Quantity = 1, UnitPrice = 2m }],
        });
        await broker.WaitForQueueCount(queue, 1);

        var e = Assert.Single(broker.AuditEvents(), x => S(x, "action") == "declare" && S(x, "queue") == queue);
        Assert.Equal("created", S(e, "outcome"));
        Assert.Equal(SubmittedExchange, S(e, "exchange"));
    }

    [Fact]
    public async Task Existing_entities_are_never_changed()
    {
        var queue = $"it-topology-{Guid.NewGuid():N}";
        var manager = Manager();
        await manager.DeclareQueueAsync(queue, null, true, null, null, null, confirm: true, "it", default);

        var same = await manager.DeclareQueueAsync(queue, null, true, null, null, null, confirm: true, "it", default);
        Assert.Equal("existed", same.Outcome);

        var different = await manager.DeclareQueueAsync(queue, "quorum", true, null, null, null, confirm: true, "it", default);
        Assert.Equal("existed-different", different.Outcome);
        Assert.True(await broker.Connection.QueueExistsAsync(queue, default)); // still there, still classic
    }

    [Fact]
    public async Task MassTransits_own_exchange_has_the_settings_rabbit_mcp_creates()
    {
        // The bus already declared it: re-declaring with our MassTransit settings must be a no-op, not a 406.
        var result = await Manager().DeclareExchangeAsync(SubmittedExchange, "fanout", true, false, null, null, null,
            confirm: true, "it", default);

        Assert.Equal("existed", result.Outcome);
        await Assert.ThrowsAsync<McpException>(() => Manager().DeclareExchangeAsync(SubmittedExchange, "topic", true, false,
            null, null, null, confirm: true, "it", default));
    }

    [Fact]
    public async Task Binding_to_a_missing_exchange_is_refused_and_nothing_is_created()
    {
        var name = $"it-topology-{Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<McpException>(() => Manager().DeclareExchangeAsync(name, "fanout", true, false, null,
            "it-no-such-exchange", null, confirm: true, "it", default));

        Assert.Contains("does not exist", ex.Message);
        Assert.False(await broker.Connection.ExchangeExistsAsync(name, default));
        var e = Assert.Single(broker.AuditEvents(), x => S(x, "action") == "declare" && S(x, "exchange") == name);
        Assert.Equal("refused", S(e, "outcome"));
    }
}
