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

        /// <summary>The questions the person was shown, in order.</summary>
        public required List<string> Asked { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    /// <param name="answer">null: the client does not support elicitation at all.</param>
    /// <param name="whileAsked">Runs while the question is open, after the preview and before the answer.</param>
    private async Task<Session> StartAsync(bool? answer, string mode = "required", Func<Task>? whileAsked = null)
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

        var asked = new List<string>();
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
                ElicitationHandler = async (question, _) =>
                {
                    lock (asked)
                        asked.Add(question?.Message ?? "");
                    if (whileAsked is not null)
                        await whileAsked();
                    return new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement> { [ElicitationPrompt.Field] = JsonSerializer.SerializeToElement(approve) },
                    };
                },
            };
        }

        var client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer, clientIn), options);
        return new Session { Host = host, Client = client, Asked = asked };
    }

    private async Task QueueWithMessageAsync(string queue, string target, bool withId = true)
    {
        await using (var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueDeclareAsync(target, durable: true, exclusive: false, autoDelete: false);
        }
        await PublishAsync(queue, withId);
        await broker.WaitForQueueCount(queue, 1);
    }

    private async Task PublishAsync(string queue, bool withId = true, string? exceptionType = null)
    {
        await using var channel = await broker.Connection.CreateChannelAsync(publisherConfirms: false, CancellationToken.None);
        await channel.BasicPublishAsync("", queue, mandatory: false,
            new BasicProperties
            {
                MessageId = withId ? Guid.NewGuid().ToString() : null,
                Headers = exceptionType is null ? null
                    : new Dictionary<string, object?> { ["MT-Reason"] = "fault", ["MT-Fault-ExceptionType"] = exceptionType },
            }, """{"x":1}"""u8.ToArray());
    }

    private static Dictionary<string, object?> Republish(string queue, string target) => new()
    {
        ["errorQueue"] = queue, ["count"] = 5, ["confirm"] = true, ["targetQueue"] = target,
    };

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static bool? MoreMayRemain(CallToolResult result) =>
        result.StructuredContent?.GetProperty("moreMayRemain").GetBoolean();

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

        // The person saw what the server's own preview found, not only the model's arguments.
        var question = Assert.Single(session.Asked);
        Assert.Contains("Preview just now: would republish 1 message from it-human-approved", question);
        Assert.Contains("targetQueue: it-human-approved-target", question);
        Assert.Contains("\ncount: 1\n", question); // what runs (held to the preview), not the model's 5
        Assert.False(MoreMayRemain(result)); // the queue is empty: nothing to call again for
    }

    [Fact]
    public async Task A_message_that_arrives_while_the_person_reads_the_question_is_not_moved()
    {
        await QueueWithMessageAsync("it-human-late", "it-human-late-target");
        await using var session = await StartAsync(answer: true, whileAsked: async () =>
        {
            // A new fault lands behind the previewed message before the person answers.
            await PublishAsync("it-human-late");
            await broker.WaitForQueueCount("it-human-late", 2);
        });

        var result = await session.Client.CallToolAsync("republish_from_error", Republish("it-human-late", "it-human-late-target"));

        Assert.NotEqual(true, result.IsError);
        await broker.WaitForQueueCount("it-human-late-target", 1);
        await broker.WaitForQueueCount("it-human-late", 1); // the newcomer, which the person never saw, stays
        Assert.Contains("Only these previewed messages are moved", Assert.Single(session.Asked));
        Assert.True(MoreMayRemain(result)); // the newcomer needs a call (and a question) of its own
    }

    [Fact]
    public async Task A_message_without_an_id_is_held_to_the_previewed_depth()
    {
        await QueueWithMessageAsync("it-human-noid", "it-human-noid-target", withId: false);
        await using var session = await StartAsync(answer: true, whileAsked: async () =>
        {
            await PublishAsync("it-human-noid", withId: false);
            await broker.WaitForQueueCount("it-human-noid", 2);
        });

        var result = await session.Client.CallToolAsync("republish_from_error", Republish("it-human-noid", "it-human-noid-target"));

        Assert.NotEqual(true, result.IsError);
        await broker.WaitForQueueCount("it-human-noid-target", 1);
        await broker.WaitForQueueCount("it-human-noid", 1); // nothing to hold it by but its place: the newcomer behind it stays
        var question = Assert.Single(session.Asked);
        Assert.Contains("the first 1 message of the queue", question);
        Assert.Contains("\ncount: 1\n", question);
    }

    [Fact]
    public async Task A_filtered_move_is_held_to_the_scanned_depth()
    {
        await QueueWithMessageAsync("it-human-filter", "it-human-filter-target");
        await PublishAsync("it-human-filter", exceptionType: "System.TimeoutException");
        await broker.WaitForQueueCount("it-human-filter", 2);
        await using var session = await StartAsync(answer: true, whileAsked: async () =>
        {
            await PublishAsync("it-human-filter", exceptionType: "System.TimeoutException");
            await broker.WaitForQueueCount("it-human-filter", 3);
        });

        var result = await session.Client.CallToolAsync("republish_from_error", new Dictionary<string, object?>
        {
            ["errorQueue"] = "it-human-filter", ["exceptionType"] = "Timeout", ["confirm"] = true, ["targetQueue"] = "it-human-filter-target",
        });

        Assert.NotEqual(true, result.IsError);
        await broker.WaitForQueueCount("it-human-filter-target", 1);
        await broker.WaitForQueueCount("it-human-filter", 2); // the non-matching message and the late match stay
        var question = Assert.Single(session.Asked);
        Assert.Contains("1 message in it-human-filter matches the filter and would be republished to queue:it-human-filter-target", question);
        Assert.Contains("Only the first 2 messages of the queue", question);
        Assert.True(MoreMayRemain(result));
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
        Assert.Contains(session.Asked, q => q.Contains("would send a corrected", StringComparison.Ordinal) && q.Contains("reason", StringComparison.Ordinal));

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
