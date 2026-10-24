// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Shop.Service;

/// <summary>
/// The shop's PostgreSQL database: catalog with stock, orders, their history and the e-mails sent to customers, plus
/// MassTransit's inbox/outbox tables. Saving an order and the events it causes happens in one transaction (outbox), and
/// a message that was already processed is not processed again (inbox).
/// </summary>
public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderHistoryEntry> OrderHistory => Set<OrderHistoryEntry>();
    public DbSet<CustomerEmail> Emails => Set<CustomerEmail>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>(product =>
        {
            product.HasKey(p => p.Sku);
            product.Property(p => p.Sku).HasMaxLength(32);
            product.Property(p => p.Name).HasMaxLength(200);
            product.Property(p => p.Price).HasPrecision(12, 2);
            // PostgreSQL's xmin: two orders reserving the last items at once → one gets DbUpdateConcurrencyException
            // and MassTransit retries it (Program.cs), so stock never goes negative.
            product.Property(p => p.Version).IsRowVersion();
        });

        model.Entity<Order>(order =>
        {
            order.HasKey(o => o.Id);
            order.Property(o => o.CustomerEmail).HasMaxLength(320);
            order.Property(o => o.Status).HasMaxLength(32);
            order.Property(o => o.Total).HasPrecision(12, 2);
            order.HasIndex(o => o.Status);
            order.OwnsMany(o => o.Lines, line =>
            {
                line.ToTable("OrderLines");
                line.WithOwner().HasForeignKey("OrderId");
                line.Property<int>("Id");
                line.HasKey("Id");
                line.Property(l => l.Sku).HasMaxLength(32);
                line.Property(l => l.UnitPrice).HasPrecision(12, 2);
            });
            order.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId);
        });

        model.Entity<OrderHistoryEntry>().HasIndex(h => new { h.OrderId, h.AtUtc });
        model.Entity<CustomerEmail>().HasIndex(e => e.OrderId);

        model.AddInboxStateEntity();
        model.AddOutboxMessageEntity();
        model.AddOutboxStateEntity();
    }
}

public sealed class Product
{
    public required string Sku { get; init; }
    public required string Name { get; set; }
    public required decimal Price { get; set; }
    public int Stock { get; set; }
    public uint Version { get; set; }
}

public sealed class Order
{
    public const string Approved = "Approved";
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string ApprovalTimedOut = "ApprovalTimedOut";
    public const string Shipped = "Shipped";

    public Guid Id { get; init; }
    public required string CustomerEmail { get; init; }
    public required string Status { get; set; }
    public decimal Total { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The approval request is out (reset at startup, so a restart asks again instead of losing it).</summary>
    public bool ApprovalRequested { get; set; }

    public List<OrderLineItem> Lines { get; init; } = [];
    public List<OrderHistoryEntry> History { get; init; } = [];

    public void Change(string status, string? note, string history)
    {
        Status = status;
        Note = note;
        UpdatedAtUtc = DateTime.UtcNow;
        Log(history);
    }

    public void Log(string text) => History.Add(new OrderHistoryEntry { OrderId = Id, AtUtc = DateTime.UtcNow, Text = text });
}

public sealed class OrderLineItem
{
    public required string Sku { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public sealed class OrderHistoryEntry
{
    public long Id { get; init; }
    public Guid OrderId { get; init; }
    public DateTime AtUtc { get; init; }
    public required string Text { get; init; }
}

/// <summary>E-mails to customers. A real shop hands them to an SMTP or e-mail API here; the sample stores them.</summary>
public sealed class CustomerEmail
{
    public long Id { get; init; }
    public Guid OrderId { get; init; }
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public DateTime SentAtUtc { get; init; }
}
