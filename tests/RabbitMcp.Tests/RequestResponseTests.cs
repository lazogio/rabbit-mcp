// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

public class RequestResponseTests
{
    private static readonly ContractDescriptor GetStatus = ContractRegistry.Describe(typeof(GetOrderStatus));
    private static readonly Guid OrderId = Guid.Parse("8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11");

    private static MassTransitEnvelope Reply(Guid requestId, string messageType, string messageJson) => new()
    {
        RequestId = requestId.ToString(),
        SourceAddress = "rabbitmq://localhost/order-status",
        MessageType = [messageType],
        Message = JsonDocument.Parse(messageJson).RootElement.Clone(),
        Host = new EnvelopeHost { MachineName = "orders-01", ProcessName = "Orders.Service", Assembly = "Orders.Service" },
    };

    [Fact]
    public void Request_contract_lists_its_responses()
    {
        Assert.True(GetStatus.IsRequest);
        Assert.Equal("request_get_order_status", GetStatus.ToolName);
        Assert.Equal(
            new[] { "urn:message:RabbitMcp.Samples.Orders:OrderStatus", "urn:message:RabbitMcp.Samples.Orders:OrderNotFound" },
            GetStatus.Responses.Select(r => r.MessageTypeUrn));
        Assert.False(ContractRegistry.Describe(typeof(OrderSubmitted)).IsRequest);
    }

    [Theory]
    [InlineData("amqp://localhost/", "rabbitmq://localhost/reply-q?temporary=true")]
    [InlineData("amqp://localhost:5673/prod", "rabbitmq://localhost:5673/prod/reply-q?temporary=true")]
    public void Reply_address_is_a_MassTransit_temporary_address(string amqp, string expected) =>
        Assert.Equal(expected, ReplyEndpoint.Address(new Uri(amqp), "reply-q"));

    [Fact]
    public void Reply_queue_names_are_unique_and_entity_safe()
    {
        var a = ReplyEndpoint.CreateName("rabbit mcp/αβ");
        var b = ReplyEndpoint.CreateName("rabbit mcp/αβ");

        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9._-]+$", a);
    }

    [Fact]
    public void Request_envelope_carries_request_id_response_address_and_expiration()
    {
        var now = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var requestId = Guid.NewGuid();
        var info = new RequestEnvelopeInfo(requestId, "rabbitmq://localhost/reply-q?temporary=true", now.UtcDateTime.AddSeconds(30));

        var envelope = new EnvelopeFactory(new FixedTime(now)).Create(
            GetStatus, new GetOrderStatus { OrderId = OrderId }, new Uri("amqp://localhost/"), "rabbit-mcp",
            Guid.NewGuid(), null, info);

        using var doc = JsonDocument.Parse(EnvelopeFactory.Serialize(envelope));
        var root = doc.RootElement;
        Assert.Equal(requestId.ToString(), root.GetProperty("requestId").GetString());
        Assert.Equal("rabbitmq://localhost/reply-q?temporary=true", root.GetProperty("responseAddress").GetString());
        Assert.Equal(now.UtcDateTime.AddSeconds(30), root.GetProperty("expirationTime").GetDateTime());
    }

    [Fact]
    public async Task Pending_requests_are_completed_by_matching_request_id()
    {
        var pending = new PendingRequests();
        var requestId = Guid.NewGuid();
        var task = pending.Register(requestId);

        Assert.False(pending.TryComplete(Reply(Guid.NewGuid(), "urn:x", "{}")));
        Assert.True(pending.TryComplete(Reply(requestId, "urn:x", "{}")));

        var reply = await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(requestId.ToString(), reply.RequestId);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void Late_replies_after_timeout_are_ignored()
    {
        var pending = new PendingRequests();
        var requestId = Guid.NewGuid();
        _ = pending.Register(requestId);
        pending.Forget(requestId);

        Assert.False(pending.TryComplete(Reply(requestId, "urn:x", "{}")));
    }

    [Fact]
    public void Declared_response_is_validated_against_its_contract()
    {
        var reply = Reply(Guid.NewGuid(), "urn:message:RabbitMcp.Samples.Orders:OrderStatus",
            $$"""{"orderId":"{{OrderId}}","status":"Shipped"}""");

        var r = ReplyInterpreter.Interpret(GetStatus, reply);

        Assert.Equal("responded", r.Status);
        Assert.Equal("OrderStatus", r.ResponseType);
        Assert.True(r.ResponseCheck!.Valid);
        Assert.Null(r.ResponseCheck.Version); // response types are not versioned (was: request's version)
        Assert.Equal("rabbitmq://localhost/order-status", r.RespondedBy.Address);
        Assert.Equal("orders-01", r.RespondedBy.MachineName);
    }

    [Fact]
    public void Response_that_violates_its_contract_is_flagged()
    {
        var reply = Reply(Guid.NewGuid(), "urn:message:RabbitMcp.Samples.Orders:OrderStatus",
            $$"""{"orderId":"{{OrderId}}","status":"Lost"}""");

        var r = ReplyInterpreter.Interpret(GetStatus, reply);

        Assert.False(r.ResponseCheck!.Valid);
        Assert.Contains(r.ResponseCheck.Errors, e => e.StartsWith("status:"));
    }

    [Fact]
    public void Alternative_response_type_is_recognised()
    {
        var reply = Reply(Guid.NewGuid(), "urn:message:RabbitMcp.Samples.Orders:OrderNotFound", $$"""{"orderId":"{{OrderId}}"}""");

        var r = ReplyInterpreter.Interpret(GetStatus, reply);

        Assert.Equal("OrderNotFound", r.ResponseType);
        Assert.True(r.ResponseCheck!.Valid);
    }

    [Fact]
    public void Undeclared_response_type_is_returned_without_check()
    {
        var r = ReplyInterpreter.Interpret(GetStatus, Reply(Guid.NewGuid(), "urn:message:Other:Thing", """{"x":1}"""));

        Assert.Equal("responded", r.Status);
        Assert.Equal("urn:message:Other:Thing", r.ResponseType);
        Assert.Null(r.ResponseCheck);
    }

    [Fact]
    public void MassTransit_fault_is_reported_with_exceptions()
    {
        var reply = Reply(Guid.NewGuid(), "urn:message:MassTransit:Fault[[RabbitMcp.Samples.Orders:GetOrderStatus]]",
            """
            {
              "faultId": "f1", "faultedMessageId": "m1", "timestamp": "2026-10-12T09:30:00Z",
              "exceptions": [{ "exceptionType": "System.TimeoutException", "message": "DB timeout", "stackTrace": "   at Repo.Get()" }],
              "message": { "orderId": "8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11" }
            }
            """);

        var r = ReplyInterpreter.Interpret(GetStatus, reply);

        Assert.Equal("faulted", r.Status);
        Assert.Equal("m1", r.Fault!.FaultedMessageId);
        var ex = Assert.Single(r.Fault.Exceptions);
        Assert.Equal("System.TimeoutException", ex.ExceptionType);
        Assert.Equal("DB timeout", ex.Message);
        Assert.Null(r.ResponseCheck);
    }
}
