// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// declare_exchange / declare_queue: create an exchange or queue if it is missing, optionally with one binding.
/// Never deletes or changes what exists: an existing entity is only re-declared with the requested settings on a
/// throwaway channel, which is a no-op when they match and a 406 (reported, nothing changed) when they do not.
/// Previews (confirm=false) only use passive declares. Every executed or refused call is audited.
/// </summary>
public sealed class TopologyManager(RabbitConnection connection, TopologyPolicy policy, AuditLog audit, ILogger<TopologyManager> logger)
{
    public async Task<DeclareResult> DeclareExchangeAsync(
        string name, string? type, bool durable, bool autoDelete, IDictionary<string, JsonElement>? arguments,
        string? bindToExchange, string? routingKey, bool confirm, string? client, CancellationToken ct)
    {
        ExchangeSpec spec;
        try
        {
            spec = policy.Exchange(name, type, durable, autoDelete, arguments, bindToExchange, routingKey);
        }
        catch (McpException ex) when (confirm)
        {
            audit.Write(audit.Event(AuditActions.Declare, "refused", client, exchange: name, detail: ex.Message));
            throw;
        }

        var notes = new List<string>();
        try
        {
            var exists = await ExchangeExistsNowAsync(spec.Name, ct);
            if (spec.BindToExchange is { } source)
                await RequireExchangeAsync(source, "bindToExchange", ct);
            if (policy.IsMassTransitExchange(spec.Name))
                notes.Add("This is a MassTransit contract exchange: only MassTransit's settings are used, so its services can still declare it.");

            if (!confirm)
                return Result(spec, executed: false, exists ? "exists" : "would-create", notes, bindingNote: "would bind");

            var outcome = "created";
            if (exists)
                outcome = await SameExchangeAsync(spec, ct) ? "existed" : "existed-different";
            else
            {
                await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
                await channel.ExchangeDeclareAsync(spec.Name, spec.Type, spec.Durable, spec.AutoDelete,
                    Amqp(spec.Arguments), cancellationToken: ct);
                logger.LogInformation("Created exchange {Exchange} ({Type})", AuditLog.OneLine(spec.Name), spec.Type);
            }
            if (outcome == "existed-different")
                notes.Add("The exchange already exists with other settings (type, durability or auto-delete). It was left as it is.");

            if (spec.BindToExchange is { } from)
            {
                await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
                await channel.ExchangeBindAsync(destination: spec.Name, source: from, routingKey: spec.RoutingKey, cancellationToken: ct);
            }

            audit.Write(audit.Event(AuditActions.Declare, outcome, client, exchange: spec.Name, detail: Detail(spec)));
            return Result(spec, executed: true, outcome, notes, bindingNote: "bound");
        }
        catch (Exception ex) when (confirm && ex is not OperationCanceledException)
        {
            audit.Write(audit.Event(AuditActions.Declare, ex is McpException ? "refused" : "error", client,
                exchange: spec.Name, detail: BrokerErrors.ToMcp(ex).Message));
            throw BrokerErrors.ToMcp(ex);
        }
    }

