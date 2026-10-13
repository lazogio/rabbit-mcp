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
