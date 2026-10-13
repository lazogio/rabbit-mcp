# rabbit-mcp

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

RabbitMQ-ის MCP სერვერი, რომელიც **შეტყობინებების კონტრაქტებზეა** აგებული (.NET 10). თითო C# record კონტრაქტი ცალკე ტიპიზებულ ხელსაწყოდ იქცევა, რომლის `inputSchema` თავად კონტრაქტია. შეტყობინებები MassTransit-ის ფორმატშია, ამიტომ MassTransit-ის სერვისები მათ კონფიგურაციის გარეშე იღებენ. თავად სერვერი MassTransit-ზე დამოკიდებული არ არის.

## რას აკეთებს

- **აგზავნის:** `publish_<contract>` აქვეყნებს event-ს, `request_<contract>` აგზავნის request-ს და აბრუნებს პასუხს. ორივე შემთხვევაში შეტყობინება გაგზავნამდე კონტრაქტზე მოწმდება.
- **ხედავს:** აჩვენებს რიგებს, მათ სტატისტიკას და შეტყობინებებს ამოღების გარეშე, MassTransit-ის fault-ის დეტალებით. პოულობს, ვინ იღებს თითო კონტრაქტს.
- **აღადგენს:** `_error` რიგიდან შეტყობინებებს უსაფრთხოდ აბრუნებს საწყის რიგში.
- **პასუხობს:** responder რეჟიმში სერვისების request-ებს მოდელი პასუხობს.
- **იცავს:** `contracts diff` CI-ში აჩერებს wire-ის დამრღვევ ცვლილებებს, როლები კი ზღუდავს, ვის რა შეუძლია.

## დაყენება

