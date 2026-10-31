// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Tools;

/// <summary>Where is a message? Always registered; searching without a queue list needs RabbitMcp:ManagementUri.</summary>
[McpServerToolType]
public sealed class SearchTools(
    QueueBrowser browser, RabbitConnection connection, IOptions<RabbitMcpOptions> options, IServiceProvider services)
{
    public const int MaxQueues = 50;

    [McpServerTool(Name = "find_message", Title = "Find a message",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Finds messages by messageId, correlationId or any text in the message (e.g. an order id or e-mail) across queues, " +
        "and shows where they are: queue, payload, fault and republish target. By default it searches every _error, " +
        "_skipped and _dead queue (needs ManagementUri); scope='all' searches every non-empty queue; or pass queues. " +
        "Each queue is read from the head like peek_messages (nothing is removed; peeked messages become 'redelivered'), " +
        "at most maxPerQueue messages deep. Queues with active consumers are skipped unless force=true. Streams are " +
        "reported in the result but not searched: read them with read_stream.")]
    public async Task<FindMessageResult> FindMessageAsync(
        [Description("MessageId to look for.")] string? messageId = null,
        [Description("CorrelationId to look for.")] string? correlationId = null,
        [Description("Text the message body contains, case-insensitive, e.g. an order id.")] string? contains = null,
        [Description("Queues to search. Empty: decided by scope.")] string[]? queues = null,
        [Description("errors (default: _error/_skipped/_dead queues) or all (every non-empty queue). Needs ManagementUri.")] string scope = "errors",
        [Description("How many messages from the head of each queue to read (default 20, at most MaxMessagesPerCall).")] int maxPerQueue = 20,
        [Description("Also search queues that have active consumers (their messages come back redelivered, possibly reordered).")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var matcher = new MessageMatcher(messageId, correlationId, contains);
        var depth = Math.Clamp(maxPerQueue, 1, options.Value.MaxMessagesPerCall);
        var (targets, note) = await QueuesToSearchAsync(queues, scope, cancellationToken);

        var searched = new List<QueueSearch>();
        var matches = new List<FoundMessage>();
        foreach (var queue in targets)
        {
            // One queue that cannot be read must not end the whole search.
            QueueDeclareOk? info;
            try
            {
                info = await connection.GetQueueAsync(queue, cancellationToken);
            }
            catch (OperationInterruptedException ex) when (RabbitConnection.IsResourceLocked(ex))
            {
                searched.Add(new QueueSearch(queue, "skipped: exclusive to another connection", 0, null));
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                searched.Add(new QueueSearch(queue, "error: " + BrokerErrors.ToMcp(ex).Message, 0, null));
                continue;
            }

            if (info is null)
            {
                searched.Add(new QueueSearch(queue, "missing", 0, null));
                continue;
            }
            if (info.ConsumerCount > 0 && !force)
            {
                searched.Add(new QueueSearch(queue, "skipped: has consumers", 0, info.MessageCount));
                continue;
            }

            PeekResult peek;
            try
            {
                peek = await browser.PeekAsync(queue, depth, force: true, cancellationToken, select: matcher.Matches);
            }
            catch (StreamQueueException)
            {
                searched.Add(new QueueSearch(queue, "skipped: a stream; read it with read_stream", 0, null));
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // E.g. the queue was deleted after it was listed, or access to it is refused.
                searched.Add(new QueueSearch(queue, "error: " + BrokerErrors.ToMcp(ex).Message, 0, info.MessageCount));
                continue;
            }
            matches.AddRange(peek.Messages.Select(m => new FoundMessage(queue, m)));
            searched.Add(peek.Scanned == 0
                ? new QueueSearch(queue, "empty", 0, 0)
                : new QueueSearch(queue,
                    peek.RemainingAfterLast > 0 ? $"searched the first {peek.Scanned} of {peek.Scanned + peek.RemainingAfterLast}" : "searched",
                    peek.Scanned, peek.Scanned + peek.RemainingAfterLast));
        }

        return new FindMessageResult(matches, searched, note);
    }

    private async Task<(IReadOnlyList<string> Queues, string? Note)> QueuesToSearchAsync(
        string[]? queues, string scope, CancellationToken ct)
    {
        if (queues is { Length: > 0 })
        {
            if (queues.Length > MaxQueues)
                throw new McpException($"Pass at most {MaxQueues} queues.");
            return ([.. queues.Distinct(StringComparer.Ordinal)], null);
        }

        var all = scope.Trim().ToLowerInvariant() switch
        {
            "errors" => false,
            "all" => true,
            _ => throw new McpException("scope must be 'errors' or 'all'."),
        };
        var management = services.GetService(typeof(ManagementClient)) as ManagementClient
            ?? throw new McpException("Searching without a queue list needs RabbitMcp:ManagementUri (to list the queues). Pass queues instead.");

        var listing = await management.ListQueuesAsync(null, onlyErrorQueues: !all, onlyNonEmpty: true, MaxQueues, ct);
        var note = listing.Truncated
            ? $"Only the first {MaxQueues} of {listing.Matched} queues were searched; narrow it down with queues."
            : null;
        // Counts come from management statistics: a message that arrived seconds ago may not be listed yet.
        return ([.. listing.Queues.Select(q => q.Name)], note ?? "Queue list from management statistics (may lag a few seconds).");
    }
}

/// <summary>Matches peeked messages by id, correlation id or body text (case-insensitive).</summary>
public sealed class MessageMatcher
{
    private readonly string? _messageId;
    private readonly string? _correlationId;
    private readonly string? _contains;

    public MessageMatcher(string? messageId, string? correlationId, string? contains)
    {
        _messageId = Normalize(messageId);
        _correlationId = Normalize(correlationId);
        _contains = Normalize(contains);
        if (_messageId is null && _correlationId is null && _contains is null)
            throw new McpException("Pass messageId, correlationId or contains.");
    }

    public bool Matches(PeekedMessage m) => Matches(m, default);

    /// <param name="m">The message as shown.</param>
    /// <param name="body">Its whole body: a message over MaxPayloadChars is shown shortened, but searched in full.</param>
    public bool Matches(PeekedMessage m, ReadOnlyMemory<byte> body) =>
        (_messageId is null || string.Equals(m.MessageId, _messageId, StringComparison.OrdinalIgnoreCase)) &&
        (_correlationId is null || string.Equals(m.CorrelationId, _correlationId, StringComparison.OrdinalIgnoreCase)) &&
        (_contains is null || Text(m, body).Contains(_contains, StringComparison.OrdinalIgnoreCase));

    private static string Text(PeekedMessage m, ReadOnlyMemory<byte> body) =>
        m.Payload?.GetRawText() ?? (m.Truncated && !body.IsEmpty ? Encoding.UTF8.GetString(body.Span) : m.RawBody) ?? "";

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
