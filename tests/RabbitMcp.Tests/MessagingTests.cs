// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;

namespace RabbitMcp.Tests;

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public class BrokerAddressTests
{
    [Theory]
    [InlineData("amqp://u:p@localhost:5672/", "rabbitmq://localhost/Ns:Type")]
    [InlineData("amqp://u:p@localhost/%2F", "rabbitmq://localhost/Ns:Type")]
    [InlineData("amqp://u:p@mq.local:5673/prod", "rabbitmq://mq.local:5673/prod/Ns:Type")]
    public void ForEntity_builds_MassTransit_addresses(string amqp, string expected) =>
        Assert.Equal(expected, BrokerAddress.ForEntity(new Uri(amqp), "Ns:Type"));

    [Theory]
    [InlineData("rabbitmq://localhost/submit-order", "submit-order")]
    [InlineData("rabbitmq://localhost/prod/submit-order?temporary=false", "submit-order")]
    [InlineData("not a uri", null)]
    [InlineData(null, null)]
    public void GetEntityName_returns_last_segment(string? address, string? expected) =>
        Assert.Equal(expected, BrokerAddress.GetEntityName(address));

    [Fact]
    public void Describe_never_contains_credentials() =>
        Assert.DoesNotContain("secret", BrokerAddress.Describe(new Uri("amqp://user:secret@mq:5672/")));
}

