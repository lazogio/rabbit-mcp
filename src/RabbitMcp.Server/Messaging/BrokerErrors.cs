// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Server.Messaging;

/// <summary>
/// Turns RabbitMQ client exceptions into messages the model can act on. Without this, the MCP SDK reports any
/// exception other than <see cref="McpException"/> as "An error occurred invoking 'tool'." with no reason — e.g. a
/// 403 ACCESS_REFUSED looked the same as a bug. Broker reply texts name entities and the user, never credentials.
/// </summary>
public static class BrokerErrors
{
    public static bool TryDescribe(Exception ex, out string message)
    {
        message = ex switch
        {
            McpException mcp => mcp.Message,
            // A refusal reaches a confirmed publish as AlreadyClosedException (it derives from
            // OperationInterruptedException). It must not read as a lost connection: trying again cannot help.
            OperationInterruptedException { ShutdownReason: { ReplyCode: 403 or 404 or 405 or 406 or 501 or 540 } reason } => reason.ReplyCode switch
            {
                403 => $"RabbitMQ refused the operation (403 ACCESS_REFUSED): {reason.ReplyText}. " +
                       "The user in RabbitMcp:Uri lacks the permission (configure/write/read) for this entity.",
                404 => $"RabbitMQ: {reason.ReplyText} (404 NOT_FOUND).",
                405 => $"RabbitMQ: the queue is exclusive to another connection (405 RESOURCE_LOCKED): {reason.ReplyText}.",
                501 => $"RabbitMQ refused an oversized frame (501 FRAME_ERROR): {reason.ReplyText}. Trying again fails the same way; " +
                       "shorten what goes into message headers.",
                540 => $"RabbitMQ does not support this (540 NOT_IMPLEMENTED): {reason.ReplyText}. Streams are read with read_stream.",
                _ => $"RabbitMQ refused the operation (406 PRECONDITION_FAILED): {reason.ReplyText}.",
            },
            AlreadyClosedException closed =>
                $"The connection to RabbitMQ was lost ({closed.ShutdownReason?.ReplyText ?? "closed"}). Try again; it reconnects automatically.",
            OperationInterruptedException { ShutdownReason: { } reason } =>
                $"RabbitMQ closed the channel ({reason.ReplyCode}): {reason.ReplyText}.",
            TimeoutException or OperationCanceledException => "RabbitMQ did not answer in time; it may be overloaded or frozen. Try again later.",
            BrokerUnreachableException => "RabbitMQ is unreachable. Is the broker running and are the credentials correct?",
            PublishException { IsReturn: true } => "The broker returned the message: no queue is bound. Nothing was delivered.",
            PublishException => "The broker rejected the message (nack). Nothing was delivered.",
            RabbitMQClientException => $"RabbitMQ client error: {ex.Message}",
            _ => "",
        };
        return message.Length > 0;
    }

    /// <summary>
    /// Arguments that could not be bound to the tool's parameters (missing, wrong type, a contract's required field):
    /// the SDK would report them without a reason, and the model could not correct the call.
    /// </summary>
    public static bool TryDescribeArguments(string tool, Exception ex, out string message)
    {
        message = ex is JsonException or ArgumentException && ThrownWhileBinding(ex)
            ? $"Invalid arguments for {tool}: {ex.Message} See the tool's input schema."
            : "";
        return message.Length > 0;
    }

    /// <summary>
    /// The same exception types also come from inside a tool and from writing its result, where they say nothing about
    /// the arguments. Binding happens before the tool's code runs and reads JSON, so neither is on the stack.
    /// </summary>
    private static bool ThrownWhileBinding(Exception ex) =>
        !new StackTrace(ex).GetFrames().Select(frame => frame.GetMethod()).Any(method =>
            method?.DeclaringType?.Namespace is { } name
            && ((name.StartsWith("RabbitMcp.Server.", StringComparison.Ordinal) && !name.EndsWith(".Hosting", StringComparison.Ordinal))
                || (name.StartsWith("System.Text.Json", StringComparison.Ordinal) && method.Name.StartsWith("Serialize", StringComparison.Ordinal))));

    /// <summary>The exception itself if it is already an McpException, else a readable one (or a generic one).</summary>
    public static McpException ToMcp(Exception ex) =>
        ex as McpException
        ?? new McpException(TryDescribe(ex, out var message) ? message : $"Unexpected error ({ex.GetType().Name}).", ex);
}
