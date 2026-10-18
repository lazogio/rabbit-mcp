// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Security;
using RabbitMQ.Client;

namespace RabbitMcp.IntegrationTests;

/// <summary>
/// The real server over an in-process stdio-like transport with a real MCP client that answers elicitation like a
/// person would — the model's confirm=true alone is not enough.
/// </summary>
[Collection(BrokerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HumanInTheLoopTests(BrokerFixture broker)
{
    private sealed class Session : IAsyncDisposable
    {
        public required IHost Host { get; init; }
        public required McpClient Client { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    /// <param name="answer">null: the client does not support elicitation at all.</param>
    private async Task<Session> StartAsync(bool? answer, string mode = "required")
    {
        var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverIn = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);
        var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.ClientSafePipeHandle);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMcp:Uri"] = broker.Options.Value.Uri,
            ["RabbitMcp:ClientName"] = "rabbit-mcp-it-human",
            ["RabbitMcp:EnableRepublish"] = "true",
            ["RabbitMcp:EnablePublish"] = "true",
            ["RabbitMcp:HumanConfirmation"] = mode,
        });
        ServerComposition.Compose(builder).WithStreamServerTransport(serverIn, serverToClient);
        var host = builder.Build();
        await host.StartAsync();

        var options = new McpClientOptions();
        if (answer is { } approve)
        {
            options.Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() },
            };
            options.Handlers = new McpClientHandlers
            {
                // Like a person ticking (or not ticking) the box in the client's dialog.
                ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement> { [ElicitationPrompt.Field] = JsonSerializer.SerializeToElement(approve) },
                }),
            };
        }

        var client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer, clientIn), options);
        return new Session { Host = host, Client = client };
    }

    private async Task QueueWithMessageAsync(string queue, string target)
    {
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync(target, durable: true, exclusive: false, autoDelete: false);
        await channel.BasicPublishAsync("", queue, mandatory: false,
            new BasicProperties { MessageId = Guid.NewGuid().ToString() }, """{"x":1}"""u8.ToArray());
        await broker.WaitForQueueCount(queue, 1);
    }

    private static Dictionary<string, object?> Republish(string queue, string target) => new()
    {
        ["errorQueue"] = queue, ["count"] = 5, ["confirm"] = true, ["targetQueue"] = target,
    };

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task A_declining_person_stops_the_republish_even_with_confirm_true()
    {
        await QueueWithMessageAsync("it-human-declined", "it-human-declined-target");
        await using var session = await StartAsync(answer: false);

        var result = await session.Client.CallToolAsync("republish_from_error", Republish("it-human-declined", "it-human-declined-target"));

        Assert.True(result.IsError);
        Assert.Contains("declined", Text(result));
        await broker.WaitForQueueCount("it-human-declined", 1);
        await broker.WaitForQueueCount("it-human-declined-target", 0);
    }

    [Fact]
    public async Task An_approving_person_lets_it_happen()
    {
        await QueueWithMessageAsync("it-human-approved", "it-human-approved-target");
        await using var session = await StartAsync(answer: true);

        var result = await session.Client.CallToolAsync("republish_from_error", Republish("it-human-approved", "it-human-approved-target"));

        Assert.NotEqual(true, result.IsError);
        await broker.WaitForQueueCount("it-human-approved", 0);
        await broker.WaitForQueueCount("it-human-approved-target", 1);
    }

    [Fact]
    public async Task Required_confirmation_refuses_clients_that_cannot_ask()
    {
        await QueueWithMessageAsync("it-human-noclient", "it-human-noclient-target");
        await using var session = await StartAsync(answer: null);

        var result = await session.Client.CallToolAsync("republish_from_error", Republish("it-human-noclient", "it-human-noclient-target"));

        Assert.True(result.IsError);
        Assert.Contains("cannot ask", Text(result));
        await broker.WaitForQueueCount("it-human-noclient", 1);
    }

    [Fact]
    public async Task A_bad_message_is_corrected_resent_and_the_original_parked()
    {
        // it-order-cancelled fails the first delivery of every order (→ _error) and accepts the next one.
        const string errorQueue = BrokerFixture.OrderCancelledQueue + "_error";
        const string deadQueue = BrokerFixture.OrderCancelledQueue + "_dead";
        var orderId = Guid.NewGuid();
        var sent = await broker.Publisher.PublishAsync(new OrderCancelled { OrderId = orderId, Reason = "typo in reasn" });
        var original = await WaitForMessageAsync(errorQueue, sent.MessageId);

        await using var session = await StartAsync(answer: true);
        var arguments = new Dictionary<string, object?>
        {
            ["errorQueue"] = errorQueue,
            ["messageId"] = sent.MessageId,
            ["corrected"] = new { orderId, reason = "Customer changed their mind" },
        };

        var preview = await session.Client.CallToolAsync("resend_corrected", arguments);
        Assert.NotEqual(true, preview.IsError);
        Assert.Contains("reason", Text(preview));
        Assert.NotNull(await WaitForMessageAsync(errorQueue, sent.MessageId)); // a preview changes nothing

        arguments["confirm"] = true;
        var result = await session.Client.CallToolAsync("resend_corrected", arguments);
        Assert.NotEqual(true, result.IsError);

        var processed = await broker.WaitForCancelled(orderId);
        Assert.Equal("Customer changed their mind", processed.Message.Reason);
        Assert.NotEqual(Guid.Parse(sent.MessageId), processed.MessageId);

        var parked = await WaitForMessageAsync(deadQueue, sent.MessageId);
        Assert.Equal(processed.MessageId.ToString(), parked!.Headers["X-Corrected-To"]);
        Assert.Equal("typo in reasn", parked.Payload!.Value.GetProperty("reason").GetString()); // the original, unchanged
        Assert.NotNull(parked.Fault);
        Assert.NotNull(original);
    }

    private async Task<RabbitMcp.Server.Tools.PeekedMessage?> WaitForMessageAsync(string queue, string messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await broker.Connection.GetQueueAsync(queue, CancellationToken.None) is not null)
            {
                var peek = await broker.Browser.PeekAsync(queue, 50, force: false, CancellationToken.None);
                if (peek.Messages.FirstOrDefault(m => m.MessageId == messageId) is { } found)
                    return found;
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"{messageId} did not show up in {queue}.");
    }
}
