// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

/// <summary>Large contract sets: list_contracts and contract_consumers filter and cap what they return.</summary>
public sealed class ListingTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);

    [Fact]
    public void Contracts_are_found_by_name_tool_type_or_urn_case_insensitively()
    {
        Assert.Equal(["OrderSubmitted"], Names(BrokerTools.Select(Registry.Contracts, "submitted", 10).Selected));
        Assert.Equal(["GetOrderStatus"], Names(BrokerTools.Select(Registry.Contracts, "request_get_order", 10).Selected));
        var byUrn = BrokerTools.Select(Registry.Contracts, "urn:message:RabbitMcp.Samples", 10);
        Assert.Equal(Registry.Contracts.Count, byUrn.Matched);
    }

    [Fact]
    public void The_limit_caps_the_result_and_reports_how_many_matched()
    {
        var (selected, matched) = BrokerTools.Select(Registry.Contracts, null, 1);
        Assert.Single(selected);
        Assert.Equal(Registry.Contracts.Count, matched);
        Assert.True(matched > 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BrokerTools.MaxContractLimit + 1)]
    public void An_out_of_range_limit_is_refused(int limit) =>
        Assert.Throws<McpException>(() => BrokerTools.Select(Registry.Contracts, null, limit));

    private static string[] Names(IEnumerable<ContractDescriptor> contracts) => [.. contracts.Select(c => c.Name)];
}
