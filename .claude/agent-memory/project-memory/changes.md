# Changes and decisions (newest first)

### 2026-10-13 Phase 5 / OpenTelemetry — branch phase-5/telemetry, fast-forwarded into main
- What: `Telemetry` (ActivitySource + Meter "RabbitMcp"); spans publish/request/republish with messaging.* tags;
  headers `traceparent`/`tracestate` + `MT-Activity-Id`; metrics `rabbitmcp.operations` (from audit events) and
  `rabbitmcp.request.duration`; OTLP (traces + metrics, incl. MCP SDK "Experimental.ModelContextProtocol") when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set (OpenTelemetry 1.19.1, `UseOtlpExporter`).
- Decision: republish = new trace with a link to the original send (not a child of it) — retries are separate work.
- Decision: telemetry only via System.Diagnostics in core code; OTel SDK wired in Program.cs only.
- Verified (first run green): MassTransit consume span shares the trace for publish, request, and republish.

### 2026-10-13 Phase 3 review — branch review/phase-3 (27cb9e4), fast-forwarded into main
- What: user asked to review phase 3; found and fixed BUG-004..007 (path separator, name clash, load error text,
  list_queues paging). Not changed (noted): https Management API with self-signed certs has no trust option (by design,
  no insecure switch); a contracts DLL depending on a *newer* version of an assembly the server ships cannot load.

### 2026-10-13 Phase 4 control — branch phase-4/control, fast-forwarded into main 2026-10-13
- What: `PublishPolicy` (PublishAllow/PublishDeny, `*` patterns over name/tool/type/URN, deny wins) applied at tool
  registration and inside `ContractPublisher`; `AuditLog` (stderr + optional JSONL `AuditLogPath`, payload opt-in)
  for publish/request/republish incl. errors and partial republish; MCP client from injected `McpServer`.
- Decision: patterns are exact with `*` wildcards (not substring) — "Order" does not allow "OrderSubmitted".
- Decision: audit file failures after a broker operation are logged, not thrown (the operation already happened);
  an unwritable audit file is caught at startup instead (fail fast).
- Decision: payload excluded from audit by default (PII); ids, contract, exchange, client always recorded.
- Verified: all green first run; CI smoke step proves deny removes request_* tools in the real binary and the audit
  file is created at startup.

### 2026-10-13 Phase 3 usability — branch phase-3/usability, fast-forwarded into main 2026-10-13
- What: `RabbitMcp:ContractAssemblies` (external DLLs, deps probed next to them, samples only when unset,
  readable startup error + exit 1); `ExistenceCache` for exchange checks (30 s, positive only, invalidated on 404);
  optional `list_queues` via Management API (`ManagementUri`); `RabbitMcp.Contracts` nupkg in releases.
- Decision: `RabbitMcp.Contracts` AssemblyVersion fixed at 1.0.0.0 — release builds set Version from the tag, and
  external DLLs compiled against another version would otherwise fail to bind to the bundled copy (single file).
- Decision: Management API is optional (tool registered only with `ManagementUri`) — user did not answer
  whether the API is reachable; this works either way.
- Decision: list_queues uses server-side pagination (500/page, max 20 pages) + the API's case-insensitive name filter;
  error-queue filter uses `use_regex=true` with `_(error|skipped)$` (verified in rabbit_mgmt_util.erl).
- Verified: everything green on first CI run; external contracts load in the single-file binary (smoke test);
  deleted-exchange publish surfaces as OperationInterruptedException 404 against real RabbitMQ.

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
