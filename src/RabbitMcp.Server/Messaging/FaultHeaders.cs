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

/// <summary>Reads and strips MassTransit fault headers (MT-Reason, MT-Fault-*, MT-Host-*).</summary>
public static class FaultHeaders
{
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
        || key.StartsWith(HeaderNames.HostPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copy of the headers without fault information, for republishing.</summary>
    public static Dictionary<string, object?> WithoutFaultHeaders(IDictionary<string, object?>? headers)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (headers is null)
            return result;

        foreach (var (key, value) in headers)
        {
            if (!IsFaultHeader(key))
                result[key] = value;
        }
        return result;
    }

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
            long l => (int)l,
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
        AmqpTimestamp ts => DateTimeOffset.FromUnixTimeSeconds(ts.UnixTime).ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(ToDisplayString)) + "]",
        _ => value.ToString(),
    };

    private static string? TrimStackTrace(string? stackTrace)
    {
        if (stackTrace is null)
            return null;

        var lines = stackTrace.Split('\n');
        return lines.Length <= MaxStackTraceLines
            ? stackTrace
            : string.Join('\n', lines.Take(MaxStackTraceLines)) + $"\n   ... ({lines.Length - MaxStackTraceLines} more lines)";
    }
}

/// <summary>Where a faulted message goes back to.</summary>
public sealed record RepublishTarget(string Exchange, string ResolvedFrom);

public static class RepublishTargetResolver
{
    private static readonly string[] Suffixes = ["_error", "_skipped"];

    /// <summary>
    /// MassTransit binds every receive endpoint queue to a fanout exchange of the
    /// same name, so the original queue is reached by publishing to that exchange.
    /// The queue is taken from MT-Fault-InputAddress, falling back to the error
    /// queue name without its _error/_skipped suffix.
    /// </summary>
    public static RepublishTarget? Resolve(string errorQueue, IDictionary<string, object?>? headers)
    {
        var fromHeader = BrokerAddress.GetEntityName(FaultHeaders.GetString(headers, HeaderNames.FaultInputAddress));
        if (!string.IsNullOrEmpty(fromHeader))
            return new RepublishTarget(fromHeader, HeaderNames.FaultInputAddress);

        foreach (var suffix in Suffixes)
        {
            if (errorQueue.EndsWith(suffix, StringComparison.Ordinal) && errorQueue.Length > suffix.Length)
                return new RepublishTarget(errorQueue[..^suffix.Length], $"queue name without '{suffix}'");
        }

        return null;
    }
}
