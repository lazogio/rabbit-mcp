# Roadmap

Status: ⬜ todo · 🔄 in progress · ✅ done. Each phase is developed on its own branch and merged via PR after CI is green.

## Phase 1 — Reliability (`phase-1/reliability`) ✅ (CI green, awaiting merge)
1. ✅ Fix: republish preview ignores `targetExchange` override; reply `ContractCheck.Version` shows the request's version.
2. ✅ Integration tests (`tests/RabbitMcp.IntegrationTests`): Testcontainers RabbitMQ 4 + MassTransit v8 consumer —
   publish delivered, missing exchange, unbound exchange, request responded / not found / faulted / timeout,
   peek keeps messages, republish acks only after confirm. Separate CI job.
3. ✅ Peek guard: refuse to peek a queue with active consumers unless `force=true`.

## Phase 2 — Process & distribution
4. ⬜ Dependabot (NuGet + GitHub Actions, weekly).
5. ⬜ Release workflow on `v*` tags: self-contained binaries (platforms: **decision needed**), GitHub Release.
6. ⬜ PR-based flow with branch protection on `main` (enabled by the repo owner).

## Phase 3 — Usability
7. ⬜ Contract assemblies from configuration (`RabbitMcp__ContractAssemblies`), no rebuild needed.
8. ⬜ `list_queues` via the Management API (optional `ManagementUri`) — **decision needed: is the API reachable?**
9. ⬜ Short-lived cache for exchange-exists checks.

## Phase 4 — Control
10. ⬜ Allowlist of publishable/requestable contracts (`RabbitMcp__Publish__Allow`).
11. ⬜ Audit log of publish / request / republish (no credentials).

## Phase 5 — New capabilities (short design first)
12. ⬜ Responder mode: `pending_requests` + `respond(requestId, response)` — **decision needed: manual vs automatic replies.**
13. ⬜ OpenTelemetry: propagate `traceparent`, metrics.
14. ⬜ HTTP transport with authorization.
