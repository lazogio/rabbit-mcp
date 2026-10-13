using System.Collections.Concurrent;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Remembers for a short time that a broker entity exists, so publishing does not open an extra
/// channel for a passive declare on every call. Only positive results are cached: a newly created
/// exchange is seen immediately, and a deleted one is caught by the publish itself (404) and invalidated.
/// </summary>
public sealed class ExistenceCache(TimeProvider time, TimeSpan ttl)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _validUntil = new(StringComparer.Ordinal);

    public bool Enabled => ttl > TimeSpan.Zero;

    public bool IsKnownToExist(string name) =>
        Enabled && _validUntil.TryGetValue(name, out var until) && time.GetUtcNow() < until;

    public void MarkExists(string name)
    {
        if (Enabled)
            _validUntil[name] = time.GetUtcNow() + ttl;
    }

    public void Invalidate(string name) => _validUntil.TryRemove(name, out _);
}
