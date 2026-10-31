// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Tests;

[Collection("telemetry")] // listeners are process-wide: keep these tests sequential
public class TelemetryTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private static ActivityListener Listen(List<Activity>? stopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => stopped?.Add(a),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void Inject_writes_w3c_and_MassTransit_headers()
    {
        using var listener = Listen();
        using var activity = Telemetry.StartSend("publish", "Ns:Type")!;
        activity.TraceStateString = "vendor=1";
        var headers = new Dictionary<string, object?>();

        Telemetry.Inject(activity, headers);

        Assert.Equal(activity.Id, headers["traceparent"]);
        Assert.Equal(activity.Id, headers["MT-Activity-Id"]);
        Assert.Equal("vendor=1", headers["tracestate"]);
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$", activity.Id);
    }

    [Fact]
    public void Inject_without_activity_leaves_headers_untouched()
    {
        var headers = new Dictionary<string, object?> { ["traceparent"] = TraceParent };
        Telemetry.Inject(null, headers);
        Assert.Equal(TraceParent, headers["traceparent"]);
    }

    [Fact]
    public void Extract_reads_amqp_byte_arrays_and_falls_back_to_MassTransit_header()
    {
        var fromTraceParent = Telemetry.Extract(new Dictionary<string, object?> { ["traceparent"] = Encoding.UTF8.GetBytes(TraceParent) });
        var fromMassTransit = Telemetry.Extract(new Dictionary<string, object?> { ["MT-Activity-Id"] = TraceParent });

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", fromTraceParent!.Value.TraceId.ToHexString());
        Assert.True(fromTraceParent.Value.IsRemote);
        Assert.Equal(fromTraceParent, fromMassTransit);
        Assert.Null(Telemetry.Extract(new Dictionary<string, object?> { ["traceparent"] = "garbage" }));
        Assert.Null(Telemetry.Extract(null));
    }

    [Fact]
    public void Send_span_has_messaging_tags_parent_and_link()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        var link = Telemetry.Extract(new Dictionary<string, object?> { ["traceparent"] = TraceParent });

        using (var parent = Telemetry.Source.StartActivity("tool call"))
        using (Telemetry.StartSend("republish", "orders", linkTo: link)) { }

        var span = stopped.Single(a => a.DisplayName == "republish orders");
        var parentSpan = stopped.Single(a => a.DisplayName == "tool call");
        Assert.Equal(ActivityKind.Producer, span.Kind);
        Assert.Equal(parentSpan.SpanId, span.ParentSpanId);
        Assert.Equal("rabbitmq", span.GetTagItem("messaging.system"));
        Assert.Equal("orders", span.GetTagItem("messaging.destination.name"));
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", Assert.Single(span.Links).Context.TraceId.ToHexString());
    }

    [Fact]
    public void Every_audit_event_is_counted()
    {
        // Thread-safe: the listener also sees audit events of tests running in parallel in other classes.
        var points = new ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "rabbitmcp.operations")
                l.EnableMeasurementEvents(instrument);
        };
        meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            points.Enqueue((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        meters.Start();

        Telemetry.Count(new AuditEvent(DateTimeOffset.UtcNow, "publish", "sent", "c", Contract: "OrderSubmitted"));
        Telemetry.Count(new AuditEvent(DateTimeOffset.UtcNow, "republish", "failed", null, Queue: "q_error"));

        Assert.Contains(points, p => p.Value == 1 && (string?)p.Tags["action"] == "publish" && (string?)p.Tags["contract"] == "OrderSubmitted");
        Assert.Contains(points, p => (string?)p.Tags["outcome"] == "failed" && (string?)p.Tags["contract"] == "q_error");
    }
}
