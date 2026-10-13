// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using RabbitMcp.Contracts;

namespace Billing.Contracts;

[MessageContract("An invoice was issued to a customer.", Version = 2)]
public sealed record InvoiceIssued
{
    [Description("Invoice number, e.g. INV-2026-0001.")]
    [RegularExpression("^INV-[0-9]{4}-[0-9]{4}$")]
    public required string InvoiceNumber { get; init; }

    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal Amount { get; init; }
}

[MessageContract("Asks billing for an invoice.", Responses = new[] { typeof(InvoiceFound), typeof(InvoiceMissing) })]
public sealed record GetInvoice
{
    public required string InvoiceNumber { get; init; }
}

public sealed record InvoiceFound
{
    public required string InvoiceNumber { get; init; }
    public required decimal Amount { get; init; }
}

public sealed record InvoiceMissing
{
    public required string InvoiceNumber { get; init; }
}
