// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

public class PublishPolicyTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);
    private static ContractDescriptor C<T>() => Registry.Get(typeof(T));

    [Fact]
    public void Empty_policy_allows_everything()
    {
        Assert.False(PublishPolicy.AllowAll.IsRestricted);
        Assert.All(Registry.Contracts, c => Assert.True(PublishPolicy.AllowAll.IsAllowed(c)));
    }

    [Theory]
    [InlineData("Order*")]                                       // contract name
    [InlineData("publish_order_*")]                              // tool name
    [InlineData("RabbitMcp.Samples.Orders.*")]                 // full type name
    [InlineData("urn:message:RabbitMcp.Samples.Orders:*")]     // URN
    [InlineData("orderSUBMITTED")]                               // case-insensitive, exact
    public void Allow_patterns_match_name_tool_type_or_urn(string pattern) =>
        Assert.True(new PublishPolicy(pattern, null).IsAllowed(C<OrderSubmitted>()));

    [Fact]
    public void Allow_list_excludes_everything_else()
    {
        var policy = new PublishPolicy("OrderSubmitted", null);
        Assert.True(policy.IsAllowed(C<OrderSubmitted>()));
        Assert.False(policy.IsAllowed(C<OrderCancelled>()));
        Assert.False(policy.IsAllowed(C<GetOrderStatus>()));
    }

    [Fact]
    public void Deny_wins_over_allow()
    {
        var policy = new PublishPolicy("Order*; GetOrderStatus", "OrderCancelled");
        Assert.True(policy.IsAllowed(C<OrderSubmitted>()));
        Assert.False(policy.IsAllowed(C<OrderCancelled>()));
        Assert.True(policy.IsAllowed(C<GetOrderStatus>()));
    }

    [Fact]
    public void Pattern_without_wildcard_is_not_a_substring_match() =>
        Assert.False(new PublishPolicy("Order", null).IsAllowed(C<OrderSubmitted>()));

    [Fact]
    public void Unmatched_patterns_are_reported()
    {
        var policy = new PublishPolicy("Order*; Ordr*", "Paymnt*");
        Assert.Equal(new[] { "Ordr*", "Paymnt*" }, policy.UnmatchedPatterns(Registry.Contracts));
    }

    [Fact]
    public void Denied_contracts_get_no_tool()
    {
        var names = ContractToolFactory.CreateTools(Registry, new PublishPolicy(null, "request_*"))
            .Select(t => t.ProtocolTool.Name);
        Assert.Equal(new[] { "publish_order_cancelled", "publish_order_submitted" }, names);
    }

    [Fact]
    public void McpServer_parameter_is_injected_not_part_of_the_schema()
    {
        foreach (var tool in ContractToolFactory.CreateTools(Registry))
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("server", out _), tool.ProtocolTool.Name);
    }
}

public class AuditLogTests
{
    private static AuditLog Create(string? path, bool payload = false) =>
        new(Options.Create(new RabbitMcpOptions { AuditLogPath = path, AuditIncludePayload = payload }),
            new FixedTime(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)), NullLogger<AuditLog>.Instance);

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"rabbit-mcp-audit-{Guid.NewGuid():N}", "audit.jsonl");

    [Fact]
    public void Events_are_appended_as_json_lines_without_payload_by_default()
    {
        var path = TempFile();
        var audit = Create(path);
        audit.EnsureWritable();

        audit.Write(audit.Event(AuditActions.Publish, "sent", "Claude Code 2.1", "OrderSubmitted", "ex",
            messageId: "m-1", message: new { secret = "x" }));
        audit.Write(audit.Event(AuditActions.Republish, "failed", null, queue: "q_error", detail: "no route"));

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var e = first.RootElement;
        Assert.Equal("publish", e.GetProperty("action").GetString());
        Assert.Equal("sent", e.GetProperty("outcome").GetString());
        Assert.Equal("Claude Code 2.1", e.GetProperty("client").GetString());
        Assert.Equal("m-1", e.GetProperty("messageId").GetString());
        Assert.Equal("2026-10-13T12:00:00+00:00", e.GetProperty("timestamp").GetString());
        Assert.False(e.TryGetProperty("message", out _));
        Assert.False(e.TryGetProperty("requestId", out _)); // nulls are omitted
    }

    [Fact]
    public void Payload_is_included_only_when_enabled()
    {
        var path = TempFile();
        var audit = Create(path, payload: true);
        audit.EnsureWritable();

        audit.Write(audit.Event(AuditActions.Publish, "sent", null, message: new OrderCancelled { OrderId = Guid.Empty, Reason = "why" }));

        using var doc = JsonDocument.Parse(File.ReadAllLines(path).Single());
        Assert.Equal("why", doc.RootElement.GetProperty("message").GetProperty("reason").GetString());
    }

    [Fact]
    public void Without_a_path_nothing_is_written_to_disk()
    {
        var audit = Create(null);
        Assert.Null(audit.FilePath);
        audit.EnsureWritable();
        audit.Write(audit.Event(AuditActions.Publish, "sent", null)); // logs only, no exception
    }

    [Fact]
    public void Unwritable_path_fails_fast()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"rabbit-mcp-file-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file, not a directory");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Create(Path.Combine(blocker, "audit.jsonl")).EnsureWritable());
            Assert.Contains("AuditLogPath", ex.Message);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Client_is_null_outside_an_mcp_call() => Assert.Null(AuditLog.ClientOf(null));
}
