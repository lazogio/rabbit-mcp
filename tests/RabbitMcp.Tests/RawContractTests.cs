// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMcp.Contracts;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;
using RabbitMcp.Tests.Raw;
using RabbitMQ.Client;

namespace RabbitMcp.Tests.Raw
{
    // Contracts of services that use RabbitMQ without MassTransit.

    [MessageContract("Stock of a product changed.", Format = MessageFormat.Raw, Exchange = "inventory",
        RoutingKey = "stock.adjusted", Urn = "stock.adjusted")]
    public sealed record StockAdjusted
    {
        [RegularExpression("^[A-Z0-9-]{3,32}$")]
        public required string Sku { get; init; }

        public required int Delta { get; init; }

        public decimal? UnitCost { get; init; }
    }

    [MessageContract("Asks the pricing service for a quote.", Format = MessageFormat.Raw, RoutingKey = "quotes",
        Responses = new[] { typeof(Quote), typeof(NoQuote) })]
    public sealed record GetQuote
    {
        public required string Sku { get; init; }
    }

    public sealed record Quote
    {
        public required string Sku { get; init; }

        [Range(typeof(decimal), "0.01", "100000")]
        public required decimal Price { get; init; }
    }

    public sealed record NoQuote
    {
        public required string Sku { get; init; }
    }

    [MessageContract("A request with a single reply type.", Format = MessageFormat.Raw, Exchange = "rpc", RoutingKey = "ping",
        Responses = new[] { typeof(RawPong) })]
    public sealed record RawPing
    {
        public required Guid Id { get; init; }
    }

    public sealed record RawPong
    {
        public required Guid Id { get; init; }
    }

    [MessageContract("Raw without a destination: invalid.", Format = MessageFormat.Raw)]
    public sealed record NowhereToGo
    {
        public required Guid Id { get; init; }
    }

    [MessageContract("Default exchange without a queue: invalid.", Exchange = "")]
    public sealed record DefaultExchangeWithoutQueue
    {
        public required Guid Id { get; init; }
    }
}

namespace RabbitMcp.Tests.RawV1
{
    [MessageContract("v1", Urn = "urn:message:Wire:Evt", Exchange = "evt")]
    public sealed record Evt
    {
        public required Guid Id { get; init; }
    }
}

namespace RabbitMcp.Tests.RawV2
{
    [MessageContract("v2", Version = 2, Urn = "urn:message:Wire:Evt", Exchange = "evt", RoutingKey = "evt.created",
        Format = MessageFormat.Raw)]
    public sealed record Evt
    {
        public required Guid Id { get; init; }
    }
}

namespace RabbitMcp.Tests
{
    public class RawContractTests
    {
        private static readonly ContractRegistry Registry =
            new([typeof(StockAdjusted), typeof(GetQuote), typeof(RawPing)]);

        private static readonly ContractDescriptor Stock = Registry.Get(typeof(StockAdjusted));
        private static readonly ContractDescriptor Quotes = Registry.Get(typeof(GetQuote));
        private static readonly ContractDescriptor Ping = Registry.Get(typeof(RawPing));

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        // --- descriptors

        [Fact]
        public void Raw_contracts_keep_their_own_routing()
        {
            Assert.True(Stock.IsRaw);
            Assert.Equal("raw", Stock.FormatName);
            Assert.Equal("inventory", Stock.Exchange);
            Assert.Equal("stock.adjusted", Stock.RoutingKey);
            Assert.Equal("stock.adjusted", Stock.MessageTypeUrn); // the AMQP type property
            Assert.Equal("publish_stock_adjusted", Stock.ToolName);
            Assert.Equal("exchange 'inventory' with routing key 'stock.adjusted'", Stock.Destination);

            // RoutingKey alone: straight to that queue through the default exchange.
            Assert.True(Quotes.UsesDefaultExchange);
            Assert.Equal("", Quotes.Exchange);
            Assert.Equal("queue 'quotes'", Quotes.Destination);
            Assert.Equal("quotes", Quotes.DestinationName);
            Assert.Equal("urn:message:RabbitMcp.Tests.Raw:GetQuote", Quotes.MessageTypeUrn);
        }

