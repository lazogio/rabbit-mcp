# rabbit-mcp

<!-- mcp-name: io.github.lazogio/rabbit-mcp -->

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) · **ქართული**

MCP სერვერი RabbitMQ-სთვის, აგებული **შეტყობინებების კონტრაქტებზე** (.NET 10). თითო კონტრაქტი ცალკე ტიპიზებულ ხელსაწყოდ იქცევა, რომლის input schema თავად კონტრაქტია. შეტყობინებები MassTransit-ის wire ფორმატს იყენებს, ამიტომ MassTransit სერვისები მათ ისე იღებს, როგორც არის; MassTransit-ის გარეშე სერვისები უბრალო JSON-ს იღებს. თავად სერვერი MassTransit-ზე არ არის დამოკიდებული.

- **გაგზავნა:** `publish_<contract>` და `request_<contract>`, გაგზავნამდე კონტრაქტით შემოწმებული.
- **ნახვა:** რიგები, შეტყობინებები (შეცდომის დეტალებით) და ვინ იღებს თითოეულ კონტრაქტს.
- **აღდგენა:** `_error` რიგებიდან შეტყობინებების დაბრუნება, გასწორება და თავიდან გაგზავნა ან poison შეტყობინებების გადადება. არაფერი იშლება.
- **პასუხი:** responder რეჟიმში მოდელი თქვენი სერვისების request-ებს პასუხობს.
- **დაცვა:** `contracts diff` CI-ში wire-ის დამრღვევ ცვლილებებს იჭერს; გუნდებისთვის — როლები, ლიმიტები და audit.

## დაყენება

