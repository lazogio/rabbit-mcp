// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.Contracts;

namespace Shop.Service;

/// <summary>
/// Takes an order: checks the catalog, reserves stock and decides. Everything it writes — the order, the stock and the
/// OrderConfirmed/OrderCancelled event — is committed in one PostgreSQL transaction (MassTransit's EF Core outbox,
/// Program.cs), so an event is never published for an order that was not saved, nor lost for one that was.
/// </summary>
public sealed class OrderPlacedConsumer(ShopDb db, Warehouse warehouse, ILogger<OrderPlacedConsumer> logger)
    : IConsumer<OrderPlaced>
{
    public const decimal ApprovalLimit = 1000m;

    public async Task Consume(ConsumeContext<OrderPlaced> context)
    {
        var placed = context.Message;
        var ct = context.CancellationToken;

        if (await db.Orders.AnyAsync(o => o.Id == placed.OrderId, ct))
        {
            logger.LogInformation("Order {OrderId} already exists, ignoring the duplicate", placed.OrderId);
            return;
        }

        // A transient failure: the warehouse is briefly unreachable. MassTransit moves the message to order-placed_error;
        // republish_from_error brings it back once "the warehouse is up again".
        warehouse.CheckAvailable(placed);

        // A permanent failure: the SKU is not (or no longer) in the catalog. Retrying never helps — park it
        // (dead_letter_from_error) or fix the SKU and send it again (resend_corrected).
        var skus = placed.Lines.Select(l => l.Sku).Distinct().ToList();
        var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);
        if (skus.FirstOrDefault(s => !products.ContainsKey(s)) is { } unknown)
            throw new InvalidOperationException($"Product {unknown} no longer exists in the catalog.");

        var order = new Order
        {
            Id = placed.OrderId, CustomerEmail = placed.CustomerEmail, Status = Order.AwaitingApproval,
            Total = placed.Lines.Sum(l => l.Quantity * l.UnitPrice), UpdatedAtUtc = DateTime.UtcNow,
            Lines = placed.Lines.Select(l => new OrderLineItem { Sku = l.Sku, Quantity = l.Quantity, UnitPrice = l.UnitPrice }).ToList(),
        };
        order.Log($"Placed: {string.Join(", ", placed.Lines.Select(l => $"{l.Quantity} × {l.Sku}"))}, total {order.Total}");
        db.Orders.Add(order);

        var missing = placed.Lines
            .GroupBy(l => l.Sku)
            .Select(g => (Product: products[g.Key], Wanted: g.Sum(l => l.Quantity)))
            .Where(x => x.Product.Stock < x.Wanted)
            .ToList();

        if (missing.Count > 0)
        {
            var reason = "Out of stock: " + string.Join(", ", missing.Select(m => $"{m.Product.Sku} ({m.Product.Stock} left)"));
            order.Change(Order.Cancelled, reason, $"Cancelled: {reason}");
            await context.Publish(new OrderCancelled { OrderId = order.Id, CustomerEmail = order.CustomerEmail, Reason = reason }, ct);
        }
        else
        {
            foreach (var line in placed.Lines)
                products[line.Sku].Stock -= line.Quantity; // xmin guards concurrent reservations
            order.Log("Stock reserved");

            if (order.Total > ApprovalLimit)
            {
                // ApprovalWorker picks it up after the commit and asks a person (ApproveLargeOrder).
                order.Change(Order.AwaitingApproval, null, $"Above {ApprovalLimit}: waiting for a person's approval");
            }
            else
            {
                order.Change(Order.Approved, "Approved automatically (below the limit)", "Approved automatically");
                await context.Publish(new OrderConfirmed
                {
                    OrderId = order.Id, CustomerEmail = order.CustomerEmail, Total = order.Total, ApprovedBy = "automatic",
                }, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Order {OrderId}: {Total} → {Status}", order.Id, order.Total, order.Status);
    }
}

/// <summary>Answers GetOrderStatus from the database.</summary>
public sealed class GetOrderStatusConsumer(ShopDb db) : IConsumer<GetOrderStatus>
{
    public async Task Consume(ConsumeContext<GetOrderStatus> context)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);

        if (order is null)
        {
            await context.RespondAsync(new OrderNotFound { OrderId = context.Message.OrderId });
            return;
        }

        await context.RespondAsync(new OrderStatus
        {
            OrderId = order.Id, Status = order.Status, Total = order.Total, Note = order.Note, UpdatedAtUtc = order.UpdatedAtUtc,
            History = order.History.OrderBy(h => h.AtUtc).ThenBy(h => h.Id).Select(h => $"{h.AtUtc:u} {h.Text}").ToList(),
        });
    }
}

/// <summary>
/// The customer-facing side: e-mails the customer when an order is confirmed or cancelled. Its own queue
/// (customer-notifications), so a slow or failing e-mail provider never holds up orders.
/// </summary>
public sealed class CustomerNotificationsConsumer(ShopDb db, ILogger<CustomerNotificationsConsumer> logger)
    : IConsumer<OrderConfirmed>, IConsumer<OrderCancelled>
{
    public Task Consume(ConsumeContext<OrderConfirmed> context) =>
        SendAsync(context.Message.OrderId, context.Message.CustomerEmail,
            $"Your order {context.Message.OrderId} is confirmed",
            $"Thank you! Total: {context.Message.Total}.", "Confirmation", context.CancellationToken);

    public Task Consume(ConsumeContext<OrderCancelled> context) =>
        SendAsync(context.Message.OrderId, context.Message.CustomerEmail,
            $"Your order {context.Message.OrderId} was cancelled",
            $"We are sorry: {context.Message.Reason}", "Cancellation", context.CancellationToken);

    private async Task SendAsync(Guid orderId, string to, string subject, string body, string kind, CancellationToken ct)
    {
        db.Emails.Add(new CustomerEmail { OrderId = orderId, To = to, Subject = subject, Body = body, SentAtUtc = DateTime.UtcNow });
        db.OrderHistory.Add(new OrderHistoryEntry { OrderId = orderId, AtUtc = DateTime.UtcNow, Text = $"{kind} e-mail sent to {to}" });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("{Kind} e-mail for order {OrderId} sent to {To}", kind, orderId, to);
    }
}

/// <summary>Stands in for a warehouse API: FLAKY-* items find it unreachable the first time (in memory, per order).</summary>
public sealed class Warehouse
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public void CheckAvailable(OrderPlaced order)
    {
        if (order.Lines.Any(l => l.Sku.StartsWith("FLAKY-", StringComparison.OrdinalIgnoreCase)) &&
            _attempts.AddOrUpdate(order.OrderId, 1, (_, n) => n + 1) == 1)
            throw new InvalidOperationException("Inventory service unavailable (demo: FLAKY-* items fail once).");
    }
}
