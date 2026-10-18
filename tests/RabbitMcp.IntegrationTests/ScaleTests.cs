// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using ModelContextProtocol;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;

namespace RabbitMcp.IntegrationTests;

/// <summary>Bulk recovery by filter and reading streams, against a real broker.</summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ScaleTests(BrokerFixture broker)
{
    [Fact]
    public async Task Messages_are_parked_by_their_fault_not_by_id()
    {
        const string queue = "it-bulk-errors";
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            for (var i = 0; i < 5; i++)
            {
                var type = i < 3 ? "System.TimeoutException" : "System.ArgumentException";
                await channel.BasicPublishAsync("", queue, mandatory: false, new BasicProperties
                {
                    MessageId = $"bulk-{i}",
                    Headers = new Dictionary<string, object?> { ["MT-Reason"] = "fault", ["MT-Fault-ExceptionType"] = type, ["MT-Fault-Message"] = $"failure {i}" },
                }, Encoding.UTF8.GetBytes($$"""{"n":{{i}}}"""));
            }
        }
        await broker.WaitForQueueCount(queue, 5);

        var timeouts = new MessageFilter(ExceptionType: "Timeout");
        var summary = await broker.Browser.SummarizeAsync(queue, timeouts, 1000, force: false, CancellationToken.None);
        Assert.Equal(5, summary.Scanned);
        Assert.Equal(3, summary.Matched);
        var group = Assert.Single(summary.Groups, g => g.ExceptionType == "System.TimeoutException" && g.ExceptionMessage == "failure 0");
        Assert.Equal(new[] { "bulk-0" }, group.SampleMessageIds);
        await broker.WaitForQueueCount(queue, 5); // a summary changes nothing

        // Without a filter count is limited to MaxMessagesPerCall; with one, to MaxBulkMessages.
        await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.DeadLetterAsync(queue, 100, null, null, "x", CancellationToken.None));
        var moved = await broker.Browser.DeadLetterAsync(queue, 100, null, null, "database was down", CancellationToken.None,
            "it", timeouts);

        Assert.Equal(3, moved.Moved);
        Assert.False(moved.MoreMayRemain);
        await broker.WaitForQueueCount(queue, 2);
        await broker.WaitForQueueCount(queue + "_dead", 3);
        var left = await broker.Browser.PeekAsync(queue, 5, false, CancellationToken.None);
        Assert.All(left.Messages, m => Assert.Equal("System.ArgumentException", m.Fault!.ExceptionType));
    }

    [Fact]
    public async Task A_stream_is_read_from_a_position_and_stays_as_it_was()
    {
        const string stream = "it-stream";
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: true, CancellationToken.None))
        {
            await channel.QueueDeclareAsync(stream, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "stream" });
            for (var i = 0; i < 5; i++)
                await channel.BasicPublishAsync("", stream, mandatory: true,
                    new BasicProperties { MessageId = $"s-{i}", Type = "stock.adjusted" },
                    Encoding.UTF8.GetBytes($$"""{"sku":"LAMP-01","delta":{{i}}}"""));
        }

        var first = await broker.Browser.ReadStreamAsync(stream, "first", 3, isStream: null, force: false, CancellationToken.None);
        Assert.Equal(3, first.Returned);
        Assert.Equal(new[] { "s-0", "s-1", "s-2" }, first.Messages.Select(m => m.MessageId));
        Assert.Equal("RawStockAdjusted", first.Messages[0].Contract!.Contract);
        Assert.Equal(first.FirstOffset + 2, first.LastOffset);

        var rest = await broker.Browser.ReadStreamAsync(stream, (first.LastOffset + 1).ToString(), 10, true, false, CancellationToken.None);
        Assert.Equal(new[] { "s-3", "s-4" }, rest.Messages.Select(m => m.MessageId));

        // Reading changed nothing: from the start again, everything is still there.
        var again = await broker.Browser.ReadStreamAsync(stream, "10m", 10, true, false, CancellationToken.None);
        Assert.Equal(5, again.Returned);
    }

    [Fact]
    public async Task Reading_a_classic_queue_as_a_stream_gives_everything_back()
    {
        const string queue = "it-not-a-stream";
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.BasicPublishAsync("", queue, mandatory: false, new BasicProperties(), "{}"u8.ToArray());
        }
        await broker.WaitForQueueCount(queue, 1);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            broker.Browser.ReadStreamAsync(queue, "first", 5, isStream: null, force: false, CancellationToken.None));
        Assert.Contains("not a stream", ex.Message);
        await broker.WaitForQueueCount(queue, 1);
    }
}
