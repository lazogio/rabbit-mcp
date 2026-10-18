# Roadmap

Status: ⬜ todo · 🔄 in progress · ✅ done. Each phase is developed on its own branch and merged via PR after CI is green.

## Phase 1 — Reliability (`phase-1/reliability`) ✅ (merged 2026-10-13)
1. ✅ Fix: republish preview ignores `targetExchange` override; reply `ContractCheck.Version` shows the request's version.
2. ✅ Integration tests (`tests/RabbitMcp.IntegrationTests`): Testcontainers RabbitMQ 4 + MassTransit v8 consumer —
   publish delivered, missing exchange, unbound exchange, request responded / not found / faulted / timeout,
   peek keeps messages, republish acks only after confirm. Separate CI job.
3. ✅ Peek guard: refuse to peek a queue with active consumers unless `force=true`.

## Phase 2 — Process & distribution (`phase-2/process`) ✅ (merged 2026-10-13)
4. ✅ Dependabot (NuGet + GitHub Actions, weekly, grouped; MassTransit pinned < 9).
5. ✅ Release workflow on `v*` tags: self-contained single-file binaries for linux-x64/arm64, win-x64, osx-x64/arm64
   (default chosen: all five), SHA256SUMS, GitHub Release; version from the tag → MCP `serverInfo.version`.
   Bonus: `scripts/smoke-test.sh` — MCP stdio session against the published binary, in CI and release.
6. 🔄 PR template added. Branch protection on `main` + "Automatically delete head branches" — **repo owner** (Settings).

## Phase 3 — Usability (`phase-3/usability`) ✅ (merged 2026-10-13)
7. ✅ Contract assemblies from configuration (`RabbitMcp__ContractAssemblies`), no rebuild needed; fixed
   `RabbitMcp.Contracts` AssemblyVersion 1.0.0.0; nupkg attached to releases; proven on the single-file binary.
8. ✅ `list_queues` via the Management API — optional: registered only when `ManagementUri` is set
   (default chosen because the question was not answered).
9. ✅ Exchange-exists cache (30 s, positive only), invalidated on 404 during publish/republish.

## Phase 4 — Control (`phase-4/control`) ✅ (merged 2026-10-13)
10. ✅ `RabbitMcp__PublishAllow` / `PublishDeny` (`*` patterns over name, tool, type, URN; deny wins); denied
    contracts get no tool and are refused by the publisher; `list_contracts.sendable`; startup warning for unmatched patterns.
11. ✅ Audit log: publish / request / republish with outcome, ids and MCP client; stderr + optional JSON Lines file
    (`AuditLogPath`, checked at startup); payload opt-in (`AuditIncludePayload`).

