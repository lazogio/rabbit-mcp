// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMcp.Contracts.Orders;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

/// <summary>What the model sees in tools/list for the generated publish tools.</summary>
public class ToolSchemaTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);

    private static IEnumerable<string> Names(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);

    [Fact]
    public void One_tool_per_contract_publish_for_events_request_for_requests()
    {
        var names = ContractToolFactory.CreateTools(Registry).Select(t => t.ProtocolTool.Name);

        Assert.Equal(new[] { "request_get_order_status", "publish_order_cancelled", "publish_order_submitted" }, names);
    }

    [Fact]
    public void Request_tool_takes_timeout_and_returns_request_result()
    {
        var tool = ContractToolFactory.CreateTool(Registry.Get(typeof(GetOrderStatus))).ProtocolTool;
        var props = tool.InputSchema.GetProperty("properties");

        Assert.True(props.TryGetProperty("timeoutSeconds", out _));
        Assert.True(props.GetProperty("message").GetProperty("properties").TryGetProperty("orderId", out _));
        Assert.DoesNotContain("timeoutSeconds", Names(tool.InputSchema.GetProperty("required")));
        Assert.True(tool.OutputSchema!.Value.GetProperty("properties").TryGetProperty("status", out _));
        Assert.Contains("OrderStatus, OrderNotFound", tool.Description);
    }

    [Fact]
    public void Input_schema_is_the_contract_schema()
    {
        var tool = ContractToolFactory.CreateTool(Registry.Get(typeof(OrderSubmitted))).ProtocolTool;
        var schema = tool.InputSchema;

        Assert.Contains("message", Names(schema.GetProperty("required")));
        Assert.DoesNotContain("correlationId", Names(schema.GetProperty("required")));

        var message = schema.GetProperty("properties").GetProperty("message");
        var props = message.GetProperty("properties");
        Assert.True(props.TryGetProperty("orderId", out _));
        Assert.True(props.TryGetProperty("customerEmail", out _));
        Assert.True(props.TryGetProperty("lines", out var lines));
        Assert.Contains("orderId", Names(message.GetProperty("required")));
        Assert.Equal("Customer e-mail address.", props.GetProperty("customerEmail").GetProperty("description").GetString());
        Assert.True(lines.GetProperty("items").GetProperty("properties").TryGetProperty("quantity", out _));

        // CancellationToken is bound by the SDK, never part of the schema.
        Assert.False(schema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public void Output_schema_and_annotations_are_advertised()
    {
        var tool = ContractToolFactory.CreateTool(Registry.Get(typeof(OrderCancelled))).ProtocolTool;

        Assert.NotNull(tool.OutputSchema);
        Assert.True(tool.OutputSchema.Value.GetProperty("properties").TryGetProperty("messageId", out _));
        Assert.False(tool.Annotations?.ReadOnlyHint ?? true);
        Assert.False(tool.Annotations?.DestructiveHint ?? true);
        Assert.Contains("urn:message:RabbitMcp.Contracts.Orders:OrderCancelled", tool.Description);
    }

    [Fact]
    public void Tool_arguments_reject_null_for_required_strings()
    {
        // ToolJson respects nullable annotations, so "reason": null fails before reaching the broker.
        var json = """{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","reason":null}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<OrderCancelled>(json, ToolJson.Options));
    }

    [Fact]
    public void Validate_reports_contract_rules_for_tool_json()
    {
        var contract = Registry.Get(typeof(OrderCancelled));
        using var doc = JsonDocument.Parse("""{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","reason":"no"}""");

        var errors = MessageInspector.Validate(contract, doc.RootElement, ToolJson.Options);

        Assert.Single(errors);
        Assert.StartsWith("reason:", errors[0]);
    }
}
