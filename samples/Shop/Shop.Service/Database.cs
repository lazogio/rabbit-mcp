// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shop.Service;

/// <summary>Creates the schema and the catalog on first start, before the service takes messages.</summary>
public static class Database
{
    /// <summary>Always exists, so request_get_order_status has something to find right away.</summary>
    public static readonly Guid DemoOrderId = Guid.Parse("5ca1ab1e-0000-4000-8000-000000000001");

    private static readonly Product[] Catalog =
    [
        new() { Sku = "LAMP-01", Name = "Desk lamp", Price = 20m, Stock = 100 },
        new() { Sku = "FLAKY-LAMP", Name = "Desk lamp (demo: the warehouse fails the first time)", Price = 20m, Stock = 100 },
        new() { Sku = "TABLE-01", Name = "Oak table", Price = 80m, Stock = 50 },
        new() { Sku = "CHAIR-01", Name = "Chair", Price = 50m, Stock = 50 },
        new() { Sku = "TV-55", Name = "55\" TV", Price = 900m, Stock = 20 },
        new() { Sku = "SOFA-3", Name = "Three-seat sofa", Price = 2500m, Stock = 10 },
        new() { Sku = "VASE-01", Name = "Glass vase (last one)", Price = 35m, Stock = 1 },
    ];

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Database));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
                // A sample creates its schema directly; a production service would ship EF Core migrations.
                await db.Database.EnsureCreatedAsync(ct);
                await SeedAsync(db, ct);
                logger.LogInformation("Database ready: {Products} products, {Orders} orders",
                    await db.Products.CountAsync(ct), await db.Orders.CountAsync(ct));
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is not OperationCanceledException)
            {
                logger.LogWarning("PostgreSQL not ready yet ({Message}), retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static async Task SeedAsync(ShopDb db, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(ct))
            db.Products.AddRange(Catalog.Select(p => new Product { Sku = p.Sku, Name = p.Name, Price = p.Price, Stock = p.Stock }));

        if (!await db.Orders.AnyAsync(o => o.Id == DemoOrderId, ct))
        {
            var demo = new Order
            {
                Id = DemoOrderId, CustomerEmail = "demo@example.ge", Status = Order.Shipped, Total = 42.50m,
                Note = "Demo order", UpdatedAtUtc = DateTime.UtcNow,
                Lines = { new OrderLineItem { Sku = "LAMP-01", Quantity = 2, UnitPrice = 21.25m } },
            };
            demo.Log("Placed");
            demo.Log("Shipped with Georgian Post");
            db.Orders.Add(demo);
        }

        await db.SaveChangesAsync(ct);
    }
}
