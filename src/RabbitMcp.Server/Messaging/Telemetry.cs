// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Traces and metrics. Uses only System.Diagnostics, so it costs nothing unless a listener (OpenTelemetry,
/// a test) subscribes to the "RabbitMcp" source/meter. Every message the server sends carries its span
/// context in two headers: W3C <c>traceparent</c>/<c>tracestate</c> for standard consumers, and
/// <c>MT-Activity-Id</c>, which is what MassTransit 8 reads from the AMQP headers to parent its consume span.
/// </summary>
public static class Telemetry
{
    public const string Name = "RabbitMcp";

    /// <summary>ActivitySource/Meter name of the MCP C# SDK (tool-call spans and metrics).</summary>
    public const string McpSdkName = "Experimental.ModelContextProtocol";

    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";
    public const string MassTransitActivityIdHeader = "MT-Activity-Id";

    public static readonly ActivitySource Source = new(Name, ServerVersion.Current);
    public static readonly Meter Meter = new(Name, ServerVersion.Current);

    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "rabbitmcp.operations", unit: "{operation}",
        description: "publish / request / republish attempts by action and outcome.");

    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "rabbitmcp.request.duration", unit: "s",
        description: "Time from sending a request until its reply, fault or timeout.");

    /// <summary>
    /// Starts a producer/client span named like the messaging conventions ("send {destination}").
    /// Returns null when nobody listens — callers must tolerate that.
    /// </summary>
    public static Activity? StartSend(
        string operationName, string destination, ActivityKind kind = ActivityKind.Producer,
        ActivityContext? linkTo = null)
    {
        var links = linkTo is { } link && link != default ? new[] { new ActivityLink(link) } : null;
        var activity = Source.StartActivity($"{operationName} {destination}", kind, parentContext: default, links: links);
        if (activity is null)
            return null;

        activity.SetTag("messaging.system", "rabbitmq");
        activity.SetTag("messaging.operation.type", "send");
        activity.SetTag("messaging.operation.name", operationName);
        activity.SetTag("messaging.destination.name", destination);
        return activity;
    }

    /// <summary>Writes the span context into AMQP headers (no-op without an activity).</summary>
    public static void Inject(Activity? activity, IDictionary<string, object?> headers)
    {
        if (activity?.Id is not { } id || activity.IdFormat != ActivityIdFormat.W3C)
            return;

        headers[TraceParentHeader] = id;
        headers[MassTransitActivityIdHeader] = id;
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            headers[TraceStateHeader] = activity.TraceStateString;
        else
            headers.Remove(TraceStateHeader);
    }

    /// <summary>Reads a span context written by us, MassTransit or any W3C producer (string or byte[] values).</summary>
    public static ActivityContext? Extract(IDictionary<string, object?>? headers)
    {
        var parent = FaultHeaders.GetString(headers, TraceParentHeader) ?? FaultHeaders.GetString(headers, MassTransitActivityIdHeader);
        var state = FaultHeaders.GetString(headers, TraceStateHeader);
        return parent is not null && ActivityContext.TryParse(parent, state, isRemote: true, out var context) ? context : null;
    }

    /// <summary>Marks a span failed with a short reason.</summary>
    public static void Fail(Activity? activity, string reason) =>
        activity?.SetStatus(ActivityStatusCode.Error, reason);

    /// <summary>One counter point per audit event — so metrics and audit can never disagree.</summary>
    public static void Count(AuditEvent e) =>
        Operations.Add(1,
            new KeyValuePair<string, object?>("action", e.Action),
            new KeyValuePair<string, object?>("outcome", e.Outcome),
            new KeyValuePair<string, object?>("contract", e.Contract ?? e.Queue ?? ""));

    public static void RecordRequestDuration(TimeSpan elapsed, string contract, string outcome) =>
        RequestDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("contract", contract),
            new KeyValuePair<string, object?>("outcome", outcome));
}
