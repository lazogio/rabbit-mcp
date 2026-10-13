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
}
