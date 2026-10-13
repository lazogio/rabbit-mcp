// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class ExistenceCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Positive_result_is_remembered_until_the_ttl_expires()
    {
        var time = new ManualTime(T0);
        var cache = new ExistenceCache(time, TimeSpan.FromSeconds(30));

        Assert.False(cache.IsKnownToExist("ex"));
        cache.MarkExists("ex");
        Assert.True(cache.IsKnownToExist("ex"));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(cache.IsKnownToExist("ex"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.IsKnownToExist("ex"));
    }

    [Fact]
    public void Invalidate_forgets_immediately()
    {
        var cache = new ExistenceCache(new ManualTime(T0), TimeSpan.FromSeconds(30));
        cache.MarkExists("ex");
        cache.Invalidate("ex");
        Assert.False(cache.IsKnownToExist("ex"));
    }

    [Fact]
    public void Zero_ttl_disables_the_cache()
    {
        var cache = new ExistenceCache(new ManualTime(T0), TimeSpan.Zero);
        cache.MarkExists("ex");
        Assert.False(cache.Enabled);
        Assert.False(cache.IsKnownToExist("ex"));
    }

    [Fact]
    public void Names_are_case_sensitive_like_RabbitMQ()
    {
        var cache = new ExistenceCache(new ManualTime(T0), TimeSpan.FromSeconds(30));
        cache.MarkExists("Orders");
        Assert.False(cache.IsKnownToExist("orders"));
    }
}
