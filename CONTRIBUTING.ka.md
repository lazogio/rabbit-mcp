# პროექტზე მუშაობა

[English](CONTRIBUTING.md) · **ქართული**

კოდზე მომუშავეთათვის; მომხმარებლის დოკუმენტაცია [README.ka.md](README.ka.md)-შია. წესები, რომლებიც არ უნდა დაირღვეს
(MassTransit-თან თავსებადობა, შეტყობინების დაუკარგაობა, არაფრის წაშლა, audit, საიდუმლოები, უფლებები),
[CLAUDE.md](CLAUDE.md)-შია.

## აწყობა და ტესტები

```bash
dotnet build RabbitMcp.slnx
dotnet test tests/RabbitMcp.Tests                  # unit, RabbitMQ არ სჭირდება
dotnet test tests/RabbitMcp.IntegrationTests       # ნამდვილი RabbitMQ 4 + MassTransit 8 (Testcontainers, Docker სჭირდება)
dotnet publish src/RabbitMcp.Server -c Release -o out
scripts/smoke-test.sh out/rabbit-mcp               # MCP stdio-ზე
scripts/smoke-test-http.sh out/rabbit-mcp          # MCP HTTP-ზე + API key
```

## სტრუქტურა

```
src/RabbitMcp.Contracts          მხოლოდ [MessageContract] და MessageFormat (NuGet პაკეტი, სერვისები იყენებენ)
src/RabbitMcp.Samples            ჩაშენებული ნიმუშის კონტრაქტები
src/RabbitMcp.Server/Contracts   რეესტრი, ვალიდაცია, კონტრაქტების DLL-ები და AsyncAPI, contracts diff
src/RabbitMcp.Server/Messaging   AMQP, MassTransit envelope, raw JSON, peek/republish/dead-letter, request/response, audit
src/RabbitMcp.Server/Security    როლები, ადამიანის დასტური, ლიმიტები
src/RabbitMcp.Server/Hosting     stdio და HTTP ჰოსტი, ავთენტიფიკაცია
src/RabbitMcp.Server/Tools       MCP ხელსაწყოები, prompts, resources
tests/                           unit, integration, მომხმარებლის სტილის კონტრაქტების DLL
samples/Shop                     გასაშვები ნიმუში, CI მას გადის (scripts/sample-e2e.py)
```

კომპონენტების, request/response გზისა და შეცდომის აღდგენის დიაგრამები: [docs/architecture.ka.md](docs/architecture.ka.md).

## წესები

- დოკუმენტაცია ორენოვანია: `*.md` (ინგლისური) და `*.ka.md` (ქართული) ერთსა და იმავე pull request-ში შეცვალეთ.
  README მოკლე უნდა დარჩეს.
- ყოველი ფაილი იწყება `// Copyright (c) 2026 lazogio` და `// SPDX-License-Identifier: MIT` სტრიქონებით (IDE0073 აფრთხილებს).
- CI (ყოველ push-სა და pull request-ზე) აწყობს, ამოწმებს მოწყვლად პაკეტებს, უშვებს unit, integration და smoke
  ტესტებს და Shop-ის ნიმუშს MCP-ით გადის. შეცდომები annotation-ებად ჩანს.
- Dependabot განახლებებს ყოველ ორშაბათს გვთავაზობს; MassTransit 9-ზე დაბლა რჩება (კომერციულია, მხოლოდ ტესტებში).

## რელიზები

**Actions → Release → Run workflow** (branch `main`), აირჩიე `patch`, `minor` ან `major`: workflow ბოლო `vX.Y.Z`
ტეგს იღებს, შემდეგს ქმნის და აქვეყნებს. ხელით ტეგიც მუშაობს, ოღონდ მხოლოდ `v1.2.3` (ან `v1.2.3-rc.1`) ფორმით;
`V1.2.3` ან `v.1.2.3` არაფერს უშვებს.

```bash
git tag v1.1.0 && git push origin v1.1.0
```

ტეგი აწყობს ბინარებს 5 პლატფორმისთვის, Docker image-ს `ghcr.io/lazogio/rabbit-mcp`, `RabbitMcp.Contracts` და
`RabbitMcp.Server` (.NET tool) პაკეტებს და GitHub Release-ს attestation-ებით და SBOM-ით. `-`-იანი ტეგები prerelease-ია.
პაკეტები nuget.org-ზე Trusted Publishing-ით იტვირთება (nuget.org-ის policy `release.yml`-ისთვის, API key-ის გარეშე); შემდეგ `mcp-registry` job სერვერს MCP
registry-ში ამატებს (`src/RabbitMcp.Server/.mcp/server.json`, GitHub OIDC, secret არ სჭირდება). Registry ამოწმებს, რომ
GHCR image საჯაროა: პირველ რელიზამდე პაკეტი public გახადე, ან შემდეგ ეს job თავიდან გაუშვი.

## Claude Code

პროექტის აგენტები `.claude/agents/`-შია (`consumer-author` და `producer-author` სერვისს consumer-ებს და
producer-ებს Shop-ის ნიმუშის მიხედვით ამატებს); ცვლილებების, ბაგებისა და გადაწყვეტილებების ისტორია —
`.claude/agent-memory/project-memory/`-ში.
