## What and why

<!-- One or two sentences. Link the ROADMAP item or bug (BUG-NNN) if there is one. -->

## Checklist

- [ ] Unit tests for every behaviour change; integration test if broker/MassTransit semantics changed
- [ ] No silent message loss (confirms + mandatory, ack only after confirmed republish, bodies moved unchanged)
- [ ] Envelope / URN / header / reply-endpoint changes checked by `masstransit-compat-reviewer`
- [ ] Contract changes: breaking or not? (renamed/removed/required fields are breaking)
- [ ] New or changed MCP tools: honest annotations, structured output, gated if they send or remove messages
- [ ] README / CONTRIBUTING updated in both languages (`*.md` English, `*.ka.md` Georgian) and CLAUDE.md; bug or decision recorded by `project-memory`
