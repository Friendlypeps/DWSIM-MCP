#!/usr/bin/env bash
# End-to-end smoke test against a running server (HTTP transport): health, initialize, tools/list,
# one tool call. Usage: scripts/smoke.sh [http://localhost:5901] [token] [min_tools]
set -euo pipefail
BASE="${1:-http://localhost:5901}"; TOKEN="${2:-}"; MIN_TOOLS="${3:-90}"
AUTH=(); [ -n "$TOKEN" ] && AUTH=(-H "X-MCP-Token: $TOKEN")
rpc() { curl -sS "${AUTH[@]}" -H 'Content-Type: application/json' -d "$1" "$BASE/mcp"; }

for i in $(seq 1 60); do curl -fs "${AUTH[@]}" "$BASE/health" >/dev/null 2>&1 && break; sleep 2; done
curl -fs "${AUTH[@]}" "$BASE/health"; echo

init=$(rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}')
echo "$init" | grep -q 'dwsim_column_create' || { echo "instructions do not mention dwsim_column_create"; echo "$init" | head -c 600; exit 1; }
echo "initialize: ok ($(echo "$init" | grep -o '"version":"[^"]*"' | head -1))"

list=$(rpc '{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
count=$(echo "$list" | grep -o '"name":"dwsim_[a-z0-9_]*"' | sort -u | wc -l)
echo "tools/list: $count tools"
[ "$count" -ge "$MIN_TOOLS" ] || { echo "expected at least $MIN_TOOLS tools"; exit 1; }

created=$(rpc '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dwsim_flowsheet_create","arguments":{"name":"smoke"}}}')
echo "$created" | grep -q 'flowsheet_id' || { echo "dwsim_flowsheet_create failed: $created"; exit 1; }
echo "tools/call dwsim_flowsheet_create: ok"

if [ -n "$TOKEN" ]; then
  code=$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","id":4,"method":"ping"}' "$BASE/mcp")
  [ "$code" = "401" ] || { echo "expected 401 without token, got $code"; exit 1; }
  echo "token: enforced"
fi
echo "smoke: PASS"
