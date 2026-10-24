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

## Phase 9 — Operations ✅ (2026-10-18)
25. ✅ `dead_letter_from_error` (decision 2026-10-18: a dead-letter queue, never delete): poison messages move from
    `_error`/`_skipped` to `{queue}_dead` (`DeadLetterQueue`, created durable) with confirms, body and all headers kept
    plus `X-Dead-Lettered-*`/`X-Dead-Letter-Reason`; preview + `confirm`, `messageIds`, audited (`deadletter`);
    gated like republish (`EnableRepublish`, role permission `republish` — it moves, nothing is removed);
    `republish_from_error` brings messages back; `_dead` queues are listed as error queues.
26. ✅ `find_message`: by `messageId`, `correlationId` or body text (`contains`, e.g. an order id) across the error
    queues (`_error`/`_skipped`/`_dead`, default), every non-empty queue (`scope=all`) or named queues; peek-based and
    bounded (`maxPerQueue`), queues with consumers skipped unless `force`; permission `read`.
27. ✅ `recent_activity`: the audit file read backwards (newest first, stops at `since`), filters by action, outcome,
    contract, client, queue, id; payload only on request; only when `AuditLogPath` is set; permission `read`.

## Phase 10 — Usability (`phase-10/usability`) ✅ (2026-10-18)
28. ✅ MCP prompts (slash commands in clients): `diagnose_errors` (all error queues → causes → suggested action),
    `review_approvals` (pending requests one by one), `contract_health` (consumers, backlogs, faults per contract).
29. ✅ MCP resources: each contract's JSON schema and description as `contract://<Name>`, so clients can show them
    without a tool call.
30. ✅ English documentation (default chosen, 2026-10-18): `README.md`, `CONTRIBUTING.md`, `samples/Shop/README.md`
    in English, Georgian twins in `*.ka.md` with a language switch at the top; both kept in sync.

## Phase 11 — Reach (`phase-11/raw`) ✅ (2026-10-18)
31. ✅ Raw JSON contracts (`[MessageContract(Format = MessageFormat.Raw, Exchange, RoutingKey)]`) for services
    without MassTransit: the body is the message itself (`application/json`), message id / correlation id / type
    (`Urn`) / app id in AMQP properties; no default exchange (`RoutingKey` alone = the default exchange → that queue,
    checked to exist); requests are RabbitMQ RPC (`reply_to` + `correlation_id`, reply type from the AMQP `type`
    property, never guessed from the body). Around it: `RoutingKey` for every contract; `contract_consumers` matches
    direct/topic bindings and follows the default exchange; `peek_messages` matches plain JSON by `type` and shows
    `x-death`; `republish_from_error` sends RabbitMQ-dead-lettered messages back to the queue that rejected them
    (`targetQueue` override, same conflict rule as `MT-Fault-InputAddress`); `contracts diff` reports format and
    routing-key changes; responder mode stays MassTransit-only. Shop sample: a plain RabbitMQ.Client shipping desk.
32. ⏸ Several brokers/vhosts in one server — **decided 2026-10-18: not doing it.** One rabbit-mcp per broker
    (`claude mcp add rabbit-prod …`, `rabbit-staging …`) already works, keeps credentials, roles and audit separate
    per environment, and a model cannot send to the wrong broker by passing the wrong `broker` argument.
