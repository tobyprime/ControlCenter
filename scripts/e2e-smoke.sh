#!/usr/bin/env bash
# 端到端冒烟：面板 + 静态 agent 二进制联调（TOB-337 在线/离线/token 验收 + TOB-338 指标上报验收）
set -e
cd "$(dirname "$0")/.."

PORT=5500
BASE="http://127.0.0.1:$PORT"
SMOKE_DIR=${SMOKE_DIR:-$(mktemp -d /tmp/device-panel-smoke.XXXXXX)}
DATA_DIR="$SMOKE_DIR/e2e-data"
AGENT_BIN=${AGENT_BIN:-src/DevicePanel.Agent/bin/Release/net8.0/linux-musl-x64/publish/DevicePanel.Agent}
COOKIE="$SMOKE_DIR/e2e-cookies.txt"
LOG="$SMOKE_DIR/e2e-panel.log"
AGENT_LOG="$SMOKE_DIR/e2e-agent.log"

# 静态 musl 二进制优先（TOB-337 验收产物）；缺失时回退 Debug 构建经 dotnet 运行（同一套 AgentRunner 逻辑）。
# 回退仅用于本机逻辑联调：静态单二进制/零依赖属性以 build-agent.sh + file 验收为准。
AGENT_CMD=("$AGENT_BIN")
if [ ! -x "$AGENT_BIN" ]; then
  echo "== 未找到静态 agent（$AGENT_BIN），回退 Debug 构建运行（逻辑等价）"
  dotnet build src/DevicePanel.Agent -c Debug --nologo -v q
  AGENT_CMD=(dotnet src/DevicePanel.Agent/bin/Debug/net8.0/DevicePanel.Agent.dll)
fi

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
"${AGENT_CMD[@]}" --url "ws://127.0.0.1:$PORT/agent/ws" --token dpk_bad-token-will-fail || echo "exit=$? (expected 3)"

echo "== starting real agent with valid token =="
"${AGENT_CMD[@]}" --url "ws://127.0.0.1:$PORT/agent/ws" --token "$TOKEN" >"$AGENT_LOG" 2>&1 &
AGENT_PID=$!

sleep 5
ONLINE=$(curl -sf -b "$COOKIE" "$BASE/api/devices" | python3 -c "import sys,json;d=[x for x in json.load(sys.stdin) if x['id']==$DEV_ID][0];print(d['online'])")
echo "== online after 5s: $ONLINE (criterion: within 30s)"

echo "== agent listening sockets (criterion: none, outbound only) =="
ss -ltnp 2>/dev/null | grep -c "pid=$AGENT_PID" || echo "0 listening sockets for agent pid $AGENT_PID"

echo "== collecting metrics from real agent (default 30s cadence, wait 70s; criterion: points visible within 5 min, ~30s spacing) =="
sleep 70
FROM=$(date -u -d '20 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
TO=$(date -u +%Y-%m-%dT%H:%M:%SZ)
METRICS_JSON=$(curl -sf -b "$COOKIE" --get "$BASE/api/metrics/$DEV_ID/series" \
  --data-urlencode "granularity=raw" --data-urlencode "from=$FROM" --data-urlencode "to=$TO")
echo "$METRICS_JSON" | python3 -c '
import json, sys
from datetime import datetime

series = json.load(sys.stdin)
points = series["points"]
print("granularity=%s, points=%d" % (series["granularity"], len(points)))
for p in points:
    print("  t=%s cpu=%s%% mem=%s%% disk=%s%% netRx=%sB/s netTx=%sB/s" % (p["t"], p["cpu"], p["mem"], p["disk"], p["netRx"], p["netTx"]))
assert len(points) >= 2, "预期至少 2 个指标点（70s @ 30s 周期），实际 %d" % len(points)
parse = lambda t: datetime.fromisoformat(t.replace("Z", "+00:00"))
spacings = [round((parse(b["t"]) - parse(a["t"])).total_seconds()) for a, b in zip(points, points[1:])]
print("相邻点间隔（秒）: %s" % spacings)
assert all(15 <= s <= 45 for s in spacings), "间隔应约 30s（15-45s 容差），实际 %s" % spacings
assert all(0 <= p["cpu"] <= 100 and 0 <= p["mem"] <= 100 and 0 <= p["disk"] <= 100 for p in points), "百分比应在 0-100"
print("== metrics acceptance OK: points within ~30s spacing ==")
'

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
cat "$AGENT_LOG"
echo "== smoke artifacts: $SMOKE_DIR"
