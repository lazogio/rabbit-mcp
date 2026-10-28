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
/// Never changes what exists: an existing entity gets no binding, and is only re-declared with the requested settings
/// on a throwaway channel — a no-op when they match, a 406 (reported, nothing changed) when they do not. Previews
/// (confirm=false) only use passive declares. Every executed or refused call is audited.
/// </summary>
public sealed class TopologyManager(RabbitConnection connection, TopologyPolicy policy, AuditLog audit, ILogger<TopologyManager> logger)
{
    private enum Equivalence { Same, Different, Unchecked }

    private enum QueueState { Missing, Exists, Exclusive }

    public async Task<DeclareResult> DeclareExchangeAsync(
        string name, string? type, bool durable, bool autoDelete, IDictionary<string, JsonElement>? arguments,
        string? bindToExchange, string? routingKey, bool confirm, string? client, CancellationToken ct)
    {
        ExchangeSpec? spec = null;
        try
        {
            spec = policy.Exchange(name, type, durable, autoDelete, arguments, bindToExchange, routingKey);
            var notes = new List<string>();

            // Not cached: a cached "exists" could hide an exchange deleted since, which the re-declare would recreate.
            var exists = await connection.ExchangeExistsAsync(spec.Name, ct, useCache: false);
            if (policy.IsMassTransitExchange(spec.Name))
                notes.Add("This is a MassTransit contract exchange: only MassTransit's settings are used, so its services can still declare it.");
            else if (await connection.QueueExistsAsync(spec.Name, ct))
                TopologyPolicy.RequireMassTransitSettings(spec,
                    $"A queue named '{spec.Name}' exists, and MassTransit pairs each receive endpoint queue with an exchange of the same name");
            await RequireReferencedExchangesAsync(spec.Arguments, ct);
            if (spec.BindToExchange is { } source)
                await RequireBindableAsync("exchange", spec.Name, exists, source, "bindToExchange", ct);

            if (!confirm)
                return Result(spec, executed: false, exists ? "exists" : "would-create", notes);

            string outcome;
            if (exists)
                outcome = Outcome(await SameExchangeAsync(spec, ct), "exchange", notes);
            else
            {
                await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
                await channel.ExchangeDeclareAsync(spec.Name, spec.Type, spec.Durable, spec.AutoDelete, Amqp(spec.Arguments), cancellationToken: ct);
                if (spec.BindToExchange is { } from)
                    await channel.ExchangeBindAsync(destination: spec.Name, source: from, routingKey: spec.RoutingKey, cancellationToken: ct);
                logger.LogInformation("Created exchange {Exchange} ({Type})", AuditLog.OneLine(spec.Name), spec.Type);
                outcome = "created";
            }

            audit.Write(audit.Event(AuditActions.Declare, outcome, client, exchange: spec.Name, detail: Detail(spec)));
            return Result(spec, executed: true, outcome, notes);
        }
        catch (Exception ex) when (confirm && ex is not OperationCanceledException)
        {
            var error = BrokerErrors.ToMcp(ex);
            audit.Write(audit.Event(AuditActions.Declare, ex is McpException ? "refused" : "error", client,
                exchange: spec?.Name ?? name, detail: error.Message));
            throw error;
        }
    }

