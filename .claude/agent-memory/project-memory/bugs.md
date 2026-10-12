# Bugs (newest first)

Next ID: BUG-004

### BUG-003 Reply ContractCheck showed the request's version — fixed (2026-10-12)
- Symptom: `request_*` result `responseCheck.version` equalled the request contract's version, though responses are not versioned.
- Root cause: `ReplyInterpreter.Interpret` passed `request.Version` (src/RabbitMcp.Server/Messaging/ReplyInterpreter.cs).
- Fix: `ContractCheck.Version` is `int?`, null for responses — commit f7306bd.
- Guard: unit test `RequestResponseTests.Declared_response_is_validated_against_its_contract` asserts null.

### BUG-002 Republish preview ignored the targetExchange override — fixed (2026-10-12)
- Symptom: `republish_from_error(confirm=false, targetExchange=X)` showed the resolved target (from MT-Fault-InputAddress), not X.
- Root cause: preview called `PeekAsync` without the override (src/RabbitMcp.Server/Tools/RepublishTools.cs).
- Fix: `PeekAsync(..., targetExchangeOverride)` sets `RepublishTarget` — commit f7306bd.
- Guard: integration test `ErrorQueueTests.Faulted_message_can_be_peeked_and_republished_without_loss`.

### BUG-001 CI warned that GitHub Actions run on deprecated Node 20 — fixed (2026-10-12)
- Symptom: first CI run (commit ee0db8b) green but annotated: actions/checkout@v4, setup-dotnet@v4,
  upload-artifact@v4 target Node 20 and were forced onto Node 24.
- Root cause: workflow pinned to older action majors.
- Fix: checkout@v7, setup-dotnet@v6, upload-artifact@v7 — commit 84e14d7. Next run had no deprecation annotation.
- Guard: none automatic; `ci-doctor` checks run annotations after each push.
- Lesson: after adding/changing a workflow, read the job annotations, not only the conclusion.
