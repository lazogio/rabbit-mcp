# Bugs (newest first)

Next ID: BUG-019

### BUG-011..018 Security & bug review (2026-10-13) — fixed in commit 34f6e15
- BUG-011 (security, medium) Responder confused deputy: replies, rejections and automatic "busy" faults went to the
  request's `responseAddress`/`faultAddress`, which whoever publishes the request controls → rabbit-mcp could be made to
  publish into any exchange with its own credentials. Fix: `RabbitConnection.CheckTemporaryExchangeAsync` (passive
  declare, then re-declare fanout/durable:false/autoDelete:true → 406 = not a reply endpoint); such requests are
  refused at receive (audit `receive refused`). Guard: integration `Request_whose_reply_address_is_a_regular_exchange_is_refused`.
- BUG-012 (security, medium) `JwtScopes` were only advertised in the resource metadata, never required. Fix: policy
  assertion `HttpHosting.HasRequiredScopes` (scope/scp; API keys unscoped). Guard: `Jwt_must_carry_the_configured_scopes`
  (real signed tokens via static OpenIdConnectConfiguration).
- BUG-013 (security, medium-low) Republish followed `MT-Fault-InputAddress` even when it disagreed with the error queue's
  owner (headers are publisher-controlled). Fix: `RepublishTarget.Conflict` → skipped unless `targetExchange` is given;
  `PeekedMessage.RepublishWarning`. Guard: `Spoofed_fault_input_address_is_not_followed_without_an_explicit_target`.
- BUG-014 (security, low-medium) `HttpAllowAnonymous` worked on 0.0.0.0 (e.g. Docker). Fix: loopback only. Guard: `Anonymous_access_is_refused_beyond_loopback`.
- BUG-015 (bug) publish/request/republish audited only McpException failures; 403/lost connection/cancel were not
  audited and partial republish progress vanished from the audit. Fix: catch `Exception`, audit, wrap via `BrokerErrors`.
- BUG-016 (bug) RabbitMQ exceptions reached the SDK as non-McpException → "An error occurred invoking 'x'." with no reason.
  Fix: call-tool filter + `BrokerErrors.TryDescribe`.
- BUG-017 (security, low) AUDIT text log lines could be forged with CR/LF in client name or reject reason. Fix: `AuditLog.OneLine`.
- BUG-018 (bug, low) `MessageInspector.Validate` let NotSupported/InvalidOperation exceptions escape (exotic contracts broke peek).
- Hardening: CI fails on vulnerable NuGet packages; Dependabot for Docker base images.

### BUG-010 Release Docker build failed on branch runs — fixed (2026-10-13)
- Symptom: `dotnet publish` in the Dockerfile failed in release.yml only (CI's own docker build passed).
- Root cause: `VERSION=${{ steps.meta.outputs.version }}` — docker/metadata-action yields the branch name
  (`phase-6-all`) on branches, which is not a valid package version.
- Fix: same version step as the binaries (`0.0.0-ci.N` off tags) — commit b3409e2.
- Lesson: never feed metadata-action's version into MSBuild; it is a tag, not a SemVer.

### BUG-009 Duplicate request delivery faulted the requester as "busy" — fixed (2026-10-13)
- Symptom (found in review): a redelivered request already pending made `RequestInbox.TryAdd` return false and the
  responder sent a Fault "busy" — the requester failed although its request was waiting for an answer.
- Fix: `InboxAddResult { Added, Duplicate, Full }`; duplicates are ignored — commit 993e45e.
- Guard: `ResponderTests.Inbox_is_bounded_and_lists_oldest_first`.

### BUG-008 Responder stopped listening after the channel was lost — fixed (2026-10-13)
- Symptom (found in review): after a broker restart / forced close the responder sat in `Task.Delay(Infinite)` on a dead
  channel; requests were no longer received and nothing was logged.
- Fix: `ChannelShutdownAsync` completes a TCS → `ListenAsync` throws → retry loop listens again; `Listens` counter and
  `Status` exposed — commit 993e45e.
- Guard: integration `Responder_listens_again_after_the_connection_is_lost` (closes the connection via Management API).

### BUG-007 list_queues scanned up to 20 pages even when one was enough — fixed (2026-10-13)
- Symptom: every call fetched all pages (500 queues each) to count matches, even with limit=100 and no client-side filter.
- Root cause: `ManagementClient.ListQueuesAsync` always used scan mode.
- Fix: one request with `page_size=limit` and the server's `filtered_count` when the server does all filtering — commit 27cb9e4.
- Guard: `ManagementClientTests.Server_side_filtering_needs_a_single_request_and_reports_the_server_count`.

### BUG-006 ReflectionTypeLoadException at startup hid the missing dependency — fixed (2026-10-13)
- Symptom: "rabbit-mcp: Unable to load one or more of the requested types." with no assembly name.
- Fix: `ContractAssemblyLoader.Describe` reports the first LoaderException and what to do — commit 27cb9e4.
- Guard: `ContractAssemblyLoaderTests.Type_load_failures_name_the_missing_dependency`.

### BUG-005 Contracts DLL named like a server assembly was silently replaced — fixed (2026-10-13)
- Symptom: a contracts project named e.g. "RabbitMcp.Server" loaded as the server's own assembly → "No [MessageContract] types found".
- Root cause: `LoadOnce` reused any loaded assembly with the same simple name (`ReferenceMatchesDefinition` ignores versions/paths).
- Fix: reuse only the same file or the shared RabbitMcp.Contracts; otherwise a clear error — commit 27cb9e4.
- Guard: `A_different_file_named_like_a_server_assembly_is_rejected`, `A_copy_of_the_shared_contracts_assembly_resolves_to_the_bundled_one`.

### BUG-004 ContractAssemblies split paths on ',' — fixed (2026-10-13)
- Symptom: a path like `C:\Users\Doe, John\contracts.dll` became two broken paths.
- Root cause: `ParsePaths` split on ';' and ','.
- Fix: ';' only — commit 27cb9e4. Guard: `Comma_is_part_of_a_path_not_a_separator`.
- Lesson: never use ',' as a separator for file paths (legal on every OS).

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
