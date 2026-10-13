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

    /// <summary>Upper bound for the count argument of peek/republish tools.</summary>
    public int MaxMessagesPerCall { get; set; } = 50;

    /// <summary>Message bodies longer than this are shown truncated (display only, never when republishing).</summary>
    public int MaxPayloadChars { get; set; } = 16_000;

    /// <summary>
    /// DLLs with your [MessageContract] types, separated by ";" or ",". Relative paths are resolved
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

    internal Uri BrokerUri => new(Uri);
}
