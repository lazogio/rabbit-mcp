#!/usr/bin/env python3
# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
"""
Walks through the Shop sample the way a model would, over MCP Streamable HTTP:
  1. the shop service is consuming (contract_consumers) and the responder is listening (pending_requests)
  2. request_get_order_status finds the demo order
  3. publish_order_placed with a FLAKY- item → order-placed_error → peek → republish_from_error → Approved
     and a BROKEN- item → dead_letter_from_error → parked in order-placed_dead (nothing deleted)
  4. publish_order_placed above the limit → pending_requests → reply_order_approved → Approved
  5. another large order → reject_request → Rejected with the reason

usage: scripts/sample-e2e.py <mcp-url> <api-key>
"""
import json
import sys
import time
import urllib.request
import uuid

URL, KEY = sys.argv[1], sys.argv[2]
DEMO_ORDER = "5ca1ab1e-0000-4000-8000-000000000001"
_ids = iter(range(1, 1_000_000))


def rpc(method, params=None):
    body = {"jsonrpc": "2.0", "id": next(_ids), "method": method}
    if params is not None:
        body["params"] = params
    request = urllib.request.Request(URL, data=json.dumps(body).encode(), method="POST", headers={
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "X-Api-Key": KEY,
    })
    with urllib.request.urlopen(request, timeout=90) as response:
        raw = response.read().decode()
    # Streamable HTTP answers with JSON or an SSE stream ("data: {...}").
    data = [line[5:].strip() for line in raw.splitlines() if line.startswith("data:")]
    message = json.loads(data[-1] if data else raw)
    if "error" in message:
        raise SystemExit(f"{method} failed: {message['error']}")
    return message["result"]


def call(tool, **arguments):
    result = rpc("tools/call", {"name": tool, "arguments": arguments})
    if result.get("isError"):
        raise SystemExit(f"{tool} returned an error: {result['content'][0]['text']}")
    return result["structuredContent"]


def eventually(what, check, timeout=60):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = check()
        if last:
            return last
        time.sleep(1)
    raise SystemExit(f"timed out: {what} (last: {last})")


def status_of(order_id):
    result = call("request_get_order_status", message={"orderId": order_id})
    return result["response"] if result["status"] == "responded" and result["responseType"] == "OrderStatus" else None


def place(order_id, sku, quantity, price):
    return call("publish_order_placed", message={
        "orderId": order_id, "customerEmail": "ana@example.ge",
        "lines": [{"sku": sku, "quantity": quantity, "unitPrice": price}],
    })


def ready(probe):
    """eventually() probe that treats connection errors (container still starting) as 'not yet'."""
    def attempt():
        try:
            return probe()
        except (OSError, SystemExit):
            return None
    return attempt


def step(text):
    print(f"• {text}", flush=True)


