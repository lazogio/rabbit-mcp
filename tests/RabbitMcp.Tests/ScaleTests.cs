// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Server;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;

namespace RabbitMcp.Tests;

internal sealed class MovableTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

public class ScaleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static PeekedMessage Message(string? exceptionType, string? exceptionMessage, string? contract, DateTimeOffset? failedAt,
        string? deathReason = null) => new(
        Index: 0, MessageId: "m", CorrelationId: null, Redelivered: false, ContentType: null, MessageTypes: null,
        SentTimeUtc: null,
        Contract: contract is null ? null : new ContractCheck(contract, 1, true, []),
        Fault: exceptionType is null && exceptionMessage is null ? null
            : new FaultInfo("fault", exceptionType, exceptionMessage, null, null, null, null, failedAt?.ToString("O"), null, null, null),
        DeadLettered: deathReason is null ? null : new[] { new DeathEvent("q", deathReason, 1, failedAt?.ToString("O"), null, null) },
        RepublishTarget: null, RepublishWarning: null, Payload: null, RawBody: null, Truncated: false, BodyBytes: 0,
        Headers: new Dictionary<string, string?>());

    [Fact]
    public void Filter_selects_by_fault_contract_and_time()
    {
        Assert.Null(MessageFilter.From(null, " ", null, null, null, T0));

        var timeout = Message("System.TimeoutException", "Order database did not answer", "OrderPlaced", T0.AddMinutes(-30));
        var invalid = Message("System.ArgumentException", "Unknown SKU", "OrderPlaced", T0.AddHours(-5));
        var rejected = Message(null, null, null, T0.AddMinutes(-5), deathReason: "rejected");

        var byType = MessageFilter.From("timeout", null, null, null, null, T0)!;
        Assert.True(byType.Matches(timeout));
        Assert.False(byType.Matches(invalid));
        Assert.False(byType.Matches(rejected));

        Assert.True(MessageFilter.From(null, "unknown sku", null, null, null, T0)!.Matches(invalid));
        Assert.True(MessageFilter.From(null, "rejected", null, null, null, T0)!.Matches(rejected)); // x-death reason

        var lastHour = MessageFilter.From(null, null, "orderplaced", "1h", null, T0)!;
        Assert.True(lastHour.Matches(timeout));
        Assert.False(lastHour.Matches(invalid));

        var before = MessageFilter.From(null, null, null, null, "1h", T0)!;
        Assert.True(before.Matches(invalid));
        Assert.False(before.Matches(timeout));
    }

    [Theory]
    [InlineData(null, "first")]
    [InlineData("LAST", "last")]
    [InlineData("next", "next")]
    [InlineData("42", "offset 42")]
    public void Stream_offsets_are_named_numbered_or_times(string? from, string shown)
    {
        var (offset, text) = QueueBrowser.StreamOffset(from, T0);
        Assert.Equal(shown, text);
        if (from == "42")
            Assert.Equal(42L, offset);
    }

    [Fact]
    public void Stream_offset_from_a_time_is_a_timestamp()
    {
        var (offset, _) = QueueBrowser.StreamOffset("10m", T0);
        Assert.Equal(T0.AddMinutes(-10).ToUnixTimeSeconds(), Assert.IsType<AmqpTimestamp>(offset).UnixTime);
    }

    private static RateLimiter Limiter(string limits, TimeProvider time) => new(new RabbitMcpOptions { RateLimits = limits }, time);

    [Fact]
    public void Rate_limits_stop_a_caller_and_free_up_with_time()
    {
        var time = new MovableTime(T0);
        var limiter = Limiter("publish:2/h; *:3/m", time);

        Assert.Null(limiter.TryAcquire("alice", "publish"));
        Assert.Null(limiter.TryAcquire("alice", "publish"));
        var refused = limiter.TryAcquire("alice", "publish");
        Assert.Contains("at most 2/h publish calls for alice", refused);
        Assert.Null(limiter.TryAcquire("bob", "publish")); // per caller

        Assert.Null(limiter.TryAcquire("alice", "republish")); // 3rd change of alice this minute (the refused one did not count)
        Assert.Contains("changes", limiter.TryAcquire("alice", "respond"));

        time.Now = T0.AddHours(1).AddSeconds(1);
        Assert.Null(limiter.TryAcquire("alice", "publish"));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("delete:1/h")]
    [InlineData("publish:many")]
    [InlineData("publish:0/h")]
    public void Invalid_rate_limits_stop_the_server(string limits) =>
        Assert.Throws<StartupException>(() => Limiter(limits, TimeProvider.System));

    [Fact]
    public void Only_changes_count()
    {
        JsonElement True = JsonSerializer.SerializeToElement(true);
        var republish = new ToolRule(Permission.Republish, []);
        Assert.Null(RateLimiter.KindOf("peek_messages", new ToolRule(Permission.Read, []), null));
        Assert.Null(RateLimiter.KindOf("pending_requests", new ToolRule(Permission.Respond, []), null));
        Assert.Null(RateLimiter.KindOf("republish_from_error", republish, new Dictionary<string, JsonElement>()));
        Assert.Equal("republish", RateLimiter.KindOf("republish_from_error", republish, new Dictionary<string, JsonElement> { ["confirm"] = True }));
        Assert.Equal("publish", RateLimiter.KindOf("publish_x", new ToolRule(Permission.Publish, []), null));
        Assert.Null(RateLimiter.KindOf("unknown", null, null));
    }
}
