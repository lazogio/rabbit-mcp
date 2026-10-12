# rabbit-mcp

MCP სერვერი RabbitMQ-სთვის, რომელიც **შეტყობინებების კონტრაქტებზეა** აგებული. თითო კონტრაქტი (C# record) ცალკე ტიპიზებულ `publish_*` ხელსაწყოდ იქცევა. ხელსაწყოს `inputSchema` თავად კონტრაქტის JSON Schema-ა, ამიტომ მოდელი მხოლოდ კონტრაქტთან შესაბამის შეტყობინებას აგზავნის, სერვერი კი გაგზავნამდე ვალიდაციის წესებსაც ამოწმებს.

შეტყობინებები MassTransit-ის ფორმატში იწერება (`application/vnd.masstransit+json`, `urn:message:…`, exchange `Namespace:Type`), ამიტომ MassTransit v8/v9 consumer-ები მათ დამატებითი კონფიგურაციის გარეშე იღებენ. თავად სერვერი MassTransit-ის პაკეტზე დამოკიდებული არ არის, იყენებს მხოლოდ `RabbitMQ.Client`-ს.

## სტრუქტურა

```
src/RabbitMcp.Contracts   კონტრაქტები ([MessageContract] record-ები). დამოკიდებულებები არ აქვს,
                          ამიტომ შეგიძლიათ თქვენს სერვისებშიც გამოიყენოთ.
src/RabbitMcp.Server      MCP სერვერი (stdio)
  Contracts/              რეესტრი, სახელების კონვენციები, DataAnnotations ვალიდაცია
  Messaging/              კავშირი, envelope, fault-ების პარსინგი, peek/republish
  Tools/                  MCP ხელსაწყოები და მათი შედეგის ტიპები (outputSchema)
tests/RabbitMcp.Tests     unit ტესტები (RabbitMQ არ სჭირდება)
```

მოთხოვნები: .NET 10 SDK, ModelContextProtocol 2.2.0, RabbitMQ.Client 7.2.2.

## სწრაფი დაწყება

```bash
docker compose up -d                 # ლოკალური RabbitMQ, UI: http://localhost:15672
dotnet test                          # ტესტები
dotnet publish src/RabbitMcp.Server -c Release -o out
```

MCP კლიენტში **გამოქვეყნებული ბინარი** დაარეგისტრირეთ და არა `dotnet run`. `dotnet run` აგების ლოგს stdout-ზე წერს და ეს MCP პროტოკოლს აზიანებს.

**Claude Code:**

```bash
claude mcp add rabbit-mcp \
  -e RabbitMcp__Uri=amqp://guest:guest@localhost:5672/ \
  -e RabbitMcp__EnablePublish=true \
  -- /full/path/to/out/rabbit-mcp
```

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "rabbit-mcp": {
      "command": "/full/path/to/out/rabbit-mcp",
      "env": {
        "RabbitMcp__Uri": "amqp://guest:guest@localhost:5672/",
        "RabbitMcp__EnablePublish": "true",
        "RabbitMcp__EnableRepublish": "false"
      }
    }
  }
}
```

## კონფიგურაცია

| პარამეტრი | ნაგულისხმევი | აღწერა |
|---|---|---|
| `RabbitMcp__Uri` | `amqp://guest:guest@localhost:5672/` | AMQP URI. vhost მის ბოლოშია (`…/prod`). |
| `RabbitMcp__ClientName` | `rabbit-mcp` | კავშირის სახელი და envelope-ის `sourceAddress`. |
| `RabbitMcp__EnablePublish` | `false` | `publish_*` ხელსაწყოების რეგისტრაცია. |
| `RabbitMcp__EnableRepublish` | `false` | `republish_from_error` ხელსაწყოს რეგისტრაცია. |
| `RabbitMcp__MaxMessagesPerCall` | `50` | `count`-ის ზედა ზღვარი. |
| `RabbitMcp__MaxPayloadChars` | `16000` | ამაზე დიდი body ნაჩვენებია შეკვეცილი (მხოლოდ ჩვენებისას). |

## ხელსაწყოები

| ხელსაწყო | როდის ჩანს | რას აკეთებს |
|---|---|---|
| `list_contracts` | ყოველთვის | კონტრაქტები: ვერსია, URN, exchange, publish ხელსაწყოს სახელი |
| `validate_message` | ყოველთვის | JSON-ის შემოწმება კონტრაქტზე, გაგზავნის გარეშე |
| `queue_stats` | ყოველთვის | ready შეტყობინებებისა და consumer-ების რაოდენობა |
| `peek_messages` | ყოველთვის | შეტყობინებების ნახვა ამოღების გარეშე: payload, კონტრაქტის შემოწმება, MassTransit fault-ის დეტალები |
| `publish_<contract>` | `EnablePublish` | თითო კონტრაქტზე ერთი ტიპიზებული publish |
| `republish_from_error` | `EnableRepublish` | `_error`/`_skipped` რიგიდან საწყის რიგში დაბრუნება, ორ ნაბიჯად |