| გზა | როგორ |
|---|---|
| ბინარი | [Releases](https://github.com/lazogio/rabbit-mcp/releases): self-contained Linux-ის, Windows-ის და macOS-ისთვის. macOS: `xattr -d com.apple.quarantine rabbit-mcp` |
| .NET tool | `dotnet tool install -g RabbitMcp.Server`, ან დაყენების გარეშე: `dnx RabbitMcp.Server --yes` (პირველი რელიზიდან) |
| Docker | `ghcr.io/lazogio/rabbit-mcp` — HTTP რეჟიმი, იხ. [გუნდებისთვის](#გუნდებისთვის-http) |
| კოდიდან | `dotnet publish src/RabbitMcp.Server -c Release -o out` (არასოდეს `dotnet run`: ის stdout-ზე წერს) |

```bash
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/rabbit-mcp
```

Claude Desktop: იგივე, `"command"` და `"env"` ველებით `claude_desktop_config.json`-ში. ლოკალური RabbitMQ: `docker compose up -d`.

**სცადეთ:** [samples/Shop](samples/Shop) ერთი ბრძანებით უშვებს RabbitMQ-ს, PostgreSQL-ს, MassTransit მაღაზიას და rabbit-mcp-ს, და გადის შეკვეთებს, შეცდომების აღდგენას და დასტურებს.

## ხელსაწყოები

| ხელსაწყო | რას აკეთებს | ირთვება |
|---|---|---|
| `list_contracts`, `validate_message` | კონტრაქტები და მათი დეტალები; JSON-ის შემოწმება გაგზავნის გარეშე | ყოველთვის |
| `queue_stats`, `peek_messages` | რიგების რაოდენობები; შეტყობინებები ამოღების გარეშე (payload, კონტრაქტის შემოწმება, fault, `x-death`) | ყოველთვის |
| `find_message` | პოულობს შეტყობინებას id-ით ან ტექსტით და ამბობს, რომელ რიგშია | ყოველთვის |
| `read_stream` | კითხულობს RabbitMQ stream-ს `first`-იდან, `last`-იდან, offset-იდან ან დროიდან (`10m`) | ყოველთვის |
| `list_queues`, `contract_consumers` | რიგები; რომელ რიგებამდე აღწევს კონტრაქტი და ვინ იღებს | `ManagementUri` |
| `recent_activity` | Audit: „ვინ დააბრუნა გუშინ?“ | `AuditLogPath` |
| `publish_<contract>`, `request_<contract>` | აქვეყნებს event-ს / აგზავნის request-ს და აბრუნებს პასუხს | `EnablePublish` |
| `republish_from_error` | აბრუნებს შეტყობინებებს error ან dead-letter რიგიდან: ჯერ preview, შემდეგ `confirm=true`. ინციდენტებისთვის — ფილტრები (`exceptionType`, `contract`, `since` …) | `EnableRepublish` |
| `dead_letter_from_error` | შეტყობინებებს, რომლებიც ვერასოდეს შესრულდება, მიზეზთან ერთად `<queue>_dead`-ში გადადებს | `EnableRepublish` |
| `resend_corrected` | აგზავნის ჩავარდნილი შეტყობინების გასწორებულ ასლს და ორიგინალს გადადებს | `EnableRepublish` + `EnablePublish` |
| `pending_requests`, `reply_<response>`, `reject_request` | სერვისების request-ები, რომლებიც პასუხს ელოდება | `RespondTo` |

შედეგები სტრუქტურირებულია (`outputSchema`); შეცდომები ამბობს, რა უნდა გაკეთდეს შემდეგ. prompts `diagnose_errors`, `investigate_message`, `review_approvals` და `contract_health` ხშირ ამოცანებს გადის, ხოლო ყველა კონტრაქტი resource-ია: `contract://<Name>`.

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

- `required`, nullability, `[Description]` და DataAnnotations schema-ში ხვდება და სერვერი მათ ამოწმებს.
- სახელები MassTransit-ს მიჰყვება (`urn:message:Namespace:Type`, exchange `Namespace:Type`); `Urn` და `Exchange` მათ ცვლის.
- საკუთარი კონტრაქტები: class library, რომელიც `RabbitMcp.Contracts` პაკეტს იყენებს და `RabbitMcp__ContractAssemblies=/path/Billing.Contracts.dll`-ით იტვირთება.

**სერვისები MassTransit-ის გარეშე** — `Format = MessageFormat.Raw`: სხეული უბრალო JSON-ია, მეტამონაცემები AMQP property-ებშია, `Exchange` და/ან `RoutingKey` კი თქვენ მიუთითებთ. request-ები RabbitMQ RPC-ს იყენებს (`reply_to` + `correlation_id`; პასუხის ტიპი AMQP `type`-შია).

```csharp
[MessageContract("Asks the shipping desk for a quote.", Format = MessageFormat.Raw, RoutingKey = "shipping-quotes",
    Responses = new[] { typeof(ShippingQuote), typeof(ShippingUnavailable) })]
public sealed record GetShippingQuote { … }
```

**C#-ის გარეშე (AsyncAPI)** — `RabbitMcp__ContractFiles=warehouse.yaml`: AsyncAPI 3.0 ან 2.x ფაილის ყოველი შეტყობინება ხელსაწყოდ იქცევა ფაილის JSON Schema-თი; routing AMQP binding-ებიდან მოდის, MassTransit სერვისებისთვის — `x-rabbit-mcp: { format: masstransit }`. მაგალითი: [samples/asyncapi/warehouse.yaml](samples/asyncapi/warehouse.yaml). `rabbit-mcp contracts asyncapi Billing.Contracts.dll` C# კონტრაქტებს AsyncAPI-ად აქცევს.

**თავსებადობის შემოწმება** — CI-ში ორი build-ის (ან ორი AsyncAPI ფაილის) შედარება; დამრღვევ ცვლილებაზე exit code `1`:

```bash
rabbit-mcp contracts diff old/Billing.Contracts.dll new/Billing.Contracts.dll
# BREAKING OrderPlaced customer: Type changed from string to number.
# WARNING  OrderPlaced: Breaking change without a version bump (still v1).
```

## Request/response

`request_<contract>` პასუხს ელოდება (timeout 1–120 წმ) და აბრუნებს `responded`-ს, `faulted`-ს ან `timeout`-ს, პასუხი კი კონტრაქტით მოწმდება.

**Responder:** `RabbitMcp__RespondTo=ApproveLargeOrder`-ით თქვენი სერვისი კითხულობს და მოდელი პასუხობს. request-ები `pending_requests`-ში ჩანს; მოდელი პასუხობს `reply_<response>`-ით ან `reject_request`-ით (მომთხოვნი `RequestFaultException`-ს იღებს). request-ები მეხსიერებაში ინახება; ერთ `ClientName`-ზე ერთი ინსტანსი პასუხობს, დანარჩენები ელოდება.

## გუნდებისთვის (HTTP)

`RabbitMcp__Transport=http` ემსახურება `/mcp`-ს (და `/healthz`-ს). ავთენტიფიკაციის გარეშე არ ირთვება:

- **API keys:** `RabbitMcp__ApiKeys="alice:KEY1;ci:KEY2"` (≥ 32 სიმბოლო); კლიენტები `X-Api-Key`-ს აგზავნიან.
- **JWT / OIDC:** `RabbitMcp__JwtAuthority` + `RabbitMcp__JwtAudience`.

```bash
docker run -d -p 5080:5080 \
  -e RabbitMcp__Uri=amqp://user:pass@rabbit:5672/ \
  -e RabbitMcp__ApiKeys="team:$(openssl rand -base64 32)" \
  -e RabbitMcp__ContractAssemblies=/contracts/Billing.Contracts.dll -v ./contracts:/contracts:ro \
  ghcr.io/lazogio/rabbit-mcp:1.0
```

**როლები** (მხოლოდ HTTP; როლების გარეშე ყველას ყველა ჩართული ხელსაწყო შეუძლია):

```bash
RabbitMcp__Roles__viewer="read"
RabbitMcp__Roles__operator="read;publish:Order*;request;republish:*_error"
RabbitMcp__UserRoles="alice=operator;ci=viewer"
RabbitMcp__DefaultRoles="viewer"
```

უფლებები: `read`, `publish`, `request`, `republish`, `respond`, `*`; pattern-ები მათ კონტრაქტით ან რიგით ზღუდავს. მომხმარებელი იცნობა API key-ის სახელით, token-ის მომხმარებლით ან JWT `roles` claim-ით. მომხმარებელი მხოლოდ თავის ხელსაწყოებს ხედავს და ყოველი გამოძახება თავიდან მოწმდება.

## კონფიგურაცია

გარემოს ცვლადი `RabbitMcp__<Option>` ან `appsettings.json`. სიები `;`-ით იყოფა, `*` ნებისმიერ ტექსტს ემთხვევა.

| პარამეტრი | ნაგულისხმევი | აღწერა |
|---|---|---|
| `Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI, vhost ბოლოში |
| `ContractAssemblies` / `ContractFiles` | — | კონტრაქტების DLL-ები / AsyncAPI ფაილები. ცარიელი: ჩაშენებული ნიმუშები |
| `ManagementUri` | — | Management API, მაგ. `http://localhost:15672` |
| `EnablePublish` / `EnableRepublish` | `false` | გაგზავნის / შეტყობინებების გადატანის ნებართვა |
| `PublishAllow` / `PublishDeny` | — | კონტრაქტები, რომელთა გაგზავნა შეიძლება / არასოდეს შეიძლება |
| `DeadLetterQueue` | `{queue}_dead` | სად იდება poison შეტყობინებები |
| `RespondTo` / `MaxPendingRequests` | — / `100` | responder-ის კონტრაქტები / მომლოდინე request-ების ლიმიტი |
| `HumanConfirmation` / `ConfirmTools` | `auto` / აღდგენის სამი ხელსაწყო | ცვლილებას ადამიანი ადასტურებს კლიენტის დიალოგში (`required` უარს ამბობს, თუ კითხვა ვერ ხერხდება, `off` `confirm`-ს ენდობა) |
| `AuditLogPath` / `AuditIncludePayload` | — / `false` | Audit ფაილი (JSON Lines) / შეტყობინებების სხეულიც |
| `MaxMessagesPerCall` / `MaxBulkMessages` / `MaxPayloadChars` | `50` / `1000` / `16000` | ლიმიტები |
| `RateLimits` | — | ცვლილებები თითო მომხმარებელზე, მაგ. `publish:100/h;*:1000/d` |
| `Transport` / `HttpUrls` / `HttpPath` | `stdio` / `http://localhost:5080` / `/mcp` | ტრანსპორტი |
| `ApiKeys`, `JwtAuthority`, `JwtAudience`, `JwtScopes` | — | HTTP ავთენტიფიკაცია |
| `Roles__<role>`, `UserRoles`, `DefaultRoles` | — | როლები |
| `ClientName`, `HttpPublicUrl`, `HttpAllowedOrigins`, `HttpAllowAnonymous`, `ExchangeCacheSeconds`, `JwtRolesClaim` | | იშვიათად საჭირო |

OpenTelemetry: მიუთითეთ `OTEL_EXPORTER_OTLP_ENDPOINT`; trace-ები MassTransit consumer-ის trace-ს უერთდება.

## გარანტიები

- **ჩუმი დაკარგვა არ ხდება:** არასწორი შეტყობინება არ იგზავნება; publisher confirms და `mandatory` მიუწვდომელ შეტყობინებას შეცდომად აქცევს.
- **უსაფრთხო გადატანა:** შეტყობინება error რიგიდან მხოლოდ მაშინ გადის, როცა broker-მა ახალი ასლი დაადასტურა; სხეული არ იცვლება.
- **არაფერი იშლება:** poison შეტყობინებები ყველა header-ით იდება და შეიძლება დაბრუნდეს.
- **ადამიანი ადასტურებს:** MCP elicitation-ის მქონე კლიენტები (მაგ. Claude Code) ყოველ ცვლილებამდე დიალოგს აჩვენებს; stateless HTTP-ზე `confirm=true` მოქმედებს.
- **Audit:** ყოველი გაგზავნა, გადატანა და პასუხი — უარყოფილებიც — მომხმარებლით.
- **შინაარსს არ ენდობა:** header-ები შეტყობინების მიმართულებას ვერ ცვლის; პაროლები არც log-ებში ჩანს, არც შეცდომებში.

## ლიცენზია

[MIT](LICENSE). დეველოპმენტი, ტესტები და რელიზები: [CONTRIBUTING.ka.md](CONTRIBUTING.ka.md).
