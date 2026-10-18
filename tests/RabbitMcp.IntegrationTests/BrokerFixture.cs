// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMcp.Samples.Orders;
using RabbitMcp.IntegrationTests.Contracts;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace RabbitMcp.IntegrationTests;

/// <summary>
/// What a MassTransit consumer saw. For envelope-serialized messages MassTransit's
/// <c>ConsumeContext.Headers</c> are the envelope's headers; AMQP headers are only in
/// <c>ReceiveContext.TransportHeaders</c>.
/// </summary>
public sealed record Received<T>(
    T Message,
    Guid? MessageId,
    IReadOnlyDictionary<string, object?> Headers,
    IReadOnlyDictionary<string, object?> TransportHeaders,
    System.Diagnostics.ActivityTraceId? TraceId);

/// <summary>
/// One RabbitMQ container + one MassTransit bus (the "services") + the rabbit-mcp components
/// under test, shared by all integration tests.
/// </summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    public const string OrderSubmittedQueue = "it-order-submitted";
    public const string OrderCancelledQueue = "it-order-cancelled";
    public const string OrderStatusQueue = "it-order-status";
    public const string PingQueue = "it-ping-nobody";

    public static readonly Guid KnownOrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid FailingOrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid BadReplyOrderId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management")
        .WithPortBinding(15672, true) // Management HTTP API, for list_queues
        .Build();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Received<OrderSubmitted>>> _submitted = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Received<OrderCancelled>>> _cancelled = new();
    private readonly ConcurrentDictionary<Guid, int> _cancelAttempts = new();
    private IBusControl? _bus;

    public RabbitConnection Connection { get; private set; } = null!;
    public ContractRegistry Registry { get; private set; } = null!;
    public QueueBrowser Browser { get; private set; } = null!;
    public ContractPublisher Publisher { get; private set; } = null!;
    public ManagementClient Management { get; private set; } = null!;
    public AuditLog Audit { get; private set; } = null!;

    /// <summary>Responder mode for <see cref="AskHuman"/>, consuming since InitializeAsync.</summary>
    public ResponderService Responder { get; private set; } = null!;

    /// <summary>A MassTransit request client, like a service asking rabbit-mcp.</summary>
    /// <summary>Options of the rabbit-mcp components (for creating more of them in a test).</summary>
    public Microsoft.Extensions.Options.IOptions<RabbitMcpOptions> Options => _options;

    /// <summary>Force-closes every broker connection with this client name, like a network failure or an operator would.</summary>
    public async Task CloseConnectionsAsync(string clientName)
    {
        using var http = ManagementHttp();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var list = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("api/connections")).RootElement;
            var names = list.EnumerateArray()
                .Where(c => c.TryGetProperty("client_properties", out var p) && p.TryGetProperty("connection_name", out var n)
                            && n.GetString() == clientName)
                .Select(c => c.GetProperty("name").GetString()!)
                .ToArray();
            if (names.Length > 0)
            {
                foreach (var name in names)
                    (await http.DeleteAsync("api/connections/" + Uri.EscapeDataString(name))).EnsureSuccessStatusCode();
                return;
            }
            await Task.Delay(500); // the management API lists new connections after a short delay
        }
        throw new TimeoutException($"No broker connection named {clientName}.");
    }

    private HttpClient ManagementHttp()
    {
        var amqp = new Uri(_options.Value.Uri);
        var http = new HttpClient { BaseAddress = new Uri(_options.Value.ManagementUri!.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(amqp.UserInfo))));
        return http;
    }

    public IRequestClient<AskHuman> AskClient(TimeSpan timeout) =>
        _bus!.CreateRequestClient<AskHuman>(RequestTimeout.After(ms: (int)timeout.TotalMilliseconds));
    public string AuditPath { get; } = Path.Combine(Path.GetTempPath(), $"rabbit-mcp-it-audit-{Guid.NewGuid():N}", "audit.jsonl");
    private Microsoft.Extensions.Options.IOptions<RabbitMcpOptions> _options = null!;

    /// <summary>A publisher with a different allow/deny policy, sharing everything else.</summary>
    public ContractPublisher CreatePublisher(PublishPolicy policy) =>
        new(Connection, Registry, new EnvelopeFactory(TimeProvider.System), _replies, policy, Audit,
            TimeProvider.System, _options, NullLogger<ContractPublisher>.Instance);

    /// <summary>All audit events written so far (JSON Lines file).</summary>
    public IReadOnlyList<System.Text.Json.JsonElement> AuditEvents() =>
        File.ReadAllLines(AuditPath)
            .Where(l => l.Length > 0)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone())
            .ToArray();
    private ReplyListener _replies = null!;

    /// <summary>Trace of MassTransit's consume span for GetOrderStatus requests of unknown orders.</summary>
    public ConcurrentDictionary<Guid, System.Diagnostics.ActivityTraceId?> StatusTraces { get; } = new();

    /// <summary>Every finished span from rabbit-mcp and MassTransit (like an OpenTelemetry exporter would see).</summary>
    public ConcurrentQueue<System.Diagnostics.Activity> Spans { get; } = new();
    private System.Diagnostics.ActivityListener? _listener;

    public System.Diagnostics.Activity Span(string source, Func<System.Diagnostics.Activity, bool> match) =>
        Spans.Single(a => a.Source.Name == source && match(a));

    public async Task InitializeAsync()
    {
        _listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name is RabbitMcp.Server.Messaging.Telemetry.Name or "MassTransit",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Spans.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(_listener);

        await _container.StartAsync();
        var amqp = new Uri(_container.GetConnectionString());

        // --- rabbit-mcp under test, wired exactly like Program.cs but without the MCP host.
        var managementUri = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(15672)}";
        var options = Microsoft.Extensions.Options.Options.Create(new RabbitMcpOptions
        {
            Uri = amqp.ToString(), ClientName = "rabbit-mcp-it", ManagementUri = managementUri,
            AuditLogPath = AuditPath, AuditIncludePayload = true,
        });
        _options = options;
        Audit = new AuditLog(options, TimeProvider.System, NullLogger<AuditLog>.Instance);
        Audit.EnsureWritable();
        Connection = new RabbitConnection(options, TimeProvider.System, NullLogger<RabbitConnection>.Instance);
        Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly, typeof(NobodyListens).Assembly);
        var inspector = new MessageInspector(Registry, options);
        Browser = new QueueBrowser(Connection, inspector, TimeProvider.System, Audit, options, NullLogger<QueueBrowser>.Instance);
        _replies = new ReplyListener(Connection, options, NullLogger<ReplyListener>.Instance);
        Management = new ManagementClient(options);
        Publisher = CreatePublisher(PublishPolicy.AllowAll);

        // --- the "services": a plain MassTransit v8 bus, no retries (faults go straight to _error).
        var user = Uri.UnescapeDataString(amqp.UserInfo.Split(':')[0]);
        var pass = Uri.UnescapeDataString(amqp.UserInfo.Split(':')[1]);
        _bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(amqp.Host, (ushort)amqp.Port, "/", h =>
            {
                h.Username(user);
                h.Password(pass);
            });

            cfg.ReceiveEndpoint(OrderSubmittedQueue, e => e.Handler<OrderSubmitted>(ctx =>
            {
                Complete(_submitted, ctx.Message.OrderId, Capture(ctx));
                return Task.CompletedTask;
            }));

            // Fails the first delivery of every order, succeeds afterwards → exercises _error + republish.
            cfg.ReceiveEndpoint(OrderCancelledQueue, e => e.Handler<OrderCancelled>(ctx =>
            {
                if (_cancelAttempts.AddOrUpdate(ctx.Message.OrderId, 1, (_, n) => n + 1) == 1)
                    throw new InvalidOperationException("Cancellation service unavailable");

                Complete(_cancelled, ctx.Message.OrderId, Capture(ctx));
                return Task.CompletedTask;
            }));

            cfg.ReceiveEndpoint(OrderStatusQueue, e => e.Handler<GetOrderStatus>(async ctx =>
            {
                var id = ctx.Message.OrderId;
                if (id == KnownOrderId)
                    await ctx.RespondAsync(new OrderStatus { OrderId = id, Status = "Shipped", UpdatedAtUtc = DateTime.UtcNow });
                else if (id == BadReplyOrderId)
                    await ctx.RespondAsync(new OrderStatus { OrderId = id, Status = "Lost" });
                else if (id == FailingOrderId)
                    throw new InvalidOperationException("Order database unavailable");
                else
                {
                    StatusTraces[id] = System.Diagnostics.Activity.Current?.TraceId;
                    await ctx.RespondAsync(new OrderNotFound { OrderId = id });
                }
            }));
        });
        await _bus.StartAsync();

        // --- responder mode: answers AskHuman requests from the bus.
        Responder = new ResponderService(Connection, ResponderContracts.From(Registry, "AskHuman"),
            new RequestInbox(TimeProvider.System, capacity: 100), Audit, TimeProvider.System, options,
            NullLogger<ResponderService>.Instance);
        await Responder.StartAsync(CancellationToken.None);
        await Responder.Ready.WaitAsync(TimeSpan.FromSeconds(30));

        // --- topology for situations MassTransit would never create.
        await using var channel = await Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        var unbound = Registry.Get(typeof(UnboundEvent)).Exchange;
        await channel.ExchangeDeclareAsync(unbound, RabbitMQ.Client.ExchangeType.Fanout, durable: true, autoDelete: false);

        var ping = Registry.Get(typeof(PingNobody)).Exchange;
        await channel.ExchangeDeclareAsync(ping, RabbitMQ.Client.ExchangeType.Fanout, durable: true, autoDelete: false);
        await channel.QueueDeclareAsync(PingQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(PingQueue, ping, routingKey: "");
    }

    public Task<Received<OrderSubmitted>> WaitForSubmitted(Guid orderId) =>
        Get(_submitted, orderId).Task.WaitAsync(TimeSpan.FromSeconds(15));

    public Task<Received<OrderCancelled>> WaitForCancelled(Guid orderId) =>
        Get(_cancelled, orderId).Task.WaitAsync(TimeSpan.FromSeconds(15));

    public bool WasSubmitted(Guid orderId) => _submitted.TryGetValue(orderId, out var t) && t.Task.IsCompleted;

    /// <summary>Polls until the queue holds <paramref name="expected"/> ready messages.</summary>
    public async Task WaitForQueueCount(string queue, uint expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        uint last = 0;
        while (DateTime.UtcNow < deadline)
        {
            last = (await Connection.GetQueueAsync(queue, CancellationToken.None))?.MessageCount ?? 0;
            if (last == expected)
                return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Queue '{queue}' has {last} messages, expected {expected}.");
    }

    private static Received<T> Capture<T>(ConsumeContext<T> ctx) where T : class =>
        new(ctx.Message, ctx.MessageId,
            ctx.Headers.GetAll().ToDictionary(h => h.Key, h => (object?)h.Value),
            ctx.ReceiveContext.TransportHeaders.GetAll().ToDictionary(h => h.Key, h => (object?)h.Value),
            System.Diagnostics.Activity.Current?.TraceId); // MassTransit's consume span

    private static TaskCompletionSource<Received<T>> Get<T>(
        ConcurrentDictionary<Guid, TaskCompletionSource<Received<T>>> map, Guid id) =>
        map.GetOrAdd(id, _ => new TaskCompletionSource<Received<T>>(TaskCreationOptions.RunContinuationsAsynchronously));

    private static void Complete<T>(
        ConcurrentDictionary<Guid, TaskCompletionSource<Received<T>>> map, Guid id, Received<T> received) =>
        Get(map, id).TrySetResult(received);

    public async Task DisposeAsync()
    {
        _listener?.Dispose();
        if (Responder is not null)
            await Responder.StopAsync(CancellationToken.None);
        if (_bus is not null)
            await _bus.StopAsync();
        await _replies.DisposeAsync();
        Management.Dispose();
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BrokerCollection : ICollectionFixture<BrokerFixture>
{
    public const string Name = "broker";
}
