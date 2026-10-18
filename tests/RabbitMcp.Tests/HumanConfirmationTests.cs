// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using RabbitMcp.Server;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;

namespace RabbitMcp.Tests;

public class HumanConfirmationTests
{
    private sealed class FakePrompt(PersonAnswer answer) : IPersonPrompt
    {
        public List<string> Asked { get; } = [];

        public Task<PersonAnswer> AskAsync(string message, CancellationToken ct)
        {
            Asked.Add(message);
            return Task.FromResult(answer);
        }
    }

    private static Dictionary<string, JsonElement> Args(object values) =>
        JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static HumanConfirmation Create(string mode = "auto", string? tools = null) =>
        new(new RabbitMcpOptions { HumanConfirmation = mode, ConfirmTools = tools ?? new RabbitMcpOptions().ConfirmTools });

    [Fact]
    public void Previews_are_never_asked_about_executions_are()
    {
        var human = Create();
        Assert.False(human.Applies("republish_from_error", Args(new { errorQueue = "q" })));
        Assert.False(human.Applies("republish_from_error", Args(new { errorQueue = "q", confirm = false })));
        Assert.True(human.Applies("republish_from_error", Args(new { errorQueue = "q", confirm = true })));
        Assert.True(human.Applies("resend_corrected", Args(new { confirm = true })));
        Assert.False(human.Applies("publish_order_submitted", Args(new { message = new { } }))); // not listed by default
        Assert.False(human.Applies("peek_messages", Args(new { queue = "q" })));
    }

    [Fact]
    public void Listed_tools_without_a_preview_are_asked_every_time()
    {
        var human = Create(tools: "publish_*;reply_*");
        Assert.True(human.Applies("publish_order_submitted", null));
        Assert.True(human.Applies("reply_order_approved", Args(new { requestId = "x" })));
        Assert.False(human.Applies("request_get_order_status", null));
        Assert.False(Create("off", "publish_*").Applies("publish_order_submitted", null));
    }

    [Theory]
    [InlineData("auto", PersonAnswer.Approved, true, "person")]
    [InlineData("auto", PersonAnswer.Declined, false, "person")]
    [InlineData("auto", PersonAnswer.Unavailable, true, "argument")]
    [InlineData("required", PersonAnswer.Approved, true, "person")]
    [InlineData("required", PersonAnswer.Unavailable, false, "unavailable")]
    public async Task The_person_decides_when_they_can_be_asked(string mode, PersonAnswer answer, bool proceed, string how)
    {
        var prompt = new FakePrompt(answer);
        var decision = await Create(mode).DecideAsync("republish_from_error",
            Args(new { errorQueue = "orders_error", count = 3, confirm = true }), "alice", prompt, CancellationToken.None);

        Assert.Equal(proceed, decision.Proceed);
        Assert.Equal(how, decision.How);
        var asked = Assert.Single(prompt.Asked);
        Assert.Contains("alice wants to run republish_from_error", asked);
        Assert.Contains("errorQueue: orders_error", asked);
        Assert.DoesNotContain("confirm:", asked);
        if (!proceed)
            Assert.Contains("Nothing was changed", decision.Refusal);
    }

    [Fact]
    public async Task Calls_that_need_no_confirmation_do_not_ask()
    {
        var prompt = new FakePrompt(PersonAnswer.Declined);
        var decision = await Create().DecideAsync("republish_from_error", Args(new { errorQueue = "q" }), null, prompt, CancellationToken.None);
        Assert.True(decision.Proceed);
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public void Unknown_mode_stops_the_server() =>
        Assert.Throws<StartupException>(() => Create("sometimes"));

    [Fact]
    public async Task Elicitation_is_unavailable_without_a_client_that_supports_it()
    {
        Assert.Equal(PersonAnswer.Unavailable, await new ElicitationPrompt(null).AskAsync("?", CancellationToken.None));

        Dictionary<string, JsonElement> Content(object v) => new() { [ElicitationPrompt.Field] = JsonSerializer.SerializeToElement(v) };
        Assert.True(ElicitationPrompt.Approves("accept", Content(true)));
        Assert.True(ElicitationPrompt.Approves("accept", Content("true")));
        Assert.False(ElicitationPrompt.Approves("accept", Content(false)));
        Assert.False(ElicitationPrompt.Approves("accept", null));
        Assert.False(ElicitationPrompt.Approves("decline", Content(true)));
        Assert.False(ElicitationPrompt.Approves("cancel", Content(true)));
    }

    [Fact]
    public void Summary_is_shortened()
    {
        var summary = HumanConfirmation.Summary("publish_x", Args(new { message = new string('a', 5000) }), null);
        Assert.True(summary.Length < 1600);
        Assert.StartsWith("rabbit-mcp: the assistant wants to run publish_x.", summary);
    }

    // --- resend_corrected building blocks

    [Fact]
    public void Diff_lists_changed_added_and_removed_fields()
    {
        var before = JsonDocument.Parse("""{"email":"ana@","qty":2,"price":1.50,"lines":[{"sku":"A"},{"sku":"B"}],"old":1}""").RootElement;
        var after = JsonDocument.Parse("""{"email":"ana@example.ge","qty":2,"price":1.5,"lines":[{"sku":"A"},{"sku":"C"}],"note":"x"}""").RootElement;

        var changes = JsonDiff.Compare(before, after);

        Assert.Equal(new[] { "email", "lines[1].sku", "old", "note" }, changes.Select(c => c.Path));
        Assert.Equal("ana@", changes[0].Before!.Value.GetString());
        Assert.Null(changes[2].After);
        Assert.Null(changes[3].Before);
        Assert.Empty(JsonDiff.Compare(before, before));
    }

    [Fact]
    public void Corrected_envelope_keeps_every_field_but_id_and_message()
    {
        var original = Encoding.UTF8.GetBytes(
            """{"messageId":"m-1","conversationId":"c-1","messageType":["urn:message:X:Y"],"message":{"a":1},"headers":{"h":"v"}}""");
        var corrected = JsonDocument.Parse("""{"a":2}""").RootElement;

        using var doc = JsonDocument.Parse(QueueBrowser.CorrectedEnvelope(original, "m-2", corrected));
        var root = doc.RootElement;
        Assert.Equal("m-2", root.GetProperty("messageId").GetString());
        Assert.Equal(2, root.GetProperty("message").GetProperty("a").GetInt32());
        Assert.Equal("c-1", root.GetProperty("conversationId").GetString());
        Assert.Equal("v", root.GetProperty("headers").GetProperty("h").GetString());
    }
}
