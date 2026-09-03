#!/usr/bin/env bash
# 端到端冒烟：面板 + 静态 agent 二进制联调（验收 1/2/3 部分证据）
set -e
cd "$(dirname "$0")/.."

PORT=5500
BASE="http://127.0.0.1:$PORT"
DATA_DIR=/tmp/multica-task-1351828826/opencode/e2e-data
AGENT_BIN=src/DevicePanel.Agent/bin/Release/net8.0/linux-musl-x64/publish/DevicePanel.Agent
COOKIE=/tmp/multica-task-1351828826/opencode/e2e-cookies.txt
LOG=/tmp/multica-task-1351828826/opencode/e2e-panel.log

rm -rf "$DATA_DIR" "$COOKIE"
mkdir -p "$DATA_DIR"

DevicePanel__DataDir="$DATA_DIR" DevicePanel__Auth__InitialPassword=e2e-pass-1 \
  dotnet run --project src/DevicePanel.Web --no-build --urls "$BASE" >"$LOG" 2>&1 &
PANEL_PID=$!
trap 'kill $PANEL_PID 2>/dev/null || true' EXIT

for i in $(seq 1 60); do curl -sf "$BASE/healthz" >/dev/null && break; sleep 1; done
echo "== panel up (healthz ok), pid=$PANEL_PID"

curl -sf -c "$COOKIE" -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"e2e-pass-1"}' "$BASE/api/auth/login" >/dev/null
echo "== logged in"

RESP=$(curl -sf -b "$COOKIE" -H 'Content-Type: application/json' \
  -d '{"name":"E2E 冒烟设备","tags":["机房E2E","冒烟"]}' "$BASE/api/devices")
echo "== created: $RESP"
TOKEN=$(echo "$RESP" | python3 -c 'import sys,json;print(json.load(sys.stdin)["agentToken"])')
DEV_ID=$(echo "$RESP" | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')

echo "== bad-token agent (expect auth failure, exit 3) =="
"$AGENT_BIN" --url "ws://127.0.0.1:$PORT/agent/ws" --token dpk_bad-token-will-fail || echo "exit=$? (expected 3)"

echo "== starting real agent with valid token =="
"$AGENT_BIN" --url "ws://127.0.0.1:$PORT/agent/ws" --token "$TOKEN" \
  >/tmp/multica-task-1351828826/opencode/e2e-agent.log 2>&1 &
AGENT_PID=$!

sleep 5
ONLINE=$(curl -sf -b "$COOKIE" "$BASE/api/devices" | python3 -c "import sys,json;d=[x for x in json.load(sys.stdin) if x['id']==$DEV_ID][0];print(d['online'])")
echo "== online after 5s: $ONLINE (criterion: within 30s)"

echo "== agent listening sockets (criterion: none, outbound only) =="
ss -ltnp 2>/dev/null | grep -c "pid=$AGENT_PID" || echo "0 listening sockets for agent pid $AGENT_PID"

KILL_AT=$(date +%s)
kill "$AGENT_PID" 2>/dev/null || true
wait "$AGENT_PID" 2>/dev/null || true

echo "== polling device status after agent stop (criterion: offline within 60-90s) =="
while true; do
  NOW=$(date +%s)
  ELAPSED=$((NOW - KILL_AT))
  STATE=$(curl -sf -b "$COOKIE" "$BASE/api/devices" | python3 -c "import sys,json;d=[x for x in json.load(sys.stdin) if x['id']==$DEV_ID][0];print(d['online'])")
  echo "t+${ELAPSED}s online=$STATE"
  if [ "$STATE" = "False" ]; then echo "== OFFLINE at t+${ELAPSED}s"; break; fi
  if [ "$ELAPSED" -gt 110 ]; then echo "== TIMEOUT: never went offline"; exit 1; fi
  sleep 5
done

echo "== panel log (agent access evidence) =="
grep -E "已接入|连接结束|认证失败" "$LOG" | tail -6
echo "== agent output =="
cat /tmp/multica-task-1351828826/opencode/e2e-agent.log