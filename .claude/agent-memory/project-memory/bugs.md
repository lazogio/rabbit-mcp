# Bugs (newest first)

Next ID: BUG-065

### BUG-031..047 Black-box QA run (2026-10-31) — fixed (2026-10-31, on main)
Found by driving the published binary over real MCP against RabbitMQ 4.4 and the official TypeScript client. Guards: `QaRegressionTests`,
`HttpHostingTests`, integration `ErrorQueueTests`/`SearchTests`, and `scripts/mcp_schema.py` in the smoke test and sample e2e.
- BUG-031 (critical) 14 tools broke their own outputSchema: `ToolJson` omitted nulls that the schema lists as required. The TypeScript SDK threw
  -32602 after the tool had already sent or moved messages, so a retry duplicated them. Fix: `ToolJson` writes nulls.
  Lesson: CI checked that outputSchema exists, never that results satisfy it.
- BUG-032 (high) `HttpAllowAnonymous=true` always crashed: `UseAuthentication` without `AddAuthentication`.
- BUG-033 (medium) Invalid option values ended in an unhandled exception and SIGABRT. Fix: `ServerComposition.Validate`/`ReadOptions` throw
  `StartupException`; `Program.RunAsync` reports a host that cannot start the same way.
- BUG-034 (medium) Arguments that could not be bound returned "An error occurred invoking 'x'." Fix: `BrokerErrors.TryDescribeArguments`.
  Over-long queue/exchange names now read as "does not exist" (`RabbitConnection.TooLong`).
- BUG-035 (medium) `republish_from_error confirm=true` ignored active consumers. Fix: refused unless force=true.
- BUG-036 (medium) `find_message` missed text past MaxPayloadChars yet reported `searched`. Fix: `PeekAsync(select:)` sees the whole body.
- BUG-037 (medium) A 403 on publish read "connection lost, try again". Fix: refusal codes are matched before `AlreadyClosedException`.
- BUG-038 (medium) `JwtAuthority=http://...` started, then every request was an empty 500. Fix: startup error.
- BUG-039 (low) `resend_corrected` sent fields outside the contract. Fix: refused (`FieldsOutsideContract`); fields the original had stay.
- BUG-040 (low) Previews ignored messageIds. Fix: `HavingIds`, `PeekResult.Scanned`.
- BUG-041 (low) Invalid until/from were reported as `since`. Fix: `ParseSince(argument)`, `TryParseTime`.
- BUG-042 (low) Revived messages kept X-Dead-Letter-*/X-Corrected-To. Fix: `WithoutFaultHeaders`.
- BUG-043 (low) Truncation cut bytes (U+FFFD at the end; about 5,300 Georgian characters shown). Fix: `MessageInspector.Shortened` counts characters.
- BUG-044 (low) The quorum note in peek_messages was true only for 3.x. 3.13 really drops the message: README and description now say so.
- BUG-045 (low) `durable=false` previewed would-create, then 541 closed the shared connection. Fix: refused in `TopologyPolicy`.
- BUG-046 (low) `--help`/`--version` started the server. Fix: handled in `Program.cs`.
- BUG-047 (test) `Every_audit_event_is_counted` failed about 40% of runs: a plain List filled by a process-wide listener. Fix: ConcurrentQueue.
- BUG-048 (data loss) Parked messages with a TTL were deleted: dead-letter and correction copied `expiration`, so the clock restarted in `_dead`. Fix: `QueueBrowser.Parked` moves it to `X-Original-Expiration`; `Live` restores it on republish.
- BUG-049 (poison) One message could fail peek, search and filtered previews for a whole queue (date past year 9999, lone surrogate, deep JSON, invalid UTF-8). Fix: `Inspect` never throws; `CanShow` turns what a result cannot carry into RawBody plus `Problem`; `find_message` survives any queue failing.
- BUG-050 (stall) Sends never ended during a broker alarm, and reads on the shared connection failed too. Fix: publishing connection of its own, `PublishChannel` with `PublishTimeoutSeconds`, `connection.blocked` ends the wait at once and refuses later sends.
- BUG-051 (moves) Messages with `user_id` could not be moved (406). Fix: moved to `X-Original-User-Id`.
- BUG-052 (display) Text starting like JSON was cut to its first value. Fix: `TryParseJson` needs the whole body to be one value; also used by `RawReply`.
- BUG-053 (validation) `lines:[null]` passed validation and was sent. Fix: `ContractValidator` refuses null items unless the element type is nullable.
- BUG-054 (errors) Broker timeouts had no reason. Fix: `TimeoutException` in `BrokerErrors`, and a cancellation no caller asked for reads the same.
- BUG-055 (regression of BUG-034) Faults inside a tool read "Invalid arguments". Fix: `ThrownWhileBinding` looks at the stack; anything else unexpected is logged and named.
- BUG-056 (search) `find_message` called a stream `empty`. Fix: searches every queue it can and reports streams as skipped.
- BUG-057 (connection) A 1 MB `reason` killed the shared connection (FRAME_ERROR). Fix: `MaxReasonChars`; 501 no longer says "Try again".
- BUG-058 (startup) Long ClientName failed only at the first reply or responder queue, and a URI with two path segments only at the first call. Fix: both refused in `Validate`.
- BUG-059 (regression of BUG-033) A corrupt appsettings.json crashed with a stack trace. Fix: read inside the `try` in `Program.cs`.
- BUG-060 (streams) `read_stream` failed when Management was down. Fix: type is optional, the first delivery tells.
- BUG-061 (audit) A failed confirmation question was not audited. Fix: audited as `refused-failed` in the call-tool filter.
- BUG-062 (corrections) `resend_corrected` refused enum names. Fix: `MessageInspector.WithEnumNumbers`.
- BUG-063 (text) A message held by a parallel peek read "Not found". Fix: text says it may be further back, held, or gone.
- BUG-064 (streams) `basic.get` on a stream closed the whole connection (540, reported at first as a missing hint). Fix: browsing connection, `StreamQueueException`, moves try the first message there first. A null queue name is refused plainly.

