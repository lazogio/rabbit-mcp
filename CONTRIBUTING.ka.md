# პროექტზე მუშაობა

[English](CONTRIBUTING.md) · **ქართული**

მომხმარებლის დოკუმენტაცია [README.ka.md](README.ka.md)-შია. აქ არის ის, რაც კოდზე მომუშავეს სჭირდება.

## აწყობა და ტესტები

```bash
dotnet build RabbitMcp.slnx
dotnet test tests/RabbitMcp.Tests                  # unit, RabbitMQ არ სჭირდება
dotnet test tests/RabbitMcp.IntegrationTests       # ნამდვილი RabbitMQ 4 + MassTransit 8 (Testcontainers, Docker სჭირდება)
dotnet publish src/RabbitMcp.Server -c Release -o out
scripts/smoke-test.sh out/rabbit-mcp               # MCP stdio სესია გამოქვეყნებულ ბინართან
scripts/smoke-test-http.sh out/rabbit-mcp          # HTTP + API key
docker build -t rabbit-mcp . && scripts/smoke-test-http.sh docker:rabbit-mcp
```

## სტრუქტურა

```
src/RabbitMcp.Contracts              მხოლოდ [MessageContract] attribute (NuGet პაკეტი, სერვისები იყენებენ)
src/RabbitMcp.Samples                ჩაშენებული ნიმუშის კონტრაქტები, მხოლოდ სერვერთან ერთად
src/RabbitMcp.Server/Contracts       რეესტრი, ვალიდაცია, DLL-ების ჩატვირთვა, contracts diff
src/RabbitMcp.Server/Messaging       AMQP, MassTransit envelope, peek/republish/dead-letter, request/response, responder, audit
src/RabbitMcp.Server/Security        როლები და ხელსაწყოების უფლებები
src/RabbitMcp.Server/Hosting         stdio და HTTP ჰოსტი, ავტორიზაცია
src/RabbitMcp.Server/Tools           MCP ხელსაწყოები, prompts, resources და მათი შედეგები (outputSchema)
tests/RabbitMcp.Tests                unit ტესტები
tests/RabbitMcp.IntegrationTests     ნამდვილი RabbitMQ + MassTransit
tests/RabbitMcp.ExternalContracts    „გარე“ კონტრაქტების DLL-ის ნიმუში
samples/Shop                         სრული ნიმუში: კონტრაქტები, MassTransit სერვისი, docker compose
```

წესები, რომლებიც არ უნდა დაირღვეს (MassTransit-თან თავსებადობა, შეტყობინების დაუკარგაობა, არაფრის წაშლა, audit, საიდუმლოები, უფლებები), [CLAUDE.md](CLAUDE.md)-შია.

## დოკუმენტაცია

მომხმარებლისთვის განკუთვნილი ყველა დოკუმენტი ორ ენაზეა: ინგლისურად (`README.md`, `CONTRIBUTING.md`, `samples/Shop/README.md`) და ქართულად (`*.ka.md`). ორივე ერთსა და იმავე pull request-ში შეცვალეთ.

## CI

ყოველ push-სა და PR-ზე CI:
- აგებს პროექტს და ამოწმებს მოწყვლად NuGet პაკეტებს;
- უშვებს unit და ინტეგრაციულ ტესტებს;
- უშვებს smoke test-ებს: stdio (ხელსაწყოები, prompts, resources), HTTP, Docker image, responder, გარე კონტრაქტები, `contracts diff`;
- უშვებს Shop ნიმუშს docker compose-ით და MCP-ით ბოლომდე გადის (`scripts/sample-e2e.py`).

ჩავარდნილი ტესტი PR-ში annotation-ად ჩანს.

## რელიზი

```bash
git tag v0.2.0 && git push origin v0.2.0
```

tag-ის ატვირთვა აგებს ბინარებს 5 პლატფორმისთვის, multi-arch Docker image-ს (`ghcr.io/lazogio/rabbit-mcp`), `RabbitMcp.Contracts` nupkg-ს და GitHub Release-ს.
- **ვერსია** tag-იდან მოდის და `serverInfo.version`-ში ჩანს.
- **`-` შემცველი tag** (`v0.2.0-rc.1`) prerelease-ად ქვეყნდება.
- **nuget.org:** `NUGET_API_KEY` secret-ის არსებობისას პაკეტი nuget.org-ზეც იტვირთება.
- **მიწოდების ჯაჭვი:** არქივებს და nupkg-ს აქვს GitHub build provenance attestation (`gh attestation verify`). რელიზს თან ერთვის CycloneDX SBOM. Docker image-ს registry-ში SBOM და SLSA provenance მიჰყვება.
- **base image-ები** digest-ით არის ფიქსირებული. Dependabot ახალ digest-ს ყოველ ორშაბათს გვთავაზობს.

CI runner-ები `ubuntu-24.04`-ზეა ფიქსირებული, რომ GitHub-ის `ubuntu-latest`-ის ცვლილებამ build მოულოდნელად არ შეცვალოს. Ubuntu 26.04 გადამოწმებულია (2026-10-18).

Dependabot NuGet-ის, Actions-ისა და Docker-ის განახლებებს ყოველ ორშაბათს გვთავაზობს. MassTransit 9+ გამორიცხულია, რადგან ის კომერციულია და მხოლოდ ტესტებში გამოიყენება.

## Claude Code

`.claude/agents/`-ში პროექტის აგენტებია: `contract-author`, `mcp-tool-designer`, `masstransit-compat-reviewer`, `code-reviewer`, `test-engineer`, `ci-doctor`, `docs-writer`, `project-memory`. ცვლილებების, ბაგების და გადაწყვეტილებების ისტორია `.claude/agent-memory/project-memory/`-შია.

## ლიცენზია

MIT. ყოველი წყაროს ფაილი იწყება header-ით:

```csharp
// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT
```

IDE მას ახალ ფაილს ავტომატურად უმატებს (`.editorconfig`), build კი header-ის გარეშე ფაილზე გაფრთხილებას აჩვენებს (IDE0073).
