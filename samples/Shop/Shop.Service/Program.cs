// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop.Contracts;
using Shop.Service;

var builder = Host.CreateApplicationBuilder(args);
var rabbit = new Uri(builder.Configuration["RABBITMQ_URI"] ?? "amqp://guest:guest@localhost:5672/");

builder.Services
    .AddSingleton<OrderStore>()
    .AddSingleton<ApprovalQueue>()
    .AddHostedService<ApprovalWorker>()
    // Not MassTransit: a plain RabbitMQ.Client service in the same process (Format = Raw in the contracts).
    .AddHostedService<ShippingDesk>();

builder.Services.AddMassTransit(x =>
{
    // Queues "order-placed" and "get-order-status" (+ their _error queues), as MassTransit names them.
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<OrderPlacedConsumer>();
    x.AddConsumer<GetOrderStatusConsumer>();

    // Approval requests wait for a person: give them time.
    x.AddRequestClient<ApproveLargeOrder>(RequestTimeout.After(m: 10));

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

await builder.Build().RunAsync();
