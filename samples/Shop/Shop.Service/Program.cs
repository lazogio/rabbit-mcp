// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop.Service;

var builder = Host.CreateApplicationBuilder(args);
var rabbit = new Uri(builder.Configuration["RABBITMQ_URI"] ?? "amqp://guest:guest@localhost:5672/");
var postgres = builder.Configuration.GetConnectionString("Shop")
    ?? "Host=localhost;Database=shop;Username=shop;Password=shop";

builder.Services
    .AddDbContext<ShopDb>(db => db.UseNpgsql(postgres))
    .AddSingleton<Warehouse>()
    .AddHostedService<ApprovalWorker>()
    // Not MassTransit: a plain RabbitMQ.Client service in the same process (Format = Raw in the contracts).
    .AddHostedService<ShippingDesk>();

builder.Services.AddMassTransit(x =>
{
    // Queues "order-placed", "get-order-status" and "customer-notifications" (+ their _error queues).
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<OrderPlacedConsumer>();
    x.AddConsumer<GetOrderStatusConsumer>();
    x.AddConsumer<CustomerNotificationsConsumer>();

    // Transactional outbox + inbox in PostgreSQL: events are stored with the data that caused them and delivered after
    // the commit; a message that was already processed is skipped. UseBusOutbox covers publishing outside consumers
    // (ApprovalWorker).
    x.AddEntityFrameworkOutbox<ShopDb>(outbox =>
    {
        outbox.UsePostgres();
        outbox.UseBusOutbox();
        outbox.QueryDelay = TimeSpan.FromSeconds(1);
    });

    x.AddConfigureEndpointsCallback((context, _, endpoint) =>
    {
        // Only a lost race for the same stock is worth retrying right away; every other failure goes to the _error
        // queue, where a person (through rabbit-mcp) decides: republish, fix and resend, or park it.
        endpoint.UseMessageRetry(retry =>
        {
            retry.Handle<DbUpdateConcurrencyException>();
            retry.Intervals(100, 500, 1000);
        });
        endpoint.UseEntityFrameworkOutbox<ShopDb>(context);
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        var credentials = rabbit.UserInfo.Split(':', 2);
        var vhost = rabbit.AbsolutePath.Trim('/') is { Length: > 0 } path ? Uri.UnescapeDataString(path) : "/";
        cfg.Host(rabbit.Host, (ushort)(rabbit.Port > 0 ? rabbit.Port : 5672), vhost, h =>
        {
            h.Username(Uri.UnescapeDataString(credentials[0]));
            h.Password(credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "");
        });
        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
await Database.InitializeAsync(host.Services);
await host.RunAsync();