        [Fact]
        public void MassTransit_contracts_are_unchanged()
        {
            var d = ContractRegistry.Describe(typeof(RabbitMcp.Samples.Orders.OrderSubmitted));
            Assert.Equal(MessageFormat.MassTransit, d.Format);
            Assert.Equal("masstransit", d.FormatName);
            Assert.Equal("", d.RoutingKey);
            Assert.False(d.UsesDefaultExchange);
        }

        [Theory]
        [InlineData(typeof(NowhereToGo), "needs Exchange and/or RoutingKey")]
        [InlineData(typeof(DefaultExchangeWithoutQueue), "default exchange")]
        public void Contracts_without_a_destination_are_rejected(Type type, string message)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ContractRegistry.Describe(type));
            Assert.Contains(message, ex.Message);
        }

        // --- outgoing messages

        [Fact]
        public void Raw_event_is_plain_json_with_amqp_properties()
        {
            var now = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
            var id = Guid.NewGuid();
            var correlation = Guid.NewGuid().ToString();

            var props = RawMessages.CreateProperties(Stock, id, correlation, "rabbit-mcp", now);
            var body = RawMessages.Serialize(Stock, new StockAdjusted { Sku = "LAMP-01", Delta = -2, UnitCost = 9.5m });

            Assert.Equal("application/json", props.ContentType);
            Assert.Equal(id.ToString(), props.MessageId);
            Assert.Equal(correlation, props.CorrelationId);
            Assert.Equal("stock.adjusted", props.Type);
            Assert.Equal("rabbit-mcp", props.AppId);
            Assert.Null(props.ReplyTo);
            Assert.Null(props.Expiration);
            Assert.Equal(DeliveryModes.Persistent, props.DeliveryMode);
            Assert.Equal(now.ToUnixTimeSeconds(), props.Timestamp.UnixTime);
            Assert.Equal(1, props.Headers![HeaderNames.ContractVersion]);
            Assert.Equal("rabbit-mcp", props.Headers[HeaderNames.PublishedBy]);

            // No envelope: the message itself, camelCase, decimals as numbers.
            Assert.Equal("""{"sku":"LAMP-01","delta":-2,"unitCost":9.5}""", Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void Raw_request_uses_reply_to_and_correlation_id()
        {
            var requestId = Guid.NewGuid();
            var workflow = Guid.NewGuid().ToString();

            var props = RawMessages.CreateProperties(Quotes, Guid.NewGuid(), workflow, "rabbit-mcp", DateTimeOffset.UtcNow,
                new RawRequestInfo(requestId, "rabbit-mcp_host_reply"), TimeSpan.FromSeconds(30));

            Assert.Equal(requestId.ToString(), props.CorrelationId); // RPC: correlation_id = request id
            Assert.Equal("rabbit-mcp_host_reply", props.ReplyTo);
            Assert.Equal("30000", props.Expiration);
            Assert.Equal(workflow, props.Headers![HeaderNames.CorrelationId]); // the caller's correlation id moves here
        }

        // --- replies

        private static RawReply Reply(string? type, string body, string? correlationId = null) =>
            RawReply.From(new BasicProperties { Type = type, CorrelationId = correlationId, AppId = "pricing" }, B(body), 16_000);

        [Theory]
        [InlineData("Quote")]
        [InlineData("quote")]
        [InlineData("urn:message:RabbitMcp.Tests.Raw:Quote")]
        public void Raw_reply_type_is_named_by_the_amqp_type(string type)
        {
            var r = ReplyInterpreter.InterpretRaw(Quotes, Reply(type, """{"sku":"LAMP-01","price":12.5}"""));

            Assert.Equal("responded", r.Status);
            Assert.Equal("Quote", r.ResponseType);
            Assert.True(r.ResponseCheck!.Valid);
            Assert.Equal("pricing", r.RespondedBy.ProcessName);
            Assert.Null(r.Note);
        }

        [Fact]
        public void Raw_reply_is_validated_against_its_response_contract()
        {
            var r = ReplyInterpreter.InterpretRaw(Quotes, Reply("Quote", """{"sku":"LAMP-01","price":0}"""));
            Assert.False(r.ResponseCheck!.Valid);
            Assert.Contains(r.ResponseCheck.Errors, e => e.StartsWith("price"));
        }

        [Fact]
        public void Raw_reply_without_type_matches_only_a_single_response()
        {
            var single = ReplyInterpreter.InterpretRaw(Ping, Reply(null, $$"""{"id":"{{Guid.NewGuid()}}"}"""));
            Assert.Equal("RawPong", single.ResponseType);
            Assert.True(single.ResponseCheck!.Valid);

            // Two possible replies: the body is never guessed from.
            var ambiguous = ReplyInterpreter.InterpretRaw(Quotes, Reply(null, """{"sku":"LAMP-01"}"""));
            Assert.Null(ambiguous.ResponseType);
            Assert.Null(ambiguous.ResponseCheck);
            Assert.Contains("no AMQP type", ambiguous.Note);
            Assert.Equal("LAMP-01", ambiguous.Response!.Value.GetProperty("sku").GetString());
        }

        [Fact]
        public void Raw_reply_of_an_undeclared_type_or_not_json_is_explained()
        {
            var unknown = ReplyInterpreter.InterpretRaw(Quotes, Reply("PriceList", "{}"));
            Assert.Equal("PriceList", unknown.ResponseType);
            Assert.Null(unknown.ResponseCheck);
            Assert.Contains("not one of the declared responses (Quote, NoQuote)", unknown.Note);

            var text = ReplyInterpreter.InterpretRaw(Quotes, Reply("Quote", "price: 12"));
            Assert.Contains("not JSON", text.Note);
            Assert.Equal("price: 12", text.Response!.Value.GetString());
        }

        [Fact]
        public async Task Raw_requests_are_completed_by_correlation_id_only()
        {
            var pending = new PendingRequests();
            var raw = Guid.NewGuid();
            var masstransit = Guid.NewGuid();
            var rawTask = pending.Register(raw, MessageFormat.Raw);
            _ = pending.Register(masstransit);

            // An envelope carrying the raw request's id, or a raw reply for the MassTransit request, completes nothing.
            Assert.False(pending.TryComplete(new MassTransitEnvelope
            {
                RequestId = raw.ToString(), MessageType = ["urn:x"], Message = JsonDocument.Parse("{}").RootElement,
            }));
            Assert.False(pending.TryComplete(Reply("Quote", "{}", masstransit.ToString())));
            Assert.Equal(2, pending.Count);

            Assert.True(pending.TryComplete(Reply("Quote", """{"sku":"A-1","price":1}""", raw.ToString())));
            var reply = await rawTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(reply.Envelope);
            Assert.Equal(new[] { "Quote" }, reply.MessageTypes);
            Assert.Equal(1, pending.Count);
        }

        [Fact]
        public void Raw_reply_copies_json_and_cuts_text_at_the_payload_limit()
        {
            var json = RawReply.From(new BasicProperties(), B("""{"a":1}"""), 100);
            Assert.Equal(1, json.Body!.Value.GetProperty("a").GetInt32());
            Assert.Null(json.Text);

            var big = RawReply.From(new BasicProperties(), B(new string('x', 50)), 10);
            Assert.Null(big.Body);
            Assert.Equal(10, big.Text!.Length);
            Assert.Equal(50, big.Bytes);
        }

        // --- RabbitMQ dead-lettering (x-death)

        private static Dictionary<string, object?> Death(string queue, string reason = "rejected") => new()
        {
            ["queue"] = B(queue),
            ["reason"] = B(reason),
            ["count"] = 2L,
            ["time"] = new AmqpTimestamp(1_760_000_000),
            ["exchange"] = B("inventory"),
            ["routing-keys"] = new List<object?> { B("stock.adjusted") },
        };

        private static Dictionary<string, object?> DeadLettered(string queue) => new()
        {
            [HeaderNames.Death] = new List<object?> { Death(queue) },
            ["x-first-death-queue"] = B(queue),
            ["x-first-death-reason"] = B("rejected"),
            ["x-first-death-exchange"] = B("inventory"),
            ["x-last-death-queue"] = B(queue),
            ["Tenant"] = B("acme"),
        };

        [Fact]
        public void Dead_lettered_messages_go_back_to_the_queue_that_rejected_them()
        {
            var target = RepublishTargetResolver.Resolve("inventory-worker.dlq", DeadLettered("inventory-worker"))!;

            Assert.True(target.IsQueue);
            Assert.Equal("", target.Exchange);
            Assert.Equal("inventory-worker", target.RoutingKey);
            Assert.Equal("queue:inventory-worker", target.Display);
            Assert.Contains("x-death", target.ResolvedFrom);
            Assert.Null(target.Conflict);
        }

        [Fact]
        public void X_death_that_disagrees_with_the_error_queue_is_a_conflict()
        {
            Assert.Contains("targetQueue",
                RepublishTargetResolver.Resolve("inventory-worker_error", DeadLettered("payments"))!.Conflict);
            Assert.Null(RepublishTargetResolver.Resolve("inventory-worker_dead", DeadLettered("inventory-worker"))!.Conflict);
            Assert.Contains("itself", RepublishTargetResolver.Resolve("loop", DeadLettered("loop"))!.Conflict);
        }

        [Fact]
        public void MassTransit_fault_address_wins_over_x_death()
        {
            var headers = DeadLettered("inventory-worker");
            headers["MT-Fault-InputAddress"] = B("rabbitmq://localhost/order-placed");

            var target = RepublishTargetResolver.Resolve("order-placed_error", headers)!;
            Assert.False(target.IsQueue);
            Assert.Equal("order-placed", target.Display);
        }

        [Fact]
        public void Last_death_header_is_the_fallback_for_an_empty_x_death()
        {
            var headers = new Dictionary<string, object?> { ["x-last-death-queue"] = B("worker") };
            Assert.Equal("queue:worker", RepublishTargetResolver.Resolve("worker.dlq", headers)!.Display);
        }

        [Fact]
        public void X_death_is_parsed_for_display_and_removed_when_republishing()
        {
            var headers = DeadLettered("inventory-worker");

            var death = Assert.Single(FaultHeaders.ParseDeaths(headers)!);
            Assert.Equal("inventory-worker", death.Queue);
            Assert.Equal("rejected", death.Reason);
            Assert.Equal(2L, death.Count);
            Assert.Equal("inventory", death.Exchange);
            Assert.Equal(new[] { "stock.adjusted" }, death.RoutingKeys);
            Assert.StartsWith("2025-10-09T", death.Time);

            Assert.Equal(new[] { "Tenant" }, FaultHeaders.WithoutFaultHeaders(headers).Keys);
            Assert.Equal(new[] { "Tenant" }, FaultHeaders.OtherHeadersForDisplay(headers).Keys);
            Assert.Null(FaultHeaders.ParseDeaths(new Dictionary<string, object?> { ["Tenant"] = B("acme") }));
        }

        [Fact]
        public void Republish_override_accepts_an_exchange_or_a_queue_not_both()
        {
            Assert.Equal("queue:worker", QueueBrowser.Override(null, "worker")!.Display);
            Assert.Equal("orders", QueueBrowser.Override("orders", null)!.Display);
            Assert.Null(QueueBrowser.Override(null, " "));
            Assert.Throws<ModelContextProtocol.McpException>(() => QueueBrowser.Override("orders", "worker"));
            Assert.Throws<ModelContextProtocol.McpException>(() => QueueBrowser.Override(null, "amq.gen-x"));
        }

        // --- peeking

        private static MessageInspector Inspector() =>
            new(Registry, Options.Create(new RabbitMcpOptions { MaxPayloadChars = 16_000 }));

        [Fact]
        public void Raw_message_is_matched_to_its_contract_by_amqp_type()
        {
            var props = new BasicProperties
            {
                MessageId = "m-1", Type = "stock.adjusted", ContentType = "application/json",
                Timestamp = new AmqpTimestamp(1_760_000_000), Headers = DeadLettered("inventory-worker"),
            };

            var peeked = Inspector().Inspect(0, "inventory-worker.dlq", props, B("""{"sku":"x","delta":1}"""), false);

            Assert.Equal(new[] { "stock.adjusted" }, peeked.MessageTypes);
            Assert.Equal("StockAdjusted", peeked.Contract!.Contract);
            Assert.False(peeked.Contract.Valid); // sku does not match the pattern
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_760_000_000).UtcDateTime, peeked.SentTimeUtc);
            Assert.Equal("rejected", Assert.Single(peeked.DeadLettered!).Reason);
            Assert.Equal("queue:inventory-worker", peeked.RepublishTarget);
            Assert.Null(peeked.Fault);
            Assert.Equal(new[] { "Tenant" }, peeked.Headers.Keys);
        }

        [Fact]
        public void Plain_json_without_a_known_type_has_no_contract()
        {
            var peeked = Inspector().Inspect(0, "q", new BasicProperties { Type = "something.else" }, B("{}"), false);
            Assert.Null(peeked.Contract);
            Assert.NotNull(peeked.Payload);
        }

        // --- topology

        private static TopologyInspector Topology(Dictionary<string, (string Type, string Bindings)> exchanges,
            Dictionary<string, string> queues)
        {
            var handler = new StubHandler(req =>
            {
                var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
                foreach (var (name, (type, bindings)) in exchanges)
                {
                    if (path == $"/api/exchanges/prod/{name}")
                        return StubHandler.Json($$"""{"name":"{{name}}","type":"{{type}}"}""");
                    if (path == $"/api/exchanges/prod/{name}/bindings/source")
                        return StubHandler.Json(bindings);
                }
                foreach (var (name, json) in queues)
                {
                    if (path == $"/api/queues/prod/{name}")
                        return StubHandler.Json(json);
                }
                return StubHandler.Json("""{"error":"Object Not Found","reason":"Not Found"}""", HttpStatusCode.NotFound);
            });
            return new TopologyInspector(new ManagementClient(
                new RabbitMcpOptions { Uri = "amqp://mq.local/prod", ManagementUri = "http://mq.local:15672" }, handler));
        }

        private static string Binding(string source, string destination, string key, string type = "queue") =>
            $$"""{"source":"{{source}}","vhost":"prod","destination":"{{destination}}","destination_type":"{{type}}","routing_key":"{{key}}"}""";

        private static string QueueJson(string name, long consumers) =>
            $$"""{"name":"{{name}}","type":"classic","state":"running","messages_ready":0,"messages_unacknowledged":0,"consumers":{{consumers}}}""";

        [Fact]
        public async Task Topic_bindings_are_matched_against_the_routing_key()
        {
            var inspector = Topology(
                new()
                {
                    ["inventory"] = ("topic", $"[{Binding("inventory", "stock-audit", "stock.#")},{Binding("inventory", "prices", "price.*")},{Binding("inventory", "fan", "stock.*", "exchange")}]"),
                    ["fan"] = ("fanout", $"[{Binding("fan", "stock-mirror", "")}]"),
                },
                new() { ["stock-audit"] = QueueJson("stock-audit", 1), ["prices"] = QueueJson("prices", 1), ["stock-mirror"] = QueueJson("stock-mirror", 0) });

            var result = await inspector.InspectAsync(Stock, CancellationToken.None);

            Assert.Equal(new[] { "stock-audit", "stock-mirror" }, result.Queues.Select(q => q.Queue));
            Assert.Equal(new[] { "inventory", "fan" }, result.Queues[1].Via);
            Assert.Equal("ok", result.Status);
            Assert.Equal(1, result.TotalConsumers);
        }

        [Fact]
        public async Task Nothing_bound_for_the_routing_key_is_unroutable()
        {
            var inspector = Topology(
                new() { ["inventory"] = ("direct", $"[{Binding("inventory", "prices", "price.changed")}]") },
                new() { ["prices"] = QueueJson("prices", 1) });

            var result = await inspector.InspectAsync(Stock, CancellationToken.None);

            Assert.Equal("no-queue", result.Status);
            Assert.Contains("for routing key 'stock.adjusted'", result.Warnings[0]);
        }

        [Fact]
        public async Task Headers_exchange_bindings_are_followed_with_a_warning()
        {
            var inspector = Topology(
                new() { ["inventory"] = ("headers", $"[{Binding("inventory", "stock-audit", "")}]") },
                new() { ["stock-audit"] = QueueJson("stock-audit", 1) });

            var result = await inspector.InspectAsync(Stock, CancellationToken.None);

            Assert.Equal("ok", result.Status);
            Assert.Contains(result.Warnings, w => w.Contains("headers exchange") && w.Contains("not evaluated"));
        }

        [Fact]
        public async Task Default_exchange_reaches_the_queue_named_by_the_routing_key()
        {
            var ok = await Topology(new(), new() { ["quotes"] = QueueJson("quotes", 2) }).InspectAsync(Quotes, CancellationToken.None);
            var queue = Assert.Single(ok.Queues);
            Assert.Equal("quotes", queue.Queue);
            Assert.Equal(new[] { TopologyInspector.DefaultExchange }, queue.Via);
            Assert.Equal("ok", ok.Status);
            Assert.True(ok.ExchangeExists);

            var missing = await Topology(new(), new()).InspectAsync(Quotes, CancellationToken.None);
            Assert.Equal("no-queue", missing.Status);
            Assert.Empty(missing.Queues);
            Assert.Contains("Queue 'quotes' does not exist", missing.Warnings[0]);
            Assert.Contains("time out", missing.Warnings[0]);
        }

        [Theory]
        [InlineData("stock.adjusted", "stock.adjusted", true)]
        [InlineData("stock.*", "stock.adjusted", true)]
        [InlineData("stock.*", "stock.adjusted.eu", false)]
        [InlineData("stock.#", "stock", true)]
        [InlineData("stock.#", "stock.adjusted.eu", true)]
        [InlineData("#.eu", "stock.adjusted.eu", true)]
        [InlineData("a.#.z", "a.z", true)]
        [InlineData("a.#.z", "a.b.c.z", true)]
        [InlineData("a.*.z", "a.z", false)]
        [InlineData("#", "", true)]
        [InlineData("*", "", false)]
        [InlineData("", "", true)]
        [InlineData("", "x", false)]
        [InlineData("Stock.*", "stock.adjusted", false)]
        public void Topic_matching_follows_amqp_rules(string binding, string key, bool expected) =>
            Assert.Equal(expected, RoutingKeys.TopicMatches(binding, key));

        // --- everything else that knows about formats

        [Fact]
        public void Changing_format_or_routing_key_is_breaking()
        {
            var changes = ContractDiff.Compare(
                new ContractRegistry([typeof(RawV1.Evt)]), new ContractRegistry([typeof(RawV2.Evt)]));

            Assert.Contains(changes, c => c.Severity == ContractDiff.Breaking && c.Message.Contains("Wire format changed from masstransit to raw"));
            Assert.Contains(changes, c => c.Severity == ContractDiff.Breaking && c.Message.Contains("Routing key changed"));
        }

        [Fact]
        public void Responder_mode_refuses_raw_requests()
        {
            var ex = Assert.Throws<StartupException>(() => ResponderContracts.From(Registry, "GetQuote"));
            Assert.Contains("raw", ex.Message);
            Assert.Contains("GetQuote", ex.Message);
        }

        [Fact]
        public void Tools_and_resources_describe_the_raw_wire_format()
        {
            var publish = ContractToolFactory.CreateTool(Stock).ProtocolTool.Description!;
            Assert.Contains("plain JSON", publish);
            Assert.Contains("routing key 'stock.adjusted'", publish);

            var request = ContractToolFactory.CreateTool(Quotes).ProtocolTool.Description!;
            Assert.Contains("reply_to and correlation_id", request);
            Assert.Contains("queue 'quotes'", request);

            using var resource = JsonDocument.Parse(ContractResources.Describe(Quotes));
            Assert.Equal("raw", resource.RootElement.GetProperty("format").GetString());
            Assert.Equal("quotes", resource.RootElement.GetProperty("routingKey").GetString());
            Assert.Equal(JsonValueKind.Null, resource.RootElement.GetProperty("responses")[0].GetProperty("replyTool").ValueKind);
        }
    }
}
