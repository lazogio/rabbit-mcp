using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

namespace RabbitMcp.Server.Hosting;

/// <summary>
/// Shared server over Streamable HTTP. Every request to the MCP endpoint must be authenticated with an API key
/// or a JWT from the configured OpenID Connect authority (unless HttpAllowAnonymous). Requests carrying an
/// Origin header are accepted only from HttpAllowedOrigins (DNS-rebinding protection required by the MCP spec).
/// </summary>
public static class HttpHosting
{
    public const string PolicyName = "mcp";

    /// <param name="customize">Applied before composition — tests use it for TestServer and in-memory config.</param>
    /// <exception cref="StartupException">Invalid configuration.</exception>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        customize?.Invoke(builder);

        var mcp = ServerComposition.Compose(builder);
        var settings = ServerComposition.ReadOptions(builder.Configuration);

        // Stateless: no session affinity needed, scales horizontally; our tools need no server-to-client requests.
        mcp.WithHttpTransport(o => o.Stateless = true); // explicit, so a future SDK default cannot change it

        builder.WebHost.UseUrls(settings.HttpUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var schemes = ConfigureAuthentication(builder, settings);

        var app = builder.Build();

        var allowedOrigins = (settings.HttpAllowedOrigins ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !allowedOrigins.Contains(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Origin not allowed.");
                return;
            }
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

        var endpoint = app.MapMcp(settings.HttpPath);
        if (schemes.Count == 0)
            endpoint.AllowAnonymous();
        else
            endpoint.RequireAuthorization(PolicyName);

        return app;
    }

    /// <summary>Registers the configured schemes; returns them (empty only when anonymous access was allowed explicitly).</summary>
    private static IReadOnlyList<string> ConfigureAuthentication(WebApplicationBuilder builder, RabbitMcpOptions settings)
    {
        var keys = ApiKeyAuthenticationHandler.Parse(settings.ApiKeys);
        var useJwt = !string.IsNullOrWhiteSpace(settings.JwtAuthority);

        if (keys.Count == 0 && !useJwt)
        {
            if (!settings.HttpAllowAnonymous)
                throw new StartupException(
                    "RabbitMcp:Transport=http needs authentication: set RabbitMcp:ApiKeys and/or RabbitMcp:JwtAuthority " +
                    "(+ JwtAudience). For a local experiment only: RabbitMcp:HttpAllowAnonymous=true.");

            Console.Error.WriteLine("rabbit-mcp: warning: HTTP transport WITHOUT authentication (HttpAllowAnonymous=true).");
            builder.Services.AddAuthorization();
            return [];
        }

        if (useJwt && string.IsNullOrWhiteSpace(settings.JwtAudience))
            throw new StartupException("RabbitMcp:JwtAudience is required when RabbitMcp:JwtAuthority is set.");

        var schemes = new List<string>();
        var auth = builder.Services.AddAuthentication();

        if (keys.Count > 0)
        {
            auth.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, o => o.Keys = keys);
            schemes.Add(ApiKeyAuthenticationHandler.SchemeName);
        }

        if (useJwt)
        {
            var authority = settings.JwtAuthority!.TrimEnd('/');
            auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = authority;
                o.Audience = settings.JwtAudience;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "preferred_username",
                };
            });

            // MCP authorization spec: 401 responses point clients to the protected resource metadata
            // (/.well-known/oauth-protected-resource), which names the authorization server.
            auth.AddMcp(o => o.ResourceMetadata = new()
            {
                Resource = settings.HttpPublicUrl ?? DefaultPublicUrl(settings),
                AuthorizationServers = { authority },
                ScopesSupported = (settings.JwtScopes ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            });
            schemes.Add(McpAuthenticationDefaults.AuthenticationScheme); // authenticates via "Bearer", challenges with metadata
        }

        builder.Services.AddAuthorization(o => o.AddPolicy(PolicyName, p => p
            .AddAuthenticationSchemes([.. schemes])
            .RequireAuthenticatedUser()));
        return schemes;
    }

    private static string DefaultPublicUrl(RabbitMcpOptions settings)
    {
        var first = settings.HttpUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "http://localhost:5080";
        return first.TrimEnd('/') + "/" + settings.HttpPath.TrimStart('/');
    }
}
