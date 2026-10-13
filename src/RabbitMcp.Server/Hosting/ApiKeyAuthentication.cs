// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RabbitMcp.Server.Hosting;

/// <summary>A configured API key: the caller's name and the SHA-256 of the key (the key itself is not kept).</summary>
public sealed record ApiKey(string Name, byte[] Hash);

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public IReadOnlyList<ApiKey> Keys { get; set; } = [];
}

/// <summary>
/// Authenticates <c>X-Api-Key: KEY</c> or <c>Authorization: Bearer KEY</c>. A bearer value that is not one
/// of our keys yields "no result", so a JWT in the same header can still be validated by the JWT scheme.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const int MinimumKeyLength = 32;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var fromHeader = Request.Headers[HeaderName].ToString();
        var fromBearer = Request.Headers.Authorization.ToString() is { } auth &&
                         auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : "";

        var candidate = fromHeader.Length > 0 ? fromHeader : fromBearer;
        if (candidate.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var match = Find(Options.Keys, candidate);
        if (match is null)
        {
            // An explicit X-Api-Key that is wrong is a failure; an unknown bearer token may be a JWT.
            return Task.FromResult(fromHeader.Length > 0
                ? AuthenticateResult.Fail("Invalid API key.")
                : AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, match.Name), new Claim("auth_method", "api_key") }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Append("WWW-Authenticate", $"ApiKey header=\"{HeaderName}\"");
        return Task.CompletedTask;
    }

    /// <summary>Constant-time comparison of hashes, so timing does not reveal how much of a key matched.</summary>
    public static ApiKey? Find(IReadOnlyList<ApiKey> keys, string candidate)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        ApiKey? found = null;
        foreach (var key in keys)
        {
            if (CryptographicOperations.FixedTimeEquals(key.Hash, hash))
                found = key;
        }
        return found;
    }

    /// <summary>
    /// "alice:KEY;ci:KEY2;KEY3" → named keys (unnamed ones become api-key-N). ':' separates the name because
    /// it never occurs in base64 keys, while '=' does (padding).
    /// </summary>
    /// <exception cref="StartupException">A key is shorter than <see cref="MinimumKeyLength"/> or a name repeats.</exception>
    public static IReadOnlyList<ApiKey> Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return [];

        var keys = new List<ApiKey>();
        var entries = configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var eq = entries[i].IndexOf(':');
            var name = eq > 0 ? entries[i][..eq].Trim() : $"api-key-{i + 1}";
            var key = eq > 0 ? entries[i][(eq + 1)..].Trim() : entries[i];

            if (key.Length < MinimumKeyLength)
                throw new StartupException(
                    $"RabbitMcp:ApiKeys: the key for '{name}' is shorter than {MinimumKeyLength} characters. " +
                    "Generate one with: openssl rand -base64 32");
            if (keys.Any(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new StartupException($"RabbitMcp:ApiKeys: the name '{name}' is used twice.");

            keys.Add(new ApiKey(name, SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        }
        return keys;
    }
}
