// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Contracts;

namespace Shop.Service;

public sealed class ApprovalQueue
{
    private readonly Channel<ApproveLargeOrder> _channel = Channel.CreateUnbounded<ApproveLargeOrder>();

    public void Enqueue(ApproveLargeOrder request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<ApproveLargeOrder> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Asks for approval of large orders with an ordinary MassTransit request. Nobody in this service answers it:
/// rabbit-mcp in responder mode does (RabbitMcp:RespondTo=ApproveLargeOrder), so a person decides through the model.
/// </summary>
public sealed class ApprovalWorker(
    ApprovalQueue queue, OrderStore orders, IServiceScopeFactory scopes, ILogger<ApprovalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
            _ = AskAsync(request, stoppingToken); // each approval waits on its own
    }

    private async Task AskAsync(ApproveLargeOrder request, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IRequestClient<ApproveLargeOrder>>();
            logger.LogInformation("Asking for approval of order {OrderId} ({Total})", request.OrderId, request.Total);

            var response = await client.GetResponse<OrderApproved, OrderRejected>(request, ct);
            if (response.Is(out Response<OrderApproved>? approved))
                orders.Update(request.OrderId, "Approved", $"Approved by {approved!.Message.ApprovedBy}");
            else if (response.Is(out Response<OrderRejected>? rejected))
                orders.Update(request.OrderId, "Rejected", rejected!.Message.Reason);
        }
        catch (RequestFaultException ex)
        {
            // reject_request in rabbit-mcp: the request faulted with the person's reason.
            orders.Update(request.OrderId, "Rejected", ex.Fault?.Exceptions.FirstOrDefault()?.Message ?? ex.Message);
        }
        catch (RequestTimeoutException)
        {
            orders.Update(request.OrderId, "ApprovalTimedOut", "Nobody answered within 10 minutes");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Approval of order {OrderId} failed", request.OrderId);
        }
        finally
        {
            logger.LogInformation("Order {OrderId} is now {Status}", request.OrderId, orders.Find(request.OrderId)?.Status);
        }
    }
}