def main():
    # --- 0. the stack is up
    eventually("MCP endpoint", ready(lambda: rpc("initialize", {
        "protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "sample-e2e", "version": "1"}})), 180)
    tools = {t["name"] for t in rpc("tools/list")["tools"]}
    expected = {"publish_order_placed", "request_get_order_status", "peek_messages", "republish_from_error",
                "contract_consumers", "pending_requests", "reply_order_approved", "reply_order_rejected", "reject_request", "dead_letter_from_error"}
    assert expected <= tools, f"missing tools: {expected - tools}"
    assert "request_approve_large_order" not in tools, "PublishDeny should hide request_approve_large_order"
    step(f"{len(tools)} tools, including the shop's contracts")

    def shop_consuming():
        statuses = {c["contract"]: c["status"] for c in call("contract_consumers")["contracts"]}
        return statuses.get("OrderPlaced") == "ok" and statuses.get("GetOrderStatus") == "ok"


    eventually("shop service consuming OrderPlaced and GetOrderStatus", shop_consuming, 120)
    eventually("responder listening", lambda: call("pending_requests")["status"] == "listening")
    step("shop service is consuming, responder is listening")

    # --- 1. request/response
    demo = eventually("demo order", lambda: status_of(DEMO_ORDER))
    assert demo["status"] == "Shipped", demo
    step(f"request_get_order_status: demo order is {demo['status']}")

    # --- 2. _error queue and republish
    flaky = str(uuid.uuid4())
    place(flaky, "FLAKY-LAMP", 1, 20)
    # The error queue only exists after MassTransit's first fault, so "does not exist" just means "not yet".
    peeked = eventually("FLAKY order in order-placed_error", ready(lambda: next((
        m for m in call("peek_messages", queue="order-placed_error", count=10)["messages"]
        if (m.get("payload") or {}).get("orderId") == flaky), None)))
    assert "Inventory service unavailable" in (peeked.get("fault") or {}).get("exceptionMessage", ""), peeked
    step("FLAKY order failed and is in order-placed_error with the exception")

    republished = call("republish_from_error", errorQueue="order-placed_error", count=10, confirm=True,
                       messageIds=[peeked["messageId"]])
    assert republished["republished"] == 1, republished
    order = eventually("republished order approved", lambda: (s := status_of(flaky)) and s["status"] == "Approved" and s)
    step(f"republish_from_error: the order was processed again → {order['status']}")

    # --- 2b. a poison message goes to the dead-letter queue, nothing is deleted
    broken = str(uuid.uuid4())
    place(broken, "BROKEN-CHAIR", 1, 50)
    poison = eventually("BROKEN order in order-placed_error", ready(lambda: next((
        m for m in call("peek_messages", queue="order-placed_error", count=10)["messages"]
        if (m.get("payload") or {}).get("orderId") == broken), None)))
    preview = call("dead_letter_from_error", errorQueue="order-placed_error", count=10)
    assert preview["executed"] is False and preview["deadLetterQueue"] == "order-placed_dead", preview
    parked = call("dead_letter_from_error", errorQueue="order-placed_error", count=10, confirm=True,
                  messageIds=[poison["messageId"]], reason="Product no longer exists")
    assert parked["moved"] == 1, parked
    dead = eventually("parked message in order-placed_dead", ready(lambda: next((
        m for m in call("peek_messages", queue="order-placed_dead", count=10)["messages"]
        if m.get("messageId") == poison["messageId"]), None)))
    assert dead["headers"].get("X-Dead-Letter-Reason") == "Product no longer exists", dead["headers"]
    assert "no longer exists" in (dead.get("fault") or {}).get("exceptionMessage", ""), dead
    step("dead_letter_from_error: the poison message is parked in order-placed_dead with its fault and the reason")

    # --- 3. responder: the shop asks, the model answers
    large = str(uuid.uuid4())
    place(large, "TV-55", 2, 900)
    pending = eventually("approval request", lambda: next((
        r for r in call("pending_requests", contract="ApproveLargeOrder", waitSeconds=10)["requests"]
        if r["message"]["orderId"] == large), None))
    assert pending["check"]["valid"] and "reply_order_approved" in pending["replyTools"], pending
    step(f"pending_requests: the shop asks to approve {pending['message']['total']} for {large[:8]}…")

    reply = call("reply_order_approved", requestId=pending["requestId"],
                 response={"orderId": large, "approvedBy": "sample-e2e"})
    assert reply["status"] == "replied", reply
    order = eventually("large order approved", lambda: (s := status_of(large)) and s["status"] == "Approved" and s)
    assert "sample-e2e" in order.get("note", ""), order
    step(f"reply_order_approved → shop stored: {order['note']}")

    # --- 4. reject
    rejected = str(uuid.uuid4())
    place(rejected, "SOFA-3", 1, 2500)
    pending = eventually("second approval request", lambda: next((
        r for r in call("pending_requests", contract="ApproveLargeOrder", waitSeconds=10)["requests"]
        if r["message"]["orderId"] == rejected), None))
    call("reject_request", requestId=pending["requestId"], reason="Customer is over the credit limit")
    order = eventually("order rejected", lambda: (s := status_of(rejected)) and s["status"] == "Rejected" and s)
    assert "credit limit" in order.get("note", ""), order
    step(f"reject_request → shop stored: {order['status']} ({order['note']})")

    print("ok: the Shop sample works end to end over MCP")


if __name__ == "__main__":
    try:
        main()
    except (SystemExit, AssertionError, OSError) as failure:
        if isinstance(failure, SystemExit) and failure.code in (0, None):
            raise
        message = str(failure) or type(failure).__name__
        # GitHub Actions annotation, so the reason is visible without opening the log.
        print(f"::error title=sample-e2e::{message[:3000]}".replace("\n", "%0A"))
        sys.exit(1)
