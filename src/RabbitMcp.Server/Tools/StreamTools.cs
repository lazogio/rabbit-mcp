// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>RabbitMQ streams: always registered (read only).</summary>
[McpServerToolType]
public sealed class StreamTools(QueueBrowser browser, IServiceProvider services)
{
    [McpServerTool(Name = "read_stream", Title = "Read a RabbitMQ stream",
        ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(
        "Reads messages of a RabbitMQ stream (x-queue-type=stream) from a position: first, last, next, an offset, or a " +
        "time such as 10m (\"the last 10 minutes\") or an ISO 8601 timestamp. Reading a stream removes nothing and does not " +
        "disturb its consumers. Returns payloads, contract checks and offsets; continue with from=LastOffset+1.")]
    public async Task<StreamReadResult> ReadStreamAsync(
        [Description("Stream name.")] string queue,
        [Description("first (default), last, next, an offset, or a time: 10m, 2h, 7d, 2026-10-18T09:00:00Z.")] string? from = null,
        [Description("How many messages (default 20).")] int count = 20,
        [Description("Read even when it cannot be confirmed to be a stream (no ManagementUri) and it has consumers.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        // The Management API knows the queue type; without it the first delivery tells.
        bool? isStream = null;
        if (services.GetService<ManagementClient>() is { } management)
        {
            try
            {
                if (await management.GetQueueAsync(queue, cancellationToken) is { Type: { } type })
                    isStream = type == "stream";
            }
            catch (McpException)
            {
                // Down or refused: reading needs only AMQP.
            }
        }
        return await browser.ReadStreamAsync(queue, from, count, isStream, force, cancellationToken);
    }
}
