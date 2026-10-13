#!/usr/bin/env bash
# Starts a published rabbit-mcp binary and talks MCP to it over stdio, like a real client:
# initialize -> initialized -> tools/list. Fails if stdout contains anything that is not
# JSON-RPC (stdout corruption breaks every MCP client) or expected tools are missing.
# No broker is needed: the server connects to RabbitMQ lazily on the first tool call.
#
# usage: scripts/smoke-test.sh <path-to-binary>
set -euo pipefail

BIN=${1:?usage: smoke-test.sh <path-to-binary>}
OUT=$(mktemp)
ERR=$(mktemp)
trap 'rm -f "$OUT" "$ERR"' EXIT

export RabbitMcp__EnablePublish=true
export RabbitMcp__EnableRepublish=true

{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke-test","version":"1.0"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  sleep 5   # keep stdin open until the server has answered
} | timeout 30 "$BIN" >"$OUT" 2>"$ERR" || true

python3 - "$OUT" "$ERR" <<'PY'
import json, sys

out, err = sys.argv[1], sys.argv[2]
lines = [l for l in open(out, encoding="utf-8").read().splitlines() if l.strip()]
problems, responses = [], {}

for i, line in enumerate(lines, 1):
    try:
        msg = json.loads(line)
    except json.JSONDecodeError:
        problems.append(f"stdout line {i} is not JSON (stdout must carry only MCP): {line[:200]}")
        continue
    if msg.get("jsonrpc") != "2.0":
        problems.append(f"stdout line {i} is not JSON-RPC 2.0: {line[:200]}")
    if "id" in msg:
        responses[msg["id"]] = msg

init = responses.get(1, {}).get("result")
if not init:
    problems.append(f"no initialize result (got {responses.get(1)})")
else:
    print(f"server: {init['serverInfo']['name']} {init['serverInfo']['version']}, protocol {init['protocolVersion']}")

tools = {t["name"]: t for t in responses.get(2, {}).get("result", {}).get("tools", [])}
expected = {"list_contracts", "validate_message", "queue_stats", "peek_messages", "republish_from_error",
            "publish_order_submitted", "publish_order_cancelled", "request_get_order_status"}
missing = expected - tools.keys()
if missing:
    problems.append(f"missing tools: {sorted(missing)} (got {sorted(tools)})")
for name in expected & tools.keys():
    if "outputSchema" not in tools[name]:
        problems.append(f"tool {name} has no outputSchema")

if problems:
    print("SMOKE TEST FAILED", *problems, "--- stderr ---", open(err, encoding="utf-8").read()[-3000:], sep="\n")
    sys.exit(1)
print(f"ok: {len(tools)} tools, all with outputSchema; stdout is clean JSON-RPC")
PY
