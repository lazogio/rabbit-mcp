// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Net;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

public class TopologyInspectorTests
{
    private static readonly ContractDescriptor Submitted = ContractRegistry.Describe(typeof(OrderSubmitted));

    private static TopologyInspector Create(Dictionary<string, string> routes)
    {
        var handler = new StubHandler(req =>
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            if (path.StartsWith("/api/exchanges/") && !path.EndsWith("/bindings/source")
                && routes.ContainsKey(path + "/bindings/source"))
                return StubHandler.Json("""{"name":"x","type":"fanout"}""");
            return routes.TryGetValue(path, out var json)
                ? StubHandler.Json(json)
                : StubHandler.Json("""{"error":"Object Not Found","reason":"Not Found"}""", HttpStatusCode.NotFound);
        });
        var client = new ManagementClient(
            new RabbitMcpOptions { Uri = "amqp://mq.local/prod", ManagementUri = "http://mq.local:15672" }, handler);
        return new TopologyInspector(client);
    }

    private static string Binding(string source, string destination, string type) =>
        $$"""{"source":"{{source}}","vhost":"prod","destination":"{{destination}}","destination_type":"{{type}}","routing_key":""}""";

    private static string Queue(string name, long consumers, long ready) =>
        $$"""{"name":"{{name}}","type":"classic","state":"running","messages_ready":{{ready}},"messages_unacknowledged":0,"consumers":{{consumers}}}""";

    [Fact]
    public async Task Follows_exchange_to_exchange_bindings_like_MassTransit_topology()
    {
        var inspector = Create(new()
        {
            [$"/api/exchanges/prod/{Submitted.Exchange}/bindings/source"] =
                $"[{Binding(Submitted.Exchange, "order-submitted", "exchange")}]",
            ["/api/exchanges/prod/order-submitted/bindings/source"] =
                $"[{Binding("order-submitted", "order-submitted", "queue")}]",
            ["/api/queues/prod/order-submitted"] = Queue("order-submitted", consumers: 0, ready: 5),
            ["/api/queues/prod/order-submitted_error"] = Queue("order-submitted_error", 0, 3),
        });

        var result = await inspector.InspectAsync(Submitted, CancellationToken.None);

        Assert.Equal("no-consumers", result.Status);
        var queue = Assert.Single(result.Queues);
        Assert.Equal(new[] { Submitted.Exchange, "order-submitted" }, queue.Via);
        Assert.Equal(5, queue.MessagesReady);
        Assert.Equal(3, queue.ErrorQueueMessages);
        Assert.Null(queue.SkippedQueueMessages);
        Assert.Contains(result.Warnings, w => w.Contains("order-submitted_error"));
        Assert.Contains(result.Warnings, w => w.Contains("pile up"));
    }

    [Fact]
    public async Task Missing_exchange_and_cycles_are_handled()
    {
        Assert.Equal("no-exchange", (await Create(new()).InspectAsync(Submitted, CancellationToken.None)).Status);

        var cyclic = Create(new()
        {
            [$"/api/exchanges/prod/{Submitted.Exchange}/bindings/source"] = $"[{Binding(Submitted.Exchange, "a", "exchange")}]",
            ["/api/exchanges/prod/a/bindings/source"] = $"[{Binding("a", Submitted.Exchange, "exchange")},{Binding("a", "q", "queue")}]",
            ["/api/queues/prod/q"] = Queue("q", consumers: 2, ready: 0),
        });

        var result = await cyclic.InspectAsync(Submitted, CancellationToken.None);
        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.TotalConsumers);
        Assert.Empty(result.Warnings);
    }
}
