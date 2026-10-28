# rabbit-mcp

<!-- mcp-name: io.github.lazogio/rabbit-mcp -->

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**English** · [ქართული](README.ka.md)

An MCP server for RabbitMQ built on **message contracts** (.NET 10). Each contract becomes a typed tool whose input schema is the contract itself. Messages use the MassTransit wire format, so MassTransit services receive them as they are; services without MassTransit get plain JSON. The server itself does not depend on MassTransit.

- **Send:** `publish_<contract>` and `request_<contract>`, validated against the contract first.
- **See:** queues, messages (with fault details) and who consumes each contract.
- **Recover:** move messages from `_error` queues back, fix and resend them, or park poison messages. Nothing is ever deleted.
- **Answer:** in responder mode the model answers requests sent by your services.
- **Protect:** `contracts diff` catches wire-breaking changes in CI; roles, rate limits and audit for teams.

## Install

| Way | How |
|---|---|
| Binary | [Releases](https://github.com/lazogio/rabbit-mcp/releases): self-contained for Linux, Windows and macOS. macOS: `xattr -d com.apple.quarantine rabbit-mcp` |
| .NET tool | `dotnet tool install -g RabbitMcp.Server`, or without installing: `dnx RabbitMcp.Server --yes` (from the first release) |
| Docker | `ghcr.io/lazogio/rabbit-mcp` — HTTP mode, see [For teams](#for-teams-http) |
| Source | `dotnet publish src/RabbitMcp.Server -c Release -o out` (never `dotnet run`: it writes to stdout) |

```bash
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/rabbit-mcp
```

Claude Desktop: the same as `"command"` and `"env"` in `claude_desktop_config.json`. Local RabbitMQ: `docker compose up -d`.

**Try it:** [samples/Shop](samples/Shop) starts RabbitMQ, PostgreSQL, a MassTransit shop and rabbit-mcp with one command, and walks through orders, error recovery and approvals.

## Tools

| Tool | What it does | Enabled by |
|---|---|---|
| `list_contracts`, `validate_message` | Contracts and their details; check JSON without sending | always |
| `queue_stats`, `peek_messages` | Queue counts; messages without removing them (payload, contract check, fault, `x-death`) | always |
| `find_message` | Finds a message by id or text and tells which queue it is in | always |
| `read_stream` | Reads a RabbitMQ stream from `first`, `last`, an offset or a time (`10m`) | always |
| `list_queues`, `contract_consumers` | Queues; which queues a contract reaches and who consumes it | `ManagementUri` |
| `recent_activity` | Audit: "who republished yesterday?" | `AuditLogPath` |
| `publish_<contract>`, `request_<contract>` | Publishes an event / sends a request and returns the reply | `EnablePublish` |
| `republish_from_error` | Moves messages back from an error or dead-letter queue: preview, then `confirm=true`. Filters (`exceptionType`, `contract`, `since` …) for incidents | `EnableRepublish` |
| `dead_letter_from_error` | Parks messages that can never succeed in `<queue>_dead`, with a reason | `EnableRepublish` |
| `resend_corrected` | Sends a fixed copy of a failed message and parks the original | `EnableRepublish` + `EnablePublish` |
| `declare_exchange`, `declare_queue` | Creates a missing exchange / queue, optionally with a binding; never changes or deletes one | `EnableTopology` |
| `pending_requests`, `reply_<response>`, `reject_request` | Requests from services waiting for an answer | `RespondTo` |

Results are structured (`outputSchema`); errors say what to do next. Prompts `diagnose_errors`, `investigate_message`, `review_approvals` and `contract_health` walk through common tasks, and every contract is a resource `contract://<Name>`.

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

- `required`, nullability, `[Description]` and DataAnnotations go into the schema and are enforced.
- Names follow MassTransit (`urn:message:Namespace:Type`, exchange `Namespace:Type`); `Urn` and `Exchange` override them.
- Your contracts: a class library referencing the `RabbitMcp.Contracts` package, loaded with `RabbitMcp__ContractAssemblies=/path/Billing.Contracts.dll`.

**Services without MassTransit** — `Format = MessageFormat.Raw`: the body is plain JSON, metadata goes into AMQP properties, and you set `Exchange` and/or `RoutingKey`. Requests use RabbitMQ RPC (`reply_to` + `correlation_id`; the reply type in the AMQP `type`).

```csharp
[MessageContract("Asks the shipping desk for a quote.", Format = MessageFormat.Raw, RoutingKey = "shipping-quotes",
    Responses = new[] { typeof(ShippingQuote), typeof(ShippingUnavailable) })]
public sealed record GetShippingQuote { … }
```

**Without C# (AsyncAPI)** — `RabbitMcp__ContractFiles=warehouse.yaml`: every message of an AsyncAPI 3.0 or 2.x file becomes a tool with the file's JSON Schema; routing comes from the AMQP bindings, `x-rabbit-mcp: { format: masstransit }` for MassTransit services. Example: [samples/asyncapi/warehouse.yaml](samples/asyncapi/warehouse.yaml). `rabbit-mcp contracts asyncapi Billing.Contracts.dll` exports C# contracts.

**Compatibility check** — compare two builds (or two AsyncAPI files) in CI; exit code `1` on breaking changes:

```bash
rabbit-mcp contracts diff old/Billing.Contracts.dll new/Billing.Contracts.dll
# BREAKING OrderPlaced customer: Type changed from string to number.
# WARNING  OrderPlaced: Breaking change without a version bump (still v1).
```

## Request/response

`request_<contract>` waits for the reply (timeout 1–120 s) and returns `responded`, `faulted` or `timeout`, with the reply checked against its contract.

**Responder:** `RabbitMcp__RespondTo=ApproveLargeOrder` lets your service ask and the model answer. Requests appear in `pending_requests`; the model answers with `reply_<response>` or `reject_request` (the requester gets a `RequestFaultException`). Requests are kept in memory; one instance per `ClientName` answers, others stand by.

## For teams (HTTP)

`RabbitMcp__Transport=http` serves `/mcp` (and `/healthz`). It refuses to start without authentication:

- **API keys:** `RabbitMcp__ApiKeys="alice:KEY1;ci:KEY2"` (≥ 32 characters); clients send `X-Api-Key`.
- **JWT / OIDC:** `RabbitMcp__JwtAuthority` + `RabbitMcp__JwtAudience`.

```bash
docker run -d -p 5080:5080 \
  -e RabbitMcp__Uri=amqp://user:pass@rabbit:5672/ \
  -e RabbitMcp__ApiKeys="team:$(openssl rand -base64 32)" \
  -e RabbitMcp__ContractAssemblies=/contracts/Billing.Contracts.dll -v ./contracts:/contracts:ro \
  ghcr.io/lazogio/rabbit-mcp:1.0
```

**Roles** (HTTP only; without roles every caller may use every enabled tool):

```bash
RabbitMcp__Roles__viewer="read"
RabbitMcp__Roles__operator="read;publish:Order*;request;republish:*_error"
RabbitMcp__UserRoles="alice=operator;ci=viewer"
RabbitMcp__DefaultRoles="viewer"
```

Permissions: `read`, `publish`, `request`, `republish`, `respond`, `topology`, `*`; patterns limit them by contract, queue or (`topology`) exchange/queue name. Users are matched by API key name, token user or the JWT `roles` claim. Callers see only the tools they may use, and each call is checked again.

## Configuration

Environment variable `RabbitMcp__<Option>` or `appsettings.json`. Lists are `;`-separated, `*` is a wildcard.

| Option | Default | Description |
|---|---|---|
| `Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI, vhost at the end |
| `ContractAssemblies` / `ContractFiles` | — | Contract DLLs / AsyncAPI files. Empty: built-in samples |
| `ManagementUri` | — | Management API, e.g. `http://localhost:15672` |
| `EnablePublish` / `EnableRepublish` | `false` | Allow sending / moving messages |
| `EnableTopology` / `TopologyAllow` / `TopologyDeny` | `false` / — / — | Allow creating exchanges and queues / names that may / may never be created |
| `TopologyArguments` | DLX, alternate exchange, single active consumer | x-arguments the declare tools accept (`*` any, `none`); TTL and max-length drop messages, so add them deliberately |
| `PublishAllow` / `PublishDeny` | — | Contracts that may / may never be sent |
| `DeadLetterQueue` | `{queue}_dead` | Where poison messages are parked |
| `RespondTo` / `MaxPendingRequests` | — / `100` | Responder contracts / waiting-request limit |
| `HumanConfirmation` / `ConfirmTools` | `auto` / the recovery and declare tools | The person confirms changes in the client's dialog (`required` refuses when it cannot ask, `off` trusts `confirm`) |
| `AuditLogPath` / `AuditIncludePayload` | — / `false` | Audit file (JSON Lines) / include message bodies |
| `MaxMessagesPerCall` / `MaxBulkMessages` / `MaxPayloadChars` | `50` / `1000` / `16000` | Limits |
| `RateLimits` | — | Changes per caller, e.g. `publish:100/h;*:1000/d` |
| `Transport` / `HttpUrls` / `HttpPath` | `stdio` / `http://localhost:5080` / `/mcp` | Transport |
| `ApiKeys`, `JwtAuthority`, `JwtAudience`, `JwtScopes` | — | HTTP authentication |
| `Roles__<role>`, `UserRoles`, `DefaultRoles` | — | Roles |
| `ClientName`, `HttpPublicUrl`, `HttpAllowedOrigins`, `HttpAllowAnonymous`, `ExchangeCacheSeconds`, `JwtRolesClaim` | | Rarely needed |

OpenTelemetry: set `OTEL_EXPORTER_OTLP_ENDPOINT`; traces join the MassTransit consumer's trace.

## Guarantees

- **No silent loss:** invalid messages are not sent; publisher confirms and `mandatory` turn an undeliverable message into an error.
- **Safe moves:** a message leaves the error queue only after the broker confirmed its new copy; the body never changes.
- **Nothing is deleted:** poison messages are parked with every header, and can be brought back; exchanges and queues are only ever created.
- **A person confirms:** clients with MCP elicitation (e.g. Claude Code) show a dialog before anything moves; stateless HTTP falls back to `confirm=true`.
- **Audited:** every send, move and answer — including refused ones — with the user.
- **Untrusted content:** message headers never redirect where messages go; passwords never appear in logs or errors.

## License

[MIT](LICENSE). Development, tests and releases: [CONTRIBUTING.md](CONTRIBUTING.md).
