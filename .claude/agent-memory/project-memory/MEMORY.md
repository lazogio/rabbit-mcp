# rabbit-mcp memory — index

Last updated: 2026-10-13 · Bugs: 7 (0 open) · Next ID: BUG-008

## Files
- `bugs.md` — bug log (symptom, root cause, fix, guard, lesson)
- `changes.md` — changes and design decisions with commit SHAs
- `gotchas.md` — non-obvious stack facts and anti-patterns

## Latest
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
8. Release = push a `vX.Y.Z` tag; the smoke test must pass on the single-file binary.
9. Keep RabbitMcp.Contracts AssemblyVersion 1.0.0.0 (external contract DLLs bind to it).
10. Anything that sends/moves messages must respect PublishPolicy and write AuditLog events (incl. failures).

## Open ideas (not bugs)
- Server as responder/consumer for requests from other services.
- HTTP transport with authorization for shared deployments.