ყველა ხელსაწყო აბრუნებს `structuredContent`-ს და აცხადებს `outputSchema`-ს. მოსალოდნელი შეცდომები, მაგალითად ვალიდაციის ჩავარდნა ან გამოუყენებელი exchange, `isError: true`-ით ბრუნდება გასაგები ტექსტით, რომ მოდელმა შეცდომა გაასწოროს.

## კონტრაქტის დამატება

```csharp
namespace Shop.Contracts.Payments;

[MessageContract("Payment was captured by the PSP.", Version = 1)]
public sealed record PaymentCaptured
{
    [Description("Order the payment belongs to.")]
    public required Guid OrderId { get; init; }

    [Description("Captured amount, > 0.")]
    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal Amount { get; init; }

    [Description("ISO 4217 currency, e.g. GEL.")]
    [RegularExpression("^[A-Z]{3}$")]
    public required string Currency { get; init; }
}
```

- `required`/nullability: სქემის `required` ველი, `null` უარიყოფა.
- `[Description]`: ველის აღწერა, რომელსაც მოდელი კითხულობს.
- DataAnnotations (`[Range]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]`, `[MinLength]`): SDK მათ სქემაშიც ასახავს (`minimum`, `pattern`, `format` ...), სერვერი კი publish-ამდე თავადაც ამოწმებს, ჩადგმულ ობიექტებსა და სიებშიც.

თუ კონტრაქტი სხვა ასამბლეაშია, დაამატეთ ის `Program.cs`-ში: `ContractRegistry.FromAssemblies(...)`.

**MassTransit-თან თავსებადობა.** consumer მაშინ მიიღებს შეტყობინებას, როცა მისი ტიპის სრული სახელი (`Namespace:Type`) URN-ს ემთხვევა. თუ consumer-ების კონტრაქტები სხვა namespace-შია, მიუთითეთ `[MessageContract(..., Urn = "urn:message:Their.Namespace:Type", Exchange = "Their.Namespace:Type")]`. ყველაზე მარტივი გზა კი ისაა, რომ Contracts პროექტი სერვისებშიც გამოიყენოთ.

## გარანტიები

**publish:**
- ვალიდაცია ჩავარდნისას არაფერი იგზავნება.
- exchange-ის არსებობა მოწმდება წინასწარ. MassTransit მას consumer-ის გაშვებისას ქმნის, ამიტომ თუ exchange არ არსებობს, შეტყობინებას ჯერ არავინ იღებს.
- publisher confirms + `mandatory`: თუ შეტყობინება ვერცერთ რიგში ვერ მოხვდა, ბრუნდება შეცდომა და შეტყობინება ჩუმად არ იკარგება.
- შეტყობინებები persistent-ია.

**republish_from_error:**
- `confirm`-ის გარეშე მხოლოდ preview-ს აბრუნებს.
- თითოეული შეტყობინება error რიგში ack-დება მხოლოდ მას შემდეგ, რაც ბროკერმა republish დაადასტურა. ჩავარდნილი და გამოტოვებული შეტყობინებები რიგში რჩება.
- body ბაიტ-ბაიტ უცვლელად გადაიგზავნება და არასდროს იკვეცება. იშლება მხოლოდ `MT-Reason`, `MT-Fault-*` და `MT-Host-*` ჰედერები, ემატება `X-Republished-*`.
- `messageIds`-ით შეგიძლიათ ზუსტად ის შეტყობინებები გადააგზავნოთ, რომლებიც preview-ში ნახეთ.
- მიწოდება at-least-once-ია: confirm-სა და ack-ს შორის crash-ის შემთხვევაში შესაძლოა დუბლიკატი გაჩნდეს. consumer-ები `messageId`-ით იდემპოტენტური უნდა იყვნენ.

**peek_messages:** შეტყობინებებს ack-ის გარეშე იღებს და მაშინვე აბრუნებს რიგში. ამიტომ მათ `redelivered` დროშა ედება და რიგში თანმიმდევრობა შეიძლება შეიცვალოს. quorum რიგებზე თითოეული peek delivery-limit-ში ითვლება. აქტიური consumer-ების მქონე რიგებს ნუ დაათვალიერებთ.

## შემდეგი ნაბიჯები

- ინტეგრაციული ტესტები Testcontainers-ით (ნამდვილი RabbitMQ + MassTransit consumer).
- `list_queues` Management API-ს მეშვეობით (AMQP რიგების ჩამოთვლას ვერ აკეთებს).
- Streamable HTTP ტრანსპორტი (`ModelContextProtocol.AspNetCore`) და ავტორიზაცია, თუ სერვერი გაზიარებული იქნება.
