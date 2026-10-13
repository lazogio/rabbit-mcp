// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace RabbitMcp.Contracts.Orders;

// Sample contracts. Replace them with your own; everything else is driven by
// [MessageContract] + the property attributes:
//   - `required` and nullability    -> JSON Schema "required"
//   - [Description]                 -> schema descriptions the model reads
//   - DataAnnotations ([Range], ...) -> enforced by the server before publishing

[MessageContract("A customer submitted a new order. Published once per order.")]
public sealed record OrderSubmitted
{
    [Description("Unique order id (GUID).")]
    public required Guid OrderId { get; init; }

    [Description("Customer e-mail address.")]
    [EmailAddress]
    public required string CustomerEmail { get; init; }

    [Description("Order lines. At least one.")]
    [MinLength(1)]
    public required IReadOnlyList<OrderLine> Lines { get; init; }

    [Description("Optional free-text note from the customer.")]
    [StringLength(500)]
    public string? Note { get; init; }
}

public sealed record OrderLine
{
    [Description("Stock keeping unit, e.g. ABC-123.")]
    [RegularExpression("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    public required string Sku { get; init; }

    [Description("Number of units, 1-1000.")]
    [Range(1, 1000)]
    public required int Quantity { get; init; }

    [Description("Price per unit in the order currency.")]
    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal UnitPrice { get; init; }
}

// Request/response: the MCP server sends GetOrderStatus and waits for the consumer's
// reply. A MassTransit consumer answers with context.RespondAsync(new OrderStatus { ... })
// or context.RespondAsync(new OrderNotFound { ... }).
[MessageContract("Asks the order service for the current status of an order.",
    Responses = new[] { typeof(OrderStatus), typeof(OrderNotFound) })]
public sealed record GetOrderStatus
{
    [Description("Id of the order to look up.")]
    public required Guid OrderId { get; init; }
}

public sealed record OrderStatus
{
    public required Guid OrderId { get; init; }

    [Description("Submitted, Paid, Shipped or Cancelled.")]
    [RegularExpression("^(Submitted|Paid|Shipped|Cancelled)$")]
    public required string Status { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }
}

public sealed record OrderNotFound
{
    public required Guid OrderId { get; init; }
}

[MessageContract("An order was cancelled before shipping.", Version = 1)]
public sealed record OrderCancelled
{
    [Description("Id of the cancelled order.")]
    public required Guid OrderId { get; init; }

    [Description("Why the order was cancelled (3-200 characters).")]
    [StringLength(200, MinimumLength = 3)]
    public required string Reason { get; init; }
}
