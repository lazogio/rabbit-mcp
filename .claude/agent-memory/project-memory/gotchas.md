# Gotchas (non-obvious facts; one line each where possible)

## RabbitMQ / RabbitMQ.Client 7.x
- Passive declare of a missing exchange/queue (404) closes the channel → always use a throwaway channel.
- AMQP header string values arrive as `byte[]`, not `string` → decode with `FaultHeaders.ToDisplayString`.
- `CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)` makes
  `BasicPublishAsync` await the confirm; with `mandatory: true` an unroutable message throws `PublishException { IsReturn: true }`.
- `basic.get` + nack(requeue) sets `redelivered` and may reorder; on quorum queues each peek counts toward the delivery-limit.
- `BasicDeliverEventArgs.Body` is only valid inside the handler → parse/copy before returning.
- Passive declare of a queue exclusive to another connection fails with 405 RESOURCE_LOCKED (closes the channel), yet
  publishing to it works → `QueueExistsAsync` treats 405 as "exists"; tools iterating queues must not abort on it.
- RabbitMQ 4 quorum queues: `basic.reject` and channel closure with unacked messages count towards the delivery limit
  (default 20, then dropped or dead-lettered); `basic.nack(requeue)` does not → always nack in finally, never just close.
- RabbitMQ 4 refuses transient non-exclusive queues (`durable:false, exclusive:false`): 541 INTERNAL_ERROR
  "transient_nonexcl_queues is deprecated" closes the whole connection → declare test/sample queues durable.
- The default exchange ("") always exists and is never declared (declaring it is refused) → for default-exchange contracts check the queue
  (`GetQueueAsync(RoutingKey)`); mandatory publish to a missing queue is returned (`PublishException.IsReturn`).
- `x-death` (RabbitMQ dead-lettering) = AMQP array of tables, newest first, compressed per {queue, reason}; fields
  queue, reason, count (long), time (AmqpTimestamp), exchange, routing-keys (array); also x-first-death-*/x-last-death-*.
  Publishers can set these headers too → treat like MT-Fault-InputAddress (untrusted, conflict check).
- Dead-lettering removes the per-message `expiration` (kept as original-expiration in x-death), so an expired-TTL
  request parked in a DLQ does not expire again when republished.

## C# / tests
- `JsonValue.TryGetValue<T>` succeeds only for the stored CLR type (a `JsonValue.Create(long)` is not a decimal);
  for numbers from mixed sources use `GetValueKind()` + the JSON text.
- MCP C# SDK: `Tool.InputSchema` (ProtocolTool) is settable — a generated tool's schema can be replaced after `Create`.
- An in-process MCP session for tests: two AnonymousPipeServerStream/ClientStream pairs, server
  `.WithStreamServerTransport(in, out)`, client `McpClient.CreateAsync(new StreamClientTransport(toServer, fromServer), options)`
  — elicitation works there (stdio-like), unlike the stateless HTTP TestServer.
- Integration tests share queues across classes (e.g. `it-order-cancelled_dead`): assert counts relative to before.
- Changing a positional record (e.g. `PeekedMessage`) breaks target-typed `new(...)` calls too — grep for `new(` in
  helpers, not only `new PeekedMessage(` (missed once in SearchAndAuditTests, phase 11).

## MassTransit wire format (verified against v8.5.5 source)
- URN `urn:message:{Namespace}:{Type}`; publish exchange `{Namespace}:{Type}` (fanout, created when a consumer starts).
- Generic/nested names: `Fault[[Ns:Type]]`, `Outer+Inner` → contracts must be non-generic, non-nested.
- Envelope JSON is camelCase; enums are numbers; decimals may arrive as strings (MT reads both).
- Faults of requests go to `FaultAddress ?? ResponseAddress` with the same `requestId`.
- `?temporary=true` address = exchange `durable:false, autoDelete:true`; a mismatching declare → PRECONDITION_FAILED.
- Error queues: `<queue>_error` / `<queue>_skipped`; original queue is in header `MT-Fault-InputAddress`;
  every receive queue has a same-name fanout exchange bound to it (republish target).
- MassTransit v9 is commercial; v8 stays open source (maintenance ends after 2026) → never depend on the package in the server.

- For envelope-serialized messages MassTransit's `ConsumeContext.Headers` are the **envelope** headers; AMQP headers
  (e.g. `X-Republished-From`, `MT-Fault-*`) are only in `ReceiveContext.TransportHeaders` (proven by integration test, 12c0df5).
- MassTransit sends replies to our `responseAddress` even if its host part differs from MassTransit's own host config
  (send transport uses its own connection; only the path/vhost/query of the address matter).

## RabbitMQ Management API
- Name filter without `use_regex` is a case-insensitive substring match; `page_size` max is 500; pages past
  `page_count` → 400 "page out of range" (rabbit_mgmt_util.erl). Stats (counts, consumers) lag ~5 s → poll in tests.
- The user needs the `monitoring` or `management` tag; AMQP-only users get 401.
- Testcontainers RabbitMqBuilder maps only 5672 — add `.WithPortBinding(15672, true)` for the HTTP API.

## .NET loading
- Publishing to a deleted exchange with confirms → `OperationInterruptedException` (ReplyCode 404) from `BasicPublishAsync`.
- External contract DLLs: load into the default ALC; keep a stable AssemblyVersion on the shared attribute assembly.

## Tracing
- MassTransit 8.5 does NOT read `traceparent`: it parents its receive span from the AMQP header `MT-Activity-Id`
  (a W3C id string) and writes the same header on send (`LogContextActivityExtensions.cs`). Write both headers.
