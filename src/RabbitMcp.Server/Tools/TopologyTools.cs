// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>Registered only when RabbitMcp:EnableTopology is true.</summary>
[McpServerToolType]
public sealed class TopologyTools(TopologyManager topology)
{
    private const string ConfirmHelp = "Set to true to execute. Without it, only a preview is returned (nothing changes).";
    private const string ArgumentsHelp =
        "Optional x-arguments, e.g. {\"x-dead-letter-exchange\":\"billing.dlx\"}. Only those allowed by " +
        "RabbitMcp:TopologyArguments; values are strings, whole numbers or true/false.";
    private const string RoutingKeyHelp = "Routing key of the binding (for direct/topic source exchanges). Default empty.";

    [McpServerTool(Name = "declare_exchange", Title = "Create an exchange",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Creates an exchange if it does not exist, optionally bound to an existing source exchange. Never changes or " +
        "deletes an existing exchange: if it exists, the outcome is existed (same settings) or existed-different (left " +
        "as it is). Exchanges of MassTransit contracts (Namespace:Type) can only be created with MassTransit's settings " +
        "(fanout, durable, no arguments) so the services can still declare them. " +
        "Two steps: call without confirm to preview, then with confirm=true.")]
    public Task<DeclareResult> DeclareExchangeAsync(
        [Description("Exchange name, e.g. billing.events. Names starting with amq. are reserved.")] string name,
        [Description("fanout (default), direct, topic or headers.")] string type = "fanout",
        [Description("Survives a broker restart (default true).")] bool durable = true,
        [Description("RabbitMQ deletes the exchange when its last binding is removed (default false).")] bool autoDelete = false,
        [Description(ArgumentsHelp)] Dictionary<string, JsonElement>? arguments = null,
        [Description("Also bind this exchange to an existing source exchange, so it receives the source's messages.")] string? bindToExchange = null,
        [Description(RoutingKeyHelp)] string? routingKey = null,
        [Description(ConfirmHelp)] bool confirm = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default) =>
        topology.DeclareExchangeAsync(name, type, durable, autoDelete, arguments, bindToExchange, routingKey, confirm,
            AuditLog.ClientOf(server, user), cancellationToken);

    [McpServerTool(Name = "declare_queue", Title = "Create a queue",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Creates a queue if it does not exist, optionally bound to an existing exchange so it receives its messages. " +
        "Never changes, purges or deletes an existing queue: if it exists, the outcome is existed (same settings) or " +
        "existed-different (left as it is). Queues are never exclusive or auto-delete. " +
        "Two steps: call without confirm to preview, then with confirm=true.")]
    public Task<DeclareResult> DeclareQueueAsync(
        [Description("Queue name, e.g. billing-audit. Names starting with amq. are reserved.")] string name,
        [Description("classic (default), quorum or stream. Quorum and stream queues are always durable.")] string queueType = "classic",
        [Description("Survives a broker restart (default true).")] bool durable = true,
        [Description(ArgumentsHelp)] Dictionary<string, JsonElement>? arguments = null,
        [Description("Bind the queue to this existing exchange, e.g. a contract's Namespace:Type exchange.")] string? bindExchange = null,
        [Description(RoutingKeyHelp)] string? routingKey = null,
        [Description(ConfirmHelp)] bool confirm = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default) =>
        topology.DeclareQueueAsync(name, queueType, durable, arguments, bindExchange, routingKey, confirm,
            AuditLog.ClientOf(server, user), cancellationToken);
}
