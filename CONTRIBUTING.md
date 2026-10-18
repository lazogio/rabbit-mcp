# Working on rabbit-mcp

**English** · [ქართული](CONTRIBUTING.ka.md)

User documentation is in [README.md](README.md). This page is for people working on the code.

## Build and test

```bash
dotnet build RabbitMcp.slnx
dotnet test tests/RabbitMcp.Tests                  # unit, no RabbitMQ needed
dotnet test tests/RabbitMcp.IntegrationTests       # real RabbitMQ 4 + MassTransit 8 (Testcontainers, needs Docker)
dotnet publish src/RabbitMcp.Server -c Release -o out
scripts/smoke-test.sh out/rabbit-mcp               # MCP stdio session with the published binary
scripts/smoke-test-http.sh out/rabbit-mcp          # HTTP + API key
docker build -t rabbit-mcp . && scripts/smoke-test-http.sh docker:rabbit-mcp
```

## Layout

```
src/RabbitMcp.Contracts              the [MessageContract] attribute and MessageFormat only (the NuGet package services reference)
src/RabbitMcp.Samples                built-in sample contracts, shipped with the server only
src/RabbitMcp.Server/Contracts       registry, validation, loading contract DLLs, contracts diff
src/RabbitMcp.Server/Messaging       AMQP, MassTransit envelope, raw JSON, peek/republish/dead-letter, request/response, responder, audit
src/RabbitMcp.Server/Security        roles and tool permissions
src/RabbitMcp.Server/Hosting         stdio and HTTP hosts, authentication
src/RabbitMcp.Server/Tools           MCP tools, prompts, resources and their results (outputSchema)
tests/RabbitMcp.Tests                unit tests
tests/RabbitMcp.IntegrationTests     real RabbitMQ + MassTransit (and plain RabbitMQ.Client services for raw contracts)
tests/RabbitMcp.ExternalContracts    a user-style contracts DLL
samples/Shop                         full sample: contracts, MassTransit service + plain RabbitMQ shipping desk, docker compose
```

The rules that must not be broken (MassTransit compatibility, no message loss, nothing deleted, audit, secrets,
permissions) are in [CLAUDE.md](CLAUDE.md).

## Documentation

Every user-facing document exists in English (`README.md`, `CONTRIBUTING.md`, `samples/Shop/README.md`) and in
Georgian (`*.ka.md`). Change both in the same pull request.

## CI

On every push and pull request CI:
- builds the solution and checks for vulnerable NuGet packages;
- runs the unit and integration tests;
- runs smoke tests: stdio (tools, prompts, resources), HTTP, the Docker image, responder mode, external contracts, `contracts diff`;
- starts the Shop sample with docker compose and walks through it over MCP (`scripts/sample-e2e.py`).

A failing test shows up as an annotation on the pull request.

## Releases

```bash
git tag v0.2.0 && git push origin v0.2.0
```

A tag builds binaries for 5 platforms, the multi-arch Docker image (`ghcr.io/lazogio/rabbit-mcp`), the
`RabbitMcp.Contracts` nupkg and a GitHub Release.
- **Version** comes from the tag and is shown in `serverInfo.version`.
- **Tags with `-`** (`v0.2.0-rc.1`) are published as prereleases.
- **nuget.org:** the package is pushed there too when the `NUGET_API_KEY` secret exists.
- **Supply chain:** archives and the nupkg get GitHub build provenance attestations (`gh attestation verify`), the release
  includes a CycloneDX SBOM, and the Docker image carries an SBOM and SLSA provenance in the registry.
- **Base images** are pinned by digest; Dependabot proposes new digests every Monday.

CI runners are pinned to `ubuntu-24.04`, so a change of GitHub's `ubuntu-latest` cannot alter the build unannounced
(Ubuntu 26.04 verified on 2026-10-18).

Dependabot proposes NuGet, Actions and Docker updates every Monday. MassTransit 9+ is excluded: it is commercial and
only used in tests.

## Claude Code

`.claude/agents/` holds the project's agents: `contract-author`, `mcp-tool-designer`, `masstransit-compat-reviewer`,
`code-reviewer`, `test-engineer`, `ci-doctor`, `docs-writer`, `project-memory`. The history of changes, bugs and
decisions is in `.claude/agent-memory/project-memory/`.

## License

MIT. Every source file starts with the header:

```csharp
// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT
```

IDEs add it to new files automatically (`.editorconfig`), and the build warns about files without it (IDE0073).
