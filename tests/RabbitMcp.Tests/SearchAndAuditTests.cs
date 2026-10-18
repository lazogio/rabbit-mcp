// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMcp.Server;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

public class SearchAndAuditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static PeekedMessage Peeked(string? messageId, string? correlationId, string? json = null, string? raw = null) =>
        new(0, messageId, correlationId, false, "application/json", null, null, null, null, null, null,
            json is null ? null : JsonDocument.Parse(json).RootElement.Clone(), raw, false, 0,
            new Dictionary<string, string?>());

    [Fact]
    public void Matcher_needs_a_criterion_and_combines_them()
    {
        Assert.Throws<McpException>(() => new MessageMatcher(null, " ", null));

        var message = Peeked("m-1", "c-1", """{"orderId":"5CA1AB1E-0000-4000-8000-000000000001","email":"ana@example.ge"}""");
        Assert.True(new MessageMatcher("M-1", null, null).Matches(message));
        Assert.True(new MessageMatcher(null, "c-1", "ana@EXAMPLE.ge").Matches(message));
        Assert.True(new MessageMatcher(null, null, "5ca1ab1e-0000").Matches(message)); // case-insensitive
        Assert.False(new MessageMatcher("m-1", "c-2", null).Matches(message));      // all criteria must hold
        Assert.True(new MessageMatcher(null, null, "timeout").Matches(Peeked(null, null, raw: "plain text: Timeout")));
    }

    private static (AuditLog Log, string Path, ManualTime Time) NewAudit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rabbit-mcp-audit-{Guid.NewGuid():N}.jsonl");
        var time = new ManualTime(T0);
        var log = new AuditLog(Options.Create(new RabbitMcpOptions { AuditLogPath = path, AuditIncludePayload = true }),
            time, NullLogger<AuditLog>.Instance);
        return (log, path, time);
    }

    private static (string Path, ManualTime Time) SampleAudit()
    {
        var (log, path, time) = NewAudit();
        void At(int minutes, string action, string outcome, string client, string? contract = null, string? queue = null, string? id = null)
        {
            time.Now = T0.AddMinutes(minutes);
            log.Write(log.Event(action, outcome, client, contract, queue: queue, messageId: id,
                detail: new string('x', 150), message: new { orderId = id }));
        }
        At(0, AuditActions.Publish, "sent", "alice (claude-code 2.1)", "OrderPlaced", id: "m-1");
        At(10, AuditActions.Republish, "republished", "bob", queue: "order-placed_error", id: "m-1");
        At(20, AuditActions.Publish, "error", "alice", "OrderPlaced", id: "m-2");
        At(30, AuditActions.DeadLetter, "moved", "bob", queue: "order-placed_error", id: "m-3");
        File.AppendAllText(path, "{not json\n");      // damaged line
        File.AppendAllText(path, "{\"timestamp\":"); // line being written
        return (path, time);
    }

    [Fact]
    public void Recent_activity_is_newest_first_filtered_and_without_payload_by_default()
    {
        var (path, _) = SampleAudit();

        var all = AuditReader.Read(path, new AuditQuery(), chunkSize: 64); // small chunks: lines span several reads
        Assert.Equal(new[] { "deadletter", "publish", "republish", "publish" }, all.Events.Select(e => e.Action));
        Assert.All(all.Events, e => Assert.Null(e.Message));

        Assert.Equal(new[] { "m-3", "m-1" }, AuditReader.Read(path, new AuditQuery(Client: "BOB")).Events.Select(e => e.MessageId));
        Assert.Single(AuditReader.Read(path, new AuditQuery(Action: "publish", Outcome: "error")).Events);
        Assert.Equal(2, AuditReader.Read(path, new AuditQuery(Id: "m-1")).Events.Count);
        Assert.Equal(2, AuditReader.Read(path, new AuditQuery(Queue: "placed_error")).Events.Count);

        var withPayload = AuditReader.Read(path, new AuditQuery(Id: "m-3", IncludePayload: true));
        Assert.Equal("m-3", withPayload.Events.Single().Message!.Value.GetProperty("orderId").GetString());
    }

    [Fact]
    public void Recent_activity_stops_at_since_and_reports_truncation()
    {
        var (path, _) = SampleAudit();

        var recent = AuditReader.Read(path, new AuditQuery(Since: T0.AddMinutes(15)));
        Assert.Equal(new[] { "m-3", "m-2" }, recent.Events.Select(e => e.MessageId));

        var limited = AuditReader.Read(path, new AuditQuery(Limit: 2));
        Assert.Equal(2, limited.Events.Count);
        Assert.True(limited.Truncated);

        Assert.Empty(AuditReader.Read(path + ".missing", new AuditQuery()).Events);
    }

    [Theory]
    [InlineData("30m", -30)]
    [InlineData("2h", -120)]
    [InlineData("7d", -7 * 24 * 60)]
    public void Since_accepts_relative_times(string since, int minutes) =>
        Assert.Equal(T0.AddMinutes(minutes), AuditReader.ParseSince(since, T0));

    [Fact]
    public void Since_accepts_timestamps_and_rejects_nonsense()
    {
        Assert.Equal(T0, AuditReader.ParseSince("2026-10-18T09:00:00Z", T0.AddDays(1)));
        Assert.Null(AuditReader.ParseSince(" ", T0));
        Assert.Throws<McpException>(() => AuditReader.ParseSince("yesterday", T0));
    }
}
