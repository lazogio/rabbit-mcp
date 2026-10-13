using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Server.Hosting;

/// <summary>A configuration problem found at startup: printed as one line on stderr, exit code 1.</summary>
public sealed class StartupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything both transports share: options, contracts, services, telemetry and tools.</summary>
public static class ServerComposition
{
    public static RabbitMcpOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(RabbitMcpOptions.Section).Get<RabbitMcpOptions>() ?? new();

    /// <summary>Registers everything except the MCP transport and returns the MCP builder.</summary>
    /// <exception cref="StartupException">Invalid configuration.</exception>
    public static IMcpServerBuilder Compose(IHostApplicationBuilder builder)
    {
        // Logs always go to stderr: stdout carries MCP in stdio mode, and HTTP mode keeps the same habit.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddOptions<RabbitMcpOptions>()
            .Bind(builder.Configuration.GetSection(RabbitMcpOptions.Section))
            .Validate(o => Uri.TryCreate(o.Uri, UriKind.Absolute, out var u) && u.Scheme is "amqp" or "amqps",
                "RabbitMcp:Uri must be an amqp:// or amqps:// URI.")
            .Validate(o => o.MaxMessagesPerCall is >= 1 and <= 1000, "RabbitMcp:MaxMessagesPerCall must be 1-1000.")
            .Validate(o => string.IsNullOrWhiteSpace(o.ManagementUri)
                           || (Uri.TryCreate(o.ManagementUri, UriKind.Absolute, out var m) && m.Scheme is "http" or "https"),
                "RabbitMcp:ManagementUri must be an http:// or https:// URL.")
            .ValidateOnStart();

        var settings = ReadOptions(builder.Configuration);
        var policy = new PublishPolicy(settings.PublishAllow, settings.PublishDeny);
        var contracts = LoadContracts(settings);

        foreach (var pattern in policy.UnmatchedPatterns(contracts.Contracts))
            Console.Error.WriteLine($"rabbit-mcp: warning: PublishAllow/PublishDeny pattern '{pattern}' matches no contract.");

        builder.Services
            .AddSingleton(contracts)
            .AddSingleton(policy)
            .AddSingleton<AuditLog>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<RabbitConnection>()
            .AddSingleton<EnvelopeFactory>()
            .AddSingleton<MessageInspector>()
            .AddSingleton<QueueBrowser>()
            .AddSingleton<ReplyListener>()
            .AddSingleton<ContractPublisher>();

        AddTelemetry(builder);

        var mcp = builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "rabbit-mcp", Version = ServerVersion.Current })
            .WithTools<BrokerTools>(ToolJson.Options);

        // publish_* for events, request_* for request/response contracts.
        if (settings.EnablePublish)
            mcp.WithTools(ContractToolFactory.CreateTools(contracts, policy));

        if (settings.EnableRepublish)
            mcp.WithTools<RepublishTools>(ToolJson.Options);

        // list_queues needs the Management HTTP API (AMQP cannot enumerate queues).
        if (!string.IsNullOrWhiteSpace(settings.ManagementUri))
        {
            builder.Services.AddSingleton(sp => new ManagementClient(sp.GetRequiredService<IOptions<RabbitMcpOptions>>()));
            mcp.WithTools<ManagementTools>(ToolJson.Options);
        }

        return mcp;
    }

    /// <summary>
    /// Every [MessageContract] type in the configured assemblies becomes a contract
    /// (RabbitMcp:ContractAssemblies); without configuration the built-in samples are used.
    /// </summary>
    private static ContractRegistry LoadContracts(RabbitMcpOptions settings)
    {
        try
        {
            // The audit file must be writable before anything is sent.
            new AuditLog(Options.Create(settings), TimeProvider.System, NullLogger<AuditLog>.Instance).EnsureWritable();

            var assemblies = ContractAssemblyLoader.Load(
                settings.ContractAssemblies, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
            var contracts = ContractRegistry.FromAssemblies([.. assemblies]);

            if (contracts.Contracts.Count == 0)
                throw new InvalidOperationException(
                    $"No [MessageContract] types found in: {string.Join(", ", assemblies.Select(a => a.GetName().Name))}.");
            return contracts;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ReflectionTypeLoadException or FileNotFoundException or FileLoadException)
        {
            throw new StartupException(ContractAssemblyLoader.Describe(ex), ex);
        }
    }

    /// <summary>
    /// OpenTelemetry: on when the standard OTEL_EXPORTER_OTLP_ENDPOINT is set (OTLP only — never a console
    /// exporter: stdout belongs to MCP). OTEL_SERVICE_NAME, OTEL_EXPORTER_OTLP_PROTOCOL/HEADERS etc. apply as usual.
    /// </summary>
    private static void AddTelemetry(IHostApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            return;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "rabbit-mcp",
                serviceVersion: ServerVersion.Current))
            .WithTracing(t => t.AddSource(Telemetry.Name, Telemetry.McpSdkName, "Microsoft.AspNetCore"))
            .WithMetrics(m => m.AddMeter(Telemetry.Name, Telemetry.McpSdkName, "Microsoft.AspNetCore.Hosting"))
            .UseOtlpExporter();
    }
}
