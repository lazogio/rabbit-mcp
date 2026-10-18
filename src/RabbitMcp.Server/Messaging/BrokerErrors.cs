// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

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
            // AlreadyClosedException derives from OperationInterruptedException: match it first.
            AlreadyClosedException closed =>
                $"The connection to RabbitMQ was lost ({closed.ShutdownReason?.ReplyText ?? "closed"}). Try again; it reconnects automatically.",
            OperationInterruptedException { ShutdownReason: { } reason } => reason.ReplyCode switch
            {
                403 => $"RabbitMQ refused the operation (403 ACCESS_REFUSED): {reason.ReplyText}. " +
                       "The user in RabbitMcp:Uri lacks the permission (configure/write/read) for this entity.",
                404 => $"RabbitMQ: {reason.ReplyText} (404 NOT_FOUND).",
                405 => $"RabbitMQ: the queue is exclusive to another connection (405 RESOURCE_LOCKED): {reason.ReplyText}.",
                406 => $"RabbitMQ refused the operation (406 PRECONDITION_FAILED): {reason.ReplyText}.",
                _ => $"RabbitMQ closed the channel ({reason.ReplyCode}): {reason.ReplyText}.",
            },
            BrokerUnreachableException => "RabbitMQ is unreachable. Is the broker running and are the credentials correct?",
            PublishException { IsReturn: true } => "The broker returned the message: no queue is bound. Nothing was delivered.",
            PublishException => "The broker rejected the message (nack). Nothing was delivered.",
            RabbitMQClientException => $"RabbitMQ client error: {ex.Message}",
            _ => "",
        };
        return message.Length > 0;
    }

    /// <summary>The exception itself if it is already an McpException, else a readable one (or a generic one).</summary>
    public static McpException ToMcp(Exception ex) =>
        ex as McpException
        ?? new McpException(TryDescribe(ex, out var message) ? message : $"Unexpected error ({ex.GetType().Name}).", ex);
}
