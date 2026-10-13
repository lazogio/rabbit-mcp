using System.Collections.Concurrent;
using System.Diagnostics;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Server.Messaging;

/// <summary>A request received by the responder, waiting for the model to answer or reject it.</summary>
public sealed record PendingRequest(
    Guid RequestId,
    ContractDescriptor Contract,
    MassTransitEnvelope Envelope,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpiresAt,
    ActivityContext? TraceContext)
{
    /// <summary>Where MassTransit's request client listens for replies.</summary>
    public string ResponseAddress => Envelope.ResponseAddress!;

    /// <summary>Faults go to faultAddress if the requester set one, else to the response address.</summary>
    public string FaultAddress => Envelope.FaultAddress ?? Envelope.ResponseAddress!;
}

public enum InboxAddResult { Added, Duplicate, Full }

/// <summary>
/// In-memory inbox of unanswered requests. Bounded, and expired requests disappear: their requester has
/// already timed out, so an answer would go nowhere.
/// </summary>
public sealed class RequestInbox(TimeProvider time, int capacity)
{
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private TaskCompletionSource _added = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Capacity { get; } = capacity;

    /// <summary>Full: the caller rejects the request. Duplicate: the same request arrived twice; keep the first.</summary>
    public InboxAddResult TryAdd(PendingRequest request)
    {
        Prune();
        if (_pending.ContainsKey(request.RequestId))
            return InboxAddResult.Duplicate;
        if (_pending.Count >= Capacity)
            return InboxAddResult.Full;
        if (!_pending.TryAdd(request.RequestId, request))
            return InboxAddResult.Duplicate;

        Interlocked.Exchange(ref _added, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return InboxAddResult.Added;
    }

    /// <summary>
    /// Long poll: completes as soon as an unexpired request matching <paramref name="match"/> is pending,
    /// or after <paramref name="timeout"/>. Returns whether one is pending.
    /// </summary>
    public async Task<bool> WaitForAsync(Func<PendingRequest, bool> match, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            var signal = Volatile.Read(ref _added).Task; // read before checking, so no add is missed
            if (List().Any(match))
                return true;

            var left = deadline - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
                return false;
            try
            {
                await signal.WaitAsync(left, time, ct);
            }
            catch (TimeoutException)
            {
                return List().Any(match);
            }
        }
    }

    /// <summary>Removes and returns the request; null if unknown or expired.</summary>
    public PendingRequest? TryTake(Guid requestId)
    {
        if (!_pending.TryRemove(requestId, out var request))
            return null;
        return IsExpired(request) ? null : request;
    }

    /// <summary>Puts a request back after a recoverable send failure (e.g. broker briefly unavailable).</summary>
    public void Return(PendingRequest request)
    {
        if (!IsExpired(request))
            _pending.TryAdd(request.RequestId, request);
    }

    /// <summary>Unexpired requests, oldest first.</summary>
    public IReadOnlyList<PendingRequest> List()
    {
        Prune();
        return [.. _pending.Values.OrderBy(r => r.ReceivedAt)];
    }

    private bool IsExpired(PendingRequest r) => r.ExpiresAt is { } at && time.GetUtcNow() >= at;

    private void Prune()
    {
        foreach (var (id, request) in _pending)
        {
            if (IsExpired(request))
                _pending.TryRemove(id, out _);
        }
    }
}
