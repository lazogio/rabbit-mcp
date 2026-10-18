// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using RabbitMcp.Contracts;
using RabbitMcp.Samples.Orders;
using RabbitMcp.Server.Cli;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;
using RabbitMcp.Server.Tools;

namespace RabbitMcp.Tests;

public class AsyncApiTests
{
    private static readonly string Warehouse = Path.Combine(AppContext.BaseDirectory, "asyncapi", "warehouse.yaml");
    private static readonly ContractRegistry Registry = new([], AsyncApiLoader.Load(Warehouse));

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<string> Check(string schema, string value, bool numericStrings = false) =>
        SchemaValidator.Validate(JsonNode.Parse(schema), J(value), numericStrings);

    // --- JSON Schema subset

    [Fact]
    public void Validator_checks_types_required_and_nested_paths()
    {
        const string schema = """
            {"type":"object","required":["id","lines"],"properties":{
              "id":{"type":"string","format":"uuid"},
              "email":{"type":["string","null"],"format":"email"},
              "lines":{"type":"array","minItems":1,"items":{"type":"object","required":["qty"],"properties":{"qty":{"type":"integer","minimum":1}}}}}}
            """;
        Assert.Empty(Check(schema, """{"id":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","email":null,"lines":[{"qty":2}]}"""));

        var errors = Check(schema, """{"id":"nope","email":"x","lines":[{"qty":0},{"qty":1.5},{}]}""");
        Assert.Contains("id: must be a valid uuid.", errors);
        Assert.Contains("email: must be a valid email.", errors);
        Assert.Contains("lines[0].qty: must be at least 1.", errors);
        Assert.Contains(errors, e => e.StartsWith("lines[1].qty: must be integer"));
        Assert.Contains("lines[2].qty: is required.", errors);
        Assert.Contains("lines: is required.", Check(schema, """{"id":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11"}"""));
        Assert.Contains("message: must be object, not an array.", Check(schema, "[]"));
    }

    [Theory]
    [InlineData("""{"type":"string","minLength":2,"maxLength":3}""", "\"a\"", "at least 2 characters")]
    [InlineData("""{"type":"string","pattern":"^[A-Z]+$"}""", "\"ab\"", "must match the pattern")]
    [InlineData("""{"enum":["a","b"]}""", "\"c\"", "must be one of \"a\", \"b\"")]
    [InlineData("""{"const":1}""", "2", "must be 1")]
    [InlineData("""{"type":"number","exclusiveMaximum":10}""", "10", "less than 10")]
    [InlineData("""{"type":"number","multipleOf":0.5}""", "1.2", "multiple of 0.5")]
    [InlineData("""{"type":"object","additionalProperties":false}""", """{"x":1}""", "x: is not allowed")]
    [InlineData("""{"type":"array","uniqueItems":true}""", "[1,1]", "unique")]
    [InlineData("""{"oneOf":[{"type":"string"},{"type":"string","minLength":1}]}""", "\"a\"", "exactly one")]
    [InlineData("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""", "true", "none of the allowed")]
    [InlineData("""{"type":"string","nullable":false}""", "null", "must be string")]
    public void Validator_reports_each_keyword(string schema, string value, string message) =>
        Assert.Contains(Check(schema, value), e => e.Contains(message));

    [Fact]
    public void MassTransit_decimal_strings_count_as_numbers_when_allowed()
    {
        const string schema = """{"type":"number","minimum":1}""";
        Assert.NotEmpty(Check(schema, "\"80\""));
        Assert.Empty(Check(schema, "\"80\"", numericStrings: true));
        Assert.Contains(Check(schema, "\"0.5\"", numericStrings: true), e => e.Contains("at least 1"));
        Assert.Empty(Check("""{"type":"object","properties":{"n":{"type":"string","nullable":true}}}""", """{"n":null}"""));
    }

