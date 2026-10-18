// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

namespace RabbitMcp.Server;

/// <summary>Bound from the "RabbitMcp" configuration section (env: RabbitMcp__Uri, ...).</summary>
public sealed class RabbitMcpOptions
{
    public const string Section = "RabbitMcp";

    /// <summary>AMQP connection URI, e.g. amqp://user:pass@host:5672/vhost.</summary>
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";

    /// <summary>Connection name shown in the management UI and used as the envelope source address.</summary>
    public string ClientName { get; set; } = "rabbit-mcp";

    /// <summary>Registers the typed publish_* (events) and request_* (request/response) tools. Off by default.</summary>
    public bool EnablePublish { get; set; }

    /// <summary>Registers republish_from_error. Off by default.</summary>
    public bool EnableRepublish { get; set; }

    /// <summary>
    /// Where dead_letter_from_error parks messages that cannot be processed: a queue name, "{queue}" is replaced by the
    /// queue the error queue belongs to (order-placed_error → order-placed_dead). Created (durable) if missing.
    /// </summary>
    public string DeadLetterQueue { get; set; } = "{queue}_dead";

    /// <summary>Upper bound for the count argument of peek/republish tools.</summary>
    public int MaxMessagesPerCall { get; set; } = 50;

    /// <summary>Message bodies longer than this are shown truncated (display only, never when republishing).</summary>
    public int MaxPayloadChars { get; set; } = 16_000;

    /// <summary>
    /// DLLs with your [MessageContract] types, separated by ";" ("," is legal in paths). Relative paths are resolved
    /// against the server's folder. Empty: the built-in sample contracts are used.
    /// </summary>
    public string? ContractAssemblies { get; set; }

    /// <summary>Seconds an "exchange exists" check is cached. 0 disables the cache.</summary>
    public int ExchangeCacheSeconds { get; set; } = 30;

    /// <summary>
    /// RabbitMQ Management API base URL, e.g. http://localhost:15672. Enables list_queues.
    /// Credentials: from this URL if it has user info, otherwise the AMQP URI's.
    /// </summary>
    public string? ManagementUri { get; set; }

    /// <summary>
    /// Contracts that may be sent (publish_*/request_*), ";"-separated patterns with "*" matched against
    /// contract name, tool name, full type name or URN. Empty: all. Example: "Order*;Billing.Contracts.*".
    /// </summary>
    public string? PublishAllow { get; set; }

    /// <summary>Contracts that may never be sent; same pattern syntax; wins over PublishAllow.</summary>
    public string? PublishDeny { get; set; }

    /// <summary>JSON Lines file for audit events (publish/request/republish). Empty: audit goes to the log only.</summary>
    public string? AuditLogPath { get; set; }

    /// <summary>Include message bodies in audit events. Off by default: they may contain personal data.</summary>
    public bool AuditIncludePayload { get; set; }

    /// <summary>
    /// Responder mode: request contracts this server answers itself ("*" patterns like PublishAllow, ";"-separated).
    /// Incoming requests wait in pending_requests until the model replies (reply_*) or rejects them. Empty: off.
    /// </summary>
    public string? RespondTo { get; set; }

    /// <summary>Responder: maximum requests waiting for an answer; further requests are rejected with a Fault.</summary>
    public int MaxPendingRequests { get; set; } = 100;

    /// <summary>
    /// Who confirms changes: "auto" (default) — the server asks the person through the MCP client (elicitation) when
    /// the client can, otherwise the tool's confirm argument counts; "required" — refuse the change when the person
    /// cannot be asked; "off" — only the confirm argument.
    /// </summary>
    public string HumanConfirmation { get; set; } = "auto";

    /// <summary>
    /// Tools whose changes the person confirms (";"-separated "*" patterns). Tools with a preview (confirm argument)
    /// are asked only when executing. Add e.g. "publish_*;request_*;reply_*;reject_request" to confirm every send.
    /// </summary>
    public string ConfirmTools { get; set; } = "republish_from_error;dead_letter_from_error;resend_corrected";

    /// <summary>"stdio" (default: one client, started by it) or "http" (shared server, Streamable HTTP + authorization).</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>HTTP: listen URLs, ";"-separated. Default loopback only; use http://0.0.0.0:5080 in containers.</summary>
    public string HttpUrls { get; set; } = "http://localhost:5080";

    /// <summary>HTTP: path of the MCP endpoint.</summary>
    public string HttpPath { get; set; } = "/mcp";

    /// <summary>HTTP: API keys, ";"-separated, optionally named: "alice:KEY;ci:KEY". Keys must be at least 32 characters.</summary>
    public string? ApiKeys { get; set; }

    /// <summary>HTTP: OpenID Connect authority for JWT bearer tokens (Entra ID, Keycloak, Auth0 ...).</summary>
    public string? JwtAuthority { get; set; }

    /// <summary>HTTP: required token audience when JwtAuthority is set.</summary>
    public string? JwtAudience { get; set; }

    /// <summary>HTTP: scopes advertised in the protected resource metadata, ";"-separated.</summary>
    public string? JwtScopes { get; set; }

    /// <summary>HTTP: public URL of the MCP endpoint (resource id in OAuth metadata). Default: first HttpUrls + HttpPath.</summary>
    public string? HttpPublicUrl { get; set; }

    /// <summary>HTTP: browser origins allowed to call the server (";"-separated). Default: none — requests with an Origin header are refused.</summary>
    public string? HttpAllowedOrigins { get; set; }

    /// <summary>
    /// HTTP: roles and their permissions (read, publish, request, republish, respond or *), ";"-separated; publish,
    /// request and respond may be limited to contracts, republish to error queues: "read;publish:Billing.*,Order*".
    /// Env: RabbitMcp__Roles__operator="read;publish;request". Empty: every authenticated caller may use every tool.
    /// </summary>
    public Dictionary<string, string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HTTP: who has which roles — "alice=operator;bob@example.com=viewer,billing" (API key name or token user).</summary>
    public string? UserRoles { get; set; }

    /// <summary>HTTP: roles of authenticated callers without any assigned role (","-separated). Empty: no permissions.</summary>
    public string? DefaultRoles { get; set; }

    /// <summary>HTTP: JWT claim whose values are role names (Entra ID app roles, Keycloak with a roles mapper).</summary>
    public string JwtRolesClaim { get; set; } = "roles";

    /// <summary>HTTP: run without any authentication. Only for local experiments.</summary>
    public bool HttpAllowAnonymous { get; set; }

    internal Uri BrokerUri => new(Uri);
}
