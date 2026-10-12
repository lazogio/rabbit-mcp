namespace RabbitMcp.Server;

/// <summary>Bound from the "RabbitMcp" configuration section (env: RabbitMcp__Uri, ...).</summary>
public sealed class RabbitMcpOptions
{
    public const string Section = "RabbitMcp";

    /// <summary>AMQP connection URI, e.g. amqp://user:pass@host:5672/vhost.</summary>
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";

    /// <summary>Connection name shown in the management UI and used as the envelope source address.</summary>
    public string ClientName { get; set; } = "rabbit-mcp";

    /// <summary>Registers the typed publish_* tools. Off by default.</summary>
    public bool EnablePublish { get; set; }

    /// <summary>Registers republish_from_error. Off by default.</summary>
    public bool EnableRepublish { get; set; }

    /// <summary>Upper bound for the count argument of peek/republish tools.</summary>
    public int MaxMessagesPerCall { get; set; } = 50;

    /// <summary>Message bodies longer than this are shown truncated (display only, never when republishing).</summary>
    public int MaxPayloadChars { get; set; } = 16_000;

    internal Uri BrokerUri => new(Uri);
}
