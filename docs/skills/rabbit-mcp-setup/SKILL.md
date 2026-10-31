---
name: rabbit-mcp-setup
description: Sets up the rabbit-mcp MCP server for RabbitMQ in Claude Code, Claude Desktop or a shared HTTP deployment — install, broker URI, contracts, which tools to enable, authentication and roles, and a first check. Use when someone wants to connect Claude to RabbitMQ / MassTransit with rabbit-mcp, or rabbit-mcp does not start or shows no tools.
---

# Set up rabbit-mcp

rabbit-mcp turns `[MessageContract]` records (or AsyncAPI files) into typed MCP tools for RabbitMQ, using the
MassTransit wire format. Reference: https://github.com/lazogio/rabbit-mcp (README "Install", "For teams (HTTP)" and
"Configuration"). Follow the steps in order, ask only what you cannot find out yourself, and answer in the user's
language.

## 1. Find out the situation

Check before asking: `claude --version`, `dotnet --version` (10.x?), `docker --version`, and whether a broker
answers (`docker ps`, `nc -z localhost 5672`). Then settle, in one short round of questions:

- **Who uses it:** one person on this machine → **stdio** (the MCP client starts the binary). A team or CI → **HTTP**
  (one shared server, authentication required).
- **Client:** Claude Code, Claude Desktop, or another MCP client.
- **Broker:** host, port, vhost and a RabbitMQ user. No broker yet → `docker compose up -d` in a clone of the
  repository gives RabbitMQ 4 on 5672 (UI on 15672, guest/guest), or try `samples/Shop` for a complete demo.
- **Contracts:** a contracts DLL (class library referencing the `RabbitMcp.Contracts` package), AsyncAPI files, or
  none yet (the built-in sample contracts load then — fine for a first look).

## 2. Install

Pick the first that fits:

| Situation | Install | Command for the client |
|---|---|---|
| .NET 10 SDK present | nothing (runs from NuGet) | `dnx` with args `RabbitMcp.Server --yes` |
| .NET 10 SDK, used often | `dotnet tool install -g RabbitMcp.Server` | `rabbit-mcp` |
| No .NET | download from GitHub Releases (Linux, Windows, macOS; macOS: `xattr -d com.apple.quarantine rabbit-mcp`) | full path to the binary |
| Team / server | Docker image `ghcr.io/lazogio/rabbit-mcp` (HTTP) | the HTTP URL, see step 4 |
| From source | `dotnet publish src/RabbitMcp.Server -c Release -o out` | `out/rabbit-mcp` |

Never configure `dotnet run`: it writes build output to stdout, which is the MCP channel, and the client sees no tools.

## 3. Configure (stdio)

Every option is an environment variable `RabbitMcp__<Option>`. Start **read-only** and enable more only when asked:

| Want | Set |
|---|---|
| Connect (always) | `RabbitMcp__Uri=amqp://USER:PASS@HOST:5672/VHOST` (`amqps://` for TLS) |
| Your contracts | `RabbitMcp__ContractAssemblies=/abs/path/Billing.Contracts.dll` and/or `RabbitMcp__ContractFiles=/abs/path/api.yaml` (`;`-separated) |
| `list_queues`, `contract_consumers` | `RabbitMcp__ManagementUri=http://HOST:15672` |
| Send events / requests | `RabbitMcp__EnablePublish=true`, optionally `PublishAllow` / `PublishDeny` |
| Recover `_error` queues | `RabbitMcp__EnableRepublish=true` |
| Create exchanges / queues | `RabbitMcp__EnableTopology=true` with `TopologyAllow` (name patterns) |
| Answer the services' requests | `RabbitMcp__RespondTo=ContractName` |
| Audit trail / `recent_activity` | `RabbitMcp__AuditLogPath=/abs/path/audit.jsonl` |

Use absolute paths. Mention that changes ask the person for confirmation in Claude Code (elicitation) and that
nothing is ever deleted.

**Claude Code** (scope `local` keeps the password out of the repository; never put it in a committed `.mcp.json`):

```bash
claude mcp add rabbit-mcp -s local \
  -e RabbitMcp__Uri=amqp://USER:PASS@HOST:5672/ \
  -e RabbitMcp__ContractAssemblies=/abs/path/Billing.Contracts.dll \
  -- dnx RabbitMcp.Server --yes
```

**Claude Desktop:** add to `claude_desktop_config.json` (macOS `~/Library/Application Support/Claude/`, Windows
`%APPDATA%\Claude\`), then restart the app:

```json
{
  "mcpServers": {
    "rabbit-mcp": {
      "command": "dnx",
      "args": ["RabbitMcp.Server", "--yes"],
      "env": { "RabbitMcp__Uri": "amqp://USER:PASS@HOST:5672/" }
    }
  }
}
```

With a downloaded binary or `dotnet tool`, `command` is the binary path / `rabbit-mcp` and `args` is empty.

## 4. Configure (HTTP, for teams)

The server refuses to start without authentication. Use API keys (at least 32 characters) or OIDC:

```bash
docker run -d -p 5080:5080 \
  -e RabbitMcp__Uri=amqp://USER:PASS@rabbit:5672/ \
  -e RabbitMcp__ApiKeys="team:$(openssl rand -base64 32)" \
  -e RabbitMcp__ContractAssemblies=/contracts/Billing.Contracts.dll -v ./contracts:/contracts:ro \
  ghcr.io/lazogio/rabbit-mcp:1.0
```

- OIDC instead of keys: `RabbitMcp__JwtAuthority` (must be https) + `RabbitMcp__JwtAudience`.
- Roles (HTTP only): `RabbitMcp__Roles__viewer=read`, `RabbitMcp__Roles__operator="read;publish:Order*;republish:*_error"`,
  `RabbitMcp__UserRoles="alice=operator"`, `RabbitMcp__DefaultRoles=viewer`. Permissions: read, publish, request,
  republish, respond, topology, `*`.
- Health: `curl http://HOST:5080/healthz` → `ok`.
- Client: `claude mcp add --transport http rabbit-mcp http://HOST:5080/mcp --header "X-Api-Key: KEY"`.

Keep the key in a secret store or an environment variable; never paste it into a committed file.

## 5. Check it works

Start a new session (the client loads MCP servers at start), then:

1. `claude mcp list` (Claude Code) shows rabbit-mcp as connected.
2. Call `list_contracts`: the user's contracts (or the samples) are listed, with `sendable` matching the options.
3. Call `queue_stats` for a queue the user knows, and `contract_consumers` if `ManagementUri` is set: every
   contract should have a consumer (`ok`).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Client shows no tools or "connection closed" | started with `dotnet run`, or a wrong binary path; use the published binary / `dnx` |
| `rabbit-mcp: …` one line, exit code 1 | a configuration error; the line names the option (e.g. an unknown permission, `ApiKeys` too short) |
| `Cannot connect to RabbitMQ at host:5672/vhost` | broker down, wrong host/port/vhost or credentials; the password is never shown |
| `403 ACCESS_REFUSED` | the RabbitMQ user lacks configure/write/read permission on that vhost or entity |
| `publish_*` / `request_*` missing | `EnablePublish` not set, or `PublishDeny` / roles hide them |
| `list_queues` missing | `ManagementUri` not set |
| HTTP 401 | missing or wrong `X-Api-Key` / token |
| macOS: "cannot be opened" | `xattr -d com.apple.quarantine rabbit-mcp` |

Never print the AMQP URI with its password back to the user; describe it as `host:port/vhost`.
