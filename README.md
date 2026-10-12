# rabbit-mcp

[![CI](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/lazogio/rabbit-mcp/actions/workflows/ci.yml)

RabbitMQ-ის MCP სერვერი, რომელიც **შეტყობინებების კონტრაქტებზეა** აგებული (.NET 10).
თითო C# record კონტრაქტი ცალკე ტიპიზებულ ხელსაწყოდ იქცევა, რომლის `inputSchema` თავად კონტრაქტია. შეტყობინებები MassTransit-ის ფორმატშია, ამიტომ MassTransit v8/v9 სერვისები მათ კონფიგურაციის გარეშე იღებენ. თავად სერვერი MassTransit-ზე დამოკიდებული არ არის, იყენებს მხოლოდ `RabbitMQ.Client`-ს.

## შესაძლებლობები

- **Event-ები:** `publish_<contract>` აქვეყნებს შეტყობინებას გაგზავნამდე ვალიდაციით.
- **Request/response:** `request_<contract>` აგზავნის მოთხოვნას და ელოდება consumer-ის პასუხს. პასუხიც მოწმდება თავის კონტრაქტზე.
- **დიაგნოსტიკა:** რიგების სტატისტიკა და შეტყობინებების ნახვა ამოღების გარეშე, MassTransit-ის fault-ის დეტალებით.
- **აღდგენა:** `_error` რიგიდან შეტყობინებების უსაფრთხოდ დაბრუნება საწყის რიგში.

## სწრაფი დაწყება

```bash
docker compose up -d                                    # RabbitMQ, UI: http://localhost:15672
dotnet test
dotnet publish src/RabbitMcp.Server -c Release -o out
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/out/rabbit-mcp
```

> MCP კლიენტში დაარეგისტრირეთ `dotnet publish`-ით მიღებული ბინარი და არა `dotnet run`: `dotnet run` stdout-ზე წერს და MCP პროტოკოლს აზიანებს.

Claude Desktop-ისთვის იგივე ბინარი და env ცვლადები `claude_desktop_config.json`-ის `mcpServers`-ში ჩაწერეთ.

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

## ხელსაწყოები

| ხელსაწყო | რას აკეთებს |
|---|---|
| `list_contracts` | კონტრაქტების სია: event/request, ვერსია, URN, exchange, პასუხის ტიპები |
| `validate_message` | JSON-ს კონტრაქტზე ამოწმებს გაგზავნის გარეშე |
| `queue_stats` | რიგში მზა შეტყობინებების და consumer-ების რაოდენობა |
| `peek_messages` | აჩვენებს შეტყობინებებს ამოღების გარეშე: payload, კონტრაქტის შემოწმება, fault |
| `publish_<contract>` * | აქვეყნებს event-ს |
| `request_<contract>` * | აგზავნის request-ს და აბრუნებს პასუხს |
| `republish_from_error` ** | აბრუნებს შეტყობინებებს `_error`/`_skipped` რიგიდან: ჯერ preview, შემდეგ `confirm=true` |

\* `EnablePublish` · \** `EnableRepublish`. ყველა ხელსაწყო აბრუნებს `structuredContent`-ს და აცხადებს `outputSchema`-ს. შეცდომები `isError: true`-ით და გასაგები ტექსტით ბრუნდება.

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
- **სხვა ასამბლეის კონტრაქტები:** დაამატეთ `Program.cs`-ში, `ContractRegistry.FromAssemblies(...)`-ში.

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
- **peek:** შეტყობინება რიგში რჩება, მაგრამ იღებს `redelivered` დროშას და შეიძლება თანმიმდევრობა შეეცვალოს. quorum რიგებზე თითოეული peek delivery-limit-ში ითვლება.

## სტრუქტურა

```
src/RabbitMcp.Contracts    კონტრაქტები (დამოკიდებულებების გარეშე, სერვისებშიც გამოიყენება)
src/RabbitMcp.Server       Contracts/ რეესტრი, ვალიდაცია · Messaging/ AMQP, envelope, reply · Tools/ MCP ხელსაწყოები
tests/RabbitMcp.Tests      unit ტესტები (RabbitMQ არ სჭირდება)
```

**CI:** ყოველ push-სა და PR-ზე GitHub Actions აგებს პროექტს და უშვებს ტესტებს. `main`-ზე ასევე ქვეყნდება სერვერის ბინარი (artifact `rabbit-mcp-linux-x64`).

**შემდეგი ნაბიჯები:** ინტეგრაციული ტესტები Testcontainers-ით, `list_queues` Management API-ით, HTTP ტრანსპორტი ავტორიზაციით.
