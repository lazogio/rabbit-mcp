// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using RabbitMcp.Server.Contracts;
using RabbitMcp.Server.Messaging;

namespace RabbitMcp.Server.Tools;

/// <summary>
/// Every contract as an MCP resource (contract://Name): what it means, its MassTransit names and the JSON schema of the
/// message and of each reply — the same schema the tools use. Clients can show or attach them without a tool call.
/// </summary>
public static class ContractResources
{
    public const string Scheme = "contract://";

    public static IEnumerable<McpServerResource> Create(ContractRegistry registry) =>
        registry.Contracts.Select(contract => McpServerResource.Create(
            () => Describe(contract),
            new McpServerResourceCreateOptions
            {
                UriTemplate = Scheme + contract.Name,
                Name = contract.Name,
                Title = $"{contract.Name} (v{contract.Version}, {(contract.IsRequest ? "request" : "event")})",
                Description = contract.Description,
                MimeType = "application/json",
            }));

    public static string Describe(ContractDescriptor contract)
    {
        var json = new JsonObject
        {
            ["name"] = contract.Name,
            ["kind"] = contract.IsRequest ? "request" : "event",
            ["tool"] = contract.ToolName,
            ["version"] = contract.Version,
            ["messageType"] = contract.MessageTypeUrn,
            ["exchange"] = contract.Exchange,
            ["description"] = contract.Description,
            ["schema"] = Schema(contract.Type),
        };
        if (contract.IsRequest)
        {
            json["responses"] = new JsonArray([.. contract.Responses.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["messageType"] = r.MessageTypeUrn,
                ["replyTool"] = ResponderContracts.ReplyToolName(r),
                ["schema"] = Schema(r.Type),
            })]);
        }
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? Schema(Type type) =>
        JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(type, serializerOptions: ToolJson.Options).GetRawText());
}
