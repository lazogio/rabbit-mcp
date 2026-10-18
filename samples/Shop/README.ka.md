# ნიმუში: მაღაზია

[English](README.md) · **ქართული**

ეს ნიმუში აჩვენებს rabbit-mcp-ს ნამდვილ MassTransit სერვისთან ერთად: მოდელი შეკვეთებს აფორმებს, მათ სტატუსს კითხულობს, ჩავარდნილ შეტყობინებებს აღადგენს და დიდ შეკვეთებს ამტკიცებს. მიწოდების ფასს კი პასუხობს MassTransit-ის გარეშე დაწერილი სერვისი, უბრალო RabbitMQ-ით.

```
Shop.Contracts   კონტრაქტები: OrderPlaced, GetOrderStatus → OrderStatus | OrderNotFound,
                 ApproveLargeOrder → OrderApproved | OrderRejected,
                 GetShippingQuote → ShippingQuote | ShippingUnavailable (Format = Raw)
Shop.Service     MassTransit 8 სერვისი: იღებს შეკვეთებს, პასუხობს სტატუსზე, დიდ შეკვეთაზე დასტურს ითხოვს;
                 ShippingDesk: უბრალო RabbitMQ.Client, MassTransit-ის გარეშე, პასუხობს მიწოდების ფასზე
docker-compose   RabbitMQ + Shop.Service + rabbit-mcp (HTTP, Shop.Contracts.dll-ით)
```

## გაშვება

საჭიროა მხოლოდ Docker. რეპოზიტორიის საქაღალდიდან გაუშვით:

```bash
docker compose -f samples/Shop/docker-compose.yml up -d --build
claude mcp add --transport http shop http://localhost:5080/mcp \
  --header "X-Api-Key: shop-demo-key-change-me-0123456789abcdef"
```

RabbitMQ-ის UI: http://localhost:15672 (`shop` / `shop`). ყველა პორტი მხოლოდ `localhost`-ზეა გახსნილი. გასაღების შესაცვლელად გაშვებამდე მიუთითეთ `SHOP_MCP_KEY=...`.

## სცენარი

Claude-ს სთხოვეთ თანმიმდევრობით:

1. **„რა კონტრაქტები გვაქვს და ვინ იღებს მათ?“** აქ მუშაობს `list_contracts` და `contract_consumers`. `OrderPlaced`-ის, `GetOrderStatus`-ის და `GetShippingQuote`-ის სტატუსი `ok`-ია, რადგან Shop.Service მათ იღებს.
2. **„რა სტატუსი აქვს შეკვეთას 5ca1ab1e-0000-4000-8000-000000000001?“** `request_get_order_status`-ზე სერვისი პასუხობს: `Shipped`.
3. **„გააფორმე შეკვეთა: ana@example.ge, FLAKY-LAMP, 1 ცალი, 20 ლარი.“** `publish_order_placed` შეკვეთას გზავნის. `FLAKY-` პროდუქტი პირველად ჩავარდება, თითქოს მარაგის სერვისი დროებით გათიშული ყოფილიყო.
4. **„რა არის order-placed_error-ში? დააბრუნე.“** `peek_messages` აჩვენებს exception-ს, `republish_from_error` (ჯერ preview, შემდეგ დადასტურება) კი შეკვეთას თავიდან ამუშავებს. სტატუსი ხდება `Approved`.
5. **„გააფორმე შეკვეთა: BROKEN-CHAIR, 1 ცალი, 50 ლარი. რატომ ჩავარდა? გადაიტანე dead-letter რიგში.“** ეს პროდუქტი ყოველთვის ჩავარდება, ამიტომ დაბრუნება არ უშველის. `dead_letter_from_error` შეტყობინებას `order-placed_dead`-ში გადაიტანს მიზეზთან ერთად. არაფერი იშლება: exception-ის დეტალები რჩება და საჭიროებისას `republish_from_error`-ით უკან დაბრუნდება.
6. **„გააფორმე შეკვეთა: BROKEN-TABLE, 1 ცალი, 80 ლარი. SKU არასწორია, უნდა იყოს TABLE-01 — გაასწორე და თავიდან გაგზავნე.“** `resend_corrected` აჩვენებს ცვლილებას (`lines[0].sku`), აგზავნის გასწორებულ შეკვეთას (ის მტკიცდება) და ორიგინალს უცვლელად `order-placed_dead`-ში ინახავს.
7. **„გააფორმე შეკვეთა: TV-55, 2 ცალი, 900 ლარი.“** 1000-ზე მეტი შეკვეთისთვის სერვისი დასტურს ითხოვს (`ApproveLargeOrder`).
8. **„არის დასადასტურებელი შეკვეთა? დაამტკიცე, დამამტკიცებელი: ანა.“** `pending_requests` request-ს აჩვენებს, `reply_order_approved` კი პასუხს უგზავნის სერვისს. სტატუსი ხდება `Approved by ანა`. უარყოფისთვის თქვით „უარყავი, მიზეზი: …“: გამოიყენება `reply_order_rejected` ან `reject_request`.
9. **„რა ღირს 2 კგ ამანათის გაგზავნა საქართველოში?“** `request_get_shipping_quote` ეკითხება მიწოდების სერვისს, რომელიც MassTransit-ს არ იყენებს: request უბრალო JSON-ია `shipping-quotes` რიგში, სერვისი კი `reply_to`-ზე იმავე `correlation_id`-ით პასუხობს (RabbitMQ RPC). ანტარქტიდა (`AQ`) `ShippingUnavailable`-ს იღებს.