33. ⏸ Listing in the official MCP registry (`server.json`, `mcp-publisher`) — needs a published release; moved to
    Phase 15 (#44).

## Phase 12 — Human in the loop (`phase-12/human`) ✅ (2026-10-18)
34. ✅ **Confirmation the model cannot give itself** (MCP elicitation). Today `confirm=true` is an argument the model
    sets. With elicitation the server asks the *person* ("Republish 37 messages from order-placed_error to
    order-placed?" accept/decline) before anything moves: republish, dead-letter, publish/request of contracts marked
    as needing approval, reply/reject.
    - `RabbitMcp:HumanConfirmation` = `auto` (default: elicitation when the client supports it, else today's
      `confirm=true`) · `required` (refuse changes when the client cannot ask the person) · `off`.
    - The preview stays: the question shows its summary (count, target, ids). Declined/cancelled → nothing changes,
      audited as `declined`. The audit records who confirmed and how (`elicitation` / `argument`).
    - Works over stdio. Stateless HTTP cannot send server→client requests → **decision needed:** an optional stateful
      HTTP mode (sessions, sticky load balancing) or URL-mode elicitation with a small approval page served by
      rabbit-mcp itself (stays stateless; the page needs the caller's own authentication). Default until decided:
      `auto` falls back to `confirm=true` on HTTP. Done as a generic call-tool filter with `ConfirmTools` patterns
      (publish_*, reply_* … can be added), audit action `confirm`.
35. ✅ **Fix and resend** (`resend_corrected`, `EnableRepublish`, permission `republish`): for a message that failed
    because of its data (bad e-mail, missing field). Input: error queue, messageId, the corrected message. The
    correction is validated against the contract the original matches; the preview shows a field-by-field diff.
    Sequence like republish: get (no ack) → publish the corrected message (new messageId, same correlation/conversation,
    `X-Corrected-From` = original id) with confirms → move the original unchanged to the dead-letter queue with
    `X-Corrected-By/-At` and the diff summary → ack. Nothing is deleted; both sides audited (`correct`).
    MassTransit (envelope rebuilt, original headers kept) and raw contracts (plain JSON, original properties kept).

## Phase 13 — Incident scale (`phase-13/scale`) ✅ (2026-10-18)
36. ✅ **Recovery by filter**: `republish_from_error` / `dead_letter_from_error` select by `exceptionType`,
    `exceptionContains`, `contract`, `since`/`until` instead of only messageIds. Preview = counts per group + samples;
    confirm processes up to `RabbitMcp:MaxBulkMessages` (default 1000, one scan of the queue, same confirm-then-ack
    guarantee, non-matching messages requeued with nack). The result reports what is left for a next call.
37. ✅ **Reading RabbitMQ streams** (`read_stream`, permission `read`): from `first`, `last`, an offset or a time
    ("last 10 minutes"), bounded count, via basic.consume with `x-stream-offset` (streams do not support basic.get).
    Reading a stream changes nothing, so no `force` is needed; `peek_messages` on a stream points to it.
38. ✅ **Limits for agents**: `RabbitMcp:RateLimits` per caller and action, e.g. `publish:100/h;republish:2000/h;request:300/h`
    (optionally per role). A runaway agent loop stops at the limit with a readable error; refusals audited as
    `limited`; metrics. In memory per instance (documented).

## Phase 14 — Beyond C# (`phase-14/asyncapi`) ✅ (2026-10-18)
39. ✅ **Contracts from AsyncAPI / JSON Schema files** (`RabbitMcp:ContractFiles`): teams without .NET describe their
    messages in AsyncAPI 2.6/3.0 (channels, operations, message payload schemas, AMQP bindings → exchange, routing key)
    or plain JSON Schema + a small mapping file. Each message becomes the same typed `publish_*`/`request_*` tool
    (inputSchema = the file's schema), validated with a JSON Schema validator instead of DataAnnotations; format raw
    by default, MassTransit when declared. Done with an own JSON Schema subset validator and YamlDotNet's representation
    model (no AsyncAPI parser dependency); plain JSON Schema files are covered by embedding them in a minimal AsyncAPI
    document (no separate mapping format).
40. ✅ **AsyncAPI export**: `rabbit-mcp contracts asyncapi Billing.Contracts.dll > asyncapi.yaml` — documentation of
    the C# contracts (exchanges, routing keys, formats, request/reply pairs) for other teams and tooling.
41. ✅ `contracts diff` for AsyncAPI files (same breaking/warning rules).

## Phase 15 — Distribution ⏸ (when the owner decides to release)
42. ⏸ First release `v1.0.0` (tag pushed by the owner): binaries, Docker image on GHCR (public package), NuGet.
43. 🟡 Prepared: `RabbitMcp.Server` tool package (`dotnet tool install -g RabbitMcp.Server` / `dnx`, PackageType McpServer), built and smoke-tested by release.yml, pushed with the NuGet key. Live after #42.
44. 🟡 Prepared: `src/RabbitMcp.Server/.mcp/server.json` (nuget + oci packages), `mcp-name` in README, image label. release.yml's `mcp-registry` job publishes it on every tag (GitHub OIDC, needs NUGET_API_KEY and a public GHCR package) — was #33.
