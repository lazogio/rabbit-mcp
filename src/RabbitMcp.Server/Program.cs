using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMcp.Server;
using RabbitMcp.Server.Hosting;

// Transport is decided before building a host: stdio (default) or http.
var transport = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build()[$"{RabbitMcpOptions.Section}:Transport"] ?? "stdio";

try
{
    switch (transport.Trim().ToLowerInvariant())
    {
        case "stdio":
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                // Find appsettings.json next to the binary, whatever directory the MCP client starts us from.
                ContentRootPath = AppContext.BaseDirectory,
            });
            ServerComposition.Compose(builder).WithStdioServerTransport();
            await builder.Build().RunAsync();
            return 0;
        }
        case "http":
            await HttpHosting.Build(args).RunAsync();
            return 0;
        default:
            throw new StartupException($"RabbitMcp:Transport must be 'stdio' or 'http', not '{transport}'.");
    }
}
catch (StartupException ex)
{
    // Startup configuration error: one readable line on stderr (stdout belongs to MCP), non-zero exit.
    Console.Error.WriteLine($"rabbit-mcp: {ex.Message}");
    return 1;
}
