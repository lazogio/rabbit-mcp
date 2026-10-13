using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    .ValidateOnStart();

var settings = builder.Configuration.GetSection(RabbitMcpOptions.Section).Get<RabbitMcpOptions>() ?? new();

// Every [MessageContract] type in the configured assemblies becomes a contract
// (RabbitMcp:ContractAssemblies); without configuration the built-in samples are used.
ContractRegistry contracts;
try
{
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
    Console.Error.WriteLine($"rabbit-mcp: {ex.Message}");
    return 1;
}

builder.Services
    .AddSingleton(contracts)
    .AddSingleton(TimeProvider.System)
    .AddSingleton<RabbitConnection>()
    .AddSingleton<EnvelopeFactory>()
    .AddSingleton<MessageInspector>()
    .AddSingleton<QueueBrowser>()
    .AddSingleton<ReplyListener>()
    .AddSingleton<ContractPublisher>();

var mcp = builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "rabbit-mcp", Version = ServerVersion.Current })
    .WithStdioServerTransport()
    .WithTools<BrokerTools>(ToolJson.Options);

// publish_* for events, request_* for request/response contracts.
if (settings.EnablePublish)
    mcp.WithTools(ContractToolFactory.CreateTools(contracts));

if (settings.EnableRepublish)
    mcp.WithTools<RepublishTools>(ToolJson.Options);

await builder.Build().RunAsync();
return 0;
