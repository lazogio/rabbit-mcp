// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RabbitMcp.Server.Hosting;

namespace RabbitMcp.Server.Security;

/// <summary>
/// RabbitMcp:RateLimits — at most N changing calls per caller and kind (publish, request, republish, respond, or * for
/// all of them) in a sliding window. Read-only calls and previews never count. Kept in memory per server instance.
/// </summary>
public sealed partial class RateLimiter
{
    public const string Any = "*";

    private readonly Dictionary<string, (int Limit, TimeSpan Window, string Text)> _limits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Caller, string Kind), Queue<DateTimeOffset>> _calls = [];
    private readonly TimeProvider _time;

    /// <exception cref="StartupException">Unknown kind or a value that is not like 100/h.</exception>
    public RateLimiter(RabbitMcpOptions options, TimeProvider time)
    {
        _time = time;
        foreach (var entry in (options.RateLimits ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':');
            var kind = colon < 0 ? "" : entry[..colon].Trim().ToLowerInvariant();
            var value = colon < 0 ? "" : entry[(colon + 1)..].Trim();
            if (kind is not ("publish" or "request" or "republish" or "respond" or Any))
                throw new StartupException(
                    $"RabbitMcp:RateLimits: '{entry}' — use publish, request, republish, respond or * followed by :N/unit, e.g. publish:100/h.");
            if (ValueRegex().Match(value) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var limit) || limit < 1)
                throw new StartupException($"RabbitMcp:RateLimits: '{entry}' — the limit must look like 100/h (units s, m, h, d).");
            var window = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(1),
                "m" => TimeSpan.FromMinutes(1),
                "h" => TimeSpan.FromHours(1),
                _ => TimeSpan.FromDays(1),
            };
            _limits[kind] = (limit, window, value);
        }
    }

    public bool Enabled => _limits.Count > 0;

    /// <summary>
    /// The kind of change a call makes (the permission it needs), or null for calls that change nothing:
    /// read tools, pending_requests, and previews (confirm missing or false).
    /// </summary>
    public static string? KindOf(string tool, ToolRule? rule, IDictionary<string, JsonElement>? arguments)
    {
        if (rule is null || rule.Permission == Permission.Read || tool == "pending_requests")
            return null;
        if (HumanConfirmation.PreviewTools.Contains(tool) &&
            !(arguments is not null && arguments.TryGetValue("confirm", out var c) && c.ValueKind == JsonValueKind.True))
            return null;
        return rule.Permission.ToString().ToLowerInvariant();
    }

    /// <summary>Counts the call and returns null, or — over a limit — why not (the call is not counted).</summary>
    public string? TryAcquire(string caller, string kind)
    {
        if (!Enabled)
            return null;
        var now = _time.GetUtcNow();
        var applicable = new[] { kind, Any }.Where(_limits.ContainsKey).ToArray();
        lock (_calls)
        {
            foreach (var key in applicable)
            {
                var (limit, window, text) = _limits[key];
                var calls = Calls(caller, key);
                while (calls.Count > 0 && now - calls.Peek() >= window)
                    calls.Dequeue();
                if (calls.Count >= limit)
                {
                    var wait = window - (now - calls.Peek());
                    return $"Rate limit reached: at most {text} {(key == Any ? "changes" : key + " calls")} for {caller} " +
                           $"(RabbitMcp:RateLimits). Try again in {Math.Ceiling(wait.TotalSeconds)} s. Nothing was changed.";
                }
            }
            foreach (var key in applicable)
                Calls(caller, key).Enqueue(now);
        }
        return null;
    }

    private Queue<DateTimeOffset> Calls(string caller, string kind)
    {
        if (!_calls.TryGetValue((caller, kind), out var calls))
            _calls[(caller, kind)] = calls = new Queue<DateTimeOffset>();
        return calls;
    }

    [GeneratedRegex("^([0-9]{1,9})\\s*/\\s*([smhdSMHD])$")]
    private static partial Regex ValueRegex();
}
