using System.Text;

namespace RabbitMcp.Server.Messaging;

/// <summary>Builds and parses MassTransit-style addresses (rabbitmq://host[:port]/[vhost/]entity).</summary>
public static class BrokerAddress
{
    public static string ForEntity(Uri amqpUri, string entity)
    {
        var sb = new StringBuilder("rabbitmq://").Append(amqpUri.Host);
        if (amqpUri.Port > 0 && amqpUri.Port is not 5672 and not 5671)
            sb.Append(':').Append(amqpUri.Port);
        sb.Append('/');

        var vhost = GetVirtualHost(amqpUri);
        if (vhost != "/")
            sb.Append(Uri.EscapeDataString(vhost)).Append('/');

        return sb.Append(entity).ToString();
    }

    /// <summary>amqp://host/ and amqp://host/%2F map to "/", amqp://host/prod maps to "prod".</summary>
    public static string GetVirtualHost(Uri amqpUri)
    {
        var path = amqpUri.AbsolutePath.TrimStart('/');
        return path.Length == 0 ? "/" : Uri.UnescapeDataString(path);
    }

    /// <summary>The last path segment of an address: the queue/exchange name. Null if unparseable.</summary>
    public static string? GetEntityName(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return null;

        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is null ? null : Uri.UnescapeDataString(last);
    }

    /// <summary>host:port without credentials, safe for error messages and logs.</summary>
    public static string Describe(Uri amqpUri) =>
        $"{amqpUri.Host}:{(amqpUri.Port > 0 ? amqpUri.Port : 5672)} (vhost '{GetVirtualHost(amqpUri)}')";
}
