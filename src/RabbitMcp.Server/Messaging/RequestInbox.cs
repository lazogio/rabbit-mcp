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

/// <summary>
/// In-memory inbox of unanswered requests. Bounded, and expired requests disappear: their requester has
/// already timed out, so an answer would go nowhere.
/// </summary>
public sealed class RequestInbox(TimeProvider time, int capacity)
{
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();

    public int Capacity { get; } = capacity;

    /// <summary>False when the inbox is full (the caller rejects the request) or the id is already pending.</summary>
    public bool TryAdd(PendingRequest request)
    {
        Prune();
        return _pending.Count < Capacity && _pending.TryAdd(request.RequestId, request);
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
