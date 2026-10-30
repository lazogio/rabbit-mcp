# rabbit-mcp memory — index

Last updated: 2026-10-24 · Bugs: 28 (0 open) · Next ID: BUG-029

## Files
- `bugs.md` — bug log (symptom, root cause, fix, guard, lesson)
- `changes.md` — changes and design decisions with commit SHAs
- `gotchas.md` — non-obvious stack facts and anti-patterns

## Latest
- 2026-10-30 fix/message-ids-scan-and-warning: BUG-030 messageIds/filters scan past the head without count; NU1510 removed from the server
- 2026-10-30 fix/schema-bounds-macos-smoke: BUG-029 decimal [Range] limits are numbers in schemas; smoke scripts run on macOS (no timeout(1), no --network host)
- 2026-10-28 feat/topology-tools (PR #15): declare_exchange / declare_queue behind EnableTopology, create-only; review fixes in the same PR
- 2026-10-24 fix/validator-invariant-culture: BUG-028 decimal [Range] limits threw under ka-GE; validator uses invariant culture
- 2026-10-18 phase-14/asyncapi: ContractFiles (AsyncAPI 3/2, YAML/JSON), own schema validator, export + diff; BUG-027
- 2026-10-18 phase-13/scale: recovery by filter (MaxBulkMessages), read_stream, RateLimits
- 2026-10-18 phase-12/human: elicitation confirmations (HumanConfirmation, ConfirmTools), resend_corrected
- 2026-10-18 plan: phases 12 (elicitation, resend_corrected), 13 (bulk by filter, streams, rate limits), 14 (AsyncAPI), 15 (release-gated)
- 2026-10-18 review/phase-11: BUG-020..026 (scope bypass via auth_method claim, role name collisions / unverified e-mail, destination overrides for restricted roles, live queue as DLQ, republish into itself, 405 exclusive queues, log forging)
- 2026-10-18 phase-11/raw: raw JSON contracts (Format = Raw, RoutingKey, RabbitMQ RPC, x-death republish); #32 multi-broker declined, #33 after release
- 2026-10-18 phase-10: MCP prompts (4), contract:// resources, docs in English + Georgian (*.ka.md)
- 2026-10-18 phase-9 done: find_message (id/correlation/text across error queues), recent_activity (audit, newest first)
- 2026-10-18 phase-9/dead-letter: dead_letter_from_error ({queue}_dead), never delete messages
- 2026-10-18 phase-8/pre-release: samples out of the contracts package, runners pinned (26.04 verified), digests, SBOM, provenance
- 2026-10-13 samples/shop: Shop sample (contracts, MassTransit service, compose, CI e2e over MCP); BUG-019 decimals as strings
- 2026-10-13 feature/roles: configurable permissions (roles, user/JWT assignment, tools/list filter, call enforcement, audit)
- 2026-10-13 review/security-bugs: BUG-011..018 (responder confused deputy, JWT scopes, spoofed republish target, anonymous on 0.0.0.0, audit gaps, opaque broker errors, log forging), vuln scan in CI
- 2026-10-13 phase-6/all: review (BUG-008..010), responder HA + long poll, contract_consumers, contracts diff CLI, Docker/GHCR, NuGet
- 2026-10-13 phase-5/responder: responder mode — pending_requests, reply_<response>, reject_request (Fault<T>), manual only
- 2026-10-13 phase-5/http: HTTP transport, API keys + JWT/OIDC, MCP resource metadata, Origin check, user in audit
- 2026-10-13 phase-5/telemetry: spans + MT-Activity-Id/traceparent (MassTransit joins traces), metrics, OTLP
- 2026-10-13 review/phase-3: BUG-004..007 fixed (path split, assembly name clash, load errors, list_queues paging)
- 2026-10-13 phase-4/control: PublishAllow/Deny policy, audit log (stderr + JSONL), client identity in audit
- 2026-10-13 phase-3/usability: ContractAssemblies option, exchange cache, optional list_queues (Management API)
- 2026-10-13 phase-2/process: Dependabot, release workflow (5 platforms), MCP stdio smoke test, PR template
- 2026-10-12 phase-1/reliability: BUG-002/003 fixed, peek guard, integration tests vs real MassTransit (all green, merged to main 2026-10-13)
- 2026-10-12 0742939 project-memory agent added (seeded from history)
- 2026-10-12 25db129 subagents + CLAUDE.md
- 2026-10-12 84e14d7 BUG-001 fixed: CI actions moved to Node 24 majors
- 2026-10-12 c988032 request/response contracts (`request_*`, ReplyListener)

## Top rules (from history)
1. No silent message loss: confirms + mandatory; ack only after confirmed republish; never truncate moved bodies.
2. Wire format changes must be checked against MassTransit source (masstransit-compat-reviewer).
3. Passive declares on throwaway channels; AMQP header strings are byte[].
4. NuGet is unreachable in the authoring sandbox — CI decides whether it builds; read run annotations (test failures show there).
5. stdout belongs to MCP; register the published binary, never `dotnet run`.
6. MassTransit: AMQP headers are in ReceiveContext.TransportHeaders, not ConsumeContext.Headers.
7. Never accept a MassTransit 9.x bump (commercial); Dependabot already ignores it.
8. Message content (addresses, headers) is untrusted input: never route by it unchecked.
9. Never delete messages: park them (dead_letter_from_error) — the user's explicit rule.
10. Docs are bilingual: change `*.md` (English) and `*.ka.md` (Georgian) together.
8. Release = push a `vX.Y.Z` tag; the smoke test must pass on the single-file binary.
9. Keep RabbitMcp.Contracts AssemblyVersion 1.0.0.0 (external contract DLLs bind to it).
10. Anything that sends/moves messages must respect PublishPolicy and write AuditLog events (incl. failures).
11. Every send: Telemetry.StartSend + Telemetry.Inject (MassTransit needs MT-Activity-Id, not traceparent).

## Open ideas (not bugs)
- Server as responder/consumer for requests from other services.
