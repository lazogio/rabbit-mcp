// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using RabbitMcp.Contracts;

namespace Shop.Contracts;

// Event → publish_order_placed
[MessageContract("A customer placed an order. The shop checks the catalog and reserves stock; orders above 1000 wait " +
    "for a person's approval (ApproveLargeOrder), orders it cannot fill are cancelled.", Version = 1)]
public sealed record OrderPlaced
{
    [Description("New order id.")]
    public required Guid OrderId { get; init; }

    [Description("Customer e-mail address.")]
    [EmailAddress]
    public required string CustomerEmail { get; init; }

    [Description("Ordered items, by catalog SKU: LAMP-01, TABLE-01, CHAIR-01, TV-55, SOFA-3, VASE-01 (1 in stock). " +
        "FLAKY-LAMP fails the first time (the warehouse is briefly down: demo of the _error queue); a SKU that is not in " +
        "the catalog always fails (demo of the dead-letter queue and resend_corrected).")]
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

    [Description("Approved, AwaitingApproval, Rejected, Cancelled (out of stock), ApprovalTimedOut or Shipped.")]
    public required string Status { get; init; }

    public decimal Total { get; init; }

    [Description("Who approved it, or why it was rejected or cancelled.")]
    public string? Note { get; init; }

    public DateTime UpdatedAtUtc { get; init; }

    [Description("What happened to the order, oldest first: placed, approved, e-mails sent to the customer, ...")]
    public List<string> History { get; init; } = [];
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

// Request answered by a service WITHOUT MassTransit → request_get_shipping_quote. The shipping desk (ShippingDesk.cs) is
// a plain RabbitMQ.Client service: it reads the message as plain JSON from the "shipping-quotes" queue and replies to
// reply_to with the request's correlation_id, naming the reply type in the AMQP type property (RabbitMQ RPC).
[MessageContract("Asks the shipping desk for a delivery quote. The desk is a plain RabbitMQ service, not MassTransit.",
    Format = MessageFormat.Raw, RoutingKey = "shipping-quotes",
    Responses = new[] { typeof(ShippingQuote), typeof(ShippingUnavailable) })]
public sealed record GetShippingQuote
{
    [Description("Destination country, two-letter code, e.g. GE. AQ (Antarctica) is not served.")]
    [RegularExpression("^[A-Z]{2}$")]
    public required string Country { get; init; }

    [Description("Parcel weight in kilograms.")]
    [Range(0.1, 50)]
    public required double WeightKg { get; init; }
}

public sealed record ShippingQuote
{
    public required string Country { get; init; }

    public required string Carrier { get; init; }

    [Range(typeof(decimal), "0.01", "10000")]
    public required decimal Price { get; init; }

    [Description("Delivery time in days.")]
    public required int Days { get; init; }
}

public sealed record ShippingUnavailable
{
    public required string Country { get; init; }

    public required string Reason { get; init; }
}

// Events published BY the shop (through its transactional outbox) and consumed by its notification service. They are
// contracts so rabbit-mcp can decode, search and check them (peek_messages, find_message, contract_consumers), but the
// sample's PublishDeny keeps the model from faking them: only the shop announces a confirmed or cancelled order.
[MessageContract("Published by the shop when an order is approved (automatically or by a person); the customer is e-mailed.")]
public sealed record OrderConfirmed
{
    public required Guid OrderId { get; init; }

    public required string CustomerEmail { get; init; }

    public required decimal Total { get; init; }

    [Description("\"automatic\" below the limit, otherwise the person who approved it.")]
    public required string ApprovedBy { get; init; }
}

[MessageContract("Published by the shop when an order is rejected or cannot be filled (out of stock); the customer is e-mailed.")]
public sealed record OrderCancelled
{
    public required Guid OrderId { get; init; }

    public required string CustomerEmail { get; init; }

    public required string Reason { get; init; }
}
