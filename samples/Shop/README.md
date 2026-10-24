# Sample: a shop

**English** · [ქართული](README.ka.md)

This sample shows rabbit-mcp next to a real MassTransit service on PostgreSQL: a catalog with stock, orders with their
history, a transactional outbox for the shop's own events and a notification service that e-mails customers. The model
places orders, asks for their status, recovers failed messages, parks poison messages and approves large orders. A
shipping desk written without MassTransit answers quotes over plain RabbitMQ.

```
Shop.Contracts   contracts: OrderPlaced, GetOrderStatus → OrderStatus | OrderNotFound,
                 ApproveLargeOrder → OrderApproved | OrderRejected,
                 GetShippingQuote → ShippingQuote | ShippingUnavailable (Format = Raw),
                 events published by the shop: OrderConfirmed, OrderCancelled
Shop.Service     MassTransit 8 service on PostgreSQL (EF Core): takes orders (catalog check, stock reservation),
                 answers status requests, asks for approval of large orders, publishes OrderConfirmed/OrderCancelled
                 through its outbox; CustomerNotifications consumes them and e-mails the customer;
                 ShippingDesk: plain RabbitMQ.Client, no MassTransit, answers shipping quotes
docker-compose   RabbitMQ + PostgreSQL + Shop.Service + rabbit-mcp (HTTP, with Shop.Contracts.dll)
```

## Run it

Only Docker is needed. From the repository folder:

```bash
docker compose -f samples/Shop/docker-compose.yml up -d --build
claude mcp add --transport http shop http://localhost:5080/mcp \
  --header "X-Api-Key: shop-demo-key-change-me-0123456789abcdef"
```

RabbitMQ UI: http://localhost:15672 (`shop` / `shop`). Database: `psql postgresql://shop:shop@localhost:5432/shop`
(tables `Products`, `Orders`, `OrderLines`, `OrderHistory`, `Emails`, MassTransit's `OutboxMessage`/`InboxState`). All ports are bound to `localhost` only. Set
`SHOP_MCP_KEY=...` before starting to use another key.

## Walkthrough

Ask Claude, in this order:

1. **"Which contracts do we have and who consumes them?"** — `list_contracts` and `contract_consumers`. `OrderPlaced`,
   `GetOrderStatus` and `GetShippingQuote` are `ok` because Shop.Service consumes them; so are the shop's own events
   `OrderConfirmed` and `OrderCancelled` (queue `customer-notifications`).
2. **"What is the status of order 5ca1ab1e-0000-4000-8000-000000000001?"** — the service answers
   `request_get_order_status`: `Shipped`.
3. **"Place an order: ana@example.ge, FLAKY-LAMP, 1 piece, 20."** — `publish_order_placed` sends it. A `FLAKY-` item
   fails the first time, as if the inventory service were briefly down.
4. **"What is in order-placed_error? Bring it back."** — `peek_messages` shows the exception, and
   `republish_from_error` (preview first, then confirmation) processes the order again. Its status becomes `Approved`,
   the shop publishes `OrderConfirmed` and the order history shows "Confirmation e-mail sent to ana@example.ge".
5. **"Place an order: BROKEN-CHAIR, 1 piece, 50. Why did it fail? Move it to the dead-letter queue."** — the SKU is not
   in the catalog, so it always fails and republishing does not help. `dead_letter_from_error` moves it to `order-placed_dead` with the
   reason. Nothing is deleted: the exception details stay, and `republish_from_error` can bring it back.
6. **"Place an order: BROKEN-TABLE, 1 piece, 80. The SKU is wrong, it should be TABLE-01 — fix it and send it again."** —
   `resend_corrected` shows the change (`lines[0].sku`), sends the corrected order (it is approved) and parks the
   original unchanged in `order-placed_dead`.
7. **"Place an order: TV-55, 2 pieces, 900."** — above 1000 the service asks for approval (`ApproveLargeOrder`).
8. **"Is there anything to approve? Approve it, approver: Ana."** — `pending_requests` shows the request and
   `reply_order_approved` answers the service. The status becomes `Approved by Ana`. To refuse, say "reject it, reason:
   …": `reply_order_rejected` or `reject_request` is used; the stock is released and the customer gets a cancellation
   e-mail.
8b. **"Place an order: VASE-01, 3 pieces, 35."** — only one vase is in stock: the order is `Cancelled`
   ("Out of stock: VASE-01 (1 left)") and `OrderCancelled` e-mails the customer.
9. **"How much is shipping a 2 kg parcel to Georgia?"** — `request_get_shipping_quote` asks the shipping desk, which
   does not use MassTransit: the request is plain JSON in the `shipping-quotes` queue, and the desk replies to
   `reply_to` with the same `correlation_id` (RabbitMQ RPC). Antarctica (`AQ`) gets `ShippingUnavailable`.

Everything rabbit-mcp does is audited, and you can ask Claude about it too: **"What was moved to a dead-letter queue in
the last hour?"** (`recent_activity`) or **"Where is order …?"** (`find_message`). The prompts `diagnose_errors`,
`investigate_message`, `review_approvals` and `contract_health` do these walkthroughs for you.

## How it is built

- **Contracts** (`Shop.Contracts/ShopContracts.cs`) are ordinary records with `[MessageContract]`, checked with
  DataAnnotations (e-mail, SKU format, quantity 1–100): the model cannot send an invalid order.
- **The service** (`Shop.Service`) is a plain MassTransit application that knows nothing about rabbit-mcp. It asks for
  approval with an `ApproveLargeOrder` request; rabbit-mcp's responder answers (`RabbitMcp__RespondTo=ApproveLargeOrder`).
- **The database** (`ShopDb.cs`, `Database.cs`): PostgreSQL through EF Core. An order, its stock reservation and the
  event it causes are saved in one transaction with MassTransit's EF Core outbox, so an event is never published for an
  order that was not saved; the inbox skips messages already processed. Two orders for the last items: PostgreSQL's
  `xmin` detects it and only that race is retried — every other failure goes to the `_error` queue for a person. The
  schema is created on start (`EnsureCreated`); a production service would ship EF Core migrations.
- **Approvals survive restarts**: `ApprovalWorker` reads orders waiting for approval from the database and asks again
  after a restart.
- **Notifications** (`CustomerNotificationsConsumer`): its own queue `customer-notifications`, so a slow e-mail
  provider never holds up orders. The sample stores e-mails in the `Emails` table instead of sending them.
- **The shipping desk** (`Shop.Service/ShippingDesk.cs`) uses only RabbitMQ.Client, like a service in another language
  would. Its contract says `Format = MessageFormat.Raw, RoutingKey = "shipping-quotes"`. Requests it cannot read are
  rejected into `shipping-quotes.dlq` by RabbitMQ dead-lettering; `peek_messages` shows why (`x-death`) and
  `republish_from_error` sends them back to `shipping-quotes`.
- **rabbit-mcp** loads the contracts from `Shop.Contracts.dll` (`RabbitMcp__ContractAssemblies`).
  `PublishDeny=ApproveLargeOrder;OrderConfirmed;OrderCancelled` makes sure the model only answers approval requests and
  never sends one itself, nor fakes the shop's own events (it can still read and search them).
- **Roles:** `docker-compose.yml` has a commented example for a second, read-only key.

CI walks through the same scenario automatically: `scripts/sample-e2e.py`.

Stop it: `docker compose -f samples/Shop/docker-compose.yml down -v`.