### BUG-030 messageIds and filters only saw the first message without count — fixed (2026-10-30, branch fix/message-ids-scan-and-warning)
- Symptom (local live run): `dead_letter_from_error` with messageIds skipped a message at position 2: "Not found in the first 1 message(s)".
  Filters had the same trap although the description promises one call moves up to MaxBulkMessages.
- Root cause: `count` defaulted to 1 and is the scan depth in `QueueBrowser`.
- Fix: `count` is optional; `RepublishTools.ScanDepth` uses MaxMessagesPerCall with messageIds, MaxBulkMessages with a filter, an explicit
  count still wins. Previews stay at 1. Guard: `Selected_messages_are_found_behind_the_head_without_count`.
- Lesson: in these tools count limits the scan, not the number of messages moved.
- Update (BUG-040): previews with messageIds now scan like the confirmed call.

### BUG-029 Decimal [Range] limits were strings in tool schemas — fixed (2026-10-30, branch fix/schema-bounds-macos-smoke)
- Symptom (local run, tools/list): `unitPrice` had `"minimum": "0.01"`, `"maximum": "1000000"`; invalid JSON Schema (limits must be numbers).
- Root cause: schema generation (Microsoft.Extensions.AI) writes `[Range(typeof(decimal), …)]` limits as the attribute's strings.
- Fix: `ToolJson.SchemaOptions` turns them into numbers (invariant culture); used by typed tools, reply_* tools and contract resources.
  Guards: `Decimal_range_limits_are_numbers_in_the_input_schema`, `Decimal_range_limits_are_numbers_in_contract_resources`.
- Lesson: build new C# schema paths with `ToolJson.SchemaOptions`, or tools and resources drift apart.

### BUG-028 Decimal [Range] crashed validation on comma-decimal locales — fixed (2026-10-24, branch fix/validator-invariant-culture)
- Symptom (local unit tests, macOS region Georgia): 9 failures, `0.01 is not a valid value for Decimal`; CI (invariant/en) green.
- Root cause: `[Range(typeof(decimal), "0.01", …)]` parses its string limits with CurrentCulture; ka-GE uses ',' so
  `ContractValidator.Validate` threw for every contract with a decimal range (server too, not only tests).
- Fix: `ContractValidator.Validate` runs under `CultureInfo.InvariantCulture` (restored in finally) — works for
  external contract DLLs without `ParseLimitsInInvariantCulture`. Guard: `Decimal_range_limits_parse_under_a_comma_decimal_culture`.
- Lesson: CI runs one culture only; run tests under a comma-decimal culture when touching parsing/validation.

### BUG-027 Schema bounds written in YAML were ignored — fixed before merge (2026-10-18, commit e06c536)
- Symptom (CI unit test): `quantity: 0` passed `minimum: 1` for the warehouse.yaml contract.
- Root cause: `YamlJson` creates `JsonValue.Create(long)`; `JsonValue.TryGetValue<decimal>` does not convert a long, so
  every numeric keyword (minimum, maxLength, minItems …) from YAML read as missing. JSON-parsed schemas worked.
- Fix: read numeric keywords via `GetValueKind() == Number` + the JSON text. Guard: `Yaml_scalars_follow_the_core_schema`.
- Lesson: JsonValue.TryGetValue<T> only succeeds for the stored CLR type — test YAML and JSON inputs both.

