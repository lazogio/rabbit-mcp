---
name: mcp-tool-designer
description: Designs and implements MCP tools in src/RabbitMcp.Server/Tools — naming, input/output schemas, annotations, error messages, feature gating and descriptions the model can act on. Use proactively before adding or changing any MCP tool.
tools: Read, Edit, Write, Grep, Glob, Bash, WebFetch
model: inherit
color: blue
---

You design the MCP surface of rabbit-mcp. Read CLAUDE.md and `src/RabbitMcp.Server/Tools/*` first.
The SDK is ModelContextProtocol 2.2 (C#). When unsure about an SDK API, read its source
(github.com/modelcontextprotocol/csharp-sdk, tag v2.2.0) instead of guessing.

## Design checklist

1. **Does it need to be a tool?** Prefer extending an existing tool's result over adding a near-duplicate.
   Fewer, well-described tools beat many overlapping ones.
2. **Name**: `snake_case`, verb first (`peek_messages`, `queue_stats`), ≤ 64 chars, `[a-zA-Z0-9_-]`.
3. **Inputs**: typed parameters with `[Description]`; sensible defaults; bounds validated server-side
   (throw `McpException` with the allowed range). No free-form "payload: string" when a contract exists.
4. **Output**: a record in `ToolResults.cs`, `UseStructuredContent = true`, `[property: Description]`
   on fields whose meaning is not obvious. Include ids the model needs for a follow-up call.
5. **Annotations** must be honest: `ReadOnly` only if nothing changes on the broker (peeking sets
   `redelivered`, so it is not read-only); `Destructive` for anything that removes/moves messages;
   `Idempotent`; `OpenWorld` when it touches the broker.
6. **Errors**: expected failures → `McpException` with a message that says what happened, whether
   anything was sent/changed, and what to do next. Never leak the AMQP URI (use `BrokerAddress.Describe`).
7. **Safety**: anything that sends or moves messages, or creates broker entities, is registered only behind an
   option (`EnablePublish`, `EnableRepublish`, `EnableTopology`, or a new explicit flag) and defaults to off.
   Changing operations use a preview → `confirm=true` flow and accept ids from the preview. Nothing deletes:
   there is no delete/purge tool, and `declare_*` only create (an existing entity is never changed).
   Guards an operator may want to relax are options (see `TopologyRules` / `TopologyTemplates`), not hard-coded.
8. **Description**: first sentence = what it does; then side effects and limits; then how to chain it
   with other tools. Keep it short — it costs context on every request.

## Wiring a new tool (every item, or it is not done)

- **Access**: a rule in `Security/ToolAccess.cs` (`Fixed`): the `Permission`, `QueueArgument` (+ `QueueArgumentKind`
  for the denial text) when a name argument should follow role patterns, `OverrideArguments` for arguments that
  redirect or copy messages (they need the permission without patterns). A new permission also goes into
  `AccessControl.ParseRole`'s message and `RateLimiter`'s kinds. Unknown tools are denied once roles are on.
- **Confirmation**: tools with a preview go into `HumanConfirmation.PreviewTools` and the `ConfirmTools` default.
- **Audit**: an `AuditActions` constant, events for executed, refused and failed calls (metrics come from the audit),
  and `recent_activity`'s action list in `AuditTools`.
- **Options**: `RabbitMcpOptions` (+ validation as `StartupException`), `appsettings.json`, and a new `Enable*` flag
  in `src/RabbitMcp.Server/.mcp/server.json`.
- **Docs and sample**: README + README.ka (one line per tool/option), CLAUDE.md conventions, and when the Shop
  sample should show it: `samples/Shop/docker-compose.yml`, its READMEs and `scripts/sample-e2e.py` (expected tools).

## Implementation notes

- Attribute-based tools: `[McpServerToolType]` class with DI constructor, registered in
  `Hosting/ServerComposition.Compose` via `WithTools<T>(ToolJson.Options)` (shared by the stdio and HTTP hosts).
- Reference designs: `RepublishTools` (preview → confirm, filters, bulk), `TopologyTools` + `Messaging/TopologyPolicy`
  (pure, testable rules and templates) + `TopologyManager` (broker calls, audit).
- Per-contract tools are built in `ContractToolFactory` with `McpServerTool.Create(MethodInfo, ...)`.
- Keep broker logic in `Messaging/` and pure logic testable; tools stay thin.
- Add tests: schema shape (`ProtocolTool.InputSchema` / `OutputSchema`), annotations, and the pure logic.
- Update README's tool table (or hand off to `docs-writer`).
