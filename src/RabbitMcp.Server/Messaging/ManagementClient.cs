using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Minimal client for the RabbitMQ Management HTTP API — only what AMQP cannot do (listing queues).
/// Uses server-side pagination (max 500 per page) and the case-insensitive name filter.
/// </summary>
public sealed class ManagementClient : IDisposable
{
    public const int PageSize = 500;
    public const int MaxPages = 20; // 10,000 queues per call is plenty for an assistant
    private const string Columns = "name,type,state,messages_ready,messages_unacknowledged,consumers";
    private const string ErrorQueueRegex = "_(error|skipped)$";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _vhost;
    private readonly string _describe;

    public ManagementClient(IOptions<RabbitMcpOptions> options) : this(options.Value, new SocketsHttpHandler()) { }

    public ManagementClient(RabbitMcpOptions options, HttpMessageHandler handler)
    {
        var management = new Uri(options.ManagementUri
            ?? throw new InvalidOperationException("RabbitMcp:ManagementUri is not configured."));
        var amqp = options.BrokerUri;

        // Credentials: the management URI's own user info, else the AMQP URI's, else RabbitMQ's default.
        var (user, pass) = Credentials(management) ?? Credentials(amqp) ?? ("guest", "guest");

        var baseUri = new UriBuilder(management) { UserName = "", Password = "", Query = "", Fragment = "" };
        if (!baseUri.Path.EndsWith('/'))
            baseUri.Path += "/";

        _http = new HttpClient(handler) { BaseAddress = baseUri.Uri, Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _vhost = BrokerAddress.GetVirtualHost(amqp);
        _describe = $"{baseUri.Host}:{baseUri.Port}";
    }

    /// <summary>Lists queues of the AMQP URI's vhost, filtered and sorted by name.</summary>
    public async Task<QueueListing> ListQueuesAsync(
        string? nameContains, bool onlyErrorQueues, bool onlyNonEmpty, int limit, CancellationToken ct)
    {
        // Server-side filter: the name substring if given, else the error-queue regex if asked for.
        var (nameFilter, regex) = !string.IsNullOrWhiteSpace(nameContains)
            ? (nameContains.Trim(), false)
            : onlyErrorQueues ? (ErrorQueueRegex, true) : ((string?)null, false);

        // When the server filters everything (no onlyNonEmpty, error filter done by regex), one page of `limit`
        // items and the server's filtered_count answer the call: no need to scan every queue in the vhost.
        if (!onlyNonEmpty && (!onlyErrorQueues || regex))
        {
            var first = await GetPageAsync(1, Math.Min(limit, PageSize), nameFilter, regex, ct);
            var queues = first.Items.Select(ToInfo).Where(q => !onlyErrorQueues || q.IsErrorQueue).ToArray();
            return new QueueListing(_vhost, queues, Matched: first.FilteredCount, TotalInVhost: first.TotalCount,
                Truncated: first.FilteredCount > queues.Length, Note: StatsNote);
        }

        var matched = new List<QueueInfo>();
        int totalInVhost = 0, page = 1, pageCount;
        do
        {
            var response = await GetPageAsync(page, PageSize, nameFilter, regex, ct);
            totalInVhost = response.TotalCount;
            pageCount = response.PageCount;

            foreach (var q in response.Items)
            {
                var info = ToInfo(q);
                if (onlyErrorQueues && !info.IsErrorQueue)
                    continue;
                if (onlyNonEmpty && (info.MessagesReady ?? 0) + (info.MessagesUnacknowledged ?? 0) == 0)
                    continue;
                matched.Add(info);
            }
            page++;
        }
        while (page <= pageCount && page <= MaxPages);

        var scannedAll = pageCount <= MaxPages;
        return new QueueListing(
            Vhost: _vhost,
            Queues: [.. matched.Take(limit)],
            Matched: matched.Count,
            TotalInVhost: totalInVhost,
            Truncated: matched.Count > limit || !scannedAll,
            Note: scannedAll
                ? StatsNote
                : $"Only the first {MaxPages * PageSize} queues were scanned; narrow the search with nameContains.");
    }

    private const string StatsNote = "Message and consumer counts come from management statistics and can lag a few seconds.";

    public static QueueInfo ToInfo(ManagementQueue q)
    {
        var source = RepublishTargetResolver.Resolve(q.Name, headers: null)?.Exchange;
        return new QueueInfo(q.Name, q.Type, q.State, q.MessagesReady, q.MessagesUnacknowledged, q.Consumers,
            IsErrorQueue: source is not null, SourceQueue: source);
    }

    private async Task<ManagementPage> GetPageAsync(int page, int pageSize, string? name, bool regex, CancellationToken ct)
    {
        var query = new StringBuilder($"api/queues/{Uri.EscapeDataString(_vhost)}?page={page}&page_size={pageSize}")
            .Append("&sort=name&columns=").Append(Columns);
        if (name is not null)
            query.Append("&name=").Append(Uri.EscapeDataString(name)).Append("&use_regex=").Append(regex ? "true" : "false");

        return await GetAsync<ManagementPage>(query.ToString(), notFound: NotFoundIs.VhostError, ct)
            ?? throw new McpException("The Management API returned an empty response.");
    }

    /// <summary>Bindings whose source is this exchange; null if the exchange does not exist.</summary>
    public Task<IReadOnlyList<ManagementBinding>?> GetSourceBindingsAsync(string exchange, CancellationToken ct) =>
        GetAsync<IReadOnlyList<ManagementBinding>>(
            $"api/exchanges/{Uri.EscapeDataString(_vhost)}/{Uri.EscapeDataString(exchange)}/bindings/source",
            NotFoundIs.Null, ct);

    /// <summary>One queue's statistics; null if it does not exist.</summary>
    public Task<ManagementQueue?> GetQueueAsync(string queue, CancellationToken ct) =>
        GetAsync<ManagementQueue>(
            $"api/queues/{Uri.EscapeDataString(_vhost)}/{Uri.EscapeDataString(queue)}?columns={Columns}",
            NotFoundIs.Null, ct);

    private enum NotFoundIs { Null, VhostError }

    private async Task<T?> GetAsync<T>(string path, NotFoundIs notFound, CancellationToken ct) where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new McpException($"Cannot reach the RabbitMQ Management API at {_describe}. Is the management plugin enabled and the URL right?");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    throw new McpException(
                        $"The Management API at {_describe} rejected the credentials (HTTP {(int)response.StatusCode}). " +
                        "The user needs the 'monitoring' (or 'management') tag.");
                case HttpStatusCode.NotFound when notFound == NotFoundIs.Null:
                    return null;
                case HttpStatusCode.NotFound:
                    throw new McpException($"Virtual host '{_vhost}' was not found by the Management API at {_describe}.");
            }
            if (!response.IsSuccessStatusCode)
                throw new McpException($"The Management API at {_describe} answered HTTP {(int)response.StatusCode}.");

            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
    }

    private static (string User, string Pass)? Credentials(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var parts = uri.UserInfo.Split(':', 2);
        return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One page of GET /api/queues/{vhost}?page=...</summary>
public sealed record ManagementPage(
    [property: JsonPropertyName("items")] IReadOnlyList<ManagementQueue> Items,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("filtered_count")] int FilteredCount,
    [property: JsonPropertyName("page_count")] int PageCount);

public sealed record ManagementQueue(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("messages_ready")] long? MessagesReady,
    [property: JsonPropertyName("messages_unacknowledged")] long? MessagesUnacknowledged,
    [property: JsonPropertyName("consumers")] long? Consumers);

/// <summary>One item of GET /api/exchanges/{vhost}/{name}/bindings/source.</summary>
public sealed record ManagementBinding(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("destination_type")] string DestinationType,
    [property: JsonPropertyName("routing_key")] string? RoutingKey);
