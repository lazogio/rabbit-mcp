#!/usr/bin/env bash
# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
# Starts a published rabbit-mcp binary with the HTTP transport and talks MCP to it like a remote client:
# health check, 401 without credentials, initialize + tools/list with an API key.
# usage: scripts/smoke-test-http.sh <path-to-binary>   or   scripts/smoke-test-http.sh docker:<image>
set -euo pipefail

BIN=${1:?usage: smoke-test-http.sh <path-to-binary>}
PORT=${SMOKE_HTTP_PORT:-5089}
KEY="smoke-key-$(head -c 24 /dev/urandom | base64 | tr -d '/+=')"
LOG=$(mktemp)

export RabbitMcp__Transport=http RabbitMcp__HttpUrls="http://127.0.0.1:$PORT" \
       RabbitMcp__ApiKeys="smoke:$KEY" RabbitMcp__EnablePublish=true
if [[ "$BIN" == docker:* ]]; then
  CONTAINER="rabbit-mcp-smoke-$$"
  # A published port, not --network host: on Docker Desktop host networking does not reach the Mac or Windows host.
  docker run --rm --name "$CONTAINER" -p "127.0.0.1:$PORT:$PORT" \
    -e RabbitMcp__Transport -e RabbitMcp__HttpUrls="http://0.0.0.0:$PORT" -e RabbitMcp__ApiKeys -e RabbitMcp__EnablePublish \
    "${BIN#docker:}" >"$LOG" 2>&1 &
else
  "$BIN" >"$LOG" 2>&1 &
fi
PID=$!
trap 'kill $PID 2>/dev/null || true; [ -n "${CONTAINER:-}" ] && docker rm -f "$CONTAINER" >/dev/null 2>&1; rm -f "$LOG"' EXIT

for _ in $(seq 1 60); do
  curl -fs "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  kill -0 $PID 2>/dev/null || { echo "server exited:"; cat "$LOG"; exit 1; }
  sleep 0.5
done

mcp() {  # mcp <json> [extra curl args...]
  local body=$1; shift
  curl -s -X POST "http://127.0.0.1:$PORT/mcp" \
    -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' "$@" -d "$body"
}
INIT='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"http-smoke","version":"1.0"}}}'
LIST='{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

STATUS=$(mcp "$INIT" -o /dev/null -w '%{http_code}')
[ "$STATUS" = "401" ] || { echo "expected 401 without credentials, got $STATUS"; cat "$LOG"; exit 1; }

INIT_OUT=$(mcp "$INIT" -H "X-Api-Key: $KEY")
LIST_OUT=$(mcp "$LIST" -H "Authorization: Bearer $KEY")

python3 - "$INIT_OUT" "$LIST_OUT" <<'PY'
import json, sys

def payload(raw):
    # Streamable HTTP answers either with JSON or with an SSE stream ("data: {...}" lines).
    lines = [l[5:].strip() for l in raw.splitlines() if l.startswith("data:")]
    return json.loads(lines[-1] if lines else raw)

init, tools = payload(sys.argv[1]), payload(sys.argv[2])
names = {t["name"] for t in tools["result"]["tools"]}
missing = {"list_contracts", "publish_order_submitted", "request_get_order_status"} - names
if missing:
    sys.exit(f"HTTP SMOKE TEST FAILED: missing tools {sorted(missing)}")
print(f"ok: {init['result']['serverInfo']['name']} {init['result']['serverInfo']['version']} over HTTP, "
      f"401 without key, {len(names)} tools with key")
PY