    [Fact]
    public void Refs_are_inlined_and_recursion_is_kept_as_a_ref()
    {
        var root = JsonNode.Parse("""
            {"components":{"schemas":{"Sku":{"type":"string"},"Node":{"type":"object","properties":{"next":{"$ref":"#/components/schemas/Node"}}}}}}
            """)!;
        var inlined = SchemaInliner.Inline(JsonNode.Parse("""{"properties":{"sku":{"$ref":"#/components/schemas/Sku"},"node":{"$ref":"#/components/schemas/Node"}}}"""), root)!;
        Assert.Equal("string", inlined["properties"]!["sku"]!["type"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/Node", inlined["properties"]!["node"]!["properties"]!["next"]!["$ref"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => SchemaInliner.Inline(JsonNode.Parse("""{"$ref":"#/nope"}"""), root));
    }

    [Fact]
    public void Yaml_scalars_follow_the_core_schema()
    {
        var node = YamlJson.Parse("""
            a: 1
            b: 1.5
            c: true
            d: '1'
            e: null
            f: [x, 2]
            g: "true"
            """)!;
        Assert.Equal(1, node["a"]!.GetValue<long>());
        Assert.Equal(1.5m, node["b"]!.GetValue<decimal>());
        Assert.True(node["c"]!.GetValue<bool>());
        Assert.Equal("1", node["d"]!.GetValue<string>());
        Assert.Null(node["e"]);
        Assert.Equal(2, node["f"]![1]!.GetValue<long>());
        Assert.Equal("true", node["g"]!.GetValue<string>());
        Assert.Equal(1, YamlJson.Parse("""{"a":1}""")!["a"]!.GetValue<int>());
        Assert.Throws<InvalidOperationException>(() => YamlJson.Parse("a: [1"));

        // Bounds written in YAML are numbers the validator understands.
        var schema = YamlJson.Parse("type: integer\nminimum: 1\nmaximum: 2.5");
        Assert.Contains("at least 1", Assert.Single(SchemaValidator.Validate(schema, J("0"))));
        Assert.Contains("at most 2.5", Assert.Single(SchemaValidator.Validate(schema, J("3"))));
    }

    // --- AsyncAPI documents

    [Fact]
    public void The_warehouse_example_becomes_two_contracts()
    {
        Assert.Equal(new[] { "ReserveStock", "StockAdjusted" }, Registry.Contracts.Select(c => c.Name));

        var stock = Registry.FindByName("publish_stock_adjusted")!;
        Assert.True(stock.IsSchemaBased);
        Assert.True(stock.IsRaw);
        Assert.Equal("warehouse", stock.Exchange);
        Assert.Equal("stock.adjusted", stock.RoutingKey);
        Assert.Equal("StockAdjusted", stock.MessageTypeUrn);
        Assert.Equal("Warehouse.StockAdjusted", stock.TypeName);
        Assert.Equal("^[A-Z0-9-]{3,32}$", stock.Schema!["properties"]!["sku"]!["pattern"]!.GetValue<string>()); // $ref inlined
        Assert.StartsWith("The stock of a product changed", stock.Description);

        var reserve = Registry.FindByName("request_reserve_stock")!;
        Assert.True(reserve.UsesDefaultExchange);
        Assert.Equal("warehouse-reservations", reserve.RoutingKey);
        Assert.Equal(2, reserve.Version);
        Assert.Equal(new[] { "StockReserved", "OutOfStock" }, reserve.Responses.Select(r => r.Name));
        Assert.NotNull(reserve.Responses[1].Schema);
    }

    [Fact]
    public void Schema_contracts_are_validated_against_their_schema()
    {
        var reserve = Registry.FindByName("ReserveStock")!;
        Assert.Empty(MessageInspector.Validate(reserve, J("""{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","sku":"LAMP-01","quantity":2}"""), MassTransitJson.Options));
        var errors = MessageInspector.Validate(reserve, J("""{"orderId":"x","sku":"lamp","quantity":0}"""), MassTransitJson.Options);
        Assert.Equal(3, errors.Count);

        var reply = ReplyInterpreter.InterpretRaw(reserve, RawReply.From(new RabbitMQ.Client.BasicProperties { Type = "OutOfStock" },
            """{"orderId":"8d9e2b8e-1f3a-4f7e-9a51-0b1d7f2a6c11","available":-1}"""u8, 16_000));
        Assert.Equal("OutOfStock", reply.ResponseType);
        Assert.False(reply.ResponseCheck!.Valid);
    }

