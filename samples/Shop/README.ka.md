# ნიმუში: მაღაზია

[English](README.md) · **ქართული**

rabbit-mcp ნამდვილ MassTransit სერვისთან ერთად, PostgreSQL-ზე. მოდელი შეკვეთებს აფორმებს, მათ სტატუსს ამოწმებს,
ჩავარდნილ შეტყობინებებს აღადგენს და დიდ შეკვეთებს ამტკიცებს.

```
Shop.Contracts   OrderPlaced, GetOrderStatus, ApproveLargeOrder, GetShippingQuote (raw JSON),
                 მაღაზიის event-ები: OrderConfirmed, OrderCancelled
Shop.Service     MassTransit 8 + PostgreSQL (EF Core, outbox/inbox): კატალოგი და მარაგი, შეკვეთები, დასტურები,
                 მომხმარებლის e-mail-ები (CustomerNotifications); ShippingDesk — უბრალო RabbitMQ.Client, MassTransit-ის გარეშე
docker-compose   RabbitMQ + PostgreSQL + Shop.Service + rabbit-mcp (HTTP)
```

## გაშვება

```bash
docker compose -f samples/Shop/docker-compose.yml up -d --build
claude mcp add --transport http shop http://localhost:5080/mcp \
  --header "X-Api-Key: shop-demo-key-change-me-0123456789abcdef"
```

RabbitMQ UI: http://localhost:15672 (`shop`/`shop`). ბაზა: `psql postgresql://shop:shop@localhost:5432/shop`.
გაჩერება: `docker compose -f samples/Shop/docker-compose.yml down -v`.

## ჰკითხეთ Claude-ს

| თხოვნა | რა ხდება |
|---|---|
| „რა კონტრაქტები გვაქვს და ვინ იღებს მათ?“ | `list_contracts`, `contract_consumers`: ყველა `ok`-ია |
| „რა სტატუსი აქვს შეკვეთას 5ca1ab1e-0000-4000-8000-000000000001?“ | `request_get_order_status` → `Shipped`, ისტორიით |
| „გააფორმე შეკვეთა: ana@example.ge, FLAKY-LAMP, 1 ცალი, 20.“ შემდეგ „დააბრუნე order-placed_error-იდან.“ | ერთხელ ვარდება (საწყობი გათიშულია); `republish_from_error` → `Approved`, დადასტურების e-mail |
| „გააფორმე შეკვეთა: BROKEN-CHAIR, 1 ცალი, 50. გადადე.“ | კატალოგში არ არის, ყოველთვის ვარდება; `dead_letter_from_error` → `order-placed_dead` |
| „გააფორმე შეკვეთა: BROKEN-TABLE, 1 ცალი, 80. SKU გაასწორე TABLE-01-ზე და თავიდან გაგზავნე.“ | `resend_corrected` აგზავნის გასწორებულს, ორიგინალს გადადებს |
| „გააფორმე შეკვეთა: TV-55, 2 ცალი, 900.“ შემდეგ „დაამტკიცე, დამამტკიცებელი: ანა.“ | 1000-ზე მეტზე მაღაზია კითხულობს; `pending_requests` → `reply_order_approved` |
| „გააფორმე შეკვეთა: VASE-01, 3 ცალი, 35.“ | მარაგში ერთია → `Cancelled`, გაუქმების e-mail |
| „რა ღირს 2 კგ-ის გაგზავნა საქართველოში?“ | `request_get_shipping_quote` → უბრალო RabbitMQ სერვისი RPC-ით პასუხობს |
| „რა გადავიდა dead-letter რიგში ბოლო საათში?“ | `recent_activity` audit-იდან |

## როგორ არის აწყობილი

- **კონტრაქტები** `[MessageContract]`-იანი record-ებია DataAnnotations-ით: მოდელი არასწორ შეკვეთას ვერ გააგზავნის.
- **სერვისმა** rabbit-mcp-ის შესახებ არაფერი იცის. მის დასტურის მოთხოვნას (`ApproveLargeOrder`) rabbit-mcp-ის
  responder პასუხობს (`RabbitMcp__RespondTo`).
- **Outbox:** შეკვეთა, მარაგის დაჯავშნა და მისი event ერთ PostgreSQL ტრანზაქციაში ინახება; მეორდება მხოლოდ ერთსა და
  იმავე მარაგზე შეჯიბრი, ყველა სხვა ჩავარდნა `order-placed_error`-ში მიდის. სქემა გაშვებისას იქმნება; production
  სერვისი migrations-ს გამოიყენებდა.
- **`PublishDeny=ApproveLargeOrder;OrderConfirmed;OrderCancelled`:** მოდელი დასტურებს პასუხობს, მაგრამ თავად ვერც
  მოთხოვნას გააგზავნის და ვერც მაღაზიის event-ებს გააყალბებს.

იგივე სცენარს CI გადის: `scripts/sample-e2e.py`.
