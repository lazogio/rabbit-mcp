# Sample: a shop

**English** · [ქართული](README.ka.md)

This sample shows rabbit-mcp next to a real MassTransit service: the model places orders, asks for their status,
recovers failed messages, parks poison messages and approves large orders.

```
Shop.Contracts   contracts: OrderPlaced, GetOrderStatus → OrderStatus | OrderNotFound,
                 ApproveLargeOrder → OrderApproved | OrderRejected
Shop.Service     MassTransit 8 service: takes orders, answers status requests, asks for approval of large orders
docker-compose   RabbitMQ + Shop.Service + rabbit-mcp (HTTP, with Shop.Contracts.dll)
```

## Run it

Only Docker is needed. From the repository folder:

```bash
docker compose -f samples/Shop/docker-compose.yml up -d --build
claude mcp add --transport http shop http://localhost:5080/mcp \
  --header "X-Api-Key: shop-demo-key-change-me-0123456789abcdef"
```

RabbitMQ UI: http://localhost:15672 (`shop` / `shop`). All ports are bound to `localhost` only. Set
`SHOP_MCP_KEY=...` before starting to use another key.

## Walkthrough

Ask Claude, in this order:

1. **"Which contracts do we have and who consumes them?"** — `list_contracts` and `contract_consumers`. `OrderPlaced`
   and `GetOrderStatus` are `ok` because Shop.Service consumes them.
2. **"What is the status of order 5ca1ab1e-0000-4000-8000-000000000001?"** — the service answers
   `request_get_order_status`: `Shipped`.
3. **"Place an order: ana@example.ge, FLAKY-LAMP, 1 piece, 20."** — `publish_order_placed` sends it. A `FLAKY-` item
   fails the first time, as if the inventory service were briefly down.
4. **"What is in order-placed_error? Bring it back."** — `peek_messages` shows the exception, and
   `republish_from_error` (preview first, then confirmation) processes the order again. Its status becomes `Approved`.
5. **"Place an order: BROKEN-CHAIR, 1 piece, 50. Why did it fail? Move it to the dead-letter queue."** — this item
   always fails, so republishing does not help. `dead_letter_from_error` moves it to `order-placed_dead` with the
   reason. Nothing is deleted: the exception details stay, and `republish_from_error` can bring it back.
6. **"Place an order: TV-55, 2 pieces, 900."** — above 1000 the service asks for approval (`ApproveLargeOrder`).
7. **"Is there anything to approve? Approve it, approver: Ana."** — `pending_requests` shows the request and
   `reply_order_approved` answers the service. The status becomes `Approved by Ana`. To refuse, say "reject it, reason:
   …": `reply_order_rejected` or `reject_request` is used.

Everything rabbit-mcp does is audited, and you can ask Claude about it too: **"What was moved to a dead-letter queue in
the last hour?"** (`recent_activity`) or **"Where is order …?"** (`find_message`). The prompts `diagnose_errors`,
`investigate_message`, `review_approvals` and `contract_health` do these walkthroughs for you.

## How it is built

- **Contracts** (`Shop.Contracts/ShopContracts.cs`) are ordinary records with `[MessageContract]`, checked with
  DataAnnotations (e-mail, SKU format, quantity 1–100): the model cannot send an invalid order.
- **The service** (`Shop.Service`) is a plain MassTransit application that knows nothing about rabbit-mcp. It asks for
  approval with `IRequestClient<ApproveLargeOrder>`; rabbit-mcp's responder answers (`RabbitMcp__RespondTo=ApproveLargeOrder`).
- **rabbit-mcp** loads the contracts from `Shop.Contracts.dll` (`RabbitMcp__ContractAssemblies`).
  `PublishDeny=ApproveLargeOrder` makes sure the model only answers approval requests and never sends one itself.
- **Roles:** `docker-compose.yml` has a commented example for a second, read-only key.

CI walks through the same scenario automatically: `scripts/sample-e2e.py`.

Stop it: `docker compose -f samples/Shop/docker-compose.yml down -v`.
