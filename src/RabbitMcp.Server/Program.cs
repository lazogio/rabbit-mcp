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

// Every [MessageContract] type in these assemblies becomes a contract. Add your own assemblies here.
var contracts = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);

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
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "rabbit-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<BrokerTools>(ToolJson.Options);

// publish_* for events, request_* for request/response contracts.
if (settings.EnablePublish)
    mcp.WithTools(ContractToolFactory.CreateTools(contracts));

if (settings.EnableRepublish)
    mcp.WithTools<RepublishTools>(ToolJson.Options);

await builder.Build().RunAsync();
