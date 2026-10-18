// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

public class HttpHostingTests
{
    private const string AliceKey = "alice-key-0123456789abcdefghijklmnopqrstuv";

    private static readonly string Initialize = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id = 1, method = "initialize",
        @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "http-test", version = "1.0" } },
    });

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> config)
    {
        var app = HttpHosting.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
        });
        await app.StartAsync();
        return app;
    }

    private static Dictionary<string, string?> Config(params (string Key, string? Value)[] values)
    {
        var config = new Dictionary<string, string?> { ["RabbitMcp:Transport"] = "http" };
        foreach (var (key, value) in values)
            config[$"RabbitMcp:{key}"] = value;
        return config;
    }

    private static HttpRequestMessage Mcp(string body, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public void Http_without_authentication_refuses_to_start()
    {
        var ex = Assert.Throws<StartupException>(() => HttpHosting.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(Config());
        }));
        Assert.Contains("ApiKeys", ex.Message);
    }

    [Fact]
    public void Short_api_keys_are_rejected_at_startup()
    {
        var ex = Assert.Throws<StartupException>(() => ApiKeyAuthenticationHandler.Parse("bob:too-short"));
        Assert.Contains("'bob'", ex.Message);
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Api_keys_parse_names_and_base64_padding()
    {
        var keys = ApiKeyAuthenticationHandler.Parse($"alice:{AliceKey}; dGhpcy1pcy1hLWJhc2U2NC1rZXktd2l0aC1wYWRkaW5nLg==");

        Assert.Equal(new[] { "alice", "api-key-2" }, keys.Select(k => k.Name));
        Assert.Equal("alice", ApiKeyAuthenticationHandler.Find(keys, AliceKey)?.Name);
        Assert.Equal("api-key-2", ApiKeyAuthenticationHandler.Find(keys, "dGhpcy1pcy1hLWJhc2U2NC1rZXktd2l0aC1wYWRkaW5nLg==")?.Name);
        Assert.Null(ApiKeyAuthenticationHandler.Find(keys, AliceKey + "x"));
    }

    [Fact]
    public async Task Api_key_is_required_and_accepted_in_both_headers()
    {
        await using var app = await StartAsync(Config(("ApiKeys", $"alice:{AliceKey}")));
        var client = app.GetTestClient();

        var anonymous = await client.SendAsync(Mcp(Initialize));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("ApiKey", anonymous.Headers.WwwAuthenticate.ToString());

        var wrong = await client.SendAsync(Mcp(Initialize, r => r.Headers.Add("X-Api-Key", AliceKey + "x")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var viaHeader = await client.SendAsync(Mcp(Initialize, r => r.Headers.Add("X-Api-Key", AliceKey)));
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
        Assert.Contains("rabbit-mcp", await viaHeader.Content.ReadAsStringAsync()); // serverInfo.name

        var viaBearer = await client.SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", AliceKey)));
        Assert.Equal(HttpStatusCode.OK, viaBearer.StatusCode);
    }

    [Fact]
    public async Task Foreign_origins_are_refused_and_health_is_anonymous()
    {
        await using var app = await StartAsync(Config(("ApiKeys", AliceKey), ("HttpAllowedOrigins", "https://tools.example.ge")));
        var client = app.GetTestClient();

        var evil = await client.SendAsync(Mcp(Initialize, r =>
        {
            r.Headers.Add("X-Api-Key", AliceKey);
            r.Headers.Add("Origin", "https://evil.example");
        }));
        Assert.Equal(HttpStatusCode.Forbidden, evil.StatusCode);

        var allowed = await client.SendAsync(Mcp(Initialize, r =>
        {
            r.Headers.Add("X-Api-Key", AliceKey);
            r.Headers.Add("Origin", "https://tools.example.ge");
        }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        Assert.Equal("ok", await client.GetStringAsync("/healthz"));
    }

    [Fact]
    public async Task Jwt_mode_points_clients_to_the_authorization_server()
    {
        await using var app = await StartAsync(Config(
            ("JwtAuthority", "https://login.example.test/realms/shop"),
            ("JwtAudience", "rabbit-mcp"),
            ("JwtScopes", "rabbit-mcp.publish"),
            ("HttpPublicUrl", "https://mcp.example.test/mcp")));
        var client = app.GetTestClient();

        var response = await client.SendAsync(Mcp(Initialize));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        var metadataUrl = challenge.Split("resource_metadata=\"")[1].Split('"')[0];

        using var metadata = JsonDocument.Parse(await client.GetStringAsync(new Uri(metadataUrl).PathAndQuery));
        Assert.Equal("https://login.example.test/realms/shop",
            metadata.RootElement.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("https://mcp.example.test/mcp", metadata.RootElement.GetProperty("resource").GetString());
    }

    [Fact]
    public void Jwt_without_audience_refuses_to_start()
    {
        var ex = Assert.Throws<StartupException>(() => HttpHosting.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(Config(("JwtAuthority", "https://login.example.test")));
        }));
        Assert.Contains("JwtAudience", ex.Message);
    }

    [Fact]
    public void Audit_client_is_the_authenticated_user_or_nobody()
    {
        var alice = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "alice") }, "ApiKey"));
        var subjectOnly = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-42") }, "Bearer"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Equal("alice", AuditLog.ClientOf(null, alice));
        Assert.Equal("user-42", AuditLog.ClientOf(null, subjectOnly));
        Assert.Null(AuditLog.ClientOf(null, anonymous));
        Assert.Null(AuditLog.ClientOf(null, null));
    }

    private const string Authority = "https://login.example.test/realms/shop";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("test-signing-key-0123456789abcdef0123456789"));

    /// <summary>JWT mode with a static (offline) issuer configuration, so real signed tokens can be validated.</summary>
    private static async Task<WebApplication> StartJwtAsync(string? scopes)
    {
        var app = HttpHosting.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(Config(
                ("JwtAuthority", Authority), ("JwtAudience", "rabbit-mcp"), ("JwtScopes", scopes), ("ApiKeys", "alice:" + AliceKey)));
            b.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Configuration = new OpenIdConnectConfiguration { Issuer = Authority };
                o.Configuration.SigningKeys.Add(SigningKey);
            });
        });
        await app.StartAsync();
        return app;
    }

    private static string Token(string? scope) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Authority,
        Audience = "rabbit-mcp",
        Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = scope is null
            ? new Dictionary<string, object> { ["preferred_username"] = "bob" }
            : new Dictionary<string, object> { ["preferred_username"] = "bob", ["scope"] = scope },
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
    });

    [Fact]
    public async Task Jwt_must_carry_the_configured_scopes()
    {
        await using var app = await StartJwtAsync("rabbit-mcp.tools");
        var client = app.GetTestClient();

        var withScope = await client.SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", Token("openid rabbit-mcp.tools"))));
        var withoutScope = await client.SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", Token("openid"))));
        var noScopeClaim = await client.SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", Token(null))));
        var apiKey = await client.SendAsync(Mcp(Initialize, r => r.Headers.Add("X-Api-Key", AliceKey)));

        Assert.Equal(HttpStatusCode.OK, withScope.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noScopeClaim.StatusCode);
        Assert.Equal(HttpStatusCode.OK, apiKey.StatusCode); // API keys are not scoped
    }

    [Fact]
    public async Task Jwt_without_configured_scopes_needs_only_a_valid_token()
    {
        await using var app = await StartJwtAsync(null);
        var response = await app.GetTestClient().SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", Token(null))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void An_auth_method_claim_in_a_token_does_not_skip_the_scope_check()
    {
        // Only the API key handler makes an API-key identity; a token may carry any claim it likes.
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("auth_method", "api_key"), new Claim("scope", "openid") }, "AuthenticationTypes.Federation"));
        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "ci") }, "ApiKey"));

        Assert.False(HttpHosting.HasRequiredScopes(forged, ["rabbit-mcp.tools"]));
        Assert.True(HttpHosting.HasRequiredScopes(apiKey, ["rabbit-mcp.tools"]));
    }

    [Fact]
    public async Task Jwt_claiming_to_be_an_api_key_is_still_refused_without_scopes()
    {
        await using var app = await StartJwtAsync("rabbit-mcp.tools");
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = "rabbit-mcp",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["preferred_username"] = "eve", ["scope"] = "openid", ["auth_method"] = "api_key" },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

        var response = await app.GetTestClient().SendAsync(Mcp(Initialize, r => r.Headers.Authorization = new("Bearer", token)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("scope", "a b", true)]
    [InlineData("scp", "b a", true)]
    [InlineData("scope", "a", false)]
    public void Scope_claims_are_read_from_scope_or_scp(string claim, string value, bool allowed)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(claim, value) }, "Bearer"));
        Assert.Equal(allowed, HttpHosting.HasRequiredScopes(user, ["a", "b"]));
    }

    [Theory]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://[::1]:5080", true)]
    [InlineData("http://0.0.0.0:5080", false)]
    [InlineData("http://+:5080", false)]
    [InlineData("http://*:5080", false)]
    [InlineData("http://mcp.example.test:5080", false)]
    public void Loopback_detection(string url, bool loopback) => Assert.Equal(loopback, HttpHosting.IsLoopback(url));

    [Fact]
    public void Anonymous_access_is_refused_beyond_loopback()
    {
        var ex = Assert.Throws<StartupException>(() => HttpHosting.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(Config(("HttpAllowAnonymous", "true"), ("HttpUrls", "http://0.0.0.0:5080")));
        }));
        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public void Audit_log_lines_cannot_be_forged_with_control_characters()
    {
        Assert.Equal("evil  AUDIT publish sent", AuditLog.OneLine("evil\r\nAUDIT publish sent"));
        Assert.Equal("plain", AuditLog.OneLine("plain"));
        Assert.Null(AuditLog.OneLine(null));
    }

    private const string BobKey = "bob-key-0123456789abcdefghijklmnopqrstuvwx";

    /// <summary>Streamable HTTP answers with JSON or with an SSE stream ("data: {...}").</summary>
    private static async Task<JsonElement> Rpc(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        var data = raw.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).LastOrDefault();
        return JsonDocument.Parse(data ?? raw).RootElement.Clone();
    }

    private static string Call(string tool, object arguments) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = tool, arguments },
    });

    /// <summary>The text of a tools/call result (JSON-unescaped).</summary>
    private static string Text(JsonElement rpc) =>
        string.Join("\n", rpc.GetProperty("result").GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

    private const string ListTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    [Fact]
    public async Task Roles_decide_which_tools_a_caller_sees_and_may_call()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), $"rabbit-mcp-roles-{Guid.NewGuid():N}.jsonl");
        await using var app = await StartAsync(Config(
            ("ApiKeys", $"alice:{AliceKey};bob:{BobKey}"),
            ("EnablePublish", "true"),
            ("EnableRepublish", "true"),
            ("AuditLogPath", auditPath),
            ("Roles:viewer", "read"),
            ("Roles:operator", "read;publish:Order*;republish:*_error"),
            ("UserRoles", "alice=operator;bob=viewer")));
        var client = app.GetTestClient();

        async Task<HashSet<string>> ToolsOf(string key)
        {
            var list = await Rpc(await client.SendAsync(Mcp(ListTools, r => r.Headers.Add("X-Api-Key", key))));
            return [.. list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
        }

        var bobTools = await ToolsOf(BobKey);
        Assert.Contains("peek_messages", bobTools);
        Assert.DoesNotContain("publish_order_submitted", bobTools);
        Assert.DoesNotContain("republish_from_error", bobTools);

        var aliceTools = await ToolsOf(AliceKey);
        Assert.Contains("publish_order_submitted", aliceTools);
        Assert.Contains("republish_from_error", aliceTools);
        Assert.DoesNotContain("request_get_order_status", aliceTools); // operator has no "request"

        // Calling a tool that was not listed is refused before anything touches the broker, and audited.
        var bobPublish = await Rpc(await client.SendAsync(Mcp(
            Call("publish_order_submitted", new { message = new { orderId = Guid.NewGuid() } }), r => r.Headers.Add("X-Api-Key", BobKey))));
        Assert.True(bobPublish.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Permission 'publish'", Text(bobPublish));

        var aliceRepublish = await Rpc(await client.SendAsync(Mcp(
            Call("republish_from_error", new { errorQueue = "payments" }), r => r.Headers.Add("X-Api-Key", AliceKey))));
        Assert.Contains("on queue 'payments'", Text(aliceRepublish));

        var denials = File.ReadAllLines(auditPath)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("action").GetString() == AuditActions.Access)
            .ToArray();
        Assert.Contains(denials, e => e.GetProperty("client").GetString()!.StartsWith("bob") && e.GetProperty("contract").GetString() == "OrderSubmitted");
        Assert.Contains(denials, e => e.GetProperty("client").GetString()!.StartsWith("alice"));
    }

    private const string CarolKey = "carol-key-0123456789abcdefghijklmnopqrstu";

    [Fact]
    public async Task Prompts_and_contract_resources_are_offered_and_resources_need_read()
    {
        await using var app = await StartAsync(Config(
            ("ApiKeys", $"alice:{AliceKey};carol:{CarolKey}"),
            ("Roles:viewer", "read"),
            ("UserRoles", "alice=viewer"))); // carol has no role and no default → nothing
        var client = app.GetTestClient();
        Task<JsonElement> As(string key, string body) =>
            client.SendAsync(Mcp(body, r => r.Headers.Add("X-Api-Key", key))).ContinueWith(t => Rpc(t.Result)).Unwrap();

        var resources = await As(AliceKey, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");
        var uris = resources.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToArray();
        Assert.Contains("contract://OrderSubmitted", uris);
        Assert.Contains("contract://GetOrderStatus", uris);

        var read = await As(AliceKey, """{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"contract://GetOrderStatus"}}""");
        var text = read.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        using (var contract = JsonDocument.Parse(text))
        {
            Assert.Equal("request", contract.RootElement.GetProperty("kind").GetString());
            Assert.True(contract.RootElement.GetProperty("schema").GetProperty("properties").TryGetProperty("orderId", out _));
            Assert.Equal("reply_order_status", contract.RootElement.GetProperty("responses")[0].GetProperty("replyTool").GetString());
        }

        var carolList = await As(CarolKey, """{"jsonrpc":"2.0","id":3,"method":"resources/list"}""");
        Assert.Empty(carolList.GetProperty("result").GetProperty("resources").EnumerateArray());
        var carolRead = await As(CarolKey, """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"contract://OrderSubmitted"}}""");
        Assert.True(carolRead.TryGetProperty("error", out _));

        var prompts = await As(AliceKey, """{"jsonrpc":"2.0","id":5,"method":"prompts/list"}""");
        var names = prompts.GetProperty("result").GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "contract_health", "diagnose_errors", "investigate_message", "review_approvals" }, names.Order().ToArray());

        var prompt = await As(AliceKey, """{"jsonrpc":"2.0","id":6,"method":"prompts/get","params":{"name":"diagnose_errors","arguments":{"queue":"order-placed_error"}}}""");
        var message = prompt.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains("order-placed_error", message);
        Assert.Contains("explicit confirmation", message);
    }
}
