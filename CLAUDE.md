# rabbit-mcp — project guide for Claude

Contract-first MCP server for RabbitMQ (.NET 10, ModelContextProtocol 2.2, RabbitMQ.Client 7.2).
Every `[MessageContract]` record becomes a typed MCP tool; messages use the MassTransit wire
format but the server does **not** reference MassTransit packages.

The user writes in Georgian: answer in Georgian, keep code, identifiers, commit messages and
code comments in English. README.md is in Georgian.

## Commands

```bash
dotnet build RabbitMcp.slnx
dotnet test RabbitMcp.slnx
dotnet publish src/RabbitMcp.Server -c Release -o out   # MCP clients must run this binary, never `dotnet run`
docker compose up -d                                     # local RabbitMQ 4 (5672, UI 15672 guest/guest)
```

CI (`.github/workflows/ci.yml`) builds and tests every push/PR; it is the source of truth when
the local environment cannot restore NuGet packages.

## Layout

| Path | Responsibility |
|---|---|
| `src/RabbitMcp.Contracts` | `[MessageContract]` records. **No package dependencies** — services reference it too. |
| `src/RabbitMcp.Server/Contracts` | `ContractRegistry` (naming conventions), `ContractValidator` (DataAnnotations, nested paths) |
| `src/RabbitMcp.Server/Messaging` | `RabbitConnection`, MassTransit envelope, fault headers, `QueueBrowser` (peek/republish), `ReplyListener` + `ReplyInterpreter` (request/response) |
| `src/RabbitMcp.Server/Tools` | MCP tools and their result records (`ToolResults.cs` = outputSchemas) |
| `tests/RabbitMcp.Tests` | xUnit unit tests, no broker needed |

## Conventions (must not break)

- **MassTransit wire compatibility**: URN `urn:message:{Namespace}:{Type}`, exchange `{Namespace}:{Type}`,
  camelCase envelope fields (`messageId`, `requestId`, `responseAddress`, `messageType[]`, `message`, ...),
  content type `application/vnd.masstransit+json`, enums as numbers in envelopes.
  Reply exchange/queue: fanout, `durable:false`, `autoDelete:true` (= MassTransit `?temporary=true`).
  Verify changes against MassTransit source (`MassTransit/src`, tag v8.x) before claiming compatibility.
- **No silent message loss**: publisher confirms + `mandatory: true`; check exchange exists first;
  in republish, ack the source message only after the publish is confirmed; never truncate or
  re-serialize a body that is being moved.
- **Tool design**: one typed tool per contract (`publish_*` events, `request_*` requests);
  `UseStructuredContent = true` with a record return type; honest annotations
  (`ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld`); expected failures throw `McpException`
  (→ `isError: true` with a message the model can act on); anything that sends or removes messages is
  off by default behind `EnablePublish` / `EnableRepublish`.
- **stdout is the MCP transport**: all logging goes to stderr. Never `Console.WriteLine`.
- **Secrets**: the AMQP URI contains credentials — only ever log/return `BrokerAddress.Describe(uri)`.
- Passive declares (`ExchangeDeclarePassive`/`QueueDeclarePassive`) run on a throwaway channel:
  a 404 closes the channel.
- Pure logic stays testable without a broker (registry, validator, envelope, fault parsing,
  reply interpretation, `PendingRequests`). Add tests for every behaviour change.

## Specialised agents (`.claude/agents/`)

| Agent | Use it for |
|---|---|
| `contract-author` | adding or changing message contracts (events, requests, responses, versions) |
| `mcp-tool-designer` | designing or changing MCP tools: names, schemas, results, annotations, gating |
| `masstransit-compat-reviewer` | checking wire compatibility with MassTransit after messaging changes |
| `code-reviewer` | reviewing a diff against the guarantees above before merge |
| `test-engineer` | unit tests and Testcontainers integration tests |
| `ci-doctor` | failing GitHub Actions runs, build/restore/test errors |
| `docs-writer` | keeping the Georgian README in sync with the code |