public class EnvelopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Envelope_is_MassTransit_compatible_json()
    {
        var contract = ContractRegistry.Describe(typeof(OrderSubmitted));
        var order = ContractValidatorTests.ValidOrder();
        var id = Guid.NewGuid();

        var envelope = new EnvelopeFactory(new FixedTime(Now))
            .Create(contract, order, new Uri("amqp://localhost/"), "rabbit-mcp", id, correlationId: null);

        using var doc = JsonDocument.Parse(EnvelopeFactory.Serialize(envelope));
        var root = doc.RootElement;

        Assert.Equal(id.ToString(), root.GetProperty("messageId").GetString());
        Assert.Equal("urn:message:RabbitMcp.Contracts.Orders:OrderSubmitted", root.GetProperty("messageType")[0].GetString());
        Assert.Equal("rabbitmq://localhost/RabbitMcp.Contracts.Orders:OrderSubmitted", root.GetProperty("destinationAddress").GetString());
        Assert.Equal("rabbitmq://localhost/rabbit-mcp", root.GetProperty("sourceAddress").GetString());
        Assert.Equal(order.OrderId, root.GetProperty("message").GetProperty("orderId").GetGuid());
        Assert.Equal(2, root.GetProperty("message").GetProperty("lines")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(1, root.GetProperty("headers").GetProperty("Contract-Version").GetInt32());
        Assert.False(root.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public void Envelope_round_trips_through_TryParse()
    {
        var contract = ContractRegistry.Describe(typeof(OrderCancelled));
        var message = new OrderCancelled { OrderId = Guid.NewGuid(), Reason = "Out of stock" };
        var envelope = new EnvelopeFactory(new FixedTime(Now))
            .Create(contract, message, new Uri("amqp://localhost/"), "rabbit-mcp", Guid.NewGuid(), "c0ffee00-0000-0000-0000-000000000000");

        var parsed = MassTransitEnvelope.TryParse(EnvelopeFactory.Serialize(envelope));

        Assert.NotNull(parsed);
        Assert.Equal(envelope.MessageId, parsed.MessageId);
        Assert.Equal("c0ffee00-0000-0000-0000-000000000000", parsed.CorrelationId);
        Assert.Equal(Now.UtcDateTime, parsed.SentTime);
        Assert.Equal("Out of stock", parsed.Message!.Value.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("""{"orderId":"1"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void TryParse_returns_null_for_non_envelopes(string body) =>
        Assert.Null(MassTransitEnvelope.TryParse(Encoding.UTF8.GetBytes(body)));
}

public class FaultHeaderTests
{
    // AMQP delivers string header values as byte[].
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    internal static Dictionary<string, object?> ErrorHeaders() => new()
    {
        ["MT-Reason"] = B("fault"),
        ["MT-Fault-ExceptionType"] = B("System.InvalidOperationException"),
        ["MT-Fault-Message"] = B("Stock not available"),
        ["MT-Fault-ConsumerType"] = B("Shop.SubmitOrderConsumer"),
        ["MT-Fault-InputAddress"] = B("rabbitmq://localhost/submit-order"),
        ["MT-Fault-RetryCount"] = 3,
        ["MT-Fault-StackTrace"] = B(string.Join('\n', Enumerable.Range(1, 30).Select(i => $"   at Frame{i}()"))),
        ["MT-Host-MachineName"] = B("web-01"),
        ["MT-Host-ProcessName"] = B("Shop.Api"),
        ["Custom-Tenant"] = B("acme"),
    };

    [Fact]
    public void Parse_reads_byte_array_and_numeric_headers()
    {
        var fault = FaultHeaders.Parse(ErrorHeaders());

        Assert.NotNull(fault);
        Assert.Equal("fault", fault.Reason);
        Assert.Equal("System.InvalidOperationException", fault.ExceptionType);
        Assert.Equal("Stock not available", fault.ExceptionMessage);
        Assert.Equal("Shop.SubmitOrderConsumer", fault.ConsumerType);
        Assert.Equal(3, fault.RetryCount);
        Assert.Equal("web-01 / Shop.Api", fault.FaultedOnHost);
        Assert.Contains("more lines", fault.StackTrace);
    }

    [Fact]
    public void Parse_returns_null_without_fault_headers() =>
        Assert.Null(FaultHeaders.Parse(new Dictionary<string, object?> { ["Custom"] = B("x") }));

    [Fact]
    public void GetInt_parses_string_values() =>
        Assert.Equal(7, FaultHeaders.GetInt(new Dictionary<string, object?> { ["n"] = B("7") }, "n"));

    [Fact]
    public void WithoutFaultHeaders_keeps_only_application_headers()
    {
        var clean = FaultHeaders.WithoutFaultHeaders(ErrorHeaders());

        Assert.Equal(new[] { "Custom-Tenant" }, clean.Keys);
    }

    [Theory]
    [InlineData("submit-order_error", "rabbitmq://localhost/prod/submit-order-v2", "submit-order-v2")]
    [InlineData("submit-order_error", null, "submit-order")]
    [InlineData("submit-order_skipped", null, "submit-order")]
    [InlineData("dead-letters", null, null)]
    [InlineData("_error", null, null)]
    public void RepublishTarget_prefers_fault_input_address(string queue, string? inputAddress, string? expected)
    {
        var headers = new Dictionary<string, object?>();
        if (inputAddress is not null)
            headers["MT-Fault-InputAddress"] = B(inputAddress);

        Assert.Equal(expected, RepublishTargetResolver.Resolve(queue, headers)?.Exchange);
    }

    [Fact]
    public void RepublishTarget_header_that_disagrees_with_the_error_queue_is_a_conflict()
    {
        var spoofed = new Dictionary<string, object?> { ["MT-Fault-InputAddress"] = B("rabbitmq://localhost/payments") };
        var genuine = new Dictionary<string, object?> { ["MT-Fault-InputAddress"] = B("rabbitmq://localhost/submit-order") };

        Assert.Contains("targetExchange", RepublishTargetResolver.Resolve("submit-order_error", spoofed)!.Conflict);
        Assert.Null(RepublishTargetResolver.Resolve("submit-order_error", genuine)!.Conflict);
        Assert.Null(RepublishTargetResolver.Resolve("custom-dead-letters", spoofed)!.Conflict); // no owner to compare with
    }
}

public class MessageInspectorTests
{
    [Fact]
    public void Decimals_written_as_strings_by_MassTransit_are_valid_and_written_back_as_numbers()
    {
        // MassTransit's StringDecimalJsonConverter writes {"unitPrice":"9.99"}.
        var json = System.Text.Json.JsonDocument.Parse("""
            {"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","customerEmail":"ana@example.ge",
             "lines":[{"sku":"ABC-1","quantity":2,"unitPrice":"9.99"}]}
            """).RootElement;

        Assert.Empty(MessageInspector.Validate(typeof(RabbitMcp.Contracts.Orders.OrderSubmitted), json, MassTransitJson.Options));
        Assert.Equal("""{"amount":9.99}""",
            System.Text.Json.JsonSerializer.Serialize(new { amount = 9.99m }, MassTransitJson.Options));
        Assert.NotEmpty(MessageInspector.Validate(typeof(RabbitMcp.Contracts.Orders.OrderSubmitted),
            System.Text.Json.JsonDocument.Parse("""{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","customerEmail":"ana@example.ge","lines":[{"sku":"ABC-1","quantity":2,"unitPrice":"lots"}]}""").RootElement,
            MassTransitJson.Options));
    }

    private static MessageInspector Inspector(int maxChars = 16_000) => new(
        ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly),
        Options.Create(new RabbitMcpOptions { MaxPayloadChars = maxChars }));

    private static byte[] EnvelopeBody(object message)
    {
        var contract = ContractRegistry.Describe(message.GetType());
        var envelope = new EnvelopeFactory(TimeProvider.System)
            .Create(contract, message, new Uri("amqp://localhost/"), "test", Guid.NewGuid(), null);
        return EnvelopeFactory.Serialize(envelope);
    }

    [Fact]
    public void Faulted_message_shows_fault_contract_check_and_target()
    {
        var invalid = ContractValidatorTests.ValidOrder() with
        {
            Lines = [new OrderLine { Sku = "ABC", Quantity = 0, UnitPrice = 1m }],
        };
        var props = new BasicProperties { MessageId = "m-1", Headers = FaultHeaderTests.ErrorHeaders() };

        var peeked = Inspector().Inspect(0, "submit-order_error", props, EnvelopeBody(invalid), redelivered: false);

        Assert.Equal("m-1", peeked.MessageId);
        Assert.Equal("System.InvalidOperationException", peeked.Fault?.ExceptionType);
        Assert.Equal("submit-order", peeked.RepublishTarget);
        Assert.NotNull(peeked.Contract);
        Assert.False(peeked.Contract.Valid);
        Assert.Contains(peeked.Contract.Errors, e => e.StartsWith("lines[0].quantity:"));
        Assert.Equal(0, peeked.Payload!.Value.GetProperty("lines")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(new[] { "Custom-Tenant" }, peeked.Headers.Keys);
    }

    [Fact]
    public void Envelope_missing_required_member_is_reported_not_thrown()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"messageType":["urn:message:RabbitMcp.Contracts.Orders:OrderCancelled"],"message":{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11"}}""");

        var peeked = Inspector().Inspect(0, "q", new BasicProperties(), body, redelivered: true);

        Assert.False(peeked.Contract!.Valid);
        Assert.Contains("reason", peeked.Contract.Errors[0]);
        Assert.True(peeked.Redelivered);
    }

    [Fact]
    public void Plain_json_and_text_bodies_are_shown()
    {
        var json = Inspector().Inspect(0, "q", new BasicProperties(), """{"a":1}"""u8, false);
        Assert.Equal(1, json.Payload!.Value.GetProperty("a").GetInt32());
        Assert.Null(json.Contract);

        var text = Inspector().Inspect(0, "q", new BasicProperties(), "hello"u8, false);
        Assert.Equal("hello", text.RawBody);
        Assert.Null(text.Payload);
    }

    [Fact]
    public void Large_bodies_are_truncated_for_display_only()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 500));

        var peeked = Inspector(maxChars: 100).Inspect(0, "q", new BasicProperties(), body, false);

        Assert.True(peeked.Truncated);
        Assert.Equal(100, peeked.RawBody!.Length);
        Assert.Equal(500, peeked.BodyBytes);
    }
}
