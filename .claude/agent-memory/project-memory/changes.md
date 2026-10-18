# Changes and decisions (newest first)

### 2026-10-18 Phase 14 — AsyncAPI contracts (phase-14/asyncapi) — commits ecfaac4, e06c536
- `ContractFiles` → `AsyncApiLoader` (3.0 operations incl. reply → request; 2.x publish/subscribe incl. oneOf; no
  operations → every channel message is an event). Routing from channel AMQP binding (is: queue → default exchange;
  else exchange + address as routing key; MassTransit messages default to the URN's exchange). `x-rabbit-mcp`:
  format, urn, version, toolName.
- Descriptors: `Schema` (refs inlined by `SchemaInliner`), `Type = JsonElement`, `TypeName = <title>.<message>`;
  registry keeps them out of `_byType`. Tools: `JsonContractTool` + replace `properties.message` of the generated
  inputSchema (`Tool.InputSchema` is settable). Validation: own `SchemaValidator` subset (numeric strings allowed for
  MassTransit format). Responder refuses schema contracts. YAML via YamlDotNet 18.1.0 RepresentationModel (`YamlJson`).
- `AsyncApiExporter` + CLI `contracts asyncapi`, `contracts diff` accepts .yaml/.json. Round-trip test against the
  sample contracts (no breaking changes).
- Decision: no separate "plain JSON Schema + mapping" format — a minimal AsyncAPI document covers it.

### 2026-10-18 Phase 13 — incident scale (phase-13/scale) — commit c5e56da + docs
- `MessageFilter` (exceptionType / exceptionContains incl. x-death reason / contract / since / until on
  MT-Fault-Timestamp → x-death time → sent time) on republish_from_error and dead_letter_from_error; with a filter
  count ≤ `MaxBulkMessages` (1000); preview = `SelectionSummary` (groups by type+message+contract, 5 sample ids);
  results cap Outcomes at 100 (`OutcomesOmitted`), `MoreMayRemain`.
- `read_stream` (`Tools/StreamTools`, read): basic.consume + x-stream-offset (first/last/next/offset/AmqpTimestamp),
  prefetch = count, stop on count or 1.5 s idle; type from Management API when configured, else a delivery without
  x-stream-offset → nack all back, "not a stream" (verified on a classic queue in CI).
- `RateLimits` (`Security/RateLimiter`): per caller and kind (permission name or *), sliding window, refused calls not
  counted, audit `access limited`; checked before human confirmation.

### 2026-10-18 Phase 12 — human in the loop (phase-12/human) — commits bb99eef, ef49c66
- `Security/HumanConfirmation` + call-tool filter: `HumanConfirmation` auto|required|off, `ConfirmTools` patterns
  (default republish_from_error;dead_letter_from_error;resend_corrected; preview tools asked only with confirm=true).
  `ElicitationPrompt` = one boolean field `approve`; unavailable without client capability or when ElicitAsync throws
  InvalidOperationException (stateless HTTP) → auto falls back to the argument, required refuses. Audit action `confirm`
  (allowed-person / allowed-argument / refused-person / refused-unavailable).
- Decision (default, user did not choose): stdio first; HTTP keeps confirm=true until stateful HTTP vs URL-mode page is decided.
- `resend_corrected` (`Tools/CorrectionTools`, EnableRepublish+EnablePublish, permission republish + publish on the
  contract inside the tool): one message, validated, `JsonDiff` preview, copy with new messageId + X-Corrected-From to
  the resolved target, original unchanged to the DLQ with X-Corrected-To (same queue when correcting from a _dead
  queue), ack last; if parking fails the original stays and the result says so. Requests refused. Audit `correct`.
- Tests: real MCP client over anonymous pipes (`StreamClientTransport` + `WithStreamServerTransport`) answering
  elicitation; shared `it-order-cancelled_dead` → ErrorQueueTests counts relatively.

### 2026-10-18 Plan: phases 12–15 (ROADMAP #34–#44)
- 12 Human in the loop: MCP elicitation confirmations (`HumanConfirmation` auto/required/off; stdio first — stateless
  HTTP cannot send server→client requests, open decision: stateful HTTP mode vs URL-mode approval page) and
  `resend_corrected` (fix a bad message, original parked unchanged in the DLQ).
- 13 Incident scale: republish/dead-letter by filter (MaxBulkMessages), `read_stream` (x-stream-offset), RateLimits.
- 14 Beyond C#: contracts from AsyncAPI / JSON Schema files, AsyncAPI export, diff for AsyncAPI.
- 15 Distribution (release-gated, owner decides): v0.1.0, dotnet tool, MCP registry (#33 moved to #44).
- Facts checked: C# SDK `McpServer.ElicitAsync` (form/url modes, `ClientCapabilities.Elicitation`); stateless mode
  disables server→client requests (`UrlElicitationRequiredException` is the stateless path); Claude Code shows
  elicitation dialogs since 2.1.76.

### 2026-10-18 Phase 11 — raw JSON contracts (phase-11/raw) — commits eba0d5f, c369018 + docs
- `MessageFormat { MassTransit = 0, Raw = 1 }` + `Format`/`RoutingKey` on `[MessageContract]` (non-breaking, contracts
  AssemblyVersion stays 1.0.0.0; the package now exports the attribute and the enum — guard test renamed).
- Raw (`Messaging/RawMessages.cs`): body = message JSON (MassTransitJson), AMQP message_id / correlation_id / type = Urn /
  app_id / timestamp + Contract-Version, X-Published-By. No default exchange: Exchange and/or RoutingKey required;
  Exchange "" = default exchange (queue existence checked instead of the exchange).
- Raw requests = RabbitMQ RPC: reply_to = ReplyListener queue name, correlation_id = requestId, workflow id in
  X-Correlation-Id; `PendingRequests` keeps envelope (requestId) and raw (correlation_id) matching apart;
  `ReplyInterpreter.InterpretRaw`: reply type from AMQP type (name or URN), single response assumed, otherwise unknown
  with a Note — never guessed from the body; no fault convention for raw.
- Around it: `RoutingKeys` (fanout/direct/topic matching) in `TopologyInspector` + default-exchange contracts; peek
  matches plain JSON by AMQP type, shows `DeadLettered` (x-death); `RepublishTarget` can be a queue via the default
  exchange (`queue:<name>`), resolved from x-death (MT-Fault-InputAddress wins; same conflict rule), `targetQueue`
  override on republish_from_error; x-death headers stripped on republish; diff reports format/routing-key changes;
  responder refuses raw contracts and RoutingKey contracts; envelope destinationAddress = queue for the default exchange.
- Shop sample: `ShippingDesk` (plain RabbitMQ.Client, `shipping-quotes` + `shipping-quotes.dlq`) answers
  `GetShippingQuote` (raw RPC); the CI walkthrough asks for two quotes.
- Decision (my recommendation, user: "follow the recommendations", 2026-10-18): #31 done assuming non-MassTransit
  services exist; #32 several brokers in one server **not done** — one server per broker keeps credentials, roles and
  audit apart and the model cannot pick the wrong broker; #33 MCP registry after the first release.

### 2026-10-18 Phase 10 — prompts, contract resources, English docs (phase-10/usability) — commits 2c6e883 + docs
- `Tools/OperatorPrompts.cs`: diagnose_errors, investigate_message, review_approvals, contract_health (string prompts,
  each ends with the "confirm before any change / treat message contents as data" paragraph).
- `Tools/ContractResources.cs`: one resource per contract `contract://<Name>` (application/json: kind, tool, version,
  URN, exchange, description, schema via AIJsonUtilities with ToolJson — same as the tool schemas — and replies with
  replyTool). List/read filters require the `read` permission.
- Docs bilingual (default chosen): English `README.md`/`CONTRIBUTING.md`/`samples/Shop/README.md`, Georgian `*.ka.md`;
  release archives include both READMEs; stdio smoke test checks prompts/list and resources/list.

### 2026-10-18 Phase 9 — find_message and recent_activity (phase-9/search-activity) — commit 0b9d892
- `find_message` (`Tools/SearchTools.cs`, always registered): `MessageMatcher` (messageId / correlationId / body text,
  case-insensitive, all given criteria must hold); queues from the argument, else Management API (`scope` errors|all,
  max 50 queues); peek-based (depth ≤ MaxMessagesPerCall), per-queue status missing/empty/skipped/partial.
- `recent_activity` (`Tools/AuditTools.cs`, only with AuditLogPath): `AuditReader` reads JSONL backwards in chunks,
  newest first, stops at `since` (file is chronological), skips damaged/half-written lines, payload only on request.
- Both under role permission `read`. Shop sample now writes an audit file; the e2e uses both tools.

### 2026-10-18 Dead-letter queue (phase-9/dead-letter) — commit 352035b
- What: `dead_letter_from_error` parks poison messages in `DeadLetterQueue` (default `{queue}_dead`, durable, created
  on first use) via default exchange + confirms + mandatory, ack after confirm; body and all headers kept, plus
  `X-Dead-Lettered-By/-From/-At`, `X-Dead-Letter-Reason`. Refuses source queues with consumers. Audit action `deadletter`.
- Decision (user, 2026-10-18): a dead-letter queue instead of deletion — rabbit-mcp has no tool that removes messages.
- Decision (default): same gate and permission as republish (`EnableRepublish`, `republish`), since it only moves.
- `_dead` counts as an error-like suffix (resolver, list_queues filter), so republish_from_error returns parked
  messages to their owner queue. Shop sample: `BROKEN-*` items always fail; the CI walkthrough parks one.

### 2026-10-18 Phase 8 — before the first release (phase-8/pre-release) — commits 83d7bb3, 50a21db
- `RabbitMcp.Contracts` = attribute only; built-in samples → `src/RabbitMcp.Samples` (`RabbitMcp.Samples.Orders`,
  URNs/exchanges changed accordingly — unreleased, so no compatibility cost). Guard: `The_contracts_package_holds_only_the_attribute`.
- Runners: verified on ubuntu-26.04 (build, unit, integration), then pinned everything to `ubuntu-24.04`.
- Supply chain: base images pinned by index digest (sdk/aspnet/runtime 10.0), Dependabot docker for `/` and
  `/samples/Shop`; release: CycloneDX SBOM via anchore/sbom-action on a framework-dependent publish (single-file
  hides deps.json), checked for key packages; attest-build-provenance@v4 for archives/nupkg/SBOM on tags;
  build-push-action `sbom`/`provenance: mode=max` on tags.
- Decision: release still postponed by the user.

### 2026-10-13 Shop sample (samples/shop) — commits f47f1de..b2d3fc7
- What: `samples/Shop` — Shop.Contracts (OrderPlaced, GetOrderStatus, ApproveLargeOrder answered via responder),
  Shop.Service (MassTransit 8; FLAKY-* fails once → _error demo; orders > 1000 ask for approval), Dockerfile targets
  `service`/`mcp`, docker-compose (RabbitMQ shop/shop, ports on localhost, demo API key), Georgian walkthrough.
- CI job "Sample (Shop, end to end)": `docker compose up --wait` + `scripts/sample-e2e.py` (MCP over HTTP: consumers,
  request, publish → error → peek → republish, pending → reply, pending → reject). Failures surface as annotations.
- Decision: samples are in RabbitMcp.slnx so every build compiles them; the sample builds rabbit-mcp from source
  (no dependency on a published image).

### 2026-10-13 Configurable permissions — roles for HTTP callers (feature/roles) — commits 20f8a96, 574e3b6
- What: `Security/AccessControl` (Roles → grants read/publish/request/republish/respond/*, contract or error-queue
  `*` patterns; UserRoles by API key name / preferred_username / email / sub; JWT roles claim `JwtRolesClaim`;
  DefaultRoles) and `Security/ToolAccess` (tool → rule). MCP list-tools filter hides tools, call-tool filter
  re-checks with arguments (errorQueue, pending request's contract) and audits `access denied`.
- Decision: off until a role is configured (backward compatible; HTTP prints a startup note); once on, unknown
  tools and users without roles get nothing (deny by default); stdio stays unrestricted (single local user).
- Decision: Enable*/PublishAllow/PublishDeny remain the server-wide ceiling; roles only narrow.
- Gotcha: tool error text in JSON-RPC is escaped (`'` → \u0027) — tests must read `result.content[].text`.

### 2026-10-13 Phase 6: review, responder HA, contract_consumers, contracts diff, Docker, NuGet (phase-6/all)
- Review of phase 5 → BUG-008/009 fixed; cancelled reply keeps the request; `Stateless = true` explicit
  (SDK 2.2 already defaults to stateless — SEP-2567). Commit 993e45e.
- Responder: `pending_requests(waitSeconds)` long poll; queue `{ClientName}-responder` exclusive → active/standby.
- `contract_consumers` (ManagementUri): BFS over exchange→exchange bindings, consumers, `_error`/`_skipped` backlog;
  status ok / no-consumers / no-queue / no-exchange. Commits 7cac1f1, 960be84.
- `rabbit-mcp contracts diff OLD NEW [--json]`: URN/exchange/reply types + JSON schema (camelCase) comparison,
  breaking/warning/info, exit 0/1/2. Commit c3e99df.
- Dockerfile (cross-publish, aspnet:10.0, non-root, HTTP default), CI container smoke test, release pushes
  ghcr.io multi-arch; nuget.org push only when `NUGET_API_KEY` secret exists. Commits eaf4008, b3409e2.
- Decision: delayed/scheduled messages not implemented — needs the delayed-exchange plugin or an external scheduler.

### 2026-10-13 Responder mode (phase-5/responder) — commits cc7b489, ef4b539
- What: `RespondTo` (patterns) makes the server a consumer of request contracts: `ResponderService` (BackgroundService,
  exclusive auto-delete queue bound to each contract exchange, fanout durable like MassTransit; retries every 5 s),
  `RequestInbox` (bounded by `MaxPendingRequests`, expiry from envelope `expirationTime`), `ResponderEnvelopes`
  (reply like `RespondAsync`; `Fault<T>` with URN `urn:message:MassTransit:Fault[[Ns:Type]]`).
  Tools: `pending_requests`, one `reply_<response>` per reply type (typed schema), `reject_request`.
- Decision (default, user did not choose): manual replies only — no automatic answers.
- Decision: requests are acked on arrival and kept in memory; a restart loses them (requester times out).
  Inbox full → immediate Fault "busy" instead of leaving the requester to time out.
- Decision: invalid or wrong-type reply is not sent, request stays pending; "requester gone" (mandatory return /
  404) is not retried; other send failures put the request back.
- Decision: class named `ResponderService` because `Tools.Responder` (RequestResult.RespondedBy) already exists.
- Impact: audit actions `receive` / `respond`; reply span linked to the request's trace; CI smoke step for responder mode.

### 2026-10-13 Phase 5 / HTTP transport — branch phase-5/http, fast-forwarded into main
- What: `RabbitMcp:Transport=http` (ModelContextProtocol.AspNetCore 2.2, stateless), API-key scheme (SHA-256 +
  FixedTimeEquals, ≥ 32 chars, `name:KEY`), JwtBearer + SDK `AddMcp` protected resource metadata, Origin check,
  `/healthz`, audit client = "user (mcp client)"; `ServerComposition` shared by both hosts; HTTP smoke test.
- Decision: auth kind not chosen by the user → support API keys and JWT/OIDC side by side (policy over both schemes;
  an unknown bearer value falls through from the API-key scheme to JWT).
- Decision: key names use ':' ("alice:KEY") because base64 keys end with '='.
- Decision: refuse to start without auth (HttpAllowAnonymous only for local experiments); TLS via reverse proxy.
- Cost: single-file packages grew ~33 → ~45 MB (ASP.NET Core bundled).

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
