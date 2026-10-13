# rabbit-mcp

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml)

RabbitMQ-ის MCP სერვერი, რომელიც **შეტყობინებების კონტრაქტებზეა** აგებული (.NET 10).
თითო C# record კონტრაქტი ცალკე ტიპიზებულ ხელსაწყოდ იქცევა, რომლის `inputSchema` თავად კონტრაქტია. შეტყობინებები MassTransit-ის ფორმატშია, ამიტომ MassTransit v8/v9 სერვისები მათ კონფიგურაციის გარეშე იღებენ. თავად სერვერი MassTransit-ზე დამოკიდებული არ არის, იყენებს მხოლოდ `RabbitMQ.Client`-ს.

## შესაძლებლობები

- **Event-ები:** `publish_<contract>` აქვეყნებს შეტყობინებას გაგზავნამდე ვალიდაციით.
- **Request/response:** `request_<contract>` აგზავნის მოთხოვნას და ელოდება consumer-ის პასუხს. პასუხიც მოწმდება თავის კონტრაქტზე.
- **დიაგნოსტიკა:** რიგების სია და სტატისტიკა, შეტყობინებების ნახვა ამოღების გარეშე, MassTransit-ის fault-ის დეტალებით.
- **აღდგენა:** `_error` რიგიდან შეტყობინებების უსაფრთხოდ დაბრუნება საწყის რიგში.

## სწრაფი დაწყება

```bash
docker compose up -d                                    # RabbitMQ, UI: http://localhost:15672
dotnet test tests/RabbitMcp.Tests                     # unit ტესტები
dotnet test tests/RabbitMcp.IntegrationTests          # ინტეგრაციული: RabbitMQ + MassTransit (Docker სჭირდება)
dotnet publish src/RabbitMcp.Server -c Release -o out
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/out/rabbit-mcp
```

> MCP კლიენტში დაარეგისტრირეთ `dotnet publish`-ით მიღებული ბინარი და არა `dotnet run`: `dotnet run` stdout-ზე წერს და MCP პროტოკოლს აზიანებს.

Claude Desktop-ისთვის იგივე ბინარი და env ცვლადები `claude_desktop_config.json`-ის `mcpServers`-ში ჩაწერეთ.

