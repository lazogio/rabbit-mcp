# Gotchas (non-obvious facts; one line each where possible)

## RabbitMQ / RabbitMQ.Client 7.x
- Passive declare of a missing exchange/queue (404) closes the channel → always use a throwaway channel.
- AMQP header string values arrive as `byte[]`, not `string` → decode with `FaultHeaders.ToDisplayString`.
- `CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)` makes
  `BasicPublishAsync` await the confirm; with `mandatory: true` an unroutable message throws `PublishException { IsReturn: true }`.
- `basic.get` + nack(requeue) sets `redelivered` and may reorder; on quorum queues each peek counts toward the delivery-limit.
- `BasicDeliverEventArgs.Body` is only valid inside the handler → parse/copy before returning.

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

## Anti-patterns seen in other RabbitMQ MCP servers (do not copy)
- Republish that consumes with `ack_requeue_false` before publishing → message lost if publish fails.
- Management API `/get` with `truncate: 50000` used for republish → large bodies republished truncated.
- Untyped `publish_message(exchange, routingKey, payload: string)` → no contract validation.
