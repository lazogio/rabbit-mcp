// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using RabbitMQ.Client;

namespace RabbitMcp.Server.Messaging;

/// <summary>Why MassTransit moved a message to an _error/_skipped queue.</summary>
public sealed record FaultInfo(
    string? Reason,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace,
    string? ConsumerType,
    string? InputAddress,
    string? MessageType,
    string? Timestamp,
    int? RetryCount,
    int? RedeliveryCount,
    string? FaultedOnHost);

/// <summary>One entry of RabbitMQ's x-death header: a queue dead-lettered the message (DLX).</summary>
public sealed record DeathEvent(
    [property: System.ComponentModel.Description("Queue that dead-lettered the message.")] string? Queue,
    [property: System.ComponentModel.Description("rejected | expired | maxlen | delivery_limit")] string? Reason,
    [property: System.ComponentModel.Description("How many times this queue dead-lettered it for this reason.")] long? Count,
    [property: System.ComponentModel.Description("First time it happened.")] string? Time,
    [property: System.ComponentModel.Description("Exchange the message had been published to.")] string? Exchange,
    IReadOnlyList<string>? RoutingKeys);

/// <summary>
/// Reads and strips fault headers: MassTransit's (MT-Reason, MT-Fault-*, MT-Host-*) and RabbitMQ dead-lettering's
/// (x-death, x-first-death-*, x-last-death-*).
/// </summary>
public static class FaultHeaders
{
    private const int MaxDeathEvents = 5;
    private const int MaxStackTraceLines = 12;

    public static FaultInfo? Parse(IDictionary<string, object?>? headers)
    {
        var reason = GetString(headers, HeaderNames.Reason);
        var exceptionType = GetString(headers, HeaderNames.FaultExceptionType);
        if (reason is null && exceptionType is null)
            return null;

        var machine = GetString(headers, HeaderNames.HostMachineName);
        var process = GetString(headers, HeaderNames.HostProcessName);

        return new FaultInfo(
            Reason: reason,
            ExceptionType: exceptionType,
            ExceptionMessage: GetString(headers, HeaderNames.FaultMessage),
            StackTrace: TrimStackTrace(GetString(headers, HeaderNames.FaultStackTrace)),
            ConsumerType: GetString(headers, HeaderNames.FaultConsumerType),
            InputAddress: GetString(headers, HeaderNames.FaultInputAddress),
            MessageType: GetString(headers, HeaderNames.FaultMessageType),
            Timestamp: GetString(headers, HeaderNames.FaultTimestamp),
            RetryCount: GetInt(headers, HeaderNames.FaultRetryCount),
            RedeliveryCount: GetInt(headers, HeaderNames.FaultRedeliveryCount),
            FaultedOnHost: machine is null && process is null ? null : $"{machine ?? "?"} / {process ?? "?"}");
    }