**მზა ბინარი:** [Releases](https://github.com/lazogio/rabbit-mcp/releases)-ში ყველა ვერსიისთვის დევს self-contained ფაილი Linux-ის (x64/arm64), Windows-ის (x64) და macOS-ის (x64/arm64) პლატფორმებისთვის, `SHA256SUMS.txt`-თან ერთად. .NET-ის დაყენება არ სჭირდება. ჩამოტვირთეთ, გახსენით და დაარეგისტრირეთ `rabbit-mcp` (Windows-ზე `rabbit-mcp.exe`). macOS-ზე ბინარი ხელმოწერილი არ არის, ამიტომ პირველ გაშვებამდე: `xattr -d com.apple.quarantine rabbit-mcp`.

## კონფიგურაცია

პარამეტრები `appsettings.json`-ში ან env ცვლადებში იწერება.

| ცვლადი | ნაგულისხმევი | აღწერა |
|---|---|---|
| `RabbitMcp__Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI, vhost ბოლოშია (`…/prod`) |
| `RabbitMcp__ClientName` | `rabbit-mcp` | კავშირის სახელი და envelope-ის `sourceAddress` |
| `RabbitMcp__EnablePublish` | `false` | ჩართავს `publish_*` და `request_*` ხელსაწყოებს |
| `RabbitMcp__EnableRepublish` | `false` | ჩართავს `republish_from_error`-ს |
| `RabbitMcp__MaxMessagesPerCall` | `50` | `count`-ის ზედა ზღვარი |
| `RabbitMcp__MaxPayloadChars` | `16000` | ამაზე დიდი body ჩვენებისას იკვეცება |
| `RabbitMcp__ContractAssemblies` | — | თქვენი კონტრაქტების DLL-ები (`;`-ით გამოყოფილი). ცარიელისას ნიმუშის კონტრაქტები გამოიყენება |
| `RabbitMcp__ManagementUri` | — | Management API, მაგ. `http://localhost:15672`. ჩართავს `list_queues`-ს |
| `RabbitMcp__ExchangeCacheSeconds` | `30` | რამდენ წამს ახსოვს, რომ exchange არსებობს (`0` = გამორთული) |
| `RabbitMcp__PublishAllow` | — | რომელი კონტრაქტების გაგზავნაა ნებადართული, მაგ. `Order*;Billing.Contracts.*`. ცარიელი = ყველა |
| `RabbitMcp__PublishDeny` | — | რომლის გაგზავნა არასდროს შეიძლება. უპირატესია `PublishAllow`-ზე |
| `RabbitMcp__AuditLogPath` | — | audit ფაილი (JSON Lines). ცარიელისას audit მხოლოდ stderr-ზე იწერება |
| `RabbitMcp__AuditIncludePayload` | `false` | audit-ში შეტყობინების შიგთავსიც ჩაიწეროს (შეიძლება პერსონალურ მონაცემებს შეიცავდეს) |

## ხელსაწყოები

| ხელსაწყო | რას აკეთებს |
|---|---|
| `list_contracts` | კონტრაქტების სია: event/request, ვერსია, URN, exchange, პასუხის ტიპები |
| `validate_message` | JSON-ს კონტრაქტზე ამოწმებს გაგზავნის გარეშე |
| `list_queues` *** | რიგების სია ფილტრებით: სახელით, მხოლოდ error რიგები, მხოლოდ არაცარიელი. error რიგთან ჩანს, რომელ რიგს ეკუთვნის |
| `queue_stats` | რიგში მზა შეტყობინებების და consumer-ების რაოდენობა |
| `peek_messages` | აჩვენებს შეტყობინებებს ამოღების გარეშე: payload, კონტრაქტის შემოწმება, fault |
| `publish_<contract>` * | აქვეყნებს event-ს |
| `request_<contract>` * | აგზავნის request-ს და აბრუნებს პასუხს |
| `republish_from_error` ** | აბრუნებს შეტყობინებებს `_error`/`_skipped` რიგიდან: ჯერ preview, შემდეგ `confirm=true` |

\* `EnablePublish` · \** `EnableRepublish` · \*** `ManagementUri`. ყველა ხელსაწყო აბრუნებს `structuredContent`-ს და აცხადებს `outputSchema`-ს. შეცდომები `isError: true`-ით და გასაგები ტექსტით ბრუნდება.

## კონტროლი

**რისი გაგზავნა შეიძლება.** `PublishAllow`/`PublishDeny` ნიმუშები (`*` ნებისმიერი სიმბოლოა) ედარება კონტრაქტის სახელს, ხელსაწყოს სახელს, სრულ ტიპს ან URN-ს. რეგისტრი მნიშვნელობას არ აქვს. აკრძალული კონტრაქტის `publish_`/`request_` ხელსაწყო საერთოდ არ რეგისტრირდება, ხოლო სერვერი მას პირდაპირი გამოძახებისასაც უარყოფს. `list_contracts` თითოეულთან აჩვენებს `sendable`-ს. თუ ნიმუში არცერთ კონტრაქტს არ ემთხვევა, გაშვებისას გაფრთხილება ჩნდება. მაგალითად, production-ზე: `RabbitMcp__PublishAllow=Get*` (მხოლოდ შეკითხვები).

**Audit.** ყოველი publish, request და republish იწერება შედეგთან ერთად: `sent`/`error`, `responded`/`faulted`/`timeout`, `republished`/`failed`/`skipped`. ჩანაწერში არის messageId/requestId, კონტრაქტი, exchange/რიგი და MCP კლიენტის სახელი და ვერსია. უარყოფილი მცდელობებიც იწერება. republish-ისას თითოეული შეტყობინება ცალკე იწერება, მაშინაც კი, თუ შუაში შეცდომა მოხდა. `AuditLogPath`-ის მითითებისას სერვერი ფაილის ჩაწერადობას გაშვებისას ამოწმებს და, თუ ჩაწერა შეუძლებელია, არ ეშვება. credentials audit-ში არასდროს ხვდება.

```json
{"timestamp":"2026-10-13T12:00:00+00:00","action":"publish","outcome":"sent","client":"claude-code 2.1.0","contract":"OrderSubmitted","exchange":"Shop.Contracts:OrderSubmitted","messageId":"…"}
```

## OpenTelemetry

ჩაირთვება სტანდარტული ცვლადით `OTEL_EXPORTER_OTLP_ENDPOINT` (მაგ. `http://localhost:4317`). სერვისის სახელი `OTEL_SERVICE_NAME`-ით იცვლება, ნაგულისხმევად `rabbit-mcp`. სხვა `OTEL_*` ცვლადებიც ჩვეულებრივ მუშაობს. ექსპორტი მხოლოდ OTLP-ით ხდება და stdout-ს არ ეხება.

- **Trace:** სერვერი ქმნის span-ებს `publish {exchange}`, `request {exchange}` (შედეგით) და `republish {exchange}`. ისინი MCP SDK-ის tool-call span-ის შვილებია. ყოველ შეტყობინებას თან ახლავს `traceparent` და `MT-Activity-Id`. MassTransit 8 სწორედ ამ ორს კითხულობს, ამიტომ consumer-ის span იმავე trace-ში ჩნდება. ეს ინტეგრაციულ ტესტში დადასტურებულია.
- **Republish:** ახალ trace-ს იწყებს, რომელიც თავდაპირველ გაგზავნას **link**-ით უკავშირდება. ამიტომ განმეორებითი დამუშავება ცალკე ჩანს, თავდაპირველი შეცდომა კი ერთი დაწკაპუნებით ხელმისაწვდომია.
- **მეტრიკები:** `rabbitmcp.operations` (action/outcome/contract) და `rabbitmcp.request.duration`. მრიცხველი audit-ის ყოველი ჩანაწერიდან იზრდება, ამიტომ audit და მეტრიკები ერთმანეთს ყოველთვის ემთხვევა.

## კონტრაქტები

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

- **`required` / nullability:** ველი სავალდებულოა, `null` უარიყოფა.
- **`[Description]`:** ველის ახსნა, რომელსაც მოდელი კითხულობს.
- **DataAnnotations** (`[Range]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]`, `[MinLength]`): ჩანს სქემაში და სერვერიც ამოწმებს, ჩადგმულ ობიექტებსა და სიებშიც.
- **სახელები:** URN `urn:message:Namespace:Type`, exchange `Namespace:Type`. სხვა namespace-ის კონტრაქტზე მისაბმელად გამოიყენეთ `Urn`/`Exchange`.
- **საკუთარი კონტრაქტები სერვერის აწყობის გარეშე:** შექმენით classlib, რომელიც `RabbitMcp.Contracts`-ს (nupkg Release-შია) იყენებს. ააგეთ და მიუთითეთ DLL: `RabbitMcp__ContractAssemblies=/path/Billing.Contracts.dll`. მისი დამოკიდებულებები DLL-ის გვერდით უნდა იდოს. ფარდობითი გზა სერვერის საქაღალდიდან ითვლება. არასწორი გზის შემთხვევაში სერვერი stderr-ზე ერთ გასაგებ სტრიქონს წერს და არ ეშვება. ასეთ შემთხვევაში ნიმუშის კონტრაქტები არ ერევა.

## Request/response

MassTransit consumer ჩვეულებრივ პასუხობს: `await context.RespondAsync(new OrderStatus { ... })`.

1. სერვერი ერთ დროებით reply queue-ს ქმნის. მისი დროშები ემთხვევა MassTransit-ის `?temporary=true` მისამართისას.
2. request-ს თან ახლავს `requestId`, `responseAddress` და TTL, რომელიც timeout-ის ტოლია (1–120 წამი, ნაგულისხმევად 30).
3. პასუხი `requestId`-ით უკავშირდება request-ს. შედეგში ჩანს:
   - `status`: `responded`, `faulted` ან `timeout`;
   - `responseType` და `response`;
   - `responseCheck`: აკმაყოფილებს თუ არა პასუხი თავის კონტრაქტს;
   - `respondedBy`: endpoint, მანქანა, პროცესი.
4. consumer-ის exception MassTransit-ის `Fault<T>`-ის სახით მოდის: ტიპი, შეტყობინება, stack trace.

**Responder MassTransit-ის გარეშე** პასუხს აქვეყნებს exchange-ზე, რომლის სახელიც `responseAddress`-ის ბოლო სეგმენტია, routing key `""`-ით. envelope-ში უნდა იყოს იგივე `requestId`, `messageType: ["urn:message:Ns:Type"]` და `message`, content type კი `application/vnd.masstransit+json`.

## გარანტიები

- **publish/request:** ვალიდაციის ჩავარდნისას არაფერი იგზავნება. exchange-ის არსებობა წინასწარ მოწმდება. publisher confirms და `mandatory` უზრუნველყოფს, რომ ვერ მიწოდებული შეტყობინება შეცდომად დაბრუნდეს და ჩუმად არ დაიკარგოს. შეტყობინებები persistent-ია.
- **republish:** შეტყობინება error რიგიდან ack-დება მხოლოდ ბროკერის დადასტურების შემდეგ, წარუმატებელი კი რიგში რჩება. body უცვლელია, იშლება მხოლოდ `MT-Reason`, `MT-Fault-*` და `MT-Host-*`. `messageIds`-ით მხოლოდ preview-ში ნანახ შეტყობინებებს აბრუნებთ. მიწოდება at-least-once-ია, ამიტომ consumer-ები `messageId`-ით იდემპოტენტური უნდა იყვნენ.
- **peek:** აქტიური consumer-ის მქონე რიგზე უარს ამბობს, თუ `force=true` არ არის. შეტყობინება რიგში რჩება, მაგრამ იღებს `redelivered` დროშას და შეიძლება თანმიმდევრობა შეეცვალოს. quorum რიგებზე თითოეული peek delivery-limit-ში ითვლება.

## Claude Code აგენტები

`.claude/agents/`-ში პროექტისთვის მორგებული აგენტებია. Claude მათ ავტომატურად იყენებს შესაბამის ამოცანაზე, ან შეგიძლიათ სახელით გამოიძახოთ (`@code-reviewer`). საერთო წესები `CLAUDE.md`-შია.

| აგენტი | რისთვის |
|---|---|
| `contract-author` | ახალი ან შეცვლილი კონტრაქტი, ვერსიები, breaking ცვლილებები |
| `mcp-tool-designer` | ახალი MCP ხელსაწყოს დიზაინი: სახელი, სქემა, ანოტაციები, gating |
| `masstransit-compat-reviewer` | wire ფორმატის შემოწმება MassTransit-ის საწყის კოდზე (მხოლოდ კითხვა) |
| `code-reviewer` | diff-ის review merge-მდე (მხოლოდ კითხვა) |
| `test-engineer` | unit და Testcontainers ინტეგრაციული ტესტები |
| `ci-doctor` | წითელი CI-ის დიაგნოსტიკა და გასწორება |
| `docs-writer` | README-ის და CLAUDE.md-ის სინქრონიზაცია კოდთან |
| `project-memory` | ცვლილებების, ბაგების და გადაწყვეტილებების ცოდნის ბაზა (`.claude/agent-memory/project-memory/`) |

## სტრუქტურა

```
src/RabbitMcp.Contracts    კონტრაქტები (დამოკიდებულებების გარეშე, სერვისებშიც გამოიყენება)
src/RabbitMcp.Server       Contracts/ რეესტრი, ვალიდაცია · Messaging/ AMQP, envelope, reply · Tools/ MCP ხელსაწყოები
tests/RabbitMcp.Tests                  unit ტესტები (RabbitMQ არ სჭირდება)
tests/RabbitMcp.IntegrationTests       ნამდვილი RabbitMQ 4 + MassTransit 8 (Testcontainers)
tests/RabbitMcp.ExternalContracts      „გარე“ კონტრაქტების ნიმუში, runtime-ში იტვირთება
```

**CI:** ყოველ push-სა და PR-ზე GitHub Actions აგებს პროექტს, უშვებს unit ტესტებს, ინტეგრაციულ ტესტებს ნამდვილ RabbitMQ 4-სა და MassTransit 8-ზე და smoke test-ს (`scripts/smoke-test.sh`), რომელიც გამოქვეყნებულ ბინარს stdio-ზე MCP კლიენტივით ესაუბრება. ჩავარდნილი ტესტი PR-ში annotation-ად ჩანს.

**Release:** `git tag v0.2.0 && git push origin v0.2.0` ააგებს ბინარებს ხუთივე პლატფორმისთვის და შექმნის GitHub Release-ს. ვერსია tag-იდან მოდის და MCP კლიენტს `serverInfo.version`-ში უჩანს. `-` შემცველი tag (`v0.2.0-rc.1`) prerelease-ად ქვეყნდება. **Dependabot** NuGet-ისა და Actions-ის განახლებებს ყოველ ორშაბათს გვთავაზობს. MassTransit 9+ გამოთიშულია, რადგან ის კომერციულია.

**შემდეგი ნაბიჯები:** იხ. [ROADMAP.md](ROADMAP.md).
