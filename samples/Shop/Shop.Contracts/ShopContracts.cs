// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using RabbitMcp.Contracts;

namespace Shop.Contracts;

// Event → publish_order_placed
[MessageContract("A customer placed an order. Orders above 1000 wait for a person's approval (ApproveLargeOrder).", Version = 1)]
public sealed record OrderPlaced
{
    [Description("New order id.")]
    public required Guid OrderId { get; init; }

    [Description("Customer e-mail address.")]
    [EmailAddress]
    public required string CustomerEmail { get; init; }

    [Description("Ordered items. A SKU starting with FLAKY- fails the first time (demo of the _error queue).")]
    [MinLength(1)]
    public required List<OrderLine> Lines { get; init; }
}

public sealed record OrderLine
{
    [Description("Stock keeping unit, e.g. LAMP-01.")]
    [RegularExpression("^[A-Z0-9-]{3,32}$")]
    public required string Sku { get; init; }

    [Range(1, 100)]
    public required int Quantity { get; init; }

    [Range(typeof(decimal), "0.01", "100000")]
    public required decimal UnitPrice { get; init; }
}

// Request → request_get_order_status (answered by the shop service)
[MessageContract("Asks the shop for the status of an order.",
    Responses = new[] { typeof(OrderStatus), typeof(OrderNotFound) })]
public sealed record GetOrderStatus
{
    [Description("Order id. The demo order 5ca1ab1e-0000-4000-8000-000000000001 always exists.")]
    public required Guid OrderId { get; init; }
}

public sealed record OrderStatus
{
    public required Guid OrderId { get; init; }

    [Description("Approved, AwaitingApproval, Rejected, ApprovalTimedOut or Shipped.")]
    public required string Status { get; init; }

    public decimal Total { get; init; }

    [Description("Who approved it, or why it was rejected.")]
    public string? Note { get; init; }

    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record OrderNotFound
{
    public required Guid OrderId { get; init; }
}

// Request sent BY the shop service and answered by a person through rabbit-mcp (RabbitMcp:RespondTo=ApproveLargeOrder):
// pending_requests → reply_order_approved / reply_order_rejected / reject_request.
[MessageContract("The shop asks a person to approve an order above the limit.",
    Responses = new[] { typeof(OrderApproved), typeof(OrderRejected) })]
public sealed record ApproveLargeOrder
{
    public required Guid OrderId { get; init; }

    public required string CustomerEmail { get; init; }

    [Description("Order total; the limit for automatic approval is 1000.")]
    public required decimal Total { get; init; }

    public required int Items { get; init; }
}

public sealed record OrderApproved
{
    public required Guid OrderId { get; init; }

    [Description("Name of the person who approved the order.")]
    [Required, StringLength(100, MinimumLength = 1)]
    public required string ApprovedBy { get; init; }
}

public sealed record OrderRejected
{
    public required Guid OrderId { get; init; }

    [Description("Why the order is rejected; the shop stores it on the order.")]
    [Required, StringLength(500, MinimumLength = 1)]
    public required string Reason { get; init; }
}
