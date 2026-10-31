// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMcp.Server;
using RabbitMcp.Server.Cli;
using RabbitMcp.Server.Hosting;

// Command-line tools (not MCP): rabbit-mcp contracts diff OLD.dll NEW.dll
if (args is ["contracts", ..])
    return ContractsCommand.Run(args[1..], Console.Out, Console.Error);
if (args is ["--version" or "-v"])
{
    Console.Out.WriteLine(ServerVersion.Current);
    return 0;
}
if (args is ["--help" or "-h"])
{
    Console.Out.WriteLine(ContractsCommand.ServerUsage);
    return 0;
}

try
{
    // Transport is decided before building a host: stdio (default) or http.
    var transport = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build()[$"{RabbitMcpOptions.Section}:Transport"] ?? "stdio";

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
            await RunAsync(builder.Build());
            return 0;
        }
        case "http":
            await RunAsync(HttpHosting.Build(args));
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
catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
{
    // An appsettings.json that cannot be read or parsed: the inner exception says where.
    Console.Error.WriteLine($"rabbit-mcp: {ex.Message} {ex.InnerException?.Message}".TrimEnd());
    return 1;
}

// A host that cannot start (port in use, invalid HttpUrls ...) is reported like any other configuration problem.
static async Task RunAsync(IHost host)
{
    using (host)
    {
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StartupException(ex.Message, ex);
        }
        await host.WaitForShutdownAsync();
    }
}