    [Fact]
    public void Tools_of_schema_contracts_show_the_files_schema()
    {
        var tool = ContractToolFactory.CreateTool(Registry.FindByName("ReserveStock")!);
        var input = tool.ProtocolTool.InputSchema;
        var message = input.GetProperty("properties").GetProperty("message");

        Assert.Equal("request_reserve_stock", tool.ProtocolTool.Name);
        Assert.Equal(100, message.GetProperty("properties").GetProperty("quantity").GetProperty("maximum").GetInt32());
        Assert.Contains("message", input.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(input.GetProperty("properties").TryGetProperty("timeoutSeconds", out _));

        using var resource = JsonDocument.Parse(ContractResources.Describe(Registry.FindByName("ReserveStock")!));
        Assert.Equal("uuid", resource.RootElement.GetProperty("schema").GetProperty("properties").GetProperty("orderId").GetProperty("format").GetString());
    }

    [Fact]
    public void AsyncApi_2_documents_and_masstransit_messages_load_too()
    {
        var contracts = AsyncApiLoader.Parse("""
            asyncapi: 2.6.0
            info: {title: Billing, version: '1'}
            channels:
              billing.invoices:
                bindings:
                  amqp: {is: queue, queue: {name: invoices}}
                subscribe:
                  message:
                    oneOf:
                      - $ref: '#/components/messages/InvoiceIssued'
              Billing.Contracts:InvoicePaid:
                publish:
                  message:
                    name: InvoicePaid
                    x-rabbit-mcp: {format: masstransit, urn: 'urn:message:Billing.Contracts:InvoicePaid'}
                    payload: {type: object}
            components:
              messages:
                InvoiceIssued:
                  payload: {type: object, required: [number], properties: {number: {type: string}}}
            """);

        var issued = Assert.Single(contracts, c => c.Name == "InvoiceIssued");
        Assert.Equal("invoices", issued.RoutingKey);
        Assert.True(issued.UsesDefaultExchange);
        var paid = Assert.Single(contracts, c => c.Name == "InvoicePaid");
        Assert.False(paid.IsRaw);
        Assert.Equal("Billing.Contracts:InvoicePaid", paid.Exchange); // MassTransit's exchange from the URN
        Assert.Equal("", paid.RoutingKey);
    }

    [Theory]
    [InlineData("asyncapi: 1.2.0\ninfo: {title: x}", "not supported")]
    [InlineData("info: {title: x}", "'asyncapi'")]
    [InlineData("asyncapi: 3.0.0\nchannels: {c: {messages: {M: {x-rabbit-mcp: {format: masstransit}, payload: {}}}}}", "need x-rabbit-mcp.urn")]
    [InlineData("asyncapi: 3.0.0\nchannels: {c: {messages: {M: {payload: {}}}}}", "no destination")]
    [InlineData("asyncapi: 3.0.0\nchannels: {c: {address: q, messages: {M: {payload: {$ref: '#/nope'}}}}}", "cannot be resolved")]
    public void Broken_documents_are_readable_errors(string yaml, string message)
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => AsyncApiLoader.Parse(yaml));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Patterns_and_policies_see_the_document_name()
    {
        Assert.True(new PublishPolicy("Warehouse.*", null).IsAllowed(Registry.FindByName("StockAdjusted")!));
        Assert.False(new PublishPolicy("Billing.*", null).IsAllowed(Registry.FindByName("StockAdjusted")!));
    }

    // --- export and diff

    [Fact]
    public void Csharp_contracts_round_trip_through_asyncapi()
    {
        var original = ContractRegistry.FromAssemblies(typeof(OrderSubmitted).Assembly);
        var document = AsyncApiExporter.Export(original, "Orders");
        var loaded = new ContractRegistry([], AsyncApiLoader.Parse(document.ToJsonString()));

        Assert.Equal("3.0.0", document["asyncapi"]!.GetValue<string>());
        foreach (var c in original.Contracts)
        {
            var back = loaded.FindByName(c.Name)!;
            Assert.Equal(c.ToolName, back.ToolName);
            Assert.Equal(c.MessageTypeUrn, back.MessageTypeUrn);
            Assert.Equal(c.Exchange, back.Exchange);
            Assert.Equal(c.RoutingKey, back.RoutingKey);
            Assert.Equal(c.Format, back.Format);
            Assert.Equal(c.Version, back.Version);
            Assert.Equal(c.Responses.Select(r => r.MessageTypeUrn), back.Responses.Select(r => r.MessageTypeUrn));
        }
        Assert.DoesNotContain(ContractDiff.Compare(original, loaded), x => x.Severity == ContractDiff.Breaking);
    }

    [Fact]
    public void Cli_exports_asyncapi_and_diffs_asyncapi_files()
    {
        var output = new StringWriter();
        Assert.Equal(0, ContractsCommand.Run(["asyncapi", Warehouse, "--title", "Copy"], output, new StringWriter()));
        var exported = JsonNode.Parse(output.ToString())!;
        Assert.Equal("Copy", exported["info"]!["title"]!.GetValue<string>());

        var dir = Directory.CreateTempSubdirectory("rabbit-mcp-asyncapi-").FullName;
        var before = Path.Combine(dir, "before.json");
        var after = Path.Combine(dir, "after.yaml");
        File.WriteAllText(before, output.ToString());
        File.WriteAllText(after, File.ReadAllText(Warehouse).Replace("required: [sku, delta]", "required: [sku, delta, reason]"));

        var diff = new StringWriter();
        Assert.Equal(1, ContractsCommand.Run(["diff", before, after], diff, new StringWriter()));
        Assert.Contains("StockAdjusted reason: Became required", diff.ToString());
    }
}