**მზა ბინარი.** [Releases](https://github.com/lazogio/rabbit-mcp/releases)-ში დევს self-contained ფაილი Linux-ის (x64/arm64), Windows-ის (x64) და macOS-ის (x64/arm64) პლატფორმებისთვის. .NET-ის დაყენება არ სჭირდება. macOS-ზე პირველ გაშვებამდე: `xattr -d com.apple.quarantine rabbit-mcp`.

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

**წყაროდან:** `dotnet publish src/RabbitMcp.Server -c Release -o out` და დაარეგისტრირეთ `out/rabbit-mcp`. `dotnet run` არ გამოიყენოთ: ის stdout-ზე წერს და MCP-ს აზიანებს.

ლოკალური RabbitMQ: `docker compose up -d` (UI: http://localhost:15672, guest/guest).

## ხელსაწყოები

| ხელსაწყო | რას აკეთებს | ჩაირთვება |
|---|---|---|
| `list_contracts` | კონტრაქტების სია: event/request, ვერსია, exchange, პასუხის ტიპები | ყოველთვის |
| `validate_message` | JSON-ს კონტრაქტზე ამოწმებს გაგზავნის გარეშე | ყოველთვის |
| `queue_stats` | რიგში მზა შეტყობინებების და consumer-ების რაოდენობა | ყოველთვის |
| `peek_messages` | შეტყობინებები ამოღების გარეშე: payload, კონტრაქტის შემოწმება, fault | ყოველთვის |
| `list_queues` | რიგების სია ფილტრებით. error რიგთან ჩანს, რომელ რიგს ეკუთვნის | `ManagementUri` |
| `contract_consumers` | რომელ რიგებამდე აღწევს კონტრაქტი, ჰყავს თუ არა consumer, რამდენია `_error`/`_skipped`-ში | `ManagementUri` |
| `publish_<contract>` | აქვეყნებს event-ს | `EnablePublish` |
| `request_<contract>` | აგზავნის request-ს და აბრუნებს პასუხს | `EnablePublish` |
| `republish_from_error` | აბრუნებს შეტყობინებებს `_error`/`_skipped` რიგიდან: ჯერ preview, შემდეგ `confirm=true` | `EnableRepublish` |
| `pending_requests` | შემოსული request-ები, რომლებიც პასუხს ელოდება (`waitSeconds` = long poll) | `RespondTo` |
| `reply_<response>` / `reject_request` | პასუხობს request-ს ან უარყოფს მას | `RespondTo` |

ყველა ხელსაწყო აბრუნებს `structuredContent`-ს `outputSchema`-ით. შეცდომა `isError: true`-ით და გასაგები მიზეზით ბრუნდება. მაგალითად, 403-ის შემთხვევაში ჩანს, რომელი უფლება აკლია RabbitMQ-ის მომხმარებელს.

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

- **ველები:** `required` და nullability განსაზღვრავს სავალდებულო ველებს. `[Description]` არის ახსნა, რომელსაც მოდელი კითხულობს. DataAnnotations (`[Range]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]` …) სქემაში ჩანს და სერვერიც ამოწმებს, ჩადგმულ ობიექტებსა და სიებშიც.
- **სახელები:** URN არის `urn:message:Namespace:Type`, exchange კი `Namespace:Type`, როგორც MassTransit-ში. სხვა სახელზე მისაბმელად გამოიყენეთ `Urn`/`Exchange`.
- **თქვენი კონტრაქტები:** შექმენით classlib, რომელიც `RabbitMcp.Contracts` პაკეტს იყენებს (nupkg Release-შია), ააგეთ და მიუთითეთ: `RabbitMcp__ContractAssemblies=/path/Billing.Contracts.dll`. დამოკიდებულებები DLL-ის გვერდით უნდა იდოს. სერვერის თავიდან აწყობა საჭირო არ არის.

### თავსებადობის შემოწმება

ცვლილება, რომელიც build-ს არ არღვევს, უკვე გაშვებულ სერვისებს wire-ზე მაინც შეიძლება დაარღვევდეს. ორ build-ს ასე შეადარებთ:

```bash
rabbit-mcp contracts diff old/Billing.Contracts.dll new/Billing.Contracts.dll   # ან --json
```

```
BREAKING OrderPlaced customer: Type changed from string to number.
BREAKING OrderPlaced channel: New required property: existing publishers do not send it.
WARNING  OrderPlaced: Breaking change without a version bump (still v1).
INFO     OrderPlaced coupon: New optional property.
```

- **breaking:** კონტრაქტის, პასუხის ტიპის, სავალდებულო ველის ან enum-ის მნიშვნელობის წაშლა; URN-ის, exchange-ის ან ტიპის შეცვლა; ახალი ან სავალდებულოდ ქცეული ველი.
- **warning:** არასავალდებულო ველის წაშლა, `null`-ად ქცეული ველი, ახალი პასუხის ტიპი ან enum-ის მნიშვნელობა, დამრღვევი ცვლილება ვერსიის გაზრდის გარეშე.
- **exit code:** `0` თავსებადია, `1` breaking ცვლილებაა, `2` შეცდომაა. მოწმდება JSON-ის სტრუქტურა; DataAnnotations-ის წესები არ დარდება. შეადარეთ მხოლოდ საკუთარი build-ები, რადგან DLL-ის attribute-ების კოდი სრულდება.

სერვისის CI-ში (`actions/checkout` `fetch-depth: 0`-ით):

```yaml
- run: |
    curl -sL https://github.com/lazogio/rabbit-mcp/releases/download/v0.1.0/rabbit-mcp-0.1.0-linux-x64.tar.gz | tar xz
    git worktree add ../base origin/main && dotnet build ../base/src/Billing.Contracts -c Release -o base
    dotnet build src/Billing.Contracts -c Release -o head
    rabbit-mcp/rabbit-mcp contracts diff base/Billing.Contracts.dll head/Billing.Contracts.dll
```

## Request/response

მოდელი `request_<contract>`-ით ეკითხება სერვისს, MassTransit consumer კი ჩვეულებრივ პასუხობს: `await context.RespondAsync(new OrderStatus { … })`. request-ს თან ახლავს `requestId`, დროებითი `responseAddress` და TTL, რომელიც timeout-ის ტოლია (1–120 წამი, ნაგულისხმევად 30). შედეგში ჩანს:

- `status`: `responded`, `faulted` ან `timeout`;
- `response` და `responseCheck`: აკმაყოფილებს თუ არა პასუხი თავის კონტრაქტს;
- consumer-ის exception, თუ MassTransit-მა `Fault<T>` დააბრუნა.

### Responder: სერვისი ეკითხება, მოდელი პასუხობს

`RabbitMcp__RespondTo=AskHuman` (ან `Get*`) სერვერს consumer-ად აქცევს. სერვისი ჩვეულებრივ ეკითხება, მაგ. `GetResponse<HumanAnswer, HumanDeclined>(new AskHuman { … })`, პასუხს კი მოდელი იძლევა:

1. შემოსული request ჩანს `pending_requests`-ში: შეტყობინება, კონტრაქტის შემოწმება, პასუხის ხელსაწყოები და `expiresAt`. `waitSeconds: 50` ახალ request-ს ელოდება, ამიტომ ხშირი შემოწმება საჭირო არ არის.
2. მოდელი პასუხობს `reply_<response>`-ით (პასუხი ჯერ კონტრაქტზე მოწმდება) ან `reject_request`-ით. უარყოფისას მომთხოვნი მაშინვე `RequestFaultException`-ს იღებს.
3. ვადაგასული request ქრება. `MaxPendingRequests`-ზე მეტი request მაშინვე Fault-ით უარიყოფა.

request-ები მეხსიერებაში ინახება და გადატვირთვისას იკარგება. ავტომატური პასუხი არ არსებობს: ყოველ პასუხს მოდელი ან ადამიანი იძლევა. ერთი `ClientName`-ით პასუხობს მხოლოდ ერთი ასლი, დანარჩენები `standby`-ში არიან და აქტიურის გათიშვისას ~5 წამში ჩაენაცვლებიან. კავშირის გაწყვეტის შემდეგ responder თავიდან უერთდება.

## გუნდისთვის: HTTP სერვერი

`RabbitMcp__Transport=http` ერთ სერვერს მთელი გუნდისთვის ხელმისაწვდომს ხდის: MCP endpoint არის `/mcp`, health check `/healthz`. **ავტორიზაციის გარეშე სერვერი არ ეშვება:**

- **API key:** `RabbitMcp__ApiKeys="alice:KEY1;ci:KEY2"`. გასაღები მინიმუმ 32 სიმბოლოა (`openssl rand -base64 32`). კლიენტი აგზავნის `X-Api-Key: KEY`-ს ან `Authorization: Bearer KEY`-ს.
- **JWT / OIDC** (Entra ID, Keycloak, Auth0 …): `RabbitMcp__JwtAuthority` და `RabbitMcp__JwtAudience`. 401-ის შემთხვევაში სერვერი მიუთითებს `/.well-known/oauth-protected-resource`-ზე, ამიტომ კლიენტები ავტორიზაციის სერვერს თავად პოულობენ.

```bash
claude mcp add --transport http rabbit-mcp https://mcp.example.com/mcp --header "X-Api-Key: KEY1"
```

**Docker:** `ghcr.io/lazogio/rabbit-mcp` (amd64/arm64) HTTP რეჟიმში ეშვება `0.0.0.0:5080`-ზე, non-root მომხმარებლით. TLS-ისთვის reverse proxy-ის (nginx, Traefik, ingress) უკან დააყენეთ.

```bash
docker run -d -p 5080:5080 \
  -e RabbitMcp__Uri=amqp://user:pass@rabbit:5672/ \
  -e RabbitMcp__ApiKeys="team:$(openssl rand -base64 32)" \
  -e RabbitMcp__ContractAssemblies=/contracts/Billing.Contracts.dll -v ./contracts:/contracts:ro \
  ghcr.io/lazogio/rabbit-mcp:0.1
```

### უფლებები (როლები)

სანამ როლი არ არის განსაზღვრული, ყველა ავტორიზებულს ყველა ჩართული ხელსაწყო აქვს. როლის განსაზღვრის შემდეგ თითოეულს მხოლოდ თავისი როლის უფლებები რჩება:

| უფლება | ხელსაწყოები | შეზღუდვა |
|---|---|---|
| `read` | ნახვა: სიები, სტატისტიკა, peek, `contract_consumers` | — |
| `publish` / `request` | `publish_*` / `request_*` | კონტრაქტით: `publish:Billing.*,Order*` |
| `republish` | `republish_from_error` | error რიგით: `republish:orders-*_error` |
| `respond` | `pending_requests`, `reply_*`, `reject_request` | request კონტრაქტით: `respond:AskHuman` |
| `*` | ყველაფერი | — |

```bash
RabbitMcp__Roles__viewer="read"
RabbitMcp__Roles__operator="read;publish:Order*;request;republish:*_error"
RabbitMcp__Roles__admin="*"
RabbitMcp__UserRoles="alice=operator;ci=viewer;bob@example.com=admin"
RabbitMcp__DefaultRoles="viewer"
```

- **როლის მინიჭება:** როლი ენიჭება API key-ის სახელს (`alice:KEY` → `alice`), token-ის მომხმარებელს (`preferred_username`, `email`, `sub`) ან JWT-ის `roles` claim-ის მნიშვნელობას. claim-ის სახელს `JwtRolesClaim` ცვლის.
- **როლის გარეშე მომხმარებელი** იღებს `DefaultRoles`-ს. თუ ის ცარიელია, არაფერი შეუძლია.
- **შემოწმება ორ ეტაპად ხდება.** მომხმარებელი მხოლოდ ნებადართულ ხელსაწყოებს ხედავს. ყოველი გამოძახება კი არგუმენტებით (რიგი, კონტრაქტი) ხელახლა მოწმდება, უარი კი audit-ში იწერება.
- **ზღვარი:** როლი `Enable*`-ით და `PublishAllow`-ით ჩართული ხელსაწყოებიდან არჩევს და გამორთულს ვერ ჩართავს.
- **stdio-ში** ერთი ლოკალური მომხმარებელია, ამიტომ როლები მხოლოდ HTTP-ზე მოქმედებს.

## კონფიგურაცია

env ცვლადი `RabbitMcp__Name`, ან `appsettings.json`-ში `"RabbitMcp": { "Name": … }`. სიები `;`-ით გამოიყოფა, ნიმუშებში `*` ნებისმიერ სიმბოლოს ნიშნავს.

| ცვლადი | ნაგულისხმევი | აღწერა |
|---|---|---|
| `Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI, vhost ბოლოშია (`…/prod`) |
| `ClientName` | `rabbit-mcp` | კავშირის სახელი, envelope-ის `sourceAddress` |
| `ContractAssemblies` | — | თქვენი კონტრაქტების DLL-ები. ცარიელისას ნიმუშის კონტრაქტები გამოიყენება |
| `ManagementUri` | — | Management API (`http://localhost:15672`), რთავს `list_queues`-ს და `contract_consumers`-ს |
| `EnablePublish` / `EnableRepublish` | `false` | რთავს გაგზავნას / republish-ს |
| `PublishAllow` / `PublishDeny` | — | რომელი კონტრაქტების გაგზავნაა ნებადართული / აკრძალული (deny უპირატესია), მაგ. `Get*` |
| `RespondTo` / `MaxPendingRequests` | — / `100` | responder-ის კონტრაქტები / მოლოდინში მყოფი request-ების ზღვარი |
| `AuditLogPath` / `AuditIncludePayload` | — / `false` | audit ფაილი (JSON Lines) / ჩაიწეროს თუ არა შიგთავსი (შეიძლება პერსონალურ მონაცემებს შეიცავდეს) |
| `MaxMessagesPerCall` / `MaxPayloadChars` | `50` / `16000` | peek/republish-ის ზღვარი / ჩვენებისას body-ის შეკვეცის ზღვარი |
| `ExchangeCacheSeconds` | `30` | რამდენ წამს ახსოვს, რომ exchange არსებობს (`0` = გამორთული) |
| `Transport` | `stdio` | `stdio` ან `http` |
| `HttpUrls` / `HttpPath` / `HttpPublicUrl` | `http://localhost:5080` / `/mcp` / — | მისამართი / endpoint / საჯარო URL proxy-ს უკან |
| `ApiKeys` | — | `name:KEY;…` |
| `JwtAuthority` / `JwtAudience` / `JwtScopes` | — | OIDC. `JwtScopes`-ის ყველა scope token-ში სავალდებულოა |
| `Roles__<role>` / `UserRoles` / `DefaultRoles` / `JwtRolesClaim` | — / — / — / `roles` | უფლებები, იხ. ზემოთ |
| `HttpAllowedOrigins` | — | ბრაუზერის origin-ები, რომლებსაც წვდომა აქვთ |
| `HttpAllowAnonymous` | `false` | ავტორიზაციის გარეშე, მხოლოდ `localhost`-ზე |

**OpenTelemetry** ჩაირთვება `OTEL_EXPORTER_OTLP_ENDPOINT`-ით. ჩანს `publish`/`request`/`republish` span-ები და მეტრიკები `rabbitmcp.operations`, `rabbitmcp.request.duration`. გაგზავნილ შეტყობინებას `traceparent` და `MT-Activity-Id` ახლავს, ამიტომ MassTransit consumer-ის span იმავე trace-ში ჩნდება.

## საიმედოობა და უსაფრთხოება

- **შეტყობინება არ იკარგება.** ვალიდაციის ჩავარდნისას არაფერი იგზავნება. exchange-ის არსებობა წინასწარ მოწმდება. publisher confirms და `mandatory` ვერ მიწოდებულ შეტყობინებას შეცდომად აბრუნებს.
- **republish:** შეტყობინება error რიგიდან მხოლოდ ბროკერის დადასტურების შემდეგ იშლება. body უცვლელია, იშლება მხოლოდ fault header-ები. მიწოდება at-least-once-ია.
- **peek:** აქტიური consumer-ის მქონე რიგზე `force=true` სჭირდება. შეტყობინება რიგში რჩება, მაგრამ `redelivered` ხდება.
- **Audit:** ყოველი გაგზავნა, request, republish და პასუხი იწერება შედეგთან და მომხმარებელთან ერთად. ჩავარდნილი და უარყოფილი მცდელობებიც იწერება:
  ```json
  {"timestamp":"2026-10-13T12:00:00+00:00","action":"publish","outcome":"sent","client":"alice (claude-code 2.1.0)","contract":"OrderSubmitted","messageId":"…"}
  ```
- **საიდუმლოები:** პაროლი არც ლოგში, არც შეცდომაში, არც audit-ში ჩანს. API key-ები SHA-256-ით ინახება.
- **HTTP:** ავტორიზაცია სავალდებულოა, JWT scope-ები მოწმდება, უცხო `Origin` უარიყოფა.
- **შეტყობინების შიგთავსს არ ენდობა.** responder პასუხობს მხოლოდ MassTransit-ის დროებით reply endpoint-ს. republish არ მიჰყვება ყალბ `MT-Fault-InputAddress`-ს. peek-ით ნანახი ტექსტი მონაცემია და არა ინსტრუქცია.
- **მინიმალური უფლებები:** RabbitMQ-ის მომხმარებელს მიეცით მხოლოდ საჭირო permission-ები. Management API-სთვის საკმარისია `monitoring` tag. 403-ის შემთხვევაში ხელსაწყო წერს, რა აკლია.

## ლიცენზია

[MIT](LICENSE). პროექტზე მუშაობა, ტესტები და რელიზი: [CONTRIBUTING.md](CONTRIBUTING.md).
