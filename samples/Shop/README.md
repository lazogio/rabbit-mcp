# Sample: a shop

**English** · [ქართული](README.ka.md)

rabbit-mcp next to a real MassTransit service on PostgreSQL. The model places orders, checks their status, recovers
failed messages and approves large orders.

```
Shop.Contracts   OrderPlaced, GetOrderStatus, ApproveLargeOrder, GetShippingQuote (raw JSON),
                 events of the shop: OrderConfirmed, OrderCancelled
Shop.Service     MassTransit 8 + PostgreSQL (EF Core, outbox/inbox): catalog and stock, orders, approvals,
                 customer e-mails (CustomerNotifications); ShippingDesk — plain RabbitMQ.Client, no MassTransit
docker-compose   RabbitMQ + PostgreSQL + Shop.Service + rabbit-mcp (HTTP)
```

## Run it

```bash
docker compose -f samples/Shop/docker-compose.yml up -d --build
claude mcp add --transport http shop http://localhost:5080/mcp \
  --header "X-Api-Key: shop-demo-key-change-me-0123456789abcdef"
```

RabbitMQ UI: http://localhost:15672 (`shop`/`shop`). Database: `psql postgresql://shop:shop@localhost:5432/shop`.
Stop: `docker compose -f samples/Shop/docker-compose.yml down -v`.

## Ask Claude

| Ask | What happens |
|---|---|
| "Which contracts do we have and who consumes them?" | `list_contracts`, `contract_consumers`: everything is `ok` |
| "Status of order 5ca1ab1e-0000-4000-8000-000000000001?" | `request_get_order_status` → `Shipped`, with its history |
| "Place an order: ana@example.ge, FLAKY-LAMP, 1 piece, 20." then "Bring it back from order-placed_error." | fails once (warehouse down); `republish_from_error` → `Approved`, confirmation e-mail |
| "Place an order: BROKEN-CHAIR, 1 piece, 50. Park it." | not in the catalog, always fails; `dead_letter_from_error` → `order-placed_dead` |
| "Place an order: BROKEN-TABLE, 1 piece, 80. Fix the SKU to TABLE-01 and resend." | `resend_corrected` sends the fix, parks the original |
| "Place an order: TV-55, 2 pieces, 900." then "Approve it, approver: Ana." | above 1000 the shop asks; `pending_requests` → `reply_order_approved` |
| "Place an order: VASE-01, 3 pieces, 35." | only one in stock → `Cancelled`, cancellation e-mail |
| "Shipping for 2 kg to Georgia?" | `request_get_shipping_quote` → the plain RabbitMQ desk answers over RPC |
| "What was moved to a dead-letter queue in the last hour?" | `recent_activity` from the audit |
| "Keep a copy of every new order in shop-audit-orders." | `declare_queue` with the `order-copy` template: a queue bound to `Shop.Contracts:OrderPlaced` |

## How it is built

- **Contracts** are records with `[MessageContract]` and DataAnnotations: the model cannot send an invalid order.
- **The service** knows nothing about rabbit-mcp. Its approval request (`ApproveLargeOrder`) is answered by rabbit-mcp's
  responder (`RabbitMcp__RespondTo`).
- **Outbox:** an order, its stock reservation and its event are saved in one PostgreSQL transaction; only races for the
  same stock are retried, every other failure goes to `order-placed_error`. The schema is created on start; a
  production service would use migrations.
- **`PublishDeny=ApproveLargeOrder;OrderConfirmed;OrderCancelled`:** the model answers approvals but cannot send one
  or fake the shop's events.
- **Topology:** `TopologyAllow=shop-audit-*` limits what the model may create; the `order-copy` template fixes the
  binding, so the model only picks a name.

CI walks through the same scenario: `scripts/sample-e2e.py`.
