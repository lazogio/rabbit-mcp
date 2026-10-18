// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Selects messages of an error queue by what went wrong instead of by id: exception type and message (MassTransit
/// fault headers; for RabbitMQ dead-lettering the x-death reason), contract, and when it failed.
/// </summary>
public sealed record MessageFilter(
    string? ExceptionType = null,
    string? ExceptionContains = null,
    string? Contract = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null)
{
    /// <summary>Null when no criterion is given. since/until: 30m, 2h, 7d (before now) or an ISO 8601 time.</summary>
    public static MessageFilter? From(
        string? exceptionType, string? exceptionContains, string? contract, string? since, string? until, DateTimeOffset now)
    {
        var filter = new MessageFilter(
            Clean(exceptionType), Clean(exceptionContains), Clean(contract),
            AuditReader.ParseSince(since, now), AuditReader.ParseSince(until, now));
        return filter is { ExceptionType: null, ExceptionContains: null, Contract: null, Since: null, Until: null } ? null : filter;
    }

    public bool Matches(PeekedMessage m)
    {
        if (ExceptionType is not null && !Contains(m.Fault?.ExceptionType, ExceptionType))
            return false;
        if (ExceptionContains is not null && !Contains(m.Fault?.ExceptionMessage, ExceptionContains)
            && !Contains(m.Fault?.Reason, ExceptionContains) && !(m.DeadLettered?.Any(d => Contains(d.Reason, ExceptionContains)) ?? false))
            return false;
        if (Contract is not null && !string.Equals(m.Contract?.Contract, Contract, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Since is not null || Until is not null)
        {
            var at = FailedAt(m);
            if (at is null || at < Since || at > Until)
                return false;
        }
        return true;
    }

    /// <summary>When it failed: MT-Fault-Timestamp, else the last x-death time, else when it was sent.</summary>
    public static DateTimeOffset? FailedAt(PeekedMessage m) =>
        Parse(m.Fault?.Timestamp)
        ?? Parse(m.DeadLettered?.FirstOrDefault()?.Time)
        ?? (m.SentTimeUtc is { } sent ? new DateTimeOffset(DateTime.SpecifyKind(sent, DateTimeKind.Utc)) : null);

    private static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static bool Contains(string? value, string wanted) =>
        value?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
