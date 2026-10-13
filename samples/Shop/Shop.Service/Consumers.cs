// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using MassTransit;
using Microsoft.Extensions.Logging;
using Shop.Contracts;

namespace Shop.Service;

public sealed class OrderPlacedConsumer(OrderStore orders, ApprovalQueue approvals, ILogger<OrderPlacedConsumer> logger)
    : IConsumer<OrderPlaced>
{
    public const decimal ApprovalLimit = 1000m;

    public Task Consume(ConsumeContext<OrderPlaced> context)
    {
        var order = context.Message;

        // Demo of the _error queue: an order with a FLAKY-* item fails the first time, as if the inventory service
        // were briefly down. MassTransit moves it to order-placed_error; republish_from_error brings it back.
        if (order.Lines.Any(l => l.Sku.StartsWith("FLAKY-", StringComparison.OrdinalIgnoreCase)) &&
            orders.IsFirstAttempt(order.OrderId))
            throw new InvalidOperationException("Inventory service unavailable (demo: FLAKY-* items fail once).");

        var total = order.Lines.Sum(l => l.Quantity * l.UnitPrice);
        if (total > ApprovalLimit)
        {
            orders.Set(order.OrderId, "AwaitingApproval", total);
            approvals.Enqueue(new ApproveLargeOrder
            {
                OrderId = order.OrderId, CustomerEmail = order.CustomerEmail, Total = total,
                Items = order.Lines.Sum(l => l.Quantity),
            });
        }
        else
        {
            orders.Set(order.OrderId, "Approved", total, "Approved automatically (below the limit)");
        }

        logger.LogInformation("Order {OrderId} placed: {Total} → {Status}", order.OrderId, total, orders.Find(order.OrderId)?.Status);
        return Task.CompletedTask;
    }
}

public sealed class GetOrderStatusConsumer(OrderStore orders) : IConsumer<GetOrderStatus>
{
    public Task Consume(ConsumeContext<GetOrderStatus> context) =>
        orders.Find(context.Message.OrderId) is { } status
            ? context.RespondAsync(status)
            : context.RespondAsync(new OrderNotFound { OrderId = context.Message.OrderId });
}
