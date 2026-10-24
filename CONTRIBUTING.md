# Working on rabbit-mcp

**English** · [ქართული](CONTRIBUTING.ka.md)

For people working on the code; users read [README.md](README.md). The rules that must not be broken (MassTransit
compatibility, no message loss, nothing deleted, audit, secrets, permissions) are in [CLAUDE.md](CLAUDE.md).

## Build and test

```bash
dotnet build RabbitMcp.slnx
dotnet test tests/RabbitMcp.Tests                  # unit, no RabbitMQ needed
dotnet test tests/RabbitMcp.IntegrationTests       # real RabbitMQ 4 + MassTransit 8 (Testcontainers, needs Docker)
dotnet publish src/RabbitMcp.Server -c Release -o out
scripts/smoke-test.sh out/rabbit-mcp               # MCP over stdio
scripts/smoke-test-http.sh out/rabbit-mcp          # MCP over HTTP + API key
```

## Layout

```
src/RabbitMcp.Contracts          [MessageContract] and MessageFormat only (the NuGet package services reference)
src/RabbitMcp.Samples            built-in sample contracts
src/RabbitMcp.Server/Contracts   registry, validation, contract DLLs and AsyncAPI, contracts diff
src/RabbitMcp.Server/Messaging   AMQP, MassTransit envelope, raw JSON, peek/republish/dead-letter, request/response, audit
src/RabbitMcp.Server/Security    roles, human confirmation, rate limits
src/RabbitMcp.Server/Hosting     stdio and HTTP hosts, authentication
src/RabbitMcp.Server/Tools       MCP tools, prompts, resources
tests/                           unit, integration, a user-style contracts DLL
samples/Shop                     runnable sample, walked through by CI (scripts/sample-e2e.py)
```

## Rules

- Documentation is bilingual: change `*.md` (English) and `*.ka.md` (Georgian) in the same pull request. Keep the
  README short.
- Every source file starts with `// Copyright (c) 2026 lazogio` and `// SPDX-License-Identifier: MIT` (IDE0073 warns).
- CI (every push and pull request) builds, checks for vulnerable packages, runs unit and integration tests and smoke
  tests, and walks through the Shop sample over MCP. Failures show up as annotations.
- Dependabot proposes updates every Monday; MassTransit stays below 9 (commercial, tests only).

## Releases

```bash
git tag v0.2.0 && git push origin v0.2.0
```

A tag builds binaries for 5 platforms, the Docker image `ghcr.io/lazogio/rabbit-mcp`, the `RabbitMcp.Contracts` and
`RabbitMcp.Server` (.NET tool) packages and a GitHub Release with attestations and an SBOM. Tags with `-` are
prereleases. Packages go to nuget.org when the `NUGET_API_KEY` secret exists; then the `mcp-registry` job lists the
server in the MCP registry (`src/RabbitMcp.Server/.mcp/server.json`, GitHub OIDC, no secret). The registry checks that
the GHCR image is public: before the first release switch the package to public, or re-run that job afterwards.

## Claude Code

Project agents are in `.claude/agents/` (`consumer-author` and `producer-author` add consumers and producers to a
service, following the Shop sample); the history of changes, bugs and decisions is in
`.claude/agent-memory/project-memory/`.
