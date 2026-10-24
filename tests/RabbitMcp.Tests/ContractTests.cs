// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Globalization;
using RabbitMcp.Contracts;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Tests;

[MessageContract("Override test", Version = 3, Urn = "urn:message:Shop.Messages:PaymentCaptured",
    Exchange = "Shop.Messages:PaymentCaptured", ToolName = "publish_payment")]
public sealed record PaymentCapturedV3
{
    public required Guid PaymentId { get; init; }
}

public class ContractRegistryTests
{
    [MessageContract("nested, not allowed")]
    public sealed record NestedContract;

    [Fact]
    public void Describe_uses_MassTransit_conventions()
    {
        var d = ContractRegistry.Describe(typeof(OrderSubmitted));

        Assert.Equal("OrderSubmitted", d.Name);
        Assert.Equal("publish_order_submitted", d.ToolName);
        Assert.Equal("urn:message:RabbitMcp.Samples.Orders:OrderSubmitted", d.MessageTypeUrn);
        Assert.Equal("RabbitMcp.Samples.Orders:OrderSubmitted", d.Exchange);
        Assert.Equal(1, d.Version);
    }

    [Fact]
    public void Describe_honours_attribute_overrides()
    {
        var d = ContractRegistry.Describe(typeof(PaymentCapturedV3));

        Assert.Equal("publish_payment", d.ToolName);
        Assert.Equal("urn:message:Shop.Messages:PaymentCaptured", d.MessageTypeUrn);
        Assert.Equal("Shop.Messages:PaymentCaptured", d.Exchange);
        Assert.Equal(3, d.Version);
    }

    [Fact]
    public void Describe_rejects_nested_types() =>
        Assert.Throws<InvalidOperationException>(() => ContractRegistry.Describe(typeof(NestedContract)));

    [Fact]
    public void Describe_rejects_types_without_attribute() =>
        Assert.Throws<InvalidOperationException>(() => ContractRegistry.Describe(typeof(OrderLine)));

    [Theory]
    [InlineData("OrderSubmitted", "order_submitted")]
    [InlineData("HTTPRequestSent", "http_request_sent")]
    [InlineData("OrderV2Created", "order_v2_created")]
    [InlineData("Simple", "simple")]
    public void ToSnakeCase_handles_acronyms_and_digits(string input, string expected) =>
        Assert.Equal(expected, ContractRegistry.ToSnakeCase(input));

    [Fact]
    public void Registry_from_contracts_assembly_finds_samples_and_resolves_lookups()
    {
        var registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);

        Assert.Equal(new[] { "GetOrderStatus", "OrderCancelled", "OrderSubmitted" }, registry.Contracts.Select(c => c.Name));
        Assert.Same(registry.Get(typeof(OrderCancelled)), registry.FindByName("publish_order_cancelled"));
        Assert.Same(registry.Get(typeof(OrderCancelled)), registry.FindByName("ordercancelled"));
        Assert.Equal("OrderSubmitted",
            registry.FindByUrns(["urn:message:Other:Thing", "urn:message:RabbitMcp.Samples.Orders:OrderSubmitted"])?.Name);
        Assert.Null(registry.FindByUrns(null));
    }

    [Fact]
    public void Registry_rejects_duplicate_message_types() =>
        Assert.Throws<InvalidOperationException>(() => new ContractRegistry([typeof(OrderSubmitted), typeof(OrderSubmitted)]));
}

public class ContractValidatorTests
{
    internal static OrderSubmitted ValidOrder() => new()
    {
        OrderId = Guid.NewGuid(),
        CustomerEmail = "ana@example.ge",
        Lines = [new OrderLine { Sku = "ABC-123", Quantity = 2, UnitPrice = 9.99m }],
    };

    [Fact]
    public void Valid_message_has_no_errors() =>
        Assert.Empty(ContractValidator.Validate(ValidOrder()));

    [Fact]
    public void Errors_use_json_paths_including_nested_collections()
    {
        var order = ValidOrder() with
        {
            CustomerEmail = "not-an-email",
            Lines = [new OrderLine { Sku = "abc 123", Quantity = 0, UnitPrice = 1m }],
        };

        var errors = ContractValidator.Validate(order);

        Assert.Contains(errors, e => e.StartsWith("customerEmail:"));
        Assert.Contains(errors, e => e.StartsWith("lines[0].quantity:"));
        Assert.Contains(errors, e => e.StartsWith("lines[0].sku:"));
        Assert.DoesNotContain(errors, e => e.StartsWith("lines[0].unitPrice:"));
    }

    [Fact]
    public void Empty_collection_violates_MinLength()
    {
        var errors = ContractValidator.Validate(ValidOrder() with { Lines = [] });
        Assert.Contains(errors, e => e.StartsWith("lines:"));
    }

    [Fact]
    public void Decimal_range_limits_parse_under_a_comma_decimal_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ka-GE");
        try
        {
            Assert.Empty(ContractValidator.Validate(ValidOrder()));

            var errors = ContractValidator.Validate(ValidOrder() with
            {
                Lines = [new OrderLine { Sku = "ABC-123", Quantity = 1, UnitPrice = 0m }],
            });
            Assert.Contains(errors, e => e.StartsWith("lines[0].unitPrice:") && e.Contains("0.01"));
            Assert.Equal("ka-GE", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Null_message_is_an_error() =>
        Assert.Equal(new[] { "message: is required." }, ContractValidator.Validate(null));
}

public class ServerVersionTests
{
    [Theory]
    [InlineData("1.2.3+0a1b2c3d", "1.2.3")]
    [InlineData("0.2.0-rc.1+abc", "0.2.0-rc.1")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData(null, "0.0.0")]
    [InlineData("", "0.0.0")]
    public void Build_metadata_is_stripped(string? informational, string expected) =>
        Assert.Equal(expected, RabbitMcp.Server.ServerVersion.FromInformational(informational));

    [Fact]
    public void Current_version_is_semver_like() =>
        Assert.Matches(@"^\d+\.\d+\.\d+", RabbitMcp.Server.ServerVersion.Current);
}
