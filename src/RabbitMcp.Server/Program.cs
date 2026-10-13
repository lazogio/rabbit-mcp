using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ModelContextProtocol.Protocol;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Find appsettings.json next to the binary, whatever directory the MCP client starts us from.
    ContentRootPath = AppContext.BaseDirectory,
});

// stdout carries the MCP protocol: every log line must go to stderr.
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

var settings = builder.Configuration.GetSection(RabbitMcpOptions.Section).Get<RabbitMcpOptions>() ?? new();

// Every [MessageContract] type in the configured assemblies becomes a contract
// (RabbitMcp:ContractAssemblies); without configuration the built-in samples are used.
ContractRegistry contracts;
var policy = new PublishPolicy(settings.PublishAllow, settings.PublishDeny);
try
{
    // The audit file must be writable before anything is sent.
    new AuditLog(Options.Create(settings), TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLog>.Instance).EnsureWritable();

    var assemblies = ContractAssemblyLoader.Load(
        settings.ContractAssemblies, AppContext.BaseDirectory, typeof(OrderSubmitted).Assembly);
    contracts = ContractRegistry.FromAssemblies([.. assemblies]);

    if (contracts.Contracts.Count == 0)
        throw new InvalidOperationException(
            $"No [MessageContract] types found in: {string.Join(", ", assemblies.Select(a => a.GetName().Name))}.");
}
catch (Exception ex) when (ex is InvalidOperationException or ReflectionTypeLoadException or FileNotFoundException or FileLoadException)
{
    // Startup configuration error: one readable line on stderr (stdout belongs to MCP), non-zero exit.
    Console.Error.WriteLine($"rabbit-mcp: {ContractAssemblyLoader.Describe(ex)}");
    return 1;
}

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

// OpenTelemetry: on when the standard OTEL_EXPORTER_OTLP_ENDPOINT is set (OTLP only — never a console
// exporter: stdout belongs to MCP). OTEL_SERVICE_NAME, OTEL_EXPORTER_OTLP_PROTOCOL/HEADERS etc. apply as usual.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "rabbit-mcp",
            serviceVersion: ServerVersion.Current))
        .WithTracing(t => t.AddSource(Telemetry.Name, Telemetry.McpSdkName))
        .WithMetrics(m => m.AddMeter(Telemetry.Name, Telemetry.McpSdkName))
        .UseOtlpExporter();
}

var mcp = builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "rabbit-mcp", Version = ServerVersion.Current })
    .WithStdioServerTransport()
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

await builder.Build().RunAsync();
return 0;