    public static bool IsFaultHeader(string key) =>
        key.Equals(HeaderNames.Reason, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(HeaderNames.FaultPrefix, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(HeaderNames.HostPrefix, StringComparison.OrdinalIgnoreCase)
        || key.Equals(HeaderNames.Death, StringComparison.Ordinal)
        || key.StartsWith(HeaderNames.FirstDeathPrefix, StringComparison.Ordinal)
        || key.StartsWith(HeaderNames.LastDeathPrefix, StringComparison.Ordinal);

    /// <summary>The x-death history, newest first (at most 5 entries); null when the message was never dead-lettered.</summary>
    public static IReadOnlyList<DeathEvent>? ParseDeaths(IDictionary<string, object?>? headers)
    {
        // AMQP arrays arrive as lists of objects, tables as dictionaries (byte[] and string are not IEnumerable<object>).
        if (headers is null || !headers.TryGetValue(HeaderNames.Death, out var value) || value is not IEnumerable<object?> list)
            return null;

        var deaths = new List<DeathEvent>();
        foreach (var item in list)
        {
            if (item is not IDictionary<string, object?> entry)
                continue;
            deaths.Add(new DeathEvent(
                Queue: GetString(entry, "queue"),
                Reason: GetString(entry, "reason"),
                Count: GetInt(entry, "count"),
                Time: GetString(entry, "time"),
                Exchange: GetString(entry, "exchange"),
                RoutingKeys: entry.TryGetValue("routing-keys", out var keys) && keys is IEnumerable<object?> k
                    ? k.Select(ToDisplayString).OfType<string>().ToArray()
                    : null));
            if (deaths.Count == MaxDeathEvents)
                break;
        }
        return deaths.Count == 0 ? null : deaths;
    }

    /// <summary>The queue that dead-lettered the message most recently (x-death is newest first).</summary>
    public static string? LastDeathQueue(IDictionary<string, object?>? headers) =>
        ParseDeaths(headers)?.FirstOrDefault()?.Queue is { Length: > 0 } queue
            ? queue
            : GetString(headers, HeaderNames.LastDeathPrefix + "queue") is { Length: > 0 } last
                ? last
                : GetString(headers, HeaderNames.FirstDeathPrefix + "queue") is { Length: > 0 } first ? first : null;

    /// <summary>
    /// Copy of the headers without fault information, for republishing. A message leaving a dead-letter queue also
    /// loses what parked it (X-Dead-Letter*, and the link of a corrected original): it is live again, not parked.
    /// </summary>
    public static Dictionary<string, object?> WithoutFaultHeaders(IDictionary<string, object?>? headers)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (headers is null)
            return result;

        var correctedOriginal = headers.ContainsKey(HeaderNames.CorrectedTo);
        foreach (var (key, value) in headers)
        {
            if (!IsFaultHeader(key) && !IsParkingHeader(key)
                && !(correctedOriginal && key is HeaderNames.CorrectedBy or HeaderNames.CorrectedAt))
                result[key] = value;
        }
        return result;
    }

    private static bool IsParkingHeader(string key) =>
        key is HeaderNames.DeadLetteredBy or HeaderNames.DeadLetteredFrom or HeaderNames.DeadLetteredAt
            or HeaderNames.DeadLetterReason or HeaderNames.CorrectedTo or HeaderNames.OriginalExpiration;

    /// <summary>Non-fault headers as strings, for display.</summary>
    public static Dictionary<string, string?> OtherHeadersForDisplay(IDictionary<string, object?>? headers)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (headers is null)
            return result;

        foreach (var (key, value) in headers)
        {
            if (!IsFaultHeader(key))
                result[key] = ToDisplayString(value);
        }
        return result;
    }

    public static string? GetString(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value) ? ToDisplayString(value) : null;

    public static int? GetInt(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            short s => s,
            byte b => b,
            sbyte sb => sb,
            uint ui => (int)ui,
            _ => int.TryParse(ToDisplayString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,
        };
    }

    /// <summary>AMQP header values arrive as byte[] for strings; this normalises them.</summary>
    public static string? ToDisplayString(object? value) => value switch
    {
        null => null,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        AmqpTimestamp ts => TimeOf(ts.UnixTime)?.ToString("O", CultureInfo.InvariantCulture) ?? ts.UnixTime.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(ToDisplayString)) + "]",
        _ => value.ToString(),
    };

    /// <summary>An AMQP timestamp; null when it is outside the range of a date (it is whatever the publisher wrote).</summary>
    public static DateTimeOffset? TimeOf(long unixSeconds) =>
        unixSeconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;

    public static string? TrimStackTrace(string? stackTrace)
    {
        if (stackTrace is null)
            return null;

        var lines = stackTrace.Split('\n');
        return lines.Length <= MaxStackTraceLines
            ? stackTrace
            : string.Join('\n', lines.Take(MaxStackTraceLines)) + $"\n   ... ({lines.Length - MaxStackTraceLines} more lines)";
    }
}

/// <summary>
/// Where a faulted message goes back to: an exchange (MassTransit endpoints), or a queue reached directly through the
/// default exchange (<see cref="Exchange"/> "", <see cref="RoutingKey"/> = queue; RabbitMQ dead-lettering).
/// <see cref="Conflict"/> is set when the message's own MT-Fault-InputAddress or x-death header disagrees with the
/// queue the error queue belongs to: headers are written by whoever published the message, so such a target is not
/// used without an explicit targetExchange/targetQueue.
/// </summary>
public sealed record RepublishTarget(string Exchange, string ResolvedFrom, string? Conflict = null, string RoutingKey = "")
{
    public const string QueuePrefix = "queue:";

