// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Shop.Contracts;

namespace Shop.Service;

/// <summary>In-memory orders (a real service would use a database).</summary>
public sealed class OrderStore
{
    /// <summary>Always exists, so request_get_order_status has something to find right away.</summary>
    public static readonly Guid DemoOrderId = Guid.Parse("5ca1ab1e-0000-4000-8000-000000000001");

    private readonly ConcurrentDictionary<Guid, OrderStatus> _orders = new();
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public OrderStore() => Set(DemoOrderId, "Shipped", 42.50m, "Demo order");

    public OrderStatus? Find(Guid orderId) => _orders.GetValueOrDefault(orderId);

    public void Set(Guid orderId, string status, decimal total, string? note = null) =>
        _orders[orderId] = new OrderStatus
        {
            OrderId = orderId, Status = status, Total = total, Note = note, UpdatedAtUtc = DateTime.UtcNow,
        };

    public void Update(Guid orderId, string status, string? note) =>
        _orders.AddOrUpdate(orderId,
            id => new OrderStatus { OrderId = id, Status = status, Note = note, UpdatedAtUtc = DateTime.UtcNow },
            (_, existing) => existing with { Status = status, Note = note, UpdatedAtUtc = DateTime.UtcNow });

    /// <summary>True the first time an order is processed.</summary>
    public bool IsFirstAttempt(Guid orderId) => _attempts.AddOrUpdate(orderId, 1, (_, n) => n + 1) == 1;
}
