# Changes and decisions (newest first)

### 2026-10-13 Phase 2 process & distribution — branch phase-2/process (70b5c25..), fast-forwarded into main 2026-10-13
- What: Dependabot (NuGet via Directory.Packages.props + Actions, weekly, grouped); release workflow (`v*` tag →
  self-contained single-file binaries linux-x64/arm64, win-x64, osx-x64/arm64 + SHA256SUMS → GitHub Release);
  `ServerVersion` reports the assembly version (tag) as MCP `serverInfo.version`; `scripts/smoke-test.sh`
  (initialize → tools/list over stdio against the published binary) in CI and release; PR template.
- Decision: platforms default to all five — the user did not choose; dropping one is a one-line matrix change.
- Decision: not trimmed / no AOT — MCP SDK + System.Text.Json reflect over contract types; ~31–33 MB per package.
- Decision: all RIDs cross-published from one ubuntu runner (self-contained, non-AOT needs no native toolchain);
  only linux-x64 is smoke-tested by execution; macOS binaries are unsigned (README documents `xattr`).
- Decision: Dependabot ignores MassTransit >= 9 (commercial; test-only dependency).
- Verified: smoke test fails on a fake server with polluted stdout / missing tools / missing outputSchema.

### 2026-10-12 Phase 1 reliability — branch phase-1/reliability (f7306bd..12c0df5), fast-forwarded into main 2026-10-13
- What: BUG-002/003 fixed; peek and republish preview refuse queues with active consumers unless `force=true`;
  `tests/RabbitMcp.IntegrationTests` (Testcontainers RabbitMQ 4-management + MassTransit 8.5.10 consumers, 13 tests);
  CI runs on every branch push, separate integration job, failures reported as annotations (GitHubActionsTestLogger 3.0.5).
- Why: MassTransit compatibility had only been checked by reading source; now proven end to end.
- Result: first run passed everything except one test whose assumption was wrong (see gotchas: MT transport headers);
  request/response, Fault<T>, temporary reply exchange flags, republish from _error all work against real MassTransit.
- Decision: integration tests call server components directly (not over MCP stdio) — they test broker semantics;
  MCP schema shape stays covered by unit tests.

### 2026-10-12 Project memory agent — commit 0742939
- What: `project-memory` subagent (`memory: project`) with bugs/changes/gotchas files, seeded from history.
- Why: keep knowledge about changes and bugs across sessions.

### 2026-10-12 Project subagents + CLAUDE.md — commit 25db129
- What: `.claude/agents/` contract-author, mcp-tool-designer, masstransit-compat-reviewer, code-reviewer,
  test-engineer, ci-doctor, docs-writer; `CLAUDE.md` with conventions and guarantees.
- Decision: reviewers are read-only (no Edit/Write) so a review cannot silently change code.

### 2026-10-12 CI workflow — commits ee0db8b, 84e14d7
- What: `.github/workflows/ci.yml`: restore → build (Release) → test (trx artifact) on push/PR;
  on `main` also `dotnet publish` and artifact `rabbit-mcp-linux-x64` (14 days).
- Why: the authoring environment cannot reach NuGet, so CI is the first real build and the verifier.
- Impact: first CI run built and passed every test; see BUG-001 for the action-version follow-up.

### 2026-10-12 README rewritten concise — commit e515d96
- What: README (Georgian) cut from 178 to 114 lines without dropping facts; fixed section order.

### 2026-10-12 Request/response contracts — commit c988032
- What: `[MessageContract(Responses = new[] {...})]` → `request_*` tool; `ReplyListener` (temporary reply
  exchange+queue, `PendingRequests` by requestId), `ReplyInterpreter` (declared response validated,
  `Fault<T>` → `faulted`, unknown type), sample `GetOrderStatus` → `OrderStatus | OrderNotFound`.
- Why: the user asked that publishers can hear back from consumers (request/response chosen over discovery).
- Decision: response types are declared on the attribute, not via a marker interface — an interface would
  add an extra URN to MassTransit's `messageType` list and extra exchanges on the consumer side.
- Decision: reply exchange declared fanout, `durable:false`, `autoDelete:true` to match MassTransit's
  `?temporary=true` address parsing (`RabbitMqEndpointAddress.cs`), avoiding PRECONDITION_FAILED.
- Impact: `EnablePublish` now gates `publish_*` and `request_*`. Server is still requester only —
  it cannot act as a consumer/responder (open idea).

### 2026-10-12 Initial server — commit 9a12b6e
- What: contract registry, DataAnnotations validator (nested paths), MassTransit envelope writer/reader,
  typed `publish_*` tools, `list_contracts`, `validate_message`, `queue_stats`, `peek_messages`,
  `republish_from_error`, 40+ unit tests, docker-compose.
- Decision: no MassTransit package dependency — MassTransit v9 is commercial and v8 maintenance ends
  after 2026; the wire format is implemented directly on RabbitMQ.Client 7.2.
- Decision: built from scratch rather than on existing projects: messageaid/mcp (C#) was abandoned on a
  preview SDK with one tool and BSL license; StevePic95/rabbitmq-masstransit-mcp (TS) had no contracts
  and unsafe republish (see gotchas).
- Decision: AMQP for everything (passive declares, basic.get) instead of the Management HTTP API;
  consequence: queues cannot be listed (`list_queues` is an open idea).
