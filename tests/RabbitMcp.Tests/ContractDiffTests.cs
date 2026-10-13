// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using RabbitMcp.Contracts;
using RabbitMcp.Server.Cli;
using RabbitMcp.Server.Contracts;

namespace RabbitMcp.Tests.DiffV1
{
    [MessageContract("v1", Urn = "urn:message:Shop:OrderPlaced", Exchange = "Shop:OrderPlaced")]
    public sealed record OrderPlaced
    {
        public required Guid OrderId { get; init; }
        public required string Customer { get; init; }
        public string? Note { get; init; }
        public required List<Line> Lines { get; init; }
    }

    public sealed record Line
    {
        public required string Sku { get; init; }
        public int Quantity { get; init; }
    }

    [MessageContract("v1", Urn = "urn:message:Shop:GetPrice", Exchange = "Shop:GetPrice", Responses = new[] { typeof(Price), typeof(NoPrice) })]
    public sealed record GetPrice
    {
        public required string Sku { get; init; }
    }

    public sealed record Price { public decimal Amount { get; init; } }
    public sealed record NoPrice { public string? Reason { get; init; } }

    [MessageContract("v1", Urn = "urn:message:Shop:Retired", Exchange = "Shop:Retired")]
    public sealed record Retired { public int Id { get; init; } }
}

namespace RabbitMcp.Tests.DiffV2
{
    [MessageContract("v2", Urn = "urn:message:Shop:OrderPlaced", Exchange = "Shop:OrderPlaced")]
    public sealed record OrderPlaced
    {
        public required Guid OrderId { get; init; }
        public required int Customer { get; init; }          // string -> int: breaking
        public required string Channel { get; init; }        // new required: breaking
        public string? Coupon { get; init; }                 // new optional: info
        public required List<Line> Lines { get; init; }      // Note removed (optional): warning
    }

    public sealed record Line
    {
        public required string Sku { get; init; }
        public required int Quantity { get; init; }          // became required: breaking
    }

    [MessageContract("v2", Version = 2, Urn = "urn:message:Shop:GetPrice", Exchange = "Shop:GetPrice", Responses = new[] { typeof(Price), typeof(PriceOnRequest) })]
    public sealed record GetPrice
    {
        public required string Sku { get; init; }
    }

    public sealed record Price { public long Amount { get; init; } }     // decimal -> long: same JSON kind
    public sealed record PriceOnRequest { public string? Contact { get; init; } }

    [MessageContract("v2", Urn = "urn:message:Shop:Brand.New", Exchange = "Shop:BrandNew")]
    public sealed record BrandNew { public int Id { get; init; } }
}

namespace RabbitMcp.Tests
{
    public class ContractDiffTests
    {
        private static readonly ContractRegistry V1 = new(typeof(DiffV1.OrderPlaced).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(DiffV1.OrderPlaced).Namespace && t.GetCustomAttributes(typeof(MessageContractAttribute), false).Length > 0));
        private static readonly ContractRegistry V2 = new(typeof(DiffV2.OrderPlaced).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(DiffV2.OrderPlaced).Namespace && t.GetCustomAttributes(typeof(MessageContractAttribute), false).Length > 0));

        private static readonly IReadOnlyList<ContractChange> Changes = ContractDiff.Compare(V1, V2);

        private static ContractChange Find(string contract, string path) =>
            Assert.Single(Changes, c => c.Contract == contract && c.Path == path);

        [Fact]
        public void Same_registry_has_no_changes() => Assert.Empty(ContractDiff.Compare(V1, V1));

        [Fact]
        public void Shape_changes_are_classified()
        {
            Assert.Equal(ContractDiff.Breaking, Find("OrderPlaced", "customer").Severity);
            Assert.Equal(ContractDiff.Breaking, Find("OrderPlaced", "channel").Severity);
            Assert.Equal(ContractDiff.Info, Find("OrderPlaced", "coupon").Severity);
            Assert.Equal(ContractDiff.Warning, Find("OrderPlaced", "note").Severity);
            Assert.Equal(ContractDiff.Breaking, Find("OrderPlaced", "lines[].quantity").Severity);
            Assert.DoesNotContain(Changes, c => c.Path == "lines[].sku");
        }

        [Fact]
        public void Breaking_change_without_version_bump_is_flagged()
        {
            Assert.Contains(Changes, c => c.Contract == "OrderPlaced" && c.Message.Contains("without a version bump"));
            Assert.DoesNotContain(Changes, c => c.Contract == "GetPrice" && c.Message.Contains("without a version bump"));
        }

        [Fact]
        public void Reply_types_and_contracts_added_or_removed()
        {
            Assert.Equal(ContractDiff.Breaking, Find("GetPrice", "→NoPrice").Severity);
            Assert.Equal(ContractDiff.Warning, Find("GetPrice", "→PriceOnRequest").Severity);
            Assert.DoesNotContain(Changes, c => c.Path == "→Price.amount"); // decimal -> long: both JSON numbers
            Assert.Equal(ContractDiff.Breaking, Find("Retired", "").Severity);
            Assert.Equal(ContractDiff.Info, Find("BrandNew", "").Severity);
        }

        [Fact]
        public void Cli_exit_codes_and_usage()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(2, ContractsCommand.Run(["diff", "only-one.dll"], output, error));
            Assert.Contains("usage", error.ToString());

            Assert.Equal(2, ContractsCommand.Run(["diff", "missing-a.dll", "missing-b.dll"], output, error));
            Assert.Contains("does not exist", error.ToString());
        }

        [Fact]
        public void Cli_compares_a_real_contracts_dll_in_isolated_contexts()
        {
            var dll = ContractAssemblyLoaderTests.ExternalContractsDll();
            var output = new StringWriter();

            Assert.Equal(0, ContractsCommand.Run(["diff", dll, dll], output, new StringWriter()));
            Assert.Contains("No contract changes.", output.ToString());

            var json = new StringWriter();
            Assert.Equal(0, ContractsCommand.Run(["diff", dll, dll, "--json"], json, new StringWriter()));
            Assert.Contains("\"compatible\": true", json.ToString());
        }
    }
}
