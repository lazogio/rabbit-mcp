// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

public sealed record AuditQuery(
    string? Action = null,
    string? Outcome = null,
    string? Contract = null,
    string? Client = null,
    string? Queue = null,
    string? Id = null,
    DateTimeOffset? Since = null,
    int Limit = 50,
    bool IncludePayload = false);

/// <summary>
/// Reads the JSON Lines audit file backwards (newest first), so a query for recent activity does not read a large
/// file from the start, and stops at <see cref="AuditQuery.Since"/>. Lines being written or damaged are skipped.
/// </summary>
public static partial class AuditReader
{
    public const int MaxLimit = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RecentActivity Read(string path, AuditQuery query, int chunkSize = 64 * 1024)
    {
        if (!File.Exists(path))
            return new RecentActivity([], Truncated: false, Scanned: 0);

        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var events = new List<AuditEvent>();
        var scanned = 0;
        var truncated = false;

        foreach (var line in LinesBackwards(path, chunkSize))
        {
            scanned++;
            AuditEvent? e;
            try
            {
                e = JsonSerializer.Deserialize<AuditEvent>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (e is null)
                continue;
            if (query.Since is { } since && e.Timestamp < since)
                break; // the file is chronological: everything before this is older too
            if (!Matches(e, query))
                continue;
            if (events.Count == limit)
            {
                truncated = true;
                break;
            }
            events.Add(query.IncludePayload ? e : e with { Message = null });
        }

        return new RecentActivity(events, truncated, scanned);
    }

    private static bool Matches(AuditEvent e, AuditQuery q) =>
        Is(e.Action, q.Action) && Is(e.Outcome, q.Outcome) && Is(e.Contract, q.Contract) &&
        Has(e.Client, q.Client) && Has(e.Queue, q.Queue) &&
        (q.Id is null || Is(e.MessageId, q.Id) || Is(e.RequestId, q.Id) || Is(e.CorrelationId, q.Id));

    private static bool Is(string? value, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(value, wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Has(string? value, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) || (value?.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>"30m", "2h", "7d" (before <paramref name="now"/>) or an ISO 8601 timestamp. Null for empty.</summary>
    public static DateTimeOffset? ParseSince(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();

        if (RelativeRegex().Match(text) is { Success: true } m)
        {
            var amount = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            try
            {
                return m.Groups[2].Value.ToLowerInvariant() switch
                {
                    "m" => now.AddMinutes(-amount),
                    "h" => now.AddHours(-amount),
                    _ => now.AddDays(-amount),
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.MinValue; // "999999d" reaches before year 1: everything
            }
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return at;

        throw new McpException($"since must be like 30m, 2h, 7d or an ISO 8601 time, not '{value}'.");
    }

    [GeneratedRegex("^([0-9]{1,6})\\s*([mhdMHD])$")]
    private static partial Regex RelativeRegex();

    /// <summary>Lines of a UTF-8 file from the last to the first, reading fixed-size chunks from the end.</summary>
    public static IEnumerable<string> LinesBackwards(string path, int chunkSize = 64 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var position = stream.Length;
        var buffer = new byte[chunkSize];
        var carry = new List<byte>(); // start of the current line, which continues into chunks read earlier

        while (position > 0)
        {
            var size = (int)Math.Min(chunkSize, position);
            position -= size;
            stream.Position = position;
            stream.ReadExactly(buffer, 0, size);

            var end = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                var line = Decode(buffer, i + 1, end - i - 1, carry);
                carry.Clear();
                end = i;
                if (line.Length > 0)
                    yield return line;
            }

            var head = new byte[end];
            Array.Copy(buffer, head, end);
            carry.InsertRange(0, head);
        }

        var first = Decode(buffer, 0, 0, carry);
        if (first.Length > 0)
            yield return first;
    }

    private static string Decode(byte[] buffer, int start, int length, List<byte> tail)
    {
        var bytes = new byte[length + tail.Count];
        Array.Copy(buffer, start, bytes, 0, length);
        tail.CopyTo(bytes, length);
        return Encoding.UTF8.GetString(bytes).Trim();
    }
}
