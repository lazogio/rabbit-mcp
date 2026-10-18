// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

public class ResponderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);
    private static readonly ContractDescriptor GetStatus = Registry.Get(typeof(GetOrderStatus));
    private static readonly Uri Broker = new("amqp://localhost/");

    private static PendingRequest Request(DateTimeOffset received, DateTimeOffset? expires = null, string? faultAddress = null)
    {
        var requestId = Guid.NewGuid();
        return new PendingRequest(requestId, GetStatus, new MassTransitEnvelope
        {
            MessageId = Guid.NewGuid().ToString(),
            RequestId = requestId.ToString(),
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            SourceAddress = "rabbitmq://localhost/web_bus_abc?temporary=true",
            ResponseAddress = "rabbitmq://localhost/web_bus_abc?temporary=true",
            FaultAddress = faultAddress,
            MessageType = [GetStatus.MessageTypeUrn],
            Message = JsonDocument.Parse("""{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11"}""").RootElement.Clone(),
        }, received, expires, null);
    }

    [Fact]
    public void Inbox_is_bounded_and_lists_oldest_first()
    {
        var time = new ManualTime(T0);
        var inbox = new RequestInbox(time, capacity: 2);
        var first = Request(T0);
        var second = Request(T0.AddSeconds(1));

        Assert.Equal(InboxAddResult.Added, inbox.TryAdd(second));
        Assert.Equal(InboxAddResult.Added, inbox.TryAdd(first));
        Assert.Equal(InboxAddResult.Duplicate, inbox.TryAdd(first)); // redelivered: keep the first, no "busy" fault
        Assert.Equal(InboxAddResult.Full, inbox.TryAdd(Request(T0)));

        Assert.Equal(new[] { first.RequestId, second.RequestId }, inbox.List().Select(r => r.RequestId));
    }

    [Fact]
    public void Inbox_take_is_once_and_return_puts_it_back()
    {
        var inbox = new RequestInbox(new ManualTime(T0), 10);
        var request = Request(T0);
        inbox.TryAdd(request);

        Assert.Same(request, inbox.TryTake(request.RequestId));
        Assert.Null(inbox.TryTake(request.RequestId));

        inbox.Return(request);
        Assert.Same(request, inbox.TryTake(request.RequestId));
    }

    [Fact]
    public void Expired_requests_disappear_and_free_capacity()
    {
        var time = new ManualTime(T0);
        var inbox = new RequestInbox(time, 1);
        var request = Request(T0, expires: T0.AddSeconds(30));
        inbox.TryAdd(request);

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(inbox.List());
        Assert.Null(inbox.TryTake(request.RequestId));
        inbox.Return(request); // expired: not put back
        Assert.Equal(InboxAddResult.Added, inbox.TryAdd(Request(T0)));
    }

    [Fact]
    public async Task Long_poll_returns_when_a_request_arrives()
    {
        var inbox = new RequestInbox(TimeProvider.System, 10);
        var request = Request(DateTimeOffset.UtcNow);

        var wait = inbox.WaitForAsync(_ => true, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        inbox.TryAdd(request);
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Long_poll_times_out_and_honours_the_filter()
    {
        var inbox = new RequestInbox(TimeProvider.System, 10);
        inbox.TryAdd(Request(DateTimeOffset.UtcNow));

        Assert.True(await inbox.WaitForAsync(_ => true, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await inbox.WaitForAsync(_ => false, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public void Reply_envelope_answers_the_request_like_RespondAsync()
    {
        var request = Request(T0);
        var response = GetStatus.Responses.Single(r => r.Type == typeof(OrderStatus));
        var messageId = Guid.NewGuid();

        var envelope = ResponderEnvelopes.Reply(request, response,
            new OrderStatus { OrderId = Guid.Empty, Status = "Paid" }, Broker, "responder-q", messageId, T0.UtcDateTime);

        Assert.Equal(messageId.ToString(), envelope.MessageId);
        Assert.Equal(request.RequestId.ToString(), envelope.RequestId);
        Assert.Equal(request.Envelope.CorrelationId, envelope.CorrelationId);
        Assert.Equal(request.Envelope.ConversationId, envelope.ConversationId);
        Assert.Equal(request.Envelope.MessageId, envelope.InitiatorId);
        Assert.Equal(request.ResponseAddress, envelope.DestinationAddress);
        Assert.Equal(new[] { "urn:message:RabbitMcp.Samples.Orders:OrderStatus" }, envelope.MessageType);
        Assert.Equal("Paid", envelope.Message!.Value.GetProperty("status").GetString());
        Assert.Null(envelope.ResponseAddress);
    }

    [Fact]
    public void Fault_envelope_is_a_MassTransit_Fault_of_the_request()
    {
        var request = Request(T0, faultAddress: "rabbitmq://localhost/faults");

        var envelope = ResponderEnvelopes.Fault(request, "No such order here", Broker, "responder-q", Guid.NewGuid(), T0.UtcDateTime);

        Assert.Equal("rabbitmq://localhost/faults", envelope.DestinationAddress);
        Assert.Equal(new[]
        {
            "urn:message:MassTransit:Fault[[RabbitMcp.Samples.Orders:GetOrderStatus]]",
            "urn:message:MassTransit:Fault",
        }, envelope.MessageType);

        var fault = envelope.Message!.Value;
        Assert.Equal(request.Envelope.MessageId, fault.GetProperty("faultedMessageId").GetString());
        var exception = fault.GetProperty("exceptions")[0];
        Assert.Equal("No such order here", exception.GetProperty("message").GetString());
        Assert.Equal(ResponderEnvelopes.RejectedExceptionType, exception.GetProperty("exceptionType").GetString());
        Assert.Equal(GetStatus.MessageTypeUrn, fault.GetProperty("faultMessageTypes")[0].GetString());
        Assert.Equal(request.Envelope.Message!.Value.GetRawText(), fault.GetProperty("message").GetRawText());
    }

    [Fact]
    public void Faults_go_to_the_response_address_without_a_fault_address() =>
        Assert.Equal(Request(T0).ResponseAddress, Request(T0).FaultAddress);

    [Fact]
    public void RespondTo_selects_request_contracts_only()
    {
        Assert.Equal(new[] { "GetOrderStatus" }, ResponderContracts.From(Registry, "*").Requests.Select(c => c.Name));
        Assert.Throws<StartupException>(() => ResponderContracts.From(Registry, "OrderSubmitted"));
    }

    [Fact]
    public void Responder_queue_name_is_stable_per_client_name() =>
        Assert.Equal("rabbit-mcp-orders-responder", ReplyEndpoint.ResponderQueueName("rabbit-mcp/orders"));

    [Fact]
    public void One_reply_tool_per_reply_type()
    {
        var tools = ReplyToolFactory.CreateTools(ResponderContracts.From(Registry, "GetOrderStatus"));

        Assert.Equal(new[] { "reply_order_status", "reply_order_not_found" }, tools.Select(t => t.ProtocolTool.Name));

        var tool = tools[0].ProtocolTool;
        var props = tool.InputSchema.GetProperty("properties");
        Assert.True(props.TryGetProperty("requestId", out _));
        Assert.True(props.GetProperty("response").GetProperty("properties").TryGetProperty("status", out _));
        Assert.False(props.TryGetProperty("server", out _));
        Assert.False(props.TryGetProperty("user", out _));
        Assert.True(tool.OutputSchema!.Value.GetProperty("properties").TryGetProperty("elapsedMs", out _));
        Assert.Contains("GetOrderStatus", tool.Description);
    }
}
