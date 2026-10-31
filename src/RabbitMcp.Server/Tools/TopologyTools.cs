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
    private const string TemplateHelp =
        "Optional name of an operator-defined template (RabbitMcp:TopologyTemplates) with the settings to use; values " +
        "passed in this call win over the template's. An unknown name lists the available ones.";

    [McpServerTool(Name = "declare_exchange", Title = "Create an exchange",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Creates an exchange if it does not exist, optionally bound to an existing source exchange. Never changes or " +
        "deletes an existing exchange: if it exists, the outcome is existed (same settings) or existed-different (left " +
        "as it is), and a binding is refused (unless RabbitMcp:TopologyRules has bind-existing). Exchanges of MassTransit contracts (Namespace:Type) and exchanges named " +
        "like an existing queue (MassTransit receive endpoints) can only be created with MassTransit's settings " +
        "(fanout, durable, no arguments) so the services can still declare them. Do not pre-create the queues or " +
        "exchanges of a MassTransit service that has not started yet: it declares them itself. " +
        "Two steps: call without confirm to preview, then with confirm=true.")]
    public Task<DeclareResult> DeclareExchangeAsync(
        [Description("Exchange name, e.g. billing.events. Names starting with amq. are reserved.")] string name,
        [Description(TemplateHelp)] string? template = null,
        [Description("fanout (default), direct, topic or headers.")] string? type = null,
        [Description("Survives a broker restart (default true).")] bool? durable = null,
        [Description("RabbitMQ deletes the exchange when its last binding is removed (default false).")] bool? autoDelete = null,
        [Description(ArgumentsHelp)] Dictionary<string, JsonElement>? arguments = null,
        [Description("Also bind this exchange to an existing source exchange, so it receives the source's messages.")] string? bindToExchange = null,
        [Description(RoutingKeyHelp)] string? routingKey = null,
        [Description(ConfirmHelp)] bool confirm = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default) =>
        topology.DeclareExchangeAsync(name, template, type, durable, autoDelete, arguments, bindToExchange, routingKey, confirm,
            AuditLog.ClientOf(server, user), cancellationToken);

    [McpServerTool(Name = "declare_queue", Title = "Create a queue",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Creates a queue if it does not exist, optionally bound to an existing exchange so it receives its messages. " +
        "Never changes, purges or deletes an existing queue: if it exists, the outcome is existed (same settings) or " +
        "existed-different (left as it is), and a binding is refused (unless RabbitMcp:TopologyRules has bind-existing). Queues are always durable, never exclusive or auto-delete. " +
        "Quorum queues need x-dead-letter-exchange (an existing exchange): RabbitMQ drops their messages after 20 " +
        "failed deliveries otherwise. " +
        "Two steps: call without confirm to preview, then with confirm=true.")]
    public Task<DeclareResult> DeclareQueueAsync(
        [Description("Queue name, e.g. billing-audit. Names starting with amq. are reserved.")] string name,
        [Description(TemplateHelp)] string? template = null,
        [Description("classic (default), quorum or stream.")] string? queueType = null,
        [Description("Must stay true (the default): transient queues lose their messages on restart and RabbitMQ 4 refuses them.")] bool? durable = null,
        [Description(ArgumentsHelp)] Dictionary<string, JsonElement>? arguments = null,
        [Description("Bind the queue to this existing exchange, e.g. a contract's Namespace:Type exchange.")] string? bindExchange = null,
        [Description(RoutingKeyHelp)] string? routingKey = null,
        [Description(ConfirmHelp)] bool confirm = false,
        McpServer? server = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default) =>
        topology.DeclareQueueAsync(name, template, queueType, durable, arguments, bindExchange, routingKey, confirm,
            AuditLog.ClientOf(server, user), cancellationToken);
}
