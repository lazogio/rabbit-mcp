using MassTransit;
using ModelContextProtocol;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.IntegrationTests.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.IntegrationTests;

/// <summary>Responder mode: a MassTransit request client asks, rabbit-mcp (the model) answers.</summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResponderTests(BrokerFixture broker)
{
    private async Task<PendingRequest> WaitForPending(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var match = broker.Responder.Pending().FirstOrDefault(r =>
                r.Envelope.Message is { } m && m.GetProperty("id").GetGuid() == id);
            if (match is not null)
                return match;
            await Task.Delay(100);
        }
        throw new TimeoutException($"AskHuman {id} never became pending.");
    }

    private static string? Field(System.Text.Json.JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    private static AskHuman Ask(Guid id) => new() { Id = id, Question = "Ship the order today?" };

    [Fact]
    public async Task Reply_completes_the_requesters_GetResponse()
    {
        var id = Guid.NewGuid();
        var response = broker.AskClient(TimeSpan.FromSeconds(30)).GetResponse<HumanAnswer, HumanDeclined>(Ask(id));

        var pending = await WaitForPending(id);
        Assert.Equal("AskHuman", pending.Contract.Name);
        Assert.NotNull(pending.ExpiresAt);

        var result = await broker.Responder.ReplyAsync(pending.RequestId,
            new HumanAnswer { Id = id, Answer = "Yes" }, "it", CancellationToken.None);

        var reply = await response.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(reply.Is(out Response<HumanAnswer>? answer));
        Assert.Equal("Yes", answer!.Message.Answer);
        Assert.Equal(Guid.Parse(result.MessageId), answer.MessageId);
        Assert.Equal(pending.RequestId, answer.RequestId);
        Assert.Equal("replied", result.Status);
        Assert.DoesNotContain(broker.Responder.Pending(), r => r.RequestId == pending.RequestId);

        var audit = broker.AuditEvents();
        Assert.Contains(audit, e => Field(e, "action") == AuditActions.Receive
                                    && Field(e, "requestId") == pending.RequestId.ToString());
        Assert.Contains(audit, e => Field(e, "action") == AuditActions.Respond && Field(e, "outcome") == "sent"
                                    && Field(e, "requestId") == pending.RequestId.ToString());
    }

    [Fact]
    public async Task Second_reply_type_is_matched_by_the_requester()
    {
        var id = Guid.NewGuid();
        var response = broker.AskClient(TimeSpan.FromSeconds(30)).GetResponse<HumanAnswer, HumanDeclined>(Ask(id));
        var pending = await WaitForPending(id);

        await broker.Responder.ReplyAsync(pending.RequestId, new HumanDeclined { Id = id }, "it", CancellationToken.None);

        var reply = await response.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(reply.Is(out Response<HumanDeclined>? declined));
        Assert.Equal(id, declined!.Message.Id);
    }

    [Fact]
    public async Task Reject_fails_the_requester_at_once_with_the_reason()
    {
        var id = Guid.NewGuid();
        var response = broker.AskClient(TimeSpan.FromSeconds(30)).GetResponse<HumanAnswer, HumanDeclined>(Ask(id));
        var pending = await WaitForPending(id);

        var result = await broker.Responder.RejectAsync(pending.RequestId, "Not my decision", "it", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RequestFaultException>(() => response.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("Not my decision", ex.Fault!.Exceptions.Single().Message);
        Assert.Equal("rejected", result.Status);
        Assert.Equal("Fault", result.ResponseType);
    }

    [Fact]
    public async Task Wrong_or_invalid_reply_is_refused_and_the_request_stays_pending()
    {
        var id = Guid.NewGuid();
        var response = broker.AskClient(TimeSpan.FromSeconds(30)).GetResponse<HumanAnswer, HumanDeclined>(Ask(id));
        var pending = await WaitForPending(id);

        var wrongType = await Assert.ThrowsAsync<McpException>(() => broker.Responder.ReplyAsync(pending.RequestId,
            new OrderNotFound { OrderId = id }, "it", CancellationToken.None));
        Assert.Contains("reply_human_answer", wrongType.Message);

        var invalid = await Assert.ThrowsAsync<McpException>(() => broker.Responder.ReplyAsync(pending.RequestId,
            new HumanAnswer { Id = id, Answer = "" }, "it", CancellationToken.None));
        Assert.Contains("nothing was sent", invalid.Message);

        Assert.Contains(broker.Responder.Pending(), r => r.RequestId == pending.RequestId);

        await broker.Responder.ReplyAsync(pending.RequestId, new HumanAnswer { Id = id, Answer = "Fine" }, "it", CancellationToken.None);
        var reply = await response.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(reply.Is(out Response<HumanAnswer>? answer));
        Assert.Equal("Fine", answer!.Message.Answer);
    }

    [Fact]
    public async Task Expired_request_cannot_be_answered()
    {
        var id = Guid.NewGuid();
        var response = broker.AskClient(TimeSpan.FromSeconds(2)).GetResponse<HumanAnswer, HumanDeclined>(Ask(id));
        var pending = await WaitForPending(id);

        await Assert.ThrowsAsync<RequestTimeoutException>(() => response.WaitAsync(TimeSpan.FromSeconds(15)));
        await Task.Delay(200);

        var ex = await Assert.ThrowsAsync<McpException>(() => broker.Responder.ReplyAsync(pending.RequestId,
            new HumanAnswer { Id = id, Answer = "Too late" }, "it", CancellationToken.None));
        Assert.Contains("expired", ex.Message);
        Assert.DoesNotContain(broker.Responder.Pending(), r => r.RequestId == pending.RequestId);
    }

    [Fact]
    public async Task Unknown_request_id_is_an_error()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Responder.RejectAsync(Guid.NewGuid(), "no", "it", CancellationToken.None));
        Assert.Contains("No pending request", ex.Message);
    }
}
