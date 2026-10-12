# rabbit-mcp memory — index

Last updated: 2026-10-13 · Bugs: 3 (0 open) · Next ID: BUG-004

## Files
- `bugs.md` — bug log (symptom, root cause, fix, guard, lesson)
- `changes.md` — changes and design decisions with commit SHAs
- `gotchas.md` — non-obvious stack facts and anti-patterns

## Latest
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

## Open ideas (not bugs)
- Server as responder/consumer for requests from other services.
- `list_queues` via Management API (AMQP cannot list queues).
- HTTP transport with authorization for shared deployments.
