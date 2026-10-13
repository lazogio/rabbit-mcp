using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace RabbitMcp.Server.Messaging;

/// <summary>One thing the server did (or refused to do) to the broker on behalf of an MCP client.</summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string Action,
    string Outcome,
    string? Client,
    string? Contract = null,
    string? Exchange = null,
    string? Queue = null,
    string? MessageId = null,
    string? RequestId = null,
    string? CorrelationId = null,
    string? Detail = null,
    JsonElement? Message = null);

public static class AuditActions
{
    public const string Publish = "publish";
    public const string Request = "request";
    public const string Republish = "republish";
}

/// <summary>
/// Records every publish / request / republish attempt with its outcome: always to the log (stderr),
/// and, when <c>RabbitMcp:AuditLogPath</c> is set, as JSON Lines appended to that file.
/// Message bodies are included only with <c>RabbitMcp:AuditIncludePayload</c> (they may hold personal data).
/// Credentials are never part of an event.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _fileGate = new();
    private readonly ILogger<AuditLog> _logger;
    private readonly TimeProvider _time;

    public AuditLog(IOptions<RabbitMcpOptions> options, TimeProvider time, ILogger<AuditLog> logger)
    {
        _logger = logger;
        _time = time;
        IncludePayload = options.Value.AuditIncludePayload;
        FilePath = ResolvePath(options.Value.AuditLogPath);
    }

    public string? FilePath { get; }

    public bool IncludePayload { get; }

    public static string? ResolvePath(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()), AppContext.BaseDirectory);

    /// <summary>Fails fast at startup when the audit file cannot be written.</summary>
    public void EnsureWritable()
    {
        if (FilePath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var _ = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"RabbitMcp:AuditLogPath: cannot write '{FilePath}' ({ex.Message}).", ex);
        }
    }

    /// <summary>"Claude Code 2.1.0" from the MCP initialize handshake; null outside an MCP call.</summary>
    public static string? ClientOf(McpServer? server) =>
        server?.ClientInfo is { } info ? $"{info.Name} {info.Version}".Trim() : null;

    public AuditEvent Event(
        string action, string outcome, string? client,
        string? contract = null, string? exchange = null, string? queue = null,
        string? messageId = null, string? requestId = null, string? correlationId = null,
        string? detail = null, object? message = null, Type? messageType = null) =>
        new(_time.GetUtcNow(), action, outcome, client, contract, exchange, queue, messageId, requestId, correlationId, detail,
            IncludePayload && message is not null
                ? JsonSerializer.SerializeToElement(message, messageType ?? message.GetType(), MassTransitJson.Options)
                : null);

    public void Write(AuditEvent e)
    {
        _logger.LogInformation(
            "AUDIT {Action} {Outcome} contract={Contract} exchange={Exchange} queue={Queue} messageId={MessageId} " +
            "requestId={RequestId} client={Client} {Detail}",
            e.Action, e.Outcome, e.Contract, e.Exchange, e.Queue, e.MessageId, e.RequestId, e.Client, e.Detail);

        if (FilePath is null)
            return;

        var line = JsonSerializer.Serialize(e, Json) + Environment.NewLine;
        try
        {
            lock (_fileGate)
                File.AppendAllText(FilePath, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The broker operation already happened; losing the file line must not hide that from the caller.
            _logger.LogError(ex, "AUDIT could not append to {AuditFile}; event: {Event}", FilePath, line.TrimEnd());
        }
    }
}
