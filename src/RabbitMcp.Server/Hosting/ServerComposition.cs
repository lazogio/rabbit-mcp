// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;
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
            .Validate(o => o.MaxBulkMessages is >= 1 and <= 100_000, "RabbitMcp:MaxBulkMessages must be 1-100000.")
            .Validate(o => string.IsNullOrWhiteSpace(o.ManagementUri)
                           || (Uri.TryCreate(o.ManagementUri, UriKind.Absolute, out var m) && m.Scheme is "http" or "https"),
                "RabbitMcp:ManagementUri must be an http:// or https:// URL.")
            .ValidateOnStart();

        var settings = ReadOptions(builder.Configuration);
        var policy = new PublishPolicy(settings.PublishAllow, settings.PublishDeny);
        var contracts = LoadContracts(settings);

        foreach (var pattern in policy.UnmatchedPatterns(contracts.Contracts))
            Console.Error.WriteLine($"rabbit-mcp: warning: PublishAllow/PublishDeny pattern '{pattern}' matches no contract.");

        var access = new AccessControl(settings);
        var confirmation = new HumanConfirmation(settings);
        var limiter = new RateLimiter(settings, TimeProvider.System);
        if (access.Enabled && settings.Transport.Trim().Equals("stdio", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine("rabbit-mcp: note: RabbitMcp:Roles apply to the HTTP transport; over stdio the local " +
                                    "user has every enabled tool (limit it with EnablePublish/EnableRepublish/PublishAllow).");

        builder.Services
            .AddSingleton(access)
            .AddSingleton(confirmation)
            .AddSingleton(limiter)
            .AddSingleton<ToolAccess>()
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
            .WithRequestFilters(f =>
            {
                // Roles: callers only see the tools they may use ...
                f.AddListToolsFilter(next => async (request, cancellationToken) =>
                {
                    var result = await next(request, cancellationToken);
                    var tools = ServicesOf(request).GetRequiredService<ToolAccess>();
                    var user = tools.Control.For(request.User);
                    if (!user.IsUnrestricted)
                        result.Tools = [.. result.Tools.Where(t => tools.IsVisible(t.Name, user))];
                    return result;
                });

                // Contract resources are reference material like list_contracts: they need the read permission.
                f.AddListResourcesFilter(next => async (request, cancellationToken) =>
                {
                    var result = await next(request, cancellationToken);
                    var tools = ServicesOf(request).GetRequiredService<ToolAccess>();
                    if (!tools.Control.For(request.User).Has(Permission.Read))
                        result.Resources = [];
                    return result;
                });
                f.AddReadResourceFilter(next => async (request, cancellationToken) =>
                {
                    var tools = ServicesOf(request).GetRequiredService<ToolAccess>();
                    if (!tools.Control.For(request.User).Has(Permission.Read))
                        throw new McpException("Permission 'read' is required to read contract resources.");
                    return await next(request, cancellationToken);
                });

                f.AddCallToolFilter(next => async (request, cancellationToken) =>
                {
                    // ... and every call is checked again (a client may call a tool it was not shown). Denials are audited.
                    var services = ServicesOf(request);
                    var tools = services.GetRequiredService<ToolAccess>();
                    var name = request.Params?.Name ?? "";
                    if (tools.Check(name, request.Params?.Arguments, tools.Control.For(request.User)) is { } denied)
                    {
                        var audit = services.GetRequiredService<AuditLog>();
                        audit.Write(audit.Event(AuditActions.Access, "denied", AuditLog.ClientOf(request.Server, request.User),
                            denied.Contract?.Name, detail: $"{name}: {denied.Reason}"));
                        throw new McpException(denied.Reason);
                    }

                    // A runaway agent stops at RabbitMcp:RateLimits (counted before the person is asked).
                    var limits = services.GetRequiredService<RateLimiter>();
                    if (limits.Enabled && RateLimiter.KindOf(name, tools.RuleFor(name), request.Params?.Arguments) is { } kind
                        && limits.TryAcquire(AuditLog.ClientOf(null, request.User) ?? "local", kind) is { } limited)
                    {
                        var audit = services.GetRequiredService<AuditLog>();
                        audit.Write(audit.Event(AuditActions.Access, "limited", AuditLog.ClientOf(request.Server, request.User),
                            detail: $"{name}: {limited}"));
                        throw new McpException(limited);
                    }

                    // The person confirms changes through the client (elicitation); the model's confirm=true is the
                    // fallback only where the person cannot be asked. Every question and answer is audited.
                    var human = services.GetRequiredService<HumanConfirmation>();
                    if (human.Applies(name, request.Params?.Arguments))
                    {
                        var audit = services.GetRequiredService<AuditLog>();
                        var client = AuditLog.ClientOf(request.Server, request.User);
                        var decision = await human.DecideAsync(name, request.Params?.Arguments, client,
                            new ElicitationPrompt(request.Server), cancellationToken);
                        audit.Write(audit.Event(AuditActions.Confirm,
                            decision.Proceed ? $"allowed-{decision.How}" : $"refused-{decision.How}", client, detail: name));
                        if (!decision.Proceed)
                            throw new McpException(decision.Refusal!);
                    }

                    // RabbitMQ errors (403 ACCESS_REFUSED, lost connection ...) become readable tool errors instead of
                    // the SDK's reason-less "An error occurred invoking 'tool'".
                    try
                    {
                        return await next(request, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not McpException and not OperationCanceledException
                                               && BrokerErrors.TryDescribe(ex, out var message))
                    {
                        throw new McpException(message, ex);
                    }
                });
            })
            .WithTools<BrokerTools>(ToolJson.Options)
            .WithTools<SearchTools>(ToolJson.Options)
            .WithTools<StreamTools>(ToolJson.Options)
            .WithPrompts<OperatorPrompts>()
            .WithResources(ContractResources.Create(contracts));

        if (!string.IsNullOrWhiteSpace(settings.AuditLogPath))
            mcp.WithTools<AuditTools>(ToolJson.Options);

        // publish_* for events, request_* for request/response contracts.
        if (settings.EnablePublish)
            mcp.WithTools(ContractToolFactory.CreateTools(contracts, policy));

        if (settings.EnableRepublish)
            mcp.WithTools<RepublishTools>(ToolJson.Options);

        // A correction moves a message (republish) and sends new content (publish): it needs both.
        if (settings.EnableRepublish && settings.EnablePublish)
            mcp.WithTools<CorrectionTools>(ToolJson.Options);

        // Responder mode: answer requests of RespondTo contracts via pending_requests + reply_* / reject_request.
        if (!string.IsNullOrWhiteSpace(settings.RespondTo))
        {
            if (settings.MaxPendingRequests is < 1 or > 10_000)
                throw new StartupException("RabbitMcp:MaxPendingRequests must be 1-10000.");

            var responder = ResponderContracts.From(contracts, settings.RespondTo);
            builder.Services
                .AddSingleton(responder)
                .AddSingleton(sp => new RequestInbox(sp.GetRequiredService<TimeProvider>(), settings.MaxPendingRequests))
                .AddSingleton<ResponderService>()
                .AddHostedService(sp => sp.GetRequiredService<ResponderService>())
                .AddSingleton<ReplyTools>();

            mcp.WithTools<ResponderTools>(ToolJson.Options);
            mcp.WithTools(ReplyToolFactory.CreateTools(responder));
        }

        // list_queues needs the Management HTTP API (AMQP cannot enumerate queues).
        if (!string.IsNullOrWhiteSpace(settings.ManagementUri))
        {
            builder.Services.AddSingleton(sp => new ManagementClient(sp.GetRequiredService<IOptions<RabbitMcpOptions>>()));
            builder.Services.AddSingleton<TopologyInspector>();
            mcp.WithTools<ManagementTools>(ToolJson.Options);
        }

        return mcp;
    }

    private static IServiceProvider ServicesOf(MessageContext request) =>
        request.Services ?? request.Server.Services
        ?? throw new InvalidOperationException("The MCP server has no service provider.");

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