ყველა მოქმედება rabbit-mcp-ის audit-ში იწერება. მისი ნახვა Claude-იდანაც შეიძლება: **„ვინ რა გადაიტანა dead-letter რიგში ბოლო საათში?“** (`recent_activity`) ან **„სად არის შეკვეთა …?“** (`find_message`). prompts `diagnose_errors`, `investigate_message`, `review_approvals` და `contract_health` ამ სცენარებს თავად გადის.

## როგორ არის აწყობილი

- **კონტრაქტები** (`Shop.Contracts/ShopContracts.cs`) ჩვეულებრივი record-ებია `[MessageContract]`-ით. ისინი DataAnnotations-ით მოწმდება: e-mail, SKU-ს ფორმატი, რაოდენობა 1–100. მოდელი არასწორ შეკვეთას ვერ გააგზავნის.
- **სერვისი** (`Shop.Service`) ჩვეულებრივი MassTransit აპლიკაციაა და rabbit-mcp-ის შესახებ არაფერი იცის. დასტურს `IRequestClient<ApproveLargeOrder>`-ით ითხოვს, პასუხს კი rabbit-mcp-ის responder იძლევა (`RabbitMcp__RespondTo=ApproveLargeOrder`).
- **მიწოდების სერვისი** (`Shop.Service/ShippingDesk.cs`) მხოლოდ RabbitMQ.Client-ს იყენებს, როგორც სხვა ენაზე დაწერილი სერვისი. მის კონტრაქტში წერია `Format = MessageFormat.Raw, RoutingKey = "shipping-quotes"`. request-ს, რომელსაც ვერ კითხულობს, უარყოფს და RabbitMQ-ის dead-lettering მას `shipping-quotes.dlq`-ში გადაიტანს. `peek_messages` აჩვენებს მიზეზს (`x-death`), `republish_from_error` კი უკან, `shipping-quotes`-ში აბრუნებს.
- **rabbit-mcp** კონტრაქტებს `Shop.Contracts.dll`-იდან ტვირთავს (`RabbitMcp__ContractAssemblies`). `PublishDeny=ApproveLargeOrder` უზრუნველყოფს, რომ მოდელი დასტურის მოთხოვნას თავად ვერ გააგზავნის, მხოლოდ უპასუხებს.
- **როლები:** `docker-compose.yml`-ში კომენტარად არის ნიმუში, თუ მეორე, მხოლოდ ნახვის უფლების მქონე გასაღები გჭირდებათ.

იგივე სცენარს CI ავტომატურად გადის: `scripts/sample-e2e.py`.

გაჩერება: `docker compose -f samples/Shop/docker-compose.yml down -v`.
