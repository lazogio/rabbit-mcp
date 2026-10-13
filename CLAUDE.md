# rabbit-mcp — project guide for Claude

Contract-first MCP server for RabbitMQ (.NET 10, ModelContextProtocol 2.2, RabbitMQ.Client 7.2).
Every `[MessageContract]` record becomes a typed MCP tool; messages use the MassTransit wire
format but the server does **not** reference MassTransit packages.

The user writes in Georgian: answer in Georgian, keep code, identifiers, commit messages and
code comments in English. README.md (for users: install, tools, configuration, security) and CONTRIBUTING.md
(for maintainers: build, tests, CI, release, agents) are in Georgian — keep maintainer material out of the README.

## Commands

```bash
dotnet build RabbitMcp.slnx
dotnet test tests/RabbitMcp.Tests                 # unit, no broker
dotnet test tests/RabbitMcp.IntegrationTests      # Testcontainers RabbitMQ 4 + MassTransit 8 consumers (needs Docker)
dotnet publish src/RabbitMcp.Server -c Release -o out   # MCP clients must run this binary, never `dotnet run`
docker compose up -d                                     # local RabbitMQ 4 (5672, UI 15672 guest/guest)
scripts/smoke-test.sh out/rabbit-mcp                     # MCP stdio session against the published binary
docker build -t rabbit-mcp . && scripts/smoke-test-http.sh docker:rabbit-mcp   # container (HTTP transport)
out/rabbit-mcp contracts diff OLD.dll NEW.dll             # wire-compatibility check (CLI, not MCP)
git tag vX.Y.Z && git push origin vX.Y.Z                 # release: binaries for 5 platforms + GitHub Release
```

CI (`.github/workflows/ci.yml`) builds and tests every push (any branch) and PR, with a separate
integration job; failing tests appear as annotations (readable via the REST API without auth).
It is the source of truth when the local environment cannot restore NuGet packages.
`.github/workflows/release.yml` builds on `v*` tags (and build-only when the workflow file changes).
Dependabot keeps MassTransit < 9 (commercial) — never accept a 9.x bump.

## Layout

| Path | Responsibility |
|---|---|
| `src/RabbitMcp.Contracts` | `[MessageContract]` records. **No package dependencies** — services reference it too. |
| `src/RabbitMcp.Server/Contracts` | `ContractRegistry` (naming conventions), `ContractValidator` (DataAnnotations, nested paths), `ContractAssemblyLoader` (`ContractAssemblies` option) |
| `src/RabbitMcp.Server/Messaging` | `RabbitConnection`, MassTransit envelope, fault headers, `QueueBrowser` (peek/republish), `ReplyListener` + `ReplyInterpreter` (request/response), `ExistenceCache`, `ManagementClient` (HTTP API, `list_queues` only), responder mode (`ResponderService`, `RequestInbox`, `ResponderEnvelopes`) |
| `src/RabbitMcp.Server/Tools` | MCP tools and their result records (`ToolResults.cs` = outputSchemas) |
| `tests/RabbitMcp.Tests` | xUnit unit tests, no broker needed |
| `tests/RabbitMcp.ExternalContracts` | a user-style contracts DLL, loaded at runtime by loader tests and smoke tests |
| `samples/Shop` | runnable sample (contracts, MassTransit service, compose); CI walks it with `scripts/sample-e2e.py` — keep it working when tools change |
| `tests/RabbitMcp.IntegrationTests` | end-to-end against real RabbitMQ + MassTransit v8 (`BrokerFixture`); test-only contracts in `TestContracts.cs` |

## Conventions (must not break)

- **MassTransit wire compatibility**: URN `urn:message:{Namespace}:{Type}`, exchange `{Namespace}:{Type}`,
  MassTransit writes decimals as JSON strings (read both: `MassTransitDecimalConverter`); camelCase envelope fields (`messageId`, `requestId`, `responseAddress`, `messageType[]`, `message`, ...),
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
  The same applies to `ManagementUri` and the Basic auth header.