### BUG-020..026 Security & bug review after phase 11 (2026-10-18) — fixed in commits 797cd9b, 3462d5b
- BUG-020 (security, medium) JWT scope enforcement was skipped for any principal with the claim `auth_method=api_key`;
  a token can carry any claim (IdP mappers, user attributes). Fix: `ApiKeyAuthenticationHandler.IsApiKeyCaller` checks
  the identity's AuthenticationType ("ApiKey"), which only the handler sets. Guards:
  `An_auth_method_claim_in_a_token_does_not_skip_the_scope_check`, `Jwt_claiming_to_be_an_api_key_is_still_refused_without_scopes`.
- BUG-021 (security, medium) Role assignment mixed name spaces: a token user whose preferred_username/sub/e-mail equalled
  an API key name got that key's roles, and an unverified (user-editable) e-mail picked up the roles of that address.
  Fix: `AccessControl.NamesOf` — API-key callers by key name only; token callers by sub / preferred_username / e-mail
  only with email_verified=true; token names equal to an API key name ignored; roles claim only from token identities.
  Guards: `Token_user_named_like_an_api_key_does_not_get_its_roles`, `Unverified_email_assigns_no_role`.
- BUG-022 (security, medium) A role limited to some error queues (`republish:orders-*_error`) could still send their
  messages anywhere via targetExchange/targetQueue or deadLetterQueue. Fix: `ToolRule.OverrideArguments` need the
  permission without patterns; queue patterns case-sensitive (`Grant.CoversQueue`). Guards:
  `Choosing_another_destination_needs_republish_without_patterns`, `Queue_patterns_are_case_sensitive_like_rabbitmq`.
- BUG-023 (bug/security) `dead_letter_from_error` accepted a live queue as deadLetterQueue: "parking" handed messages to
  its consumers. Fix: `QueueBrowser.CheckDeadLetterTargetAsync` refuses a target with consumers (preview and confirm).
  Guard: integration `A_queue_with_consumers_is_never_used_as_the_dead_letter_queue`.
- BUG-024 (bug) Republish whose destination is the error queue itself (targetQueue, or an MT exchange of the same name
  from headers) only stripped the fault headers — fault information lost. Fix: skipped per message. Guard:
  integration `Republishing_into_the_error_queue_itself_is_skipped_and_keeps_the_fault`.
- BUG-025 (bug) A queue exclusive to another connection (passive declare → 405 RESOURCE_LOCKED) aborted the whole
  find_message (scope=all lists such queues) and queue_stats, and made raw sends to it fail; one failing peek aborted the
  search. Fix: `RabbitConnection.QueueExistsAsync`/`IsResourceLocked`, per-queue "skipped: exclusive…"/"error: …",
  405 in `BrokerErrors`. Guard: integration `Queues_exclusive_to_another_connection_do_not_break_search_stats_or_sending`.
- BUG-026 (bug, low) Small ones: `since=999999d` threw ArgumentOutOfRange (now: everything); message-derived values
  (reply ids, refusal reasons with addresses, republish ids/targets) were logged raw → log forging outside AUDIT lines
  (now `AuditLog.OneLine`); `FaultUrnFor` crashed for a custom Urn without `urn:message:`.
- Accepted (documented, not changed): x-death / MT-Fault-InputAddress on a queue without an _error/_skipped/_dead
  suffix are followed (the preview shows the target, confirm is required); whoever can publish into a DLQ through the
  default exchange can reach any queue directly anyway. RabbitMQ 4 quorum queues: basic.nack(requeue) does not count
  towards the delivery limit (docs table), so peek does not push messages to the default limit of 20.
- Lesson: decide "who is this" by how a caller authenticated, never by a claim value; every argument that changes a
  destination needs its own permission check.

### BUG-019 MassTransit decimals (JSON strings) failed contract validation — fixed (2026-10-13)
- Symptom (found by the Shop sample e2e): `pending_requests` showed `check.valid=false` for an ApproveLargeOrder sent
  by MassTransit; the message had `"total": "1800"`.
- Root cause: MassTransit's `StringDecimalJsonConverter` writes decimals as strings (reads numbers or strings);
  `MassTransitJson` only read numbers. Affected peek, pending_requests and request reply checks for any decimal.
- Fix: `MassTransitDecimalConverter` (read number or numeric string, write number); `ContractDiff` clears converters
  for schema export — commit b2d3fc7. Guard: `Decimals_written_as_strings_by_MassTransit_are_valid_and_written_back_as_numbers`.
- Lesson: integration tests only sent decimals *to* MassTransit; test every direction of the wire format.

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