## Phase 5 — New capabilities (short design first)
12. ✅ Responder mode (`phase-5/responder`): `RespondTo` contracts are consumed on an exclusive queue; requests wait in
    `pending_requests` (bounded by `MaxPendingRequests`, expire with the requester's TTL); the model answers with typed
    `reply_<response>` tools or `reject_request` (MassTransit `Fault<T>`); audited (`receive`/`respond`) and traced.
    Default chosen (no answer): manual replies only — no automatic answers.
13. ✅ OpenTelemetry (`phase-5/telemetry`, merged 2026-10-13): publish/request/republish spans; `traceparent` + `MT-Activity-Id`
    (MassTransit 8 joins the trace — proven by integration tests); republish = new trace linked to the original;
    `rabbitmcp.operations` + `rabbitmcp.request.duration`; OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
14. ✅ HTTP transport (`phase-5/http`, merged 2026-10-13): `Transport=http`, stateless Streamable HTTP, `/healthz`;
    API keys and/or JWT (any OIDC authority) with MCP protected resource metadata; Origin check; refuses to start
    without auth; audit records the user. Default chosen (no answer): support both auth kinds.

## Phase 6 — Hardening & distribution (`phase-6/all`) ✅
15. ✅ Phase 5 review: responder reconnects after channel loss; duplicate deliveries ignored (no false "busy" fault);
    a cancelled reply keeps the request pending; HTTP `Stateless` set explicitly.
16. ✅ Responder: `pending_requests(waitSeconds)` long poll; deterministic exclusive queue → one active instance,
    others stand by and take over (MCP has no reliable way to wake the model, so long poll instead of notifications).
17. ✅ `contract_consumers`: bindings from each contract exchange down to queues, consumers, `_error`/`_skipped` backlog.
18. ✅ `rabbit-mcp contracts diff OLD.dll NEW.dll`: wire-compatibility check for service CI (exit 1 on breaking).
19. ✅ Docker image `ghcr.io/lazogio/rabbit-mcp` (amd64/arm64) and nuget.org publishing (when `NUGET_API_KEY` exists).
20. ⏸ Scheduled/delayed messages — not done: needs the rabbitmq_delayed_message_exchange plugin (not in standard
    RabbitMQ images) or an external scheduler (Quartz/Hangfire); revisit if a broker with the plugin is used.

## Phase 7 — Access control (`feature/roles`) ✅
21. ✅ Configurable permissions for HTTP callers: roles (read/publish/request/republish/respond/*, contract and
    error-queue patterns), user and JWT-claim assignment, default roles; tools/list filtered, tools/call enforced and audited.

Shop sample (`samples/shop`) ✅ — runnable compose demo, walked through over MCP in CI; found and fixed BUG-019.

---

**Release:** postponed by decision (2026-10-18). Recommended after Phase 8, because 8.2 changes what the
`RabbitMcp.Contracts` package contains — cheaper to change before anyone depends on it.

## Phase 8 — Before the first release (`phase-8/pre-release`) ✅ (2026-10-18)
22. ✅ CI runners: build, unit and integration verified on Ubuntu 26.04; all jobs pinned to `ubuntu-24.04` (LTS)
    so a GitHub image switch cannot change the build unannounced.
23. ✅ Sample contracts out of the `RabbitMcp.Contracts` package (`OrderSubmitted`, `GetOrderStatus` …) into an
    assembly bundled with the server only, so services that reference the package get the attribute and nothing else.
    Built-in fallback and existing tests keep working. → `src/RabbitMcp.Samples` (namespace `RabbitMcp.Samples.Orders`),
    guard test: the package exports only the attribute.
24. ✅ Supply chain: CycloneDX SBOM of the server (checked in CI) attached to releases, image SBOM + SLSA provenance,
    build provenance attestations for archives/nupkg/SBOM (tags), base images pinned by digest (Dependabot updates).

## Phase 9 — Operations (`phase-9/operations`) ⬜
25. ✅ `dead_letter_from_error` (decision 2026-10-18: a dead-letter queue, never delete): poison messages move from
    `_error`/`_skipped` to `{queue}_dead` (`DeadLetterQueue`, created durable) with confirms, body and all headers kept
    plus `X-Dead-Lettered-*`/`X-Dead-Letter-Reason`; preview + `confirm`, `messageIds`, audited (`deadletter`);
    gated like republish (`EnableRepublish`, role permission `republish` — it moves, nothing is removed);
    `republish_from_error` brings messages back; `_dead` queues are listed as error queues.
26. ⬜ `find_message`: find a message by `messageId` or `correlationId` across queues (named queues, or all queues
    via the Management API), bounded scan, never removes anything; shows queue, fault and republish target.
27. ⬜ `recent_activity`: query the audit file (who sent / republished / rejected what, filters by action, contract,
    client, time) — only when `AuditLogPath` is set; permission `read`.

## Phase 10 — Usability (`phase-10/usability`) ⬜
28. ⬜ MCP prompts (slash commands in clients): `diagnose_errors` (all error queues → causes → suggested action),
    `review_approvals` (pending requests one by one), `contract_health` (consumers, backlogs, faults per contract).
29. ⬜ MCP resources: each contract's JSON schema and description as `contract://<Name>`, so clients can show them
    without a tool call.
30. ⬜ English documentation for the public repository. **Decision needed:** `README.md` in English with the
    Georgian one in `README.ka.md` (recommended), or Georgian first with an English `README.en.md`.

## Phase 11 — Reach (`phase-11/reach`) ⬜
31. ⬜ Raw JSON contracts (`[MessageContract(Format = Raw)]`): publish/request without the MassTransit envelope
    for services that do not use MassTransit (routing key, headers and correlation configurable).
32. ⬜ Several brokers/vhosts in one server (`Brokers__prod__Uri`, `Brokers__staging__Uri`): tools take `broker`,
    roles can be limited per broker. **Decision needed:** worth it, or one server per broker (works today).
33. ⬜ Listing in the official MCP registry (`server.json`) — needs a published release, so after the release.