- `RabbitMcp.Contracts` keeps `AssemblyVersion` 1.0.0.0 so external contract DLLs bind to the bundled copy;
  change it only for a breaking change to `[MessageContract]`.
- License: MIT for the whole repository. Every `.cs` file (and scripts/Dockerfile) starts with
  `// Copyright (c) 2026 lazogio` + `// SPDX-License-Identifier: MIT` (IDE0073 warns otherwise).
- Startup configuration errors: throw `StartupException` → one line on stderr, exit code 1 (never a stack trace on stdout).
- Two hosts share `ServerComposition.Compose`: stdio (`Program.cs`) and HTTP (`HttpHosting.Build`, testable with
  TestServer). HTTP must never run unauthenticated unless `HttpAllowAnonymous`; keep the Origin check.
- **Every operation that sends or moves messages is audited** (`AuditLog`, action + outcome + ids + client),
  including failed and refused attempts. New sending tools must write audit events and respect `PublishPolicy`.
- Responder mode (`RespondTo`) is manual: requests are acked on arrival and kept in memory until the model answers
  (`reply_*`) or rejects (`Fault<T>`). Replies go to `responseAddress`, faults to `faultAddress ?? responseAddress`;
  a reply that fails validation is not sent and the request stays pending. The responder queue name is
  deterministic and exclusive (`{ClientName}-responder`): one active instance, others stand by (405 RESOURCE_LOCKED).
- **Never trust message content for routing**: envelope addresses and AMQP headers are written by whoever published
  the message. Responder replies only to temporary reply endpoints (`CheckTemporaryExchangeAsync`); republish does not
  follow an `MT-Fault-InputAddress` that disagrees with the error queue's owner (`RepublishTarget.Conflict`).
- Broker exceptions: tools may let RabbitMQ exceptions propagate — the call-tool filter in `ServerComposition` turns
  them into readable `McpException`s (`BrokerErrors`). Sending paths still catch `Exception` themselves to audit.
- `contracts diff` compares the System.Text.Json schema with `MassTransitJson` naming; keep it in sync if the
  envelope serialization options change. The CLI writes to stdout — it is never run as an MCP server.
- **Roles** (`Security/AccessControl`, `ToolAccess`): HTTP callers get permissions read/publish/request/republish/respond
  from `Roles`/`UserRoles`/`DefaultRoles`/JWT roles claim. The MCP filters in `ServerComposition` hide tools in
  tools/list and re-check tools/call (audited as `access denied`). **Every new tool needs a rule in `ToolAccess`**
  (unknown tools are denied once roles are on). Stdio is unrestricted; Enable*/PublishAllow stay the ceiling.
- Tools get the MCP client via an injected `McpServer? server = null` parameter (excluded from the schema).
- **Tracing**: every send starts a `Telemetry.StartSend` span and injects it (`Telemetry.Inject`) — MassTransit 8
  reads `MT-Activity-Id` (W3C id) from the AMQP headers, other consumers `traceparent`. Metrics come from
  `AuditLog.Write`, so new operations get metrics by auditing. Never add a console exporter.
- Passive declares (`ExchangeDeclarePassive`/`QueueDeclarePassive`) run on a throwaway channel:
  a 404 closes the channel.
- Pure logic stays testable without a broker (registry, validator, envelope, fault parsing,
  reply interpretation, `PendingRequests`). Add tests for every behaviour change.

## Project memory

Knowledge about past changes, bugs and gotchas lives in `.claude/agent-memory/project-memory/`
(`MEMORY.md` index, `bugs.md`, `changes.md`, `gotchas.md`) and is committed with the code.

- **Before** debugging a failure or designing a change: ask `project-memory` to recall related entries
  (or grep those files yourself).
- **After** fixing a bug, landing a feature/decision, or a CI break: ask `project-memory` to record it,
  with the commit SHA.

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
| `project-memory` | recording and recalling changes, bugs, decisions and gotchas |