    public static RepublishTarget ToQueue(string queue, string resolvedFrom, string? conflict = null) =>
        new("", resolvedFrom, conflict, queue);

    public bool IsQueue => Exchange.Length == 0;

    /// <summary>"exchange" or "queue:name", as shown to the model and written to the audit.</summary>
    public string Display => IsQueue ? QueuePrefix + RoutingKey : Exchange;
}

public static class RepublishTargetResolver
{
    private static readonly string[] Suffixes = ["_error", "_skipped", "_dead"];

    /// <summary>The queue an _error/_skipped/_dead queue belongs to; null for other names.</summary>
    public static string? OwnerQueue(string queue) => OwnerOf(queue)?.Queue;

    /// <summary>
    /// MassTransit binds every receive endpoint queue to a fanout exchange of the same name, so the original queue is
    /// reached by publishing to that exchange; the queue is taken from MT-Fault-InputAddress. A message RabbitMQ
    /// dead-lettered (x-death, e.g. a plain-JSON service rejected it) goes straight back to the queue that rejected it,
    /// through the default exchange. Without either, the error queue name without its _error/_skipped/_dead suffix.
    /// </summary>
    public static RepublishTarget? Resolve(string errorQueue, IDictionary<string, object?>? headers)
    {
        var fromName = OwnerOf(errorQueue);
        var fromHeader = BrokerAddress.GetEntityName(FaultHeaders.GetString(headers, HeaderNames.FaultInputAddress));

        if (!string.IsNullOrEmpty(fromHeader))
        {
            var conflict = fromName is not null && !string.Equals(fromHeader, fromName.Value.Queue, StringComparison.Ordinal)
                ? $"MT-Fault-InputAddress names '{fromHeader}', but '{errorQueue}' belongs to '{fromName.Value.Queue}'. " +
                  "Message headers can be set by whoever published the message, so pass targetExchange to choose explicitly."
                : null;
            return new RepublishTarget(fromHeader, HeaderNames.FaultInputAddress, conflict);
        }

        if (FaultHeaders.LastDeathQueue(headers) is { } died)
        {
            var conflict =
                string.Equals(died, errorQueue, StringComparison.Ordinal)
                    ? $"x-death names '{errorQueue}' itself; pass targetQueue or targetExchange to choose where it goes."
                    : fromName is not null && !string.Equals(died, fromName.Value.Queue, StringComparison.Ordinal)
                        ? $"x-death names '{died}', but '{errorQueue}' belongs to '{fromName.Value.Queue}'. Message headers can " +
                          "be set by whoever published the message, so pass targetQueue to choose explicitly."
                        : null;
            return RepublishTarget.ToQueue(died, "x-death (RabbitMQ dead-lettering)", conflict);
        }

        return fromName is { } owner
            ? new RepublishTarget(owner.Queue, $"queue name without '{owner.Suffix}'")
            : null;
    }

    private static (string Queue, string Suffix)? OwnerOf(string errorQueue)
    {
        foreach (var suffix in Suffixes)
        {
            if (errorQueue.EndsWith(suffix, StringComparison.Ordinal) && errorQueue.Length > suffix.Length)
                return (errorQueue[..^suffix.Length], suffix);
        }
        return null;
    }
}

/// <summary>Name of the dead-letter queue for an error queue (RabbitMcp:DeadLetterQueue, "{queue}" = owner queue).</summary>
public static class DeadLetterNaming
{
    public static string For(string errorQueue, string pattern) =>
        (string.IsNullOrWhiteSpace(pattern) ? "{queue}_dead" : pattern)
            .Replace("{queue}", RepublishTargetResolver.OwnerQueue(errorQueue) ?? errorQueue, StringComparison.Ordinal)
            .Trim();

    /// <exception cref="ModelContextProtocol.McpException">Not a usable queue name.</exception>
    public static void Validate(string queue)
    {
        if (string.IsNullOrWhiteSpace(queue) || Encoding.UTF8.GetByteCount(queue) > 255)
            throw new ModelContextProtocol.McpException($"'{queue}' is not a valid queue name (1-255 bytes).");
        if (queue.StartsWith("amq.", StringComparison.Ordinal))
            throw new ModelContextProtocol.McpException($"'{queue}' is reserved by RabbitMQ (amq.*).");
    }
}