    public async Task<DeclareResult> DeclareQueueAsync(
        string name, string? queueType, bool durable, IDictionary<string, JsonElement>? arguments,
        string? bindExchange, string? routingKey, bool confirm, string? client, CancellationToken ct)
    {
        QueueSpec? spec = null;
        try
        {
            spec = policy.Queue(name, queueType, durable, arguments, bindExchange, routingKey);
            var notes = new List<string>();

            var state = await QueueStateAsync(spec.Name, ct);
            await RequireReferencedExchangesAsync(spec.Arguments, ct);
            if (spec.BindExchange is { } source)
                await RequireBindableAsync("queue", spec.Name, state != QueueState.Missing, source, "bindExchange", ct);
            else
                notes.Add("Without bindExchange the queue only receives messages sent to it by name (default exchange).");

            if (!confirm)
                return Result(spec, executed: false, state == QueueState.Missing ? "would-create" : "exists", notes);

            string outcome;
            switch (state)
            {
                case QueueState.Missing:
                    await using (var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct))
                    {
                        await channel.QueueDeclareAsync(spec.Name, spec.Durable, exclusive: false, autoDelete: false,
                            Amqp(spec.Arguments), cancellationToken: ct);
                        if (spec.BindExchange is { } from)
                            await channel.QueueBindAsync(spec.Name, from, spec.RoutingKey, cancellationToken: ct);
                    }
                    logger.LogInformation("Created queue {Queue} ({Type})", AuditLog.OneLine(spec.Name), spec.QueueType);
                    outcome = "created";
                    break;
                case QueueState.Exclusive:
                    notes.Add("The queue exists and is exclusive to another connection; its settings were not checked.");
                    outcome = "existed";
                    break;
                default:
                    outcome = Outcome(await SameQueueAsync(spec, ct), "queue", notes);
                    break;
            }

            audit.Write(audit.Event(AuditActions.Declare, outcome, client, exchange: spec.BindExchange, queue: spec.Name, detail: Detail(spec)));
            return Result(spec, executed: true, outcome, notes);
        }
        catch (Exception ex) when (confirm && ex is not OperationCanceledException)
        {
            var error = BrokerErrors.ToMcp(ex);
            audit.Write(audit.Event(AuditActions.Declare, ex is McpException ? "refused" : "error", client,
                exchange: spec?.BindExchange, queue: spec?.Name ?? name, detail: error.Message));
            throw error;
        }
    }

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

    // A binding is part of creating something: adding one to an existing exchange or queue would change where its
    // messages go (e.g. a MassTransit endpoint suddenly receiving another exchange's messages).
    private async Task RequireBindableAsync(string kind, string name, bool exists, string source, string argument, CancellationToken ct)
    {
        if (exists)
            throw new McpException(
                $"The {kind} '{name}' already exists. declare_{kind} only binds what it creates, so existing routing is " +
                "never changed. Nothing was changed.");
        if (!await connection.ExchangeExistsAsync(source, ct))
            throw new McpException($"{argument}: the exchange '{source}' does not exist. Create it first (declare_exchange); nothing was changed.");
    }

    private async Task RequireReferencedExchangesAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        foreach (var (argument, exchange) in TopologyPolicy.ReferencedExchanges(arguments))
            if (!await connection.ExchangeExistsAsync(exchange, ct))
                throw new McpException(
                    $"{argument}: the exchange '{exchange}' does not exist, and RabbitMQ drops messages sent to a missing " +
                    "exchange. Create it first (declare_exchange); nothing was changed.");
    }

    // Re-declaring with identical settings is a no-op; different settings fail with 406 and close only this channel.
    // 403: the broker user may not configure it, so the settings cannot be compared (nothing needed creating anyway).
    private async Task<Equivalence> SameExchangeAsync(ExchangeSpec spec, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.ExchangeDeclareAsync(spec.Name, spec.Type, spec.Durable, spec.AutoDelete, Amqp(spec.Arguments), cancellationToken: ct);
            return Equivalence.Same;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode is 406 or 403)
        {
            return ex.ShutdownReason?.ReplyCode == 406 ? Equivalence.Different : Equivalence.Unchecked;
        }
    }

    private async Task<Equivalence> SameQueueAsync(QueueSpec spec, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
        try
        {
            await channel.QueueDeclareAsync(spec.Name, spec.Durable, exclusive: false, autoDelete: false, Amqp(spec.Arguments), cancellationToken: ct);
            return Equivalence.Same;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode is 406 or 403)
        {
            return ex.ShutdownReason?.ReplyCode == 406 ? Equivalence.Different : Equivalence.Unchecked;
        }
    }

    private static string Outcome(Equivalence equivalence, string kind, List<string> notes)
    {
        switch (equivalence)
        {
            case Equivalence.Different:
                notes.Add($"The {kind} already exists with other settings (type, durability or arguments). It was left as it is.");
                return "existed-different";
            case Equivalence.Unchecked:
                notes.Add($"The {kind} exists; its settings were not compared (the broker user may not configure it).");
                return "existed";
            default:
                return "existed";
        }
    }

    private static Dictionary<string, object?>? Amqp(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Count == 0 ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal);

    private static string Detail(ExchangeSpec s) =>
        $"exchange {s.Type} durable={s.Durable} autoDelete={s.AutoDelete}{Args(s.Arguments)}{Binding(s.BindToExchange, s.RoutingKey)}";

    private static string Detail(QueueSpec s) =>
        $"queue {s.QueueType} durable={s.Durable}{Args(s.Arguments)}{Binding(s.BindExchange, s.RoutingKey)}";

    private static string Args(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Count == 0 ? "" : " " + string.Join(" ", arguments.Select(a => $"{a.Key}={a.Value}"));

    private static string Binding(string? source, string routingKey) =>
        source is null ? "" : $"; bound to {source} key '{routingKey}'";

    private static DeclareResult Result(ExchangeSpec s, bool executed, string outcome, List<string> notes) =>
        Result("exchange", s.Name, s.Type, s.Durable, s.AutoDelete, s.Arguments, s.BindToExchange, s.RoutingKey, executed, outcome, notes);

    private static DeclareResult Result(QueueSpec s, bool executed, string outcome, List<string> notes) =>
        Result("queue", s.Name, s.QueueType, s.Durable, autoDelete: false, s.Arguments, s.BindExchange, s.RoutingKey, executed, outcome, notes);

    private static DeclareResult Result(string kind, string name, string type, bool durable, bool autoDelete,
        IReadOnlyDictionary<string, object?> arguments, string? source, string routingKey, bool executed, string outcome, List<string> notes)
    {
        if (source is not null)
            notes.Add($"{(executed ? "bound" : "would bind")}: {source} → {name}" + (routingKey.Length == 0 ? "" : $" (routing key '{routingKey}')"));
        var shown = arguments.ToDictionary(a => a.Key, a => a.Value switch { bool b => b ? "true" : "false", _ => a.Value?.ToString() ?? "" },
            StringComparer.Ordinal);
        return new DeclareResult(kind, name, type, durable, autoDelete, shown, source, source is null ? null : routingKey, executed, outcome, notes);
    }
}
