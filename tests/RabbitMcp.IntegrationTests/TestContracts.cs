// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel.DataAnnotations;
using RabbitMcp.Contracts;

namespace RabbitMcp.IntegrationTests.Contracts;

// Contracts used only by integration tests, for broker situations the sample contracts cannot produce.

[MessageContract("Nobody ever declares this exchange.")]
public sealed record NobodyListens
{
    public required Guid Id { get; init; }
}

[MessageContract("The exchange exists but no queue is bound to it.")]
public sealed record UnboundEvent
{
    public required Guid Id { get; init; }
}

[MessageContract("Bound to a queue without consumers, so requests always time out.",
    Responses = new[] { typeof(Pong) })]
public sealed record PingNobody
{
    public required Guid Id { get; init; }
}

public sealed record Pong
{
    public required Guid Id { get; init; }
}

[MessageContract("Its exchange is created and deleted by the test, to exercise the exchange cache.")]
public sealed record VolatileEvent
{
    public required Guid Id { get; init; }
}

[MessageContract("A service asks rabbit-mcp (the model) a question: answered in responder mode.",
    Responses = new[] { typeof(HumanAnswer), typeof(HumanDeclined) })]
public sealed record AskHuman
{
    public required Guid Id { get; init; }

    [Required]
    public required string Question { get; init; }
}

public sealed record HumanAnswer
{
    public required Guid Id { get; init; }

    [Required, StringLength(200, MinimumLength = 1)]
    public required string Answer { get; init; }
}

public sealed record HumanDeclined
{
    public required Guid Id { get; init; }
}

// --- Services that use RabbitMQ without MassTransit (Format = Raw); the tests play them with plain RabbitMQ.Client.

[MessageContract("Stock changed (plain JSON on a topic exchange).", Format = MessageFormat.Raw,
    Exchange = RawTopology.InventoryExchange, RoutingKey = "stock.adjusted", Urn = "stock.adjusted")]
public sealed record RawStockAdjusted
{
    [RegularExpression("^[A-Z0-9-]{3,32}$")]
    public required string Sku { get; init; }

    public required int Delta { get; init; }
}

[MessageContract("Plain RabbitMQ RPC through the default exchange.", Format = MessageFormat.Raw,
    RoutingKey = RawTopology.QuotesQueue, Responses = new[] { typeof(RawQuote), typeof(RawNoQuote) })]
public sealed record RawGetQuote
{
    public required string Sku { get; init; }
}

public sealed record RawQuote
{
    public required string Sku { get; init; }

    [Range(typeof(decimal), "0.01", "100000")]
    public required decimal Price { get; init; }
}

public sealed record RawNoQuote
{
    public required string Sku { get; init; }
}

[MessageContract("Plain JSON to a queue that never exists.", Format = MessageFormat.Raw, RoutingKey = "it-raw-nobody")]
public sealed record RawNobody
{
    public required Guid Id { get; init; }
}

public static class RawTopology
{
    public const string InventoryExchange = "it.inventory";
    public const string QuotesQueue = "it-raw-quotes";
}
