// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server;
using RabbitMcp.Server.Cli;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Hosting;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Security;
using RabbitMcp.Server.Tools;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace RabbitMcp.Tests;

/// <summary>Guards for the defects found by the black-box QA run of 2026-10-31 (BUG-031 and later).</summary>
public class QaRegressionTests
{
    private static readonly ContractRegistry Registry = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);

    private static readonly PeekedMessage EmptyMessage = new(
        0, null, null, false, null, null, null, null, null, null, null, null, null, null, false, 0, new Dictionary<string, string?>());

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>What a strict MCP client does with a result: validate it against the tool's outputSchema.</summary>
    private static void AssertFitsOutputSchema<T>(T result)
    {
        var schema = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: ToolJson.Options).GetRawText());
        Assert.Empty(SchemaValidator.Validate(schema, JsonSerializer.SerializeToElement(result, ToolJson.Options)));
    }

    [Fact]
    public void Results_with_null_fields_still_fit_their_output_schema()
    {
        AssertFitsOutputSchema(new QueueStats([new QueueStat("q", false, null, null)]));
        AssertFitsOutputSchema(new PublishResult("m", null, null, "C", 1, "raw", "urn", "x", null, DateTime.UtcNow, true));
        AssertFitsOutputSchema(new PeekResult("q", 1, 0, [EmptyMessage]));
        AssertFitsOutputSchema(new RepublishResult("q", true, null, [new RepublishOutcome("m", "republished", "t", null)], 1, 0));
        AssertFitsOutputSchema(new DeadLetterResult("q", "d", true, null, [new DeadLetterOutcome("m", "moved", null)], 1, 0));
        AssertFitsOutputSchema(new DeclareResult("queue", "q", "classic", true, false, new Dictionary<string, string>(), null, null, false, "would-create", []));
        AssertFitsOutputSchema(new CorrectionResult("q", "m", "C", true, [], "t", "d", "n", true, null));
        AssertFitsOutputSchema(new FindMessageResult([new FoundMessage("q", EmptyMessage)], [], null));
    }

    [Fact]
    public void Tool_results_write_null_fields()
    {
        var json = JsonSerializer.Serialize(new QueueStat("q", false, null, null), ToolJson.Options);

        Assert.Contains("\"readyMessages\":null", json);
    }

    [Fact]
    public void A_refusal_is_not_reported_as_a_lost_connection()
    {
        var refused = new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 403, "ACCESS_REFUSED - write access refused"));

        Assert.True(BrokerErrors.TryDescribe(refused, out var message));
        Assert.Contains("403 ACCESS_REFUSED", message);
        Assert.DoesNotContain("Try again", message);
    }

    [Fact]
    public void A_really_lost_connection_is_still_reported_as_one()
    {
        var lost = new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "CONNECTION_FORCED"));

        Assert.True(BrokerErrors.TryDescribe(lost, out var message));
        Assert.Contains("Try again", message);
    }

    [Fact]
    public void Only_binding_failures_are_described_as_invalid_arguments()
    {
        Assert.True(BrokerErrors.TryDescribeArguments("publish_x", new JsonException("missing 'orderId'"), out var message));
        Assert.Contains("publish_x", message);
        Assert.Contains("missing 'orderId'", message);
        Assert.False(BrokerErrors.TryDescribeArguments("publish_x", new InvalidOperationException("a bug"), out _));
    }

    [Fact]
    public void Long_messages_are_searched_in_full_not_only_as_shown()
    {
        var shown = EmptyMessage with { RawBody = "xxxx", Truncated = true };
        var body = Encoding.UTF8.GetBytes(new string('x', 20_000) + "NEEDLE");
        var matcher = new MessageMatcher(null, null, "needle");

        Assert.True(matcher.Matches(shown, body));
        Assert.False(matcher.Matches(shown));
    }

    private static PeekedMessage Inspect(string body, int maxChars) =>
        new MessageInspector(Registry, Options.Create(new RabbitMcpOptions { MaxPayloadChars = maxChars }))
            .Inspect(0, "q", new BasicProperties(), Encoding.UTF8.GetBytes(body), redelivered: false);

    [Fact]
    public void The_display_limit_counts_characters_and_never_cuts_one()
    {
        var georgian = new string('ა', 20); // 60 bytes
        var fits = Inspect(georgian, maxChars: 30);
        Assert.False(fits.Truncated);
        Assert.Equal(georgian, fits.RawBody);

        var cut = Inspect("x" + georgian, maxChars: 10);
        Assert.True(cut.Truncated);
        Assert.Equal("x" + georgian[..9], cut.RawBody);

        var emoji = Inspect("x" + string.Concat(Enumerable.Repeat("😀", 5)), maxChars: 4); // the limit falls inside a pair
        Assert.True(emoji.Truncated);
        Assert.Equal("x😀", emoji.RawBody);
    }

    [Fact]
    public void Time_errors_name_the_argument_that_was_wrong()
    {
        var until = Assert.Throws<McpException>(() => MessageFilter.From(null, null, null, null, "nope", DateTimeOffset.UtcNow));
        Assert.StartsWith("until", until.Message);

        var from = Assert.Throws<McpException>(() => QueueBrowser.StreamOffset("nope", DateTimeOffset.UtcNow));
        Assert.StartsWith("from", from.Message);
    }

    [Fact]
    public void A_message_leaving_a_dead_letter_queue_is_no_longer_marked_as_parked()
    {
        var parked = new Dictionary<string, object?>
        {
            [HeaderNames.DeadLetteredBy] = "a", [HeaderNames.DeadLetteredFrom] = "q_error", [HeaderNames.DeadLetteredAt] = "t",
            [HeaderNames.DeadLetterReason] = "r", [HeaderNames.CorrectedTo] = "new", [HeaderNames.CorrectedBy] = "a",
            [HeaderNames.CorrectedAt] = "t", ["Tenant"] = "acme",
        };
        Assert.Equal(new[] { "Tenant" }, FaultHeaders.WithoutFaultHeaders(parked).Keys);

        // A corrected copy that fails later keeps saying where it came from.
        var copy = new Dictionary<string, object?>
        {
            [HeaderNames.CorrectedFrom] = "old", [HeaderNames.CorrectedBy] = "a", [HeaderNames.CorrectedAt] = "t",
        };
        Assert.Equal(3, FaultHeaders.WithoutFaultHeaders(copy).Count);
    }

    [Fact]
    public void A_correction_cannot_add_fields_the_contract_does_not_define()
    {
        const string id = "5ca1ab1e-0000-4000-8000-000000000001";
        var contract = Registry.Get(typeof(OrderCancelled));
        var original = Json($$"""{"orderId":"{{id}}","reason":"old"}""");

        var added = QueueBrowser.FieldsOutsideContract(contract, original, Json($$"""{"orderId":"{{id}}","reason":"new","injected":"x"}"""));
        Assert.Equal(new[] { "injected" }, added);

        Assert.Empty(QueueBrowser.FieldsOutsideContract(contract, original, Json($$"""{"orderId":"{{id}}","reason":"new"}""")));
    }

    [Fact]
    public void A_correction_keeps_fields_the_original_already_had()
    {
        const string id = "5ca1ab1e-0000-4000-8000-000000000001";
        var contract = Registry.Get(typeof(OrderCancelled));
        var fromNewerProducer = Json($$"""{"orderId":"{{id}}","reason":"old","priority":1}""");

        Assert.Empty(QueueBrowser.FieldsOutsideContract(contract, fromNewerProducer, Json($$"""{"orderId":"{{id}}","reason":"new","priority":1}""")));
    }

    [Fact]
    public void Transient_queues_are_refused_before_they_reach_the_broker()
    {
        var policy = new TopologyPolicy(new RabbitMcpOptions(), Registry);

        var ex = Assert.Throws<McpException>(() => policy.Queue("q", null, null, durable: false, null, null, null));
        Assert.Contains("always durable", ex.Message);
        Assert.Throws<StartupException>(() => TopologyPolicy.ParseTemplate("t", "kind=queue;durable=false"));
    }

    private static MessageInspector Inspector() => new(Registry, Options.Create(new RabbitMcpOptions()));

    private static PeekedMessage Inspect(string body, BasicProperties? props = null) =>
        Inspector().Inspect(0, "q", props ?? new BasicProperties(), Encoding.UTF8.GetBytes(body), false);

    private static string Nested(int levels) => new string('[', levels) + new string(']', levels);

    [Theory]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"\ud800":1}""")]
    [InlineData("""["ok",{"b":["\udc00"]}]""")]
    public void A_message_a_tool_result_cannot_carry_is_shown_as_text(string body)
    {
        var message = Inspect(body);

        Assert.Null(message.Payload);
        Assert.Equal(body, message.RawBody);
        Assert.NotNull(message.Problem);
        JsonSerializer.SerializeToElement(new PeekResult("q", 1, 0, [message]), ToolJson.Options);
    }

    [Fact]
    public void Invalid_utf8_inside_a_json_string_is_shown_as_text()
    {
        byte[] body = [(byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}'];

        var message = Inspector().Inspect(0, "q", new BasicProperties(), body, false);

        Assert.Null(message.Payload);
        Assert.Contains('�', message.RawBody!);
        JsonSerializer.SerializeToElement(new PeekResult("q", 1, 0, [message]), ToolJson.Options);
    }

    [Fact]
    public void Deep_json_stays_json_up_to_the_limit_and_fits_every_result_it_appears_in()
    {
        var deepest = Inspect(Nested(MessageInspector.MaxShownDepth));
        Assert.NotNull(deepest.Payload);
        var results = new object[]
        {
            new PeekResult("q", 1, 0, [deepest]),
            new FindMessageResult([new FoundMessage("q", deepest)], [], null),
            new RepublishResult("q", false, new PeekResult("q", 1, 0, [deepest]), [], 0, 0),
        };
        foreach (var result in results)
            JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", result = new { structuredContent = result } }, ToolJson.Options);

        var tooDeep = Inspect(Nested(MessageInspector.MaxShownDepth + 1));
        Assert.Null(tooDeep.Payload);
        Assert.NotNull(tooDeep.RawBody);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(253402300800)] // the first second of year 10000
    public void A_timestamp_no_date_can_hold_does_not_hide_the_message(long seconds)
    {
        var message = Inspect("{}", new BasicProperties
        {
            Timestamp = new AmqpTimestamp(seconds),
            Headers = new Dictionary<string, object?> { ["sent"] = new AmqpTimestamp(seconds) },
        });

        Assert.Null(message.SentTimeUtc);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), message.Headers["sent"]);
    }

    [Fact]
    public void A_message_that_cannot_be_interpreted_is_shown_raw_instead_of_failing_the_call()
    {
        var message = Inspector().Inspect(0, "q", new ThrowingProperties(), "{}"u8, false);

        Assert.Equal("{}", message.RawBody);
        Assert.Contains(nameof(InvalidCastException), message.Problem);
    }

    /// <summary>Stands for anything unforeseen in a message: only reading its headers fails.</summary>
    private sealed class ThrowingProperties : IReadOnlyBasicProperties
    {
        public string? AppId => null;
        public string? ClusterId => null;
        public string? ContentEncoding => null;
        public string? ContentType => null;
        public string? CorrelationId => null;
        public DeliveryModes DeliveryMode => DeliveryModes.Transient;
        public string? Expiration => null;
        public IDictionary<string, object?>? Headers => throw new InvalidCastException();
        public string? MessageId => "m";
        public bool Persistent => false;
        public byte Priority => 0;
        public string? ReplyTo => null;
        public PublicationAddress? ReplyToAddress => null;
        public AmqpTimestamp Timestamp => default;
        public string? Type => null;
        public string? UserId => null;
        public bool IsAppIdPresent() => false;
        public bool IsClusterIdPresent() => false;
        public bool IsContentEncodingPresent() => false;
        public bool IsContentTypePresent() => false;
        public bool IsCorrelationIdPresent() => false;
        public bool IsDeliveryModePresent() => false;
        public bool IsExpirationPresent() => false;
        public bool IsHeadersPresent() => false;
        public bool IsMessageIdPresent() => true;
        public bool IsPriorityPresent() => false;
        public bool IsReplyToPresent() => false;
        public bool IsTimestampPresent() => false;
        public bool IsTypePresent() => false;
        public bool IsUserIdPresent() => false;
    }

    [Theory]
    [InlineData("42 is the answer")]
    [InlineData("""{"a":1} trailing text""")]
    [InlineData("null and void")]
    [InlineData("true story")]
    [InlineData("[1] [2]")]
    [InlineData("null")]
    public void Text_that_only_starts_like_json_is_shown_whole(string body)
    {
        var message = Inspect(body);

        Assert.Null(message.Payload);
        Assert.Equal(body, message.RawBody);
    }

    [Fact]
    public void Json_with_whitespace_around_it_is_still_json()
    {
        Assert.Equal(JsonValueKind.Object, Inspect("  {\"a\":1}\n").Payload?.ValueKind);
    }

    [Fact]
    public void A_raw_reply_that_only_starts_like_json_stays_text()
    {
        var reply = RawReply.From(new BasicProperties(), "42 is the answer"u8, 100);

        Assert.Null(reply.Body);
        Assert.Equal("42 is the answer", reply.Text);
    }

    [Fact]
    public void A_reply_a_result_cannot_carry_comes_back_as_text()
    {
        var shown = MessageInspector.Showable(Json("""{"a":"\ud800"}"""));

        Assert.Equal(JsonValueKind.String, shown?.ValueKind);
        JsonSerializer.SerializeToElement(new { response = shown }, ToolJson.Options);
    }

    private sealed class Lines
    {
        public List<Line> Required { get; set; } = [];
        public List<Line?> Optional { get; set; } = [];
        public string[] Names { get; set; } = [];
    }

    private sealed class Line
    {
        public int Quantity { get; set; }
    }

    [Fact]
    public void Null_list_items_fail_validation_unless_the_list_allows_them()
    {
        var errors = ContractValidator.Validate(new Lines
        {
            Required = [new Line(), null!],
            Optional = [null],
            Names = ["a", null!],
        });

        Assert.Equal(["required[1]: must not be null.", "names[1]: must not be null."], errors);
    }

    [Fact]
    public void A_null_order_line_is_refused()
    {
        var errors = MessageInspector.Validate(Registry.Get(typeof(OrderSubmitted)),
            Json("""{"orderId":"3f2c7a10-0000-4000-8000-000000000001","customerEmail":"a@example.com","lines":[null]}"""),
            ToolJson.Options);

        Assert.Contains("lines[0]: must not be null.", errors);
    }

    [Fact]
    public void Timeouts_and_oversized_frames_have_a_reason()
    {
        Assert.True(BrokerErrors.TryDescribe(new TimeoutException("The operation has timed out."), out var timeout));
        Assert.Contains("did not answer in time", timeout);

        var closed = new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 501, "FRAME_ERROR - frame_too_large"));
        Assert.True(BrokerErrors.TryDescribe(closed, out var frame));
        Assert.Contains("fails the same way", frame);
    }

    [Fact]
    public void A_refused_stream_is_told_from_a_call_on_another_queue()
    {
        var refused = new OperationInterruptedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 540,
            "NOT_IMPLEMENTED - basic.get not supported by stream queues queue 'orders' in vhost '/'"));

        Assert.True(RabbitConnection.IsStreamRefusal(refused, "orders"));
        Assert.False(RabbitConnection.IsStreamRefusal(refused, "orders_error"));
    }

    [Fact]
    public void Exceptions_from_writing_a_result_are_not_blamed_on_the_arguments()
    {
        var bound = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<int>("\"x\""));
        Assert.True(BrokerErrors.TryDescribeArguments("tool", bound, out var message));
        Assert.Contains("Invalid arguments for tool", message);

        var node = new Dictionary<string, object>();
        node["self"] = node;
        var written = Assert.ThrowsAny<JsonException>(() => JsonSerializer.SerializeToElement(node));
        Assert.False(BrokerErrors.TryDescribeArguments("tool", written, out _));
    }

    [Fact]
    public void Exceptions_from_inside_the_server_are_not_blamed_on_the_arguments()
    {
        var inside = Assert.ThrowsAny<ArgumentException>(() => new ManagementClient(new RabbitMcpOptions { ManagementUri = "http://broker" }, null!));

        Assert.False(BrokerErrors.TryDescribeArguments("tool", inside, out _));
    }

    [Fact]
    public void A_reason_too_long_for_a_header_is_refused()
    {
        QueueBrowser.CheckReason(null);
        QueueBrowser.CheckReason(new string('x', QueueBrowser.MaxReasonChars));

        Assert.Contains("at most", Assert.Throws<McpException>(() => QueueBrowser.CheckReason(new string('x', QueueBrowser.MaxReasonChars + 1))).Message);
    }

    [Fact]
    public void Enum_names_in_a_correction_become_numbers_and_nothing_else_changes()
    {
        var contract = new ContractRegistry([typeof(Graded)]).Get(typeof(Graded));

        var corrected = MessageInspector.WithEnumNumbers(contract, Json(
            """{"level":"High","levels":["Low",1],"price":"9.50","note":"High","unknown":"High","nested":{"level":"Low"}}"""));

        Assert.Equal(1, corrected.GetProperty("level").GetInt32());
        Assert.Equal([0, 1], corrected.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()));
        Assert.Equal("9.50", corrected.GetProperty("price").GetString());
        Assert.Equal("High", corrected.GetProperty("note").GetString());
        Assert.Equal("High", corrected.GetProperty("unknown").GetString());
        Assert.Equal(0, corrected.GetProperty("nested").GetProperty("level").GetInt32());
        Assert.Empty(MessageInspector.Validate(contract, corrected, MassTransitJson.Options));
    }

    [Fact]
    public void A_correction_that_cannot_be_read_is_left_for_validation_to_report()
    {
        var contract = new ContractRegistry([typeof(Graded)]).Get(typeof(Graded));
        var unreadable = Json("""{"level":"NoSuchLevel"}""");

        Assert.Equal(unreadable.GetRawText(), MessageInspector.WithEnumNumbers(contract, unreadable).GetRawText());
        Assert.NotEmpty(MessageInspector.Validate(contract, unreadable, MassTransitJson.Options));
    }

    [Theory]
    [InlineData("publish,respond")] // would become topology
    [InlineData("publish,request")] // would become republish
    [InlineData("read,publish")]
    [InlineData("read, publish")]
    [InlineData("read,publish:Order*")]
    [InlineData("1")]
    [InlineData("3")]
    public void A_role_entry_must_name_exactly_one_permission(string definition)
    {
        var ex = Assert.Throws<StartupException>(() => AccessControl.ParseRole("r", definition));

        Assert.Contains("unknown permission", ex.Message);
        Assert.Contains("separated by ';'", ex.Message);
    }

    [Theory]
    [InlineData("read", Permission.Read)]
    [InlineData("READ", Permission.Read)]
    [InlineData("Publish:Order*,Billing*", Permission.Publish)]
    [InlineData(" topology ", Permission.Topology)]
    public void Permission_names_keep_working_in_any_case(string definition, Permission expected)
    {
        Assert.Equal(expected, Assert.Single(AccessControl.ParseRole("r", definition).Grants).Permission);
    }

    [Fact]
    public void Help_describes_the_server_and_its_commands()
    {
        Assert.Contains("--version", ContractsCommand.ServerUsage);
        Assert.Contains("rabbit-mcp contracts diff", ContractsCommand.ServerUsage);
        Assert.Contains("rabbit-mcp contracts asyncapi", ContractsCommand.ServerUsage);
    }
}

public enum Grade { Low, High }

[RabbitMcp.Contracts.MessageContract("A message with enums, to correct.")]
public sealed class Graded
{
    public Grade Level { get; set; }
    public List<Grade> Levels { get; set; } = [];
    public decimal Price { get; set; }
    public string? Note { get; set; }
    public GradedPart? Nested { get; set; }
}

public sealed class GradedPart
{
    public Grade Level { get; set; }
}
