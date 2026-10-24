// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Contracts;

namespace Shop.Service;

/// <summary>
/// Asks for approval of large orders with an ordinary MassTransit request. Nobody in this service answers it:
/// rabbit-mcp in responder mode does (RabbitMcp:RespondTo=ApproveLargeOrder), so a person decides through the model.
/// The orders waiting are read from the database, so a restart asks again instead of forgetting them.
/// </summary>
public sealed class ApprovalWorker(IBus bus, IServiceScopeFactory scopes, ILogger<ApprovalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Requests from before a restart died with the old process: ask again.
        await WithDbAsync(db => db.Orders.Where(o => o.Status == Order.AwaitingApproval && o.ApprovalRequested)
            .ExecuteUpdateAsync(o => o.SetProperty(x => x.ApprovalRequested, false), stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var waiting = await WithDbAsync(async db =>
                {
                    var orders = await db.Orders.Where(o => o.Status == Order.AwaitingApproval && !o.ApprovalRequested)
                        .Include(o => o.Lines).Take(20).ToListAsync(stoppingToken);
                    orders.ForEach(o => o.ApprovalRequested = true);
                    await db.SaveChangesAsync(stoppingToken);
                    return orders;
                });

                foreach (var order in waiting)
                    _ = AskAsync(new ApproveLargeOrder
                    {
                        OrderId = order.Id, CustomerEmail = order.CustomerEmail, Total = order.Total,
                        Items = order.Lines.Sum(l => l.Quantity),
                    }, stoppingToken); // each approval waits on its own
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Looking for orders to approve failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task AskAsync(ApproveLargeOrder request, CancellationToken ct)
    {
        try
        {
            // From the bus itself, not through the outbox: the request must go out now and its answer come back here.
            var client = bus.CreateRequestClient<ApproveLargeOrder>(RequestTimeout.After(m: 10));
            logger.LogInformation("Asking for approval of order {OrderId} ({Total})", request.OrderId, request.Total);

            var response = await client.GetResponse<OrderApproved, OrderRejected>(request, ct);
            if (response.Is(out Response<OrderApproved>? approved))
                await DecideAsync(request.OrderId, approvedBy: approved!.Message.ApprovedBy, reason: null, ct);
            else if (response.Is(out Response<OrderRejected>? rejected))
                await DecideAsync(request.OrderId, approvedBy: null, reason: rejected!.Message.Reason, ct);
        }
        catch (RequestFaultException ex)
        {
            // reject_request in rabbit-mcp: the request faulted with the person's reason.
            await DecideAsync(request.OrderId, null, ex.Fault?.Exceptions.FirstOrDefault()?.Message ?? ex.Message, ct);
        }
        catch (RequestTimeoutException)
        {
            await DecideAsync(request.OrderId, null, "Nobody answered within 10 minutes", ct, Order.ApprovalTimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Approval of order {OrderId} failed", request.OrderId);
        }
    }

    /// <summary>Stores the decision and publishes its event in one transaction (bus outbox); a refusal releases the stock.</summary>
    private async Task DecideAsync(Guid orderId, string? approvedBy, string? reason, CancellationToken ct, string refusedStatus = Order.Rejected)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(); // writes to the outbox table

        var order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == orderId, ct);
        if (order.Status != Order.AwaitingApproval)
            return;

        if (approvedBy is not null)
        {
            order.Change(Order.Approved, $"Approved by {approvedBy}", $"Approved by {approvedBy}");
            await publish.Publish(new OrderConfirmed
            {
                OrderId = order.Id, CustomerEmail = order.CustomerEmail, Total = order.Total, ApprovedBy = approvedBy,
            }, ct);
        }
        else
        {
            var skus = order.Lines.Select(l => l.Sku).ToList();
            var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);
            foreach (var line in order.Lines)
                products[line.Sku].Stock += line.Quantity;

            order.Change(refusedStatus, reason, $"{refusedStatus}: {reason}; stock released");
            await publish.Publish(new OrderCancelled { OrderId = order.Id, CustomerEmail = order.CustomerEmail, Reason = reason! }, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Order {OrderId} is now {Status}", order.Id, order.Status);
    }

    private async Task<T> WithDbAsync<T>(Func<ShopDb, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ShopDb>());
    }
}