- MassTransit only creates spans when someone listens to ActivitySource "MassTransit" (tests add an ActivityListener).
- MCP SDK ActivitySource/Meter name: "Experimental.ModelContextProtocol".

## MCP C# SDK (ModelContextProtocol 2.2)
- Parameters of type `McpServer`, `RequestContext<CallToolRequestParams>`, `IProgress<ProgressNotificationValue>`,
  `ClaimsPrincipal` (and DI services) are injected and excluded from the input schema; give them `= null` defaults
  so direct callers (tests) can omit them. `McpServer.ClientInfo` = the client's initialize name/version.
- Only `McpException` messages reach the client; other exceptions become a generic "An error occurred invoking '<tool>'".
- `UseStructuredContent = true` + record return type → `outputSchema` + `structuredContent`.
- DataAnnotations (`Range`, `StringLength`, `RegularExpression`, `EmailAddress`, `MinLength`) are mapped into the input schema by MEAI.

## Environment / tooling
- `IHeaderDictionary.Append(key, value)` is an extension in `Microsoft.AspNetCore.Http` — add the using in auth handlers.
- An `AuthenticationHandler` subclass must not declare its own `Scheme` member (hides the inherited one) → `SchemeName`.
- The release workflow only runs on branch pushes that touch release.yml — don't wait for 2 runs otherwise.
- Release workflow's `paths:` filter applies to branch pushes only; tag pushes always run (GitHub ignores path filters for tags).
- Deleting a remote branch via `git push --delete` is cut off by the session git proxy ("unexpected disconnect");
  pushes work. Ask the repo owner to delete branches (or enable "Automatically delete head branches").
- Artifacts can be listed without auth (`/actions/runs/<id>/artifacts`) but not downloaded — verify binaries by
  running them inside the workflow (smoke test), not locally.
- CI logs need auth, but check-run **annotations** don't: test failures are emitted as annotations by
  `--logger GitHubActions`; read them via `/repos/lazogio/rabbit-mcp/check-runs/<job id>/annotations`.
- The cloud authoring sandbox cannot reach api.nuget.org (policy 403) → code cannot be built there; CI is the verifier.
- Registering `dotnet run` as an MCP server corrupts stdout (build output) → always register the published binary.
- `gh` CLI token in the sandbox is invalid; pushes go through the session git proxy; GitHub REST API is readable unauthenticated (the repo is public).
- GitHub repo was created with an auto-README → first push needed a rebase (kept the full README).
- C#: avoid collection expressions where target typing is ambiguous (`Assert.Equal([...], ...)`, `cond ? x : [..]`) → use `new[] { }`.

- Management API: `GET /api/exchanges/{vhost}/{name}/bindings/source` answers `200 []` for a missing exchange —
  check `GET /api/exchanges/{vhost}/{name}` (404) first.
- RabbitMQ exclusive queues are per connection: declaring one held by another connection fails with 405
  RESOURCE_LOCKED (closes the channel) — used for responder active/standby; on the *same* connection it succeeds.
- `JsonSchemaExporter` needs options with a `TypeInfoResolver` → copy the options and set `DefaultJsonTypeInfoResolver`.
- MCP has no reliable way to wake the model on a server event (logging/resource notifications are not shown to it,
  stateless HTTP cannot push) → long-poll tools (`waitSeconds`).
- Two builds of the same contracts assembly can only be compared in separate (collectible) AssemblyLoadContexts that
  defer `RabbitMcp.Contracts` to the default context (otherwise `[MessageContract]` is a different type).
- Shell: never use `|` as the sed delimiter when the pattern contains `|` (it silently mangled a README line).

- Envelope addresses (`responseAddress`, `faultAddress`) and AMQP headers (`MT-Fault-InputAddress`) are attacker-
  controlled for anyone who can publish: never route by them without checking (see BUG-011, BUG-013).
- MCP C# SDK: only `McpException` messages reach the client; anything else becomes "An error occurred invoking 'x'."
  → `WithRequestFilters(f => f.AddCallToolFilter(...))` to translate. The MCP auth scheme forwards authenticate to "Bearer".
- Testing JWT offline: PostConfigure `JwtBearerOptions.Configuration = new OpenIdConnectConfiguration { Issuer, SigningKeys }`
  before `AddJwtBearer` runs; tokens from `JsonWebTokenHandler.CreateToken`.
- RabbitMQ.Client 7: `AlreadyClosedException : OperationInterruptedException : RabbitMQClientException` — match the
  most derived first; `BrokerUnreachableException : IOException`.

- MassTransit (System.Text.Json) writes `decimal` as a JSON **string** ("1800") and reads number or string.
- MassTransit creates `<queue>_error` only on the first fault — poll for it, "does not exist" is "not yet".
- RabbitMQ in docker compose: set RABBITMQ_DEFAULT_USER/PASS; `guest` is meant for localhost connections.

- mcr.microsoft.com is not reachable from the authoring sandbox (403): get image digests in CI with
  `docker buildx imagetools inspect <image> --format '{{json .Manifest}}'` and print them as annotations.
- SBOM of a single-file .NET binary: scan a framework-dependent publish (deps.json) instead — syft cannot see inside.

## Anti-patterns seen in other RabbitMQ MCP servers (do not copy)
- Republish that consumes with `ack_requeue_false` before publishing → message lost if publish fails.
- Management API `/get` with `truncate: 50000` used for republish → large bodies republished truncated.
- Untyped `publish_message(exchange, routingKey, payload: string)` → no contract validation.
