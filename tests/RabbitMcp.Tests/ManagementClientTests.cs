using System.Net;
using System.Text;
using ModelContextProtocol;
using RabbitMcp.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ManagementClientTests
{
    private static string Page(int pageCount, int total, params string[] items) =>
        $$"""{"total_count":{{total}},"filtered_count":{{items.Length}},"item_count":{{items.Length}},"page":1,"page_size":500,"page_count":{{pageCount}},"items":[{{string.Join(",", items)}}]}""";

    private static string QueueJson(string name, long ready = 0, long unacked = 0, long consumers = 0, string type = "classic") =>
        $$"""{"name":"{{name}}","type":"{{type}}","state":"running","messages_ready":{{ready}},"messages_unacknowledged":{{unacked}},"consumers":{{consumers}}}""";

    private static (ManagementClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string amqp = "amqp://app:s3cr%40t@mq.local:5672/prod",
        string management = "http://mq.local:15672")
    {
        var handler = new StubHandler(respond);
        var client = new ManagementClient(new RabbitMcpOptions { Uri = amqp, ManagementUri = management }, handler);
        return (client, handler);
    }

    [Fact]
    public async Task Request_uses_vhost_columns_paging_and_AMQP_credentials()
    {
        var (client, handler) = Create(_ => StubHandler.Json(Page(1, 1, QueueJson("orders"))));

        await client.ListQueuesAsync(nameContains: "Ord", onlyErrorQueues: false, onlyNonEmpty: false, limit: 10, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var url = request.RequestUri!.ToString();
        Assert.StartsWith("http://mq.local:15672/api/queues/prod?page=1&page_size=500", url);
        Assert.Contains("columns=name,type,state,messages_ready,messages_unacknowledged,consumers", url);
        Assert.Contains("name=Ord&use_regex=false", url);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("app:s3cr@t", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public async Task Default_vhost_is_escaped_and_management_credentials_win()
    {
        var (client, handler) = Create(_ => StubHandler.Json(Page(1, 0)),
            amqp: "amqp://app:pw@mq.local/", management: "https://admin:adm@mq.local:15671/rabbit");

        await client.ListQueuesAsync(null, false, false, 10, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://mq.local:15671/rabbit/api/queues/%2F?", request.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("adm@", request.RequestUri.AbsoluteUri);
        Assert.Equal("admin:adm", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)));
    }

    [Fact]
    public async Task Error_queues_are_flagged_and_filtered_server_side_by_regex()
    {
        var (client, handler) = Create(_ => StubHandler.Json(Page(1, 3,
            QueueJson("submit-order_error", ready: 2), QueueJson("submit-order_skipped"))));

        var result = await client.ListQueuesAsync(null, onlyErrorQueues: true, onlyNonEmpty: false, limit: 10, CancellationToken.None);

        Assert.Contains("use_regex=true", Assert.Single(handler.Requests).RequestUri!.Query);
        Assert.All(result.Queues, q => Assert.True(q.IsErrorQueue));
        Assert.Equal("submit-order", result.Queues[0].SourceQueue);
        Assert.Equal(3, result.TotalInVhost);
    }

    [Fact]
    public async Task Client_side_filters_and_limit_are_applied_across_pages()
    {
        var pages = new Queue<string>([
            Page(2, 4, QueueJson("a", ready: 1), QueueJson("b")),
            Page(2, 4, QueueJson("c", unacked: 3), QueueJson("d", ready: 5)),
        ]);
        var (client, handler) = Create(_ => StubHandler.Json(pages.Dequeue()));

        var result = await client.ListQueuesAsync(null, false, onlyNonEmpty: true, limit: 2, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { "a", "c" }, result.Queues.Select(q => q.Name));
        Assert.Equal(3, result.Matched);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the credentials")]
    [InlineData(HttpStatusCode.NotFound, "Virtual host 'prod' was not found")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Http_errors_become_readable_tool_errors(HttpStatusCode status, string expected)
    {
        var (client, _) = Create(_ => StubHandler.Json("{}", status));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.ListQueuesAsync(null, false, false, 10, CancellationToken.None));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("s3cr", ex.Message);
    }

    [Fact]
    public async Task Unreachable_api_is_a_readable_error_without_credentials()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.ListQueuesAsync(null, false, false, 10, CancellationToken.None));

        Assert.Contains("Cannot reach the RabbitMQ Management API at mq.local:15672", ex.Message);
        Assert.DoesNotContain("s3cr", ex.Message);
    }
}