    public async Task<DeclareResult> DeclareQueueAsync(
        string name, string? queueType, bool durable, IDictionary<string, JsonElement>? arguments,
        string? bindExchange, string? routingKey, bool confirm, string? client, CancellationToken ct)
    {
        QueueSpec spec;
        try
        {
            spec = policy.Queue(name, queueType, durable, arguments, bindExchange, routingKey);
        }
        catch (McpException ex) when (confirm)
        {
            audit.Write(audit.Event(AuditActions.Declare, "refused", client, queue: name, detail: ex.Message));
            throw;
        }

        var notes = new List<string>();
        try
        {
            var state = await QueueStateAsync(spec.Name, ct);
            if (spec.BindExchange is { } source)
                await RequireExchangeAsync(source, "bindExchange", ct);
            if (spec.BindExchange is null)
                notes.Add("Without bindExchange the queue only receives messages sent to it by name (default exchange).");

            if (!confirm)
                return Result(spec, executed: false, state == QueueState.Missing ? "would-create" : "exists", notes, bindingNote: "would bind");

            string outcome;
            switch (state)
            {
                case QueueState.Missing:
                    await using (var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct))
                        await channel.QueueDeclareAsync(spec.Name, spec.Durable, exclusive: false, autoDelete: false,
                            Amqp(spec.Arguments), cancellationToken: ct);
                    logger.LogInformation("Created queue {Queue} ({Type})", AuditLog.OneLine(spec.Name), spec.QueueType);
                    outcome = "created";
                    break;
                case QueueState.Exclusive:
                    notes.Add("The queue exists and is exclusive to another connection; its settings were not checked.");
                    outcome = "existed";
                    break;
                default:
                    outcome = await SameQueueAsync(spec, ct) ? "existed" : "existed-different";
                    if (outcome == "existed-different")
                        notes.Add("The queue already exists with other settings (durability, type or arguments). It was left as it is.");
                    break;
            }

            if (spec.BindExchange is { } from)
            {
                await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
                await channel.QueueBindAsync(spec.Name, from, spec.RoutingKey, cancellationToken: ct);
            }

            audit.Write(audit.Event(AuditActions.Declare, outcome, client, exchange: spec.BindExchange, queue: spec.Name, detail: Detail(spec)));
            return Result(spec, executed: true, outcome, notes, bindingNote: "bound");
        }
        catch (Exception ex) when (confirm && ex is not OperationCanceledException)
        {
            audit.Write(audit.Event(AuditActions.Declare, ex is McpException ? "refused" : "error", client,
                exchange: spec.BindExchange, queue: spec.Name, detail: BrokerErrors.ToMcp(ex).Message));
            throw BrokerErrors.ToMcp(ex);
        }
    }

    private enum QueueState { Missing, Exists, Exclusive }

    private async Task<QueueState> QueueStateAsync(string queue, CancellationToken ct)
    {
        try
        {
            return await connection.GetQueueAsync(queue, ct) is null ? QueueState.Missing : QueueState.Exists;
        }
        catch (OperationInterruptedException ex) when (RabbitConnection.IsResourceLocked(ex))
        {
            return QueueState.Exclusive;
        }
    }

    // Not ExchangeExistsAsync: its cached "exists" could hide an exchange deleted since, which the re-declare below
    // would then silently create.
    private async Task<bool> ExchangeExistsNowAsync(string exchange, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclarePassiveAsync(exchange, ct);
            return true;
        }
        catch (OperationInterruptedException ex) when (RabbitConnection.IsNotFound(ex))
        {
            return false;
        }
    }

    private async Task RequireExchangeAsync(string exchange, string argument, CancellationToken ct)
    {
        if (!await connection.ExchangeExistsAsync(exchange, ct))
            throw new McpException($"{argument}: the exchange '{exchange}' does not exist. Create it first (declare_exchange); nothing was changed.");
    }

    // Re-declaring with identical settings is a no-op; different settings fail with 406 and close only this channel.
    private async Task<bool> SameExchangeAsync(ExchangeSpec spec, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclareAsync(spec.Name, spec.Type, spec.Durable, spec.AutoDelete, Amqp(spec.Arguments), cancellationToken: ct);
            return true;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
        {
            return false;
        }
    }

    private async Task<bool> SameQueueAsync(QueueSpec spec, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.QueueDeclareAsync(spec.Name, spec.Durable, exclusive: false, autoDelete: false, Amqp(spec.Arguments), cancellationToken: ct);
            return true;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
        {
            return false;
        }
    }

    private static Dictionary<string, object?>? Amqp(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Count == 0 ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal);

    private static string Detail(ExchangeSpec s) =>
        $"exchange {s.Type} durable={s.Durable} autoDelete={s.AutoDelete}{Args(s.Arguments)}" +
        (s.BindToExchange is null ? "" : $"; bound to {s.BindToExchange} key '{s.RoutingKey}'");

    private static string Detail(QueueSpec s) =>
        $"queue {s.QueueType} durable={s.Durable}{Args(s.Arguments)}" +
        (s.BindExchange is null ? "" : $"; bound to {s.BindExchange} key '{s.RoutingKey}'");

    private static string Args(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Count == 0 ? "" : " " + string.Join(" ", arguments.Select(a => $"{a.Key}={a.Value}"));

    private static IReadOnlyDictionary<string, string> Shown(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.ToDictionary(a => a.Key, a => a.Value switch { bool b => b ? "true" : "false", _ => a.Value?.ToString() ?? "" },
            StringComparer.Ordinal);

    private static DeclareResult Result(ExchangeSpec s, bool executed, string outcome, List<string> notes, string bindingNote)
    {
        if (s.BindToExchange is { } source)
            notes.Add($"{bindingNote}: {source} → {s.Name}" + (s.RoutingKey.Length == 0 ? "" : $" (routing key '{s.RoutingKey}')"));
        return new DeclareResult("exchange", s.Name, s.Type, s.Durable, s.AutoDelete, Shown(s.Arguments),
            s.BindToExchange, s.BindToExchange is null ? null : s.RoutingKey, executed, outcome, notes);
    }

    private static DeclareResult Result(QueueSpec s, bool executed, string outcome, List<string> notes, string bindingNote)
    {
        if (s.BindExchange is { } source)
            notes.Add($"{bindingNote}: {source} → {s.Name}" + (s.RoutingKey.Length == 0 ? "" : $" (routing key '{s.RoutingKey}')"));
        return new DeclareResult("queue", s.Name, s.QueueType, s.Durable, AutoDelete: false, Shown(s.Arguments),
            s.BindExchange, s.BindExchange is null ? null : s.RoutingKey, executed, outcome, notes);
    }
}
