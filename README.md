# rabbit-mcp

<!-- mcp-name: io.github.lazogio/rabbit-mcp -->

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** · [ქართული](README.ka.md)

An MCP server for RabbitMQ built on **message contracts** (.NET 10). Every C# record contract becomes its own typed tool whose `inputSchema` is the contract itself. Messages use the MassTransit wire format, so MassTransit services receive them without any configuration — while the server itself does not depend on MassTransit. Services without MassTransit get plain JSON with their own exchanges and routing keys instead.

## What it does

- **Sends:** `publish_<contract>` publishes an event, `request_<contract>` sends a request and returns the reply. Either way the message is validated against its contract first.
- **Sees:** lists queues and their statistics, shows messages without removing them (with MassTransit fault details), and finds out who consumes each contract.
- **Recovers:** moves messages from an `_error` queue back to their original queue safely. Messages that can never succeed are parked in a dead-letter queue — never deleted.
- **Answers:** in responder mode the model answers requests sent by your services.
- **Protects:** `contracts diff` stops wire-breaking contract changes in CI, and roles limit who may do what.

## Install

**Prebuilt binary.** [Releases](https://github.com/lazogio/rabbit-mcp/releases) has a self-contained file for Linux (x64/arm64), Windows (x64) and macOS (x64/arm64) — no .NET installation needed. On macOS, before the first start: `xattr -d com.apple.quarantine rabbit-mcp`. `gh attestation verify <file> --repo lazogio/rabbit-mcp` proves a file was built by this repository's release workflow, and every dependency is listed in the release's SBOM (`*.sbom.cdx.json`).

**As a .NET tool** (needs the .NET 10 runtime; from the first release on nuget.org): `dotnet tool install -g RabbitMcp.Server` installs the `rabbit-mcp` command, or let the client start it without installing — `"command": "dnx", "args": ["RabbitMcp.Server", "--yes"]`. The package is listed on nuget.org as an MCP server, and the same manifest (`src/RabbitMcp.Server/.mcp/server.json`) is meant for the official MCP registry as `io.github.lazogio/rabbit-mcp`.

**Claude Code:**

```bash
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/rabbit-mcp
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "rabbit-mcp": {
      "command": "/full/path/to/rabbit-mcp",
      "env": { "RabbitMcp__Uri": "amqp://guest:guest@localhost:5672/", "RabbitMcp__EnablePublish": "true" }
    }
  }
}
```

**From source:** `dotnet publish src/RabbitMcp.Server -c Release -o out` and register `out/rabbit-mcp`. Do not use `dotnet run`: it writes to stdout and breaks MCP.

Local RabbitMQ: `docker compose up -d` (UI: http://localhost:15672, guest/guest).

**Try the sample.** [samples/Shop](samples/Shop) starts RabbitMQ, a MassTransit shop service and rabbit-mcp with one command, and walks through placing orders, asking for their status, recovering an error queue, parking a poison message and approving a large order — with the model doing the work.

## Tools

| Tool | What it does | Enabled by |
|---|---|---|
| `list_contracts` | Contracts: event/request, version, format (MassTransit or plain JSON), exchange, routing key, reply types | always |
| `validate_message` | Checks JSON against a contract without sending anything | always |
| `queue_stats` | Ready messages and consumers of queues | always |
| `peek_messages` | Shows messages without removing them: payload, contract check, fault, RabbitMQ dead-lettering history (`x-death`) | always |
| `find_message` | Finds a message by messageId, correlationId or text (e.g. an order id) and shows which queue it is in. Searches every `_error`/`_skipped`/`_dead` queue by default (`ManagementUri`), or the queues you name | always |
| `read_stream` | Reads a RabbitMQ stream from `first`, `last`, an offset or a time (`10m`) — removes nothing | always |
| `list_queues` | Queues with filters; error queues show the queue they belong to | `ManagementUri` |
| `contract_consumers` | Which queues a contract reaches, whether anything consumes it, its `_error`/`_skipped` backlog | `ManagementUri` |
| `recent_activity` | Latest audit events with filters (action, outcome, contract, user, queue, id, `since: 2h`): "who republished yesterday?" | `AuditLogPath` |
| `publish_<contract>` | Publishes an event | `EnablePublish` |
| `request_<contract>` | Sends a request and returns the reply | `EnablePublish` |
| `republish_from_error` | Moves messages from an `_error`/`_skipped`/`_dead` queue or a RabbitMQ dead-letter queue back: preview first, then `confirm=true`. For incidents, select by `exceptionType`, `exceptionContains`, `contract`, `since`, `until`: the preview groups the matches by fault and one call moves up to `MaxBulkMessages` | `EnableRepublish` |
| `dead_letter_from_error` | Parks messages that can never succeed in the dead-letter queue (`<queue>_dead`) with a reason. Deletes nothing | `EnableRepublish` |
| `resend_corrected` | Fixes a message that failed because of its data: shows the changes field by field, sends the corrected copy and parks the original unchanged | `EnableRepublish` + `EnablePublish` |
| `pending_requests` | Requests from services waiting for an answer (`waitSeconds` = long poll) | `RespondTo` |
| `reply_<response>` / `reject_request` | Answers a request or rejects it | `RespondTo` |

Every tool returns `structuredContent` with an `outputSchema`. Errors come back with `isError: true` and a reason you can act on — on a 403, for example, which permission the RabbitMQ user lacks.

### Prompts and resources

Clients that support MCP prompts offer these as slash commands:

| Prompt | What it does |
|---|---|
| `diagnose_errors` (`queue`) | Goes through the error queues, groups the faults by cause and proposes republish, dead-letter or a code fix per group |
| `investigate_message` (`reference`) | Finds a message by id or text and reconstructs its history from the queues and the audit |
| `review_approvals` (`contract`) | Walks through pending requests and answers each one as you decide |
| `contract_health` (`contract`) | Checks consumers, backlogs and faults per contract |

Prompts only tell the model what to do: every change still needs your confirmation and goes through the tools, with their permissions and audit.

Every contract is also a resource, `contract://<Name>`: its description, wire format and names and the JSON schema of the message and of its replies. Reading it needs the `read` permission.

## Contracts

```csharp
// Event → publish_payment_captured
[MessageContract("Payment was captured by the PSP.", Version = 1)]
public sealed record PaymentCaptured
{
    [Description("Order the payment belongs to.")]
    public required Guid OrderId { get; init; }

    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal Amount { get; init; }
}

// Request → request_get_order_status
[MessageContract("Asks for the current status of an order.",
    Responses = new[] { typeof(OrderStatus), typeof(OrderNotFound) })]
public sealed record GetOrderStatus
{
    public required Guid OrderId { get; init; }
}
```

- **Fields:** `required` and nullability decide what is mandatory. `[Description]` is the explanation the model reads. DataAnnotations (`[Range]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]` …) appear in the schema and the server enforces them, in nested objects and lists too.
- **Names:** the URN is `urn:message:Namespace:Type` and the exchange `Namespace:Type`, as in MassTransit. Use `Urn`/`Exchange` to bind to a different name.
- **Your own contracts:** create a class library that references the `RabbitMcp.Contracts` package (the nupkg is attached to each release), build it and point the server at it: `RabbitMcp__ContractAssemblies=/path/Billing.Contracts.dll`. Its dependencies must sit next to the DLL. No need to rebuild the server.

### Services without MassTransit

A service that uses RabbitMQ directly — another language, plain RabbitMQ.Client, an older system — gets the same typed tools with `Format = MessageFormat.Raw`:

```csharp
// Plain JSON to a topic exchange → publish_stock_adjusted
[MessageContract("Stock of a product changed.", Format = MessageFormat.Raw,
    Exchange = "inventory", RoutingKey = "stock.adjusted", Urn = "stock.adjusted")]
public sealed record StockAdjusted { … }

// RabbitMQ RPC straight to a queue → request_get_shipping_quote
[MessageContract("Asks the shipping desk for a quote.", Format = MessageFormat.Raw, RoutingKey = "shipping-quotes",
    Responses = new[] { typeof(ShippingQuote), typeof(ShippingUnavailable) })]
public sealed record GetShippingQuote { … }
```

- **Body:** the message itself as JSON (`application/json`, camelCase, enums as numbers; `[JsonPropertyName]` for other names). Message id, correlation id, the type (`Urn`, here `stock.adjusted`) and the sender (`app_id`) go into AMQP properties, `Contract-Version` into a header.
- **Routing:** `Exchange` and `RoutingKey` the way the service expects them — there is no default. `RoutingKey` alone sends straight to that queue (the default exchange). Before sending, the server checks that the exchange or queue exists; an unroutable message is an error, never a silent loss.
- **Requests:** RabbitMQ RPC. The request carries `reply_to` and `correlation_id` (the request id). The service replies to `reply_to` with the same `correlation_id` and names the reply type (`ShippingQuote` or its URN) in the AMQP `type` property — optional when there is only one reply type. A workflow `correlationId` goes into the `X-Correlation-Id` header.
- **Seeing and recovering:** `peek_messages` matches plain JSON to its contract by the `type` property and shows RabbitMQ's dead-lettering history (`x-death`). `republish_from_error` on a dead-letter queue sends each message straight back to the queue that rejected it (`targetQueue` chooses another one). `contract_consumers` follows direct and topic bindings by the routing key.
- Responder mode answers MassTransit requests only.

### Contracts without C# (AsyncAPI)

Teams that do not write .NET describe their messages in [AsyncAPI](https://www.asyncapi.com) 3.0 or 2.x (YAML or JSON) and point the server at the file — no DLL needed: `RabbitMcp__ContractFiles=warehouse.yaml`. Every message of an operation becomes the same typed tool, with the file's JSON Schema as its `inputSchema`; a 3.0 operation with a `reply` becomes `request_*`. Example: [samples/asyncapi/warehouse.yaml](samples/asyncapi/warehouse.yaml).

```yaml
channels:
  stockAdjusted:
    address: stock.adjusted                      # routing key
    bindings: { amqp: { is: routingKey, exchange: { name: warehouse, type: topic } } }
    messages: { StockAdjusted: { $ref: '#/components/messages/StockAdjusted' } }
```

- **Routing** comes from the channel's AMQP binding: `is: queue` sends straight to that queue; otherwise the binding's exchange with the channel address as routing key.
- **Format:** plain JSON (raw) by default; `x-rabbit-mcp: { format: masstransit, urn: 'urn:message:Ns:Type' }` on a message for MassTransit services. `x-rabbit-mcp.version` and `toolName` work like on `[MessageContract]`.
- **Validation:** type, required, enum, string length/pattern/format, numeric bounds, items, `allOf`/`anyOf`/`oneOf`; local `$ref`s are resolved. Responder mode needs C# contracts.
- **The other way round:** `rabbit-mcp contracts asyncapi Billing.Contracts.dll > asyncapi.json` describes C# contracts as AsyncAPI 3.0 for other teams and tooling, and `contracts diff` also compares two AsyncAPI files.

### Compatibility check

A change that still compiles can break running services on the wire. Compare two builds:

```bash
rabbit-mcp contracts diff old/Billing.Contracts.dll new/Billing.Contracts.dll   # or --json
```

```
BREAKING OrderPlaced customer: Type changed from string to number.
BREAKING OrderPlaced channel: New required property: existing publishers do not send it.
WARNING  OrderPlaced: Breaking change without a version bump (still v1).
INFO     OrderPlaced coupon: New optional property.
```

- **breaking:** a removed contract, reply type, required field or enum value; a changed URN, format, exchange, routing key or type; a new or newly required field.
- **warning:** a removed optional field, a field that may now be `null`, a new reply type or enum value, a breaking change without a version bump.
- **exit code:** `0` compatible, `1` breaking changes, `2` error. The JSON structure is compared; DataAnnotations rules are not. Only compare builds you trust: loading a DLL runs its attribute code.

In a service's CI (with `actions/checkout` `fetch-depth: 0`):

```yaml
- run: |
    curl -sL https://github.com/lazogio/rabbit-mcp/releases/download/v0.1.0/rabbit-mcp-0.1.0-linux-x64.tar.gz | tar xz
    git worktree add ../base origin/main && dotnet build ../base/src/Billing.Contracts -c Release -o base
    dotnet build src/Billing.Contracts -c Release -o head
    rabbit-mcp/rabbit-mcp contracts diff base/Billing.Contracts.dll head/Billing.Contracts.dll
```

## Request/response

The model asks a service with `request_<contract>`, and a MassTransit consumer answers as usual: `await context.RespondAsync(new OrderStatus { … })`. The request carries a `requestId`, a temporary `responseAddress` and a TTL equal to the timeout (1–120 seconds, default 30). The result shows:

- `status`: `responded`, `faulted` or `timeout`;
- `response` and `responseCheck`: whether the reply satisfies its contract;
- the consumer's exception, if MassTransit returned a `Fault<T>`.

For [services without MassTransit](#services-without-masstransit) the same tool uses RabbitMQ RPC (`reply_to` + `correlation_id`).

### Responder: a service asks, the model answers

`RabbitMcp__RespondTo=AskHuman` (or `Get*`, MassTransit request contracts) turns the server into a consumer. A service asks as usual, e.g. `GetResponse<HumanAnswer, HumanDeclined>(new AskHuman { … })`, and the model answers:

1. The request shows up in `pending_requests`: the message, its contract check, the reply tools and `expiresAt`. `waitSeconds: 50` waits for the next request, so there is no need to poll.
2. The model answers with `reply_<response>` (the reply is validated against its contract first) or `reject_request`. A rejection makes the requester's call fail at once with `RequestFaultException`.
3. Expired requests disappear. Requests beyond `MaxPendingRequests` are rejected with a Fault right away.

Requests are kept in memory and lost on restart. There are no automatic answers: every answer comes from the model or a person. Only one instance per `ClientName` answers; the others stand by and take over within ~5 seconds when it goes away. After a lost connection the responder reconnects by itself.

## For teams: the HTTP server

`RabbitMcp__Transport=http` makes one server available to a whole team: the MCP endpoint is `/mcp`, the health check `/healthz`. **The server refuses to start without authentication:**

- **API keys:** `RabbitMcp__ApiKeys="alice:KEY1;ci:KEY2"`. Keys are at least 32 characters (`openssl rand -base64 32`). Clients send `X-Api-Key: KEY` or `Authorization: Bearer KEY`.
- **JWT / OIDC** (Entra ID, Keycloak, Auth0 …): `RabbitMcp__JwtAuthority` and `RabbitMcp__JwtAudience`. On a 401 the server points to `/.well-known/oauth-protected-resource`, so clients find the authorization server by themselves.

```bash
claude mcp add --transport http rabbit-mcp https://mcp.example.com/mcp --header "X-Api-Key: KEY1"
```

**Docker:** `ghcr.io/lazogio/rabbit-mcp` (amd64/arm64) runs in HTTP mode on `0.0.0.0:5080` as a non-root user. Put it behind a reverse proxy (nginx, Traefik, an ingress) for TLS.

```bash
docker run -d -p 5080:5080 \
  -e RabbitMcp__Uri=amqp://user:pass@rabbit:5672/ \
  -e RabbitMcp__ApiKeys="team:$(openssl rand -base64 32)" \
  -e RabbitMcp__ContractAssemblies=/contracts/Billing.Contracts.dll -v ./contracts:/contracts:ro \
  ghcr.io/lazogio/rabbit-mcp:0.1
```

### Permissions (roles)

Until a role is defined, every authenticated caller may use every enabled tool. Once roles exist, each caller keeps only the permissions of their roles:

| Permission | Tools | Can be limited by |
|---|---|---|
| `read` | Looking: lists, statistics, peek, `contract_consumers`, `find_message`, `recent_activity` | — |
| `publish` / `request` | `publish_*` / `request_*` | contract: `publish:Billing.*,Order*` |
| `republish` | `republish_from_error`, `dead_letter_from_error` | error queue: `republish:orders-*_error` (case-sensitive, like queue names). Choosing another destination (`targetExchange`, `targetQueue`, `deadLetterQueue`) needs `republish` without patterns |
| `respond` | `pending_requests`, `reply_*`, `reject_request` | request contract: `respond:AskHuman` |
| `*` | everything | — |

```bash
RabbitMcp__Roles__viewer="read"
RabbitMcp__Roles__operator="read;publish:Order*;request;republish:*_error"
RabbitMcp__Roles__admin="*"
RabbitMcp__UserRoles="alice=operator;ci=viewer;bob@example.com=admin"
RabbitMcp__DefaultRoles="viewer"
```

- **Who gets a role:** the API key's name (`alice:KEY` → `alice`), the token's user (`sub`, `preferred_username`, or `email` when the token says `email_verified`) or a value of the JWT `roles` claim. `JwtRolesClaim` changes the claim name. A token user whose name equals an API key's name does not get that key's roles.
- **Callers without a role** get `DefaultRoles`. If that is empty, they can do nothing.
- **Checked twice.** Callers only see the tools they may use, and every call is checked again with its arguments (queue, contract); refusals are audited.
- **Ceiling:** roles choose among the tools enabled by `Enable*` and `PublishAllow`; they cannot enable a disabled one.
- **Over stdio** there is a single local user, so roles apply to HTTP only.

## Configuration

Environment variable `RabbitMcp__Name`, or `"RabbitMcp": { "Name": … }` in `appsettings.json`. Lists are separated by `;`, and `*` in patterns matches anything.

| Option | Default | Description |
|---|---|---|
| `Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI, vhost at the end (`…/prod`) |
| `ClientName` | `rabbit-mcp` | Connection name and the envelope's `sourceAddress` |
| `ContractAssemblies` | — | Your contract DLLs. Empty: the built-in sample contracts |
| `ContractFiles` | — | AsyncAPI files (YAML/JSON) whose messages become contracts; alone, they replace the sample contracts |
| `ManagementUri` | — | Management API (`http://localhost:15672`), enables `list_queues` and `contract_consumers` |
| `EnablePublish` / `EnableRepublish` | `false` | Enables sending / republishing and dead-lettering |
| `PublishAllow` / `PublishDeny` | — | Contracts that may / may never be sent (deny wins), e.g. `Get*` |
| `DeadLetterQueue` | `{queue}_dead` | Where poison messages are parked. `{queue}` is the queue the error queue belongs to; a fixed name like `dead-letters` works too |
| `RespondTo` / `MaxPendingRequests` | — / `100` | Contracts the responder answers / limit of waiting requests |
| `HumanConfirmation` | `auto` | Who confirms changes: `auto` — the person, in the client's dialog, when the client can ask (else the tool's `confirm`); `required` — refuse when the person cannot be asked; `off` — only `confirm` |
| `ConfirmTools` | `republish_from_error;dead_letter_from_error;resend_corrected` | Tools the person confirms; add e.g. `publish_*;reply_*` |
| `AuditLogPath` / `AuditIncludePayload` | — / `false` | Audit file (JSON Lines) / record message bodies too (may contain personal data) |
| `MaxMessagesPerCall` / `MaxPayloadChars` | `50` / `16000` | Limit for peek/republish / bodies longer than this are shortened for display |
| `MaxBulkMessages` | `1000` | Limit for republish/dead-letter with a filter |
| `RateLimits` | — | Changing calls per caller, e.g. `publish:100/h;republish:500/h;*:1000/d` (`publish`, `request`, `republish`, `respond`, `*`) |
| `ExchangeCacheSeconds` | `30` | How long an existing exchange is remembered (`0` = off) |
| `Transport` | `stdio` | `stdio` or `http` |
| `HttpUrls` / `HttpPath` / `HttpPublicUrl` | `http://localhost:5080` / `/mcp` / — | Listen address / endpoint / public URL behind a proxy |
| `ApiKeys` | — | `name:KEY;…` |
| `JwtAuthority` / `JwtAudience` / `JwtScopes` | — | OIDC. Tokens must carry every scope in `JwtScopes` |
| `Roles__<role>` / `UserRoles` / `DefaultRoles` / `JwtRolesClaim` | — / — / — / `roles` | Permissions, see above |
| `HttpAllowedOrigins` | — | Browser origins allowed to call the server |
| `HttpAllowAnonymous` | `false` | No authentication, `localhost` only |

**OpenTelemetry** is enabled by `OTEL_EXPORTER_OTLP_ENDPOINT`: `publish`/`request`/`republish` spans and the metrics `rabbitmcp.operations` and `rabbitmcp.request.duration`. Sent messages carry `traceparent` and `MT-Activity-Id`, so the MassTransit consumer's span joins the same trace.

## Reliability and security

- **No message is lost.** Nothing is sent when validation fails. The exchange (or, for the default exchange, the queue) is checked first. Publisher confirms and `mandatory` turn an undeliverable message into an error.
- **Republish:** a message leaves the error queue only after the broker confirmed the republish. The body is unchanged; only fault and `x-death` headers are removed. Delivery is at-least-once.
- **Dead-letter:** there is no tool that deletes messages. A queue with consumers is never used as the dead-letter queue. Poison messages move to a durable `_dead` queue the same way, after confirmation, with body and every header (fault details included) unchanged, plus `X-Dead-Lettered-By/-From/-At` and `X-Dead-Letter-Reason`. `republish_from_error` brings them back.
- **The person confirms, not the model.** `confirm=true` is written by the model. Clients that support MCP elicitation (Claude Code, for example) show the person a dialog with the action and its arguments before anything moves; a refusal stops it and is audited. Over stdio this works today; the stateless HTTP transport cannot ask, so there `confirm=true` counts unless `HumanConfirmation=required`.
- **Limits for agents:** `RateLimits` stops a caller (e.g. an agent stuck in a loop) after N changes per minute, hour or day; refusals are audited as `limited`. Previews and reads never count.
- **Corrections keep the original.** `resend_corrected` validates the correction against the contract, sends it as a new message (`X-Corrected-From`) and parks the original unchanged in the dead-letter queue (`X-Corrected-To`).
- **Peek:** needs `force=true` on queues with active consumers. Messages stay in the queue but become `redelivered`.
- **Audit:** every send, request, republish, dead-lettering and answer is recorded with its outcome and the user — failed and refused attempts too:
  ```json
  {"timestamp":"2026-10-13T12:00:00+00:00","action":"publish","outcome":"sent","client":"alice (claude-code 2.1.0)","contract":"OrderSubmitted","messageId":"…"}
  ```
- **Secrets:** passwords never appear in logs, errors or the audit. API keys are kept as SHA-256 hashes.
- **HTTP:** authentication is mandatory, JWT scopes are enforced, foreign `Origin`s are refused.
- **Message contents are not trusted.** The responder only answers MassTransit's temporary reply endpoints; republish does not follow a forged `MT-Fault-InputAddress` or `x-death`; text seen through peek is data, not instructions.
- **Least privilege:** give the RabbitMQ user only the permissions it needs; the `monitoring` tag is enough for the Management API. On a 403 the tool says what is missing.

## License

[MIT](LICENSE). Working on the project, tests and releases: [CONTRIBUTING.md](CONTRIBUTING.md).
