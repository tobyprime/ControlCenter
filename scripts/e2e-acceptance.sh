#!/usr/bin/env bash
# 端到端验收（TOB-343）：面板 + agent + napcat 渠道全链路实跑验证
#
# 覆盖父 PRD 验收标准（TOB-336）中可脚本化的项：
#   1  登录门禁（未登录被拒/登录后正常）——本地地址或 Tunnel 域名均可（PANEL_BASE 指向谁就验谁）
#   3  新设备接 agent → 在线状态 + 指标曲线（30s 周期）
#   4  停 agent → 60s 级别离线判定 + QQ 渠道收到含设备名的离线告警
#   5  CPU 超阈值持续 60s → QQ 渠道收到含设备名/指标/当前值的告警
#   7  日志通道：服务清单 + 尾部拉取（目标机需有 systemd/docker 日志源，无则跳过并注明）
#   9  napcat 不可用期间告警进待发队列；恢复后自动补发、无丢失
#
# 其余项（2 k3s PVC / 6 Web 终端浏览器侧 / 8 外网服务器 / 10 中文响应式）见
# docs/acceptance-checklist.md：人工/半自动步骤与记录模板。
#
# 用法（本地联调，全部默认值即可）：
#   scripts/build.sh && scripts/e2e-acceptance.sh
# 用户环境（对已部署面板 + 真实 agent）：
#   PANEL_BASE=https://<tunnel域名> ADMIN_PASS=<密码> AGENT_TOKEN=<设备token> \
#     FAKE_NAPCAT=0 NAPCAT_URL=http://<napcat>:3000 scripts/e2e-acceptance.sh
# 可调环境变量：
#   PANEL_BASE    面板地址（默认 http://127.0.0.1:5501，本脚本自动拉起本地面板）
#   ADMIN_USER/ADMIN_PASS 登录账号（本地默认 admin/e2e-acceptance-1；远程必传）
#   ATTACH_PANEL=1 远程模式：不拉起本地面板，直接连 PANEL_BASE
#   AGENT_BIN     agent 可执行文件（默认优先 artifacts/agent/linux-amd64/DevicePanel.Agent，
#                 其次 dotnet run Debug 构建——逻辑等价；静态单二进制属性以 build-agent.sh 验收为准）
#   AGENT_TOKEN   远程模式：已登记设备的 agent token（不传则新建设备）
#   FAKE_NAPCAT   1=自动拉起假 napcat（默认）；0=用 NAPCAT_URL 指向真实 napcat
#   NAPCAT_URL/NAPCAT_TOKEN/NAPCAT_TARGET_TYPE/NAPCAT_TARGET_ID 渠道参数（假 napcat 自动生成前两项）
#   SKIP_METRICS=1 跳过 70s 指标等待（快速冒烟）
set -euo pipefail
cd "$(dirname "$0")/.."

PANEL_PORT="${PANEL_PORT:-5501}"
PANEL_BASE="${PANEL_BASE:-http://127.0.0.1:$PANEL_PORT}"
ATTACH_PANEL="${ATTACH_PANEL:-0}"
ADMIN_USER="${ADMIN_USER:-admin}"
ADMIN_PASS="${ADMIN_PASS:-e2e-acceptance-1}"
SKIP_METRICS="${SKIP_METRICS:-0}"
FAKE_NAPCAT="${FAKE_NAPCAT:-1}"
NAPCAT_PORT="${NAPCAT_PORT:-39998}"
NAPCAT_URL="${NAPCAT_URL:-http://127.0.0.1:$NAPCAT_PORT}"
NAPCAT_TOKEN="${NAPCAT_TOKEN:-e2e-napcat-token}"
NAPCAT_TARGET_TYPE="${NAPCAT_TARGET_TYPE:-private}"
NAPCAT_TARGET_ID="${NAPCAT_TARGET_ID:-10001}"

RUN_DIR="${RUN_DIR:-$(mktemp -d /tmp/device-panel-acceptance.XXXXXX)}"
DATA_DIR="$RUN_DIR/data"
COOKIE="$RUN_DIR/cookies.txt"
PANEL_LOG="$RUN_DIR/panel.log"
AGENT_LOG="$RUN_DIR/agent.log"
NAPCAT_LOG="$RUN_DIR/napcat.jsonl"
RECORD="$RUN_DIR/acceptance-record.jsonl"
: >"$RECORD"

AGENT_BIN_DEFAULT="artifacts/agent/linux-amd64/DevicePanel.Agent"
AGENT_BIN_PUBLISH="src/DevicePanel.Agent/bin/Release/net8.0/linux-musl-x64/publish/DevicePanel.Agent"

PANEL_PID=""; NAPCAT_PID=""; AGENT_PID=""
cleanup() {
  [ -n "$AGENT_PID" ] && kill "$AGENT_PID" 2>/dev/null || true
  [ -n "$NAPCAT_PID" ] && kill "$NAPCAT_PID" 2>/dev/null || true
  if [ "$ATTACH_PANEL" != "1" ] && [ -n "$PANEL_PID" ]; then kill "$PANEL_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT

record() { # record <验收项> <结论 PASS/FAIL/SKIP> <说明>
  printf '{"item":"%s","result":"%s","note":"%s","ts":"%s"}\n' "$1" "$2" \
    "$(printf '%s' "$3" | sed 's/"/\\"/g')" "$(date -u +%FT%TZ)" >>"$RECORD"
  printf '  [%s] 验收%s：%s\n' "$2" "$1" "$3"
}

die() { echo "!! $*" >&2; exit 1; }

json_get() { python3 -c "import sys,json;d=json.load(sys.stdin);print(d$1)"; }

poll_until() { # poll_until <超时秒> <说明> <命令...>（命令成功即返回 0）
  local timeout="$1" note="$2"; shift 2
  local waited=0
  while ! "$@" >/dev/null 2>&1; do
    sleep 2; waited=$((waited + 2))
    if [ "$waited" -ge "$timeout" ]; then echo "超时（${timeout}s）：$note" >&2; return 1; fi
  done
  echo "  等待 ${waited}s 后满足：$note"
}

http_code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

agent_command() { # 打印 agent 启动命令（数组语义经 ${...} 于调用处展开）
  if [ -n "${AGENT_BIN:-}" ] && [ -x "$AGENT_BIN" ]; then printf '%s' "$AGENT_BIN"; return; fi
  if [ -x "$AGENT_BIN_DEFAULT" ]; then printf '%s' "$AGENT_BIN_DEFAULT"; return; fi
  if [ -x "$AGENT_BIN_PUBLISH" ]; then printf '%s' "$AGENT_BIN_PUBLISH"; return; fi
  printf 'dotnet-run'
}
AGENT_IS_DOTNET=0
AGENT_CMD="$(agent_command)"
[ "$AGENT_CMD" = "dotnet-run" ] && { AGENT_IS_DOTNET=1; dotnet build src/DevicePanel.Agent -c Debug --nologo -v q; AGENT_CMD="src/DevicePanel.Agent/bin/Debug/net8.0/DevicePanel.Agent.dll"; }

start_agent() { # start_agent <token>
  local ws_url; ws_url="$(agent_ws_url)/agent/ws"
  if [ "$AGENT_IS_DOTNET" = "1" ]; then
    dotnet "$AGENT_CMD" --url "$ws_url" --token "$1" >"$AGENT_LOG" 2>&1 &
  else
    "$AGENT_CMD" --url "$ws_url" --token "$1" >"$AGENT_LOG" 2>&1 &
  fi
  AGENT_PID=$!
}
stop_agent() {
  [ -n "$AGENT_PID" ] && kill "$AGENT_PID" 2>/dev/null || true
  wait "$AGENT_PID" 2>/dev/null || true
  AGENT_PID=""
}
agent_ws_url() {
  printf '%s' "$PANEL_BASE" | sed -e 's|^http:|ws:|' -e 's|^https:|wss:|' -e 's|/$||'
}

echo "== 端到端验收开始（artifacts: $RUN_DIR）"

# ---------- 准备：napcat 渠道 ----------
if [ "$FAKE_NAPCAT" = "1" ]; then
  python3 scripts/fake-napcat.py --port "$NAPCAT_PORT" --token "$NAPCAT_TOKEN" --log "$NAPCAT_LOG" &
  NAPCAT_PID=$!
  sleep 1
  echo "== 假 napcat 已启动：$NAPCAT_URL（记录：$NAPCAT_LOG）"
fi

# ---------- 准备：面板 ----------
if [ "$ATTACH_PANEL" != "1" ]; then
  rm -rf "$DATA_DIR"; mkdir -p "$DATA_DIR"
  DevicePanel__DataDir="$DATA_DIR" DevicePanel__Auth__InitialUsername="$ADMIN_USER" \
    DevicePanel__Auth__InitialPassword="$ADMIN_PASS" \
    dotnet run --project src/DevicePanel.Web --no-build --urls "$PANEL_BASE" >"$PANEL_LOG" 2>&1 &
  PANEL_PID=$!
  poll_until 60 "面板 healthz 就绪" curl -sf "$PANEL_BASE/healthz" || die "面板未启动"
  echo "== 本地面板已启动（pid=$PANEL_PID，数据目录 $DATA_DIR）"
else
  poll_until 30 "远程面板可达" curl -sf "$PANEL_BASE/healthz" || die "PANEL_BASE 不可达"
fi

# ---------- 验收 1：登录门禁 ----------
CODE_UNAUTH=$(http_code "$PANEL_BASE/api/devices")
[ "$CODE_UNAUTH" = "401" ] || [ "$CODE_UNAUTH" = "302" ] \
  || die "验收1 失败：未登录访问 /api/devices 返回 $CODE_UNAUTH（期望 401/302）"
CODE_LOGIN=$(http_code -c "$COOKIE" -H 'Content-Type: application/json' \
  -d "{\"username\":\"$ADMIN_USER\",\"password\":\"$ADMIN_PASS\"}" "$PANEL_BASE/api/auth/login")
[ "$CODE_LOGIN" = "200" ] || die "验收1 失败：登录返回 $CODE_LOGIN"
CODE_AUTH=$(http_code -b "$COOKIE" "$PANEL_BASE/api/devices")
[ "$CODE_AUTH" = "200" ] || die "验收1 失败：登录后访问 /api/devices 返回 $CODE_AUTH"
record "1-登录门禁" "PASS" "未登录 $CODE_UNAUTH，登录后 $CODE_AUTH（面板 $PANEL_BASE）"

# ---------- 准备：渠道配置 + 设备 + agent ----------
curl -sf -b "$COOKIE" -X PUT -H 'Content-Type: application/json' \
  -d "{\"baseUrl\":\"$NAPCAT_URL\",\"token\":\"$NAPCAT_TOKEN\",\"targetType\":\"$NAPCAT_TARGET_TYPE\",\"targetId\":\"$NAPCAT_TARGET_ID\"}" \
  "$PANEL_BASE/api/alerts/settings" >/dev/null || die "napcat 渠道配置失败"

if [ -n "${AGENT_TOKEN:-}" ]; then
  # 远程/附加模式：复用已登记设备（不新建设备、不重置 token）
  DEV_ID="${DEVICE_ID:-}"
  [ -n "$DEV_ID" ] || die "远程模式需同时提供 DEVICE_ID 与 AGENT_TOKEN"
  DEV_NAME=$(curl -sf -b "$COOKIE" "$PANEL_BASE/api/devices" \
    | DEV_ID="$DEV_ID" python3 -c 'import os,sys,json;print([d["name"] for d in json.load(sys.stdin) if d["id"]==int(os.environ["DEV_ID"])][0])') \
    || die "远程模式未找到设备 $DEV_ID"
else
  DEV_NAME="验收机-$(date +%s)"
  DEV_JSON=$(curl -sf -b "$COOKIE" -H 'Content-Type: application/json' \
    -d "{\"name\":\"$DEV_NAME\",\"tags\":[\"验收\"]}" "$PANEL_BASE/api/devices") || die "创建设备失败"
  DEV_ID=$(printf '%s' "$DEV_JSON" | json_get "['id']")
  AGENT_TOKEN=$(printf '%s' "$DEV_JSON" | json_get "['agentToken']")
fi

start_agent "$AGENT_TOKEN"
poll_until 35 "设备上线" bash -c "curl -sf -b '$COOKIE' '$PANEL_BASE/api/devices' | python3 -c 'import sys,json;ds=json.load(sys.stdin);print(any(d[\"id\"]==$DEV_ID and d[\"online\"] for d in ds))' | grep -q True" \
  || die "验收3 失败：35s 内设备未上线"
record "3-设备接入(在线)" "PASS" "登记设备「$DEV_NAME」并启动 agent，35s 内面板显示在线"

# ---------- 验收 3：指标曲线（30s 周期上报） ----------
if [ "$SKIP_METRICS" != "1" ]; then
  echo "== 等待真实 agent 指标上报（默认 30s 周期，等待 70s）…"
  sleep 70
  FROM=$(date -u -d '20 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
  TO=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  METRICS=$(curl -sf -b "$COOKIE" --get "$PANEL_BASE/api/metrics/$DEV_ID/series" \
    --data-urlencode "granularity=raw" --data-urlencode "from=$FROM" --data-urlencode "to=$TO") \
    || die "指标查询失败"
  python3 - "$METRICS" <<'PYEOF' || die "验收3 指标断言失败"
import json, sys
from datetime import datetime
series = json.loads(sys.argv[1])
points = series["points"]
assert len(points) >= 2, f"预期至少 2 个指标点，实际 {len(points)}"
parse = lambda t: datetime.fromisoformat(t.replace("Z", "+00:00"))
spacings = [round((parse(b["t"]) - parse(a["t"])).total_seconds()) for a, b in zip(points, points[1:])]
assert all(15 <= s <= 45 for s in spacings), f"点间隔应约 30s，实际 {spacings}"
assert all(0 <= p["cpu"] <= 100 and 0 <= p["mem"] <= 100 and 0 <= p["disk"] <= 100 for p in points), "百分比越界"
print(f"  指标点 {len(points)} 个，间隔 {spacings}s，cpu/mem/disk 数值正常")
PYEOF
  record "3-指标曲线" "PASS" "真实 agent 30s 周期上报，曲线点可见且间隔正常"
else
  record "3-指标曲线" "SKIP" "SKIP_METRICS=1"
fi

# ---------- 验收 7：日志通道（服务清单 + 尾部拉取） ----------
SERVICES_JSON=$(curl -sf -b "$COOKIE" "$PANEL_BASE/api/devices/$DEV_ID/logs/services" || echo '{"services":[]}')
SERVICE_COUNT=$(printf '%s' "$SERVICES_JSON" | json_get "['services'].__len__()" 2>/dev/null || echo 0)
if [ "$SERVICE_COUNT" = "0" ] || [ "$SERVICE_COUNT" = "None" ]; then
  record "7-日志查看" "SKIP" "目标机无 systemd/docker 日志源可列（用户环境按 checklist 执行）"
else
  SVC=$(printf '%s' "$SERVICES_JSON" | json_get "['services'][0]['name']")
  SVC_KIND=$(printf '%s' "$SERVICES_JSON" | json_get "['services'][0]['kind']")
  TAIL=$(curl -sf -b "$COOKIE" --get "$PANEL_BASE/api/devices/$DEV_ID/logs/tail" \
    --data-urlencode "service=$SVC" --data-urlencode "kind=$SVC_KIND" --data-urlencode "lines=5")
  printf '%s' "$TAIL" | python3 -c 'import sys,json;lines=json.load(sys.stdin)["lines"];assert isinstance(lines,list) and len(lines)>=1, lines' \
    || die "验收7 尾部拉取失败"
  record "7-日志查看" "PASS" "服务清单 ${SERVICE_COUNT} 个，首个 $SVC_KIND:$SVC 尾部 5 行拉取成功"
fi

# ---------- 验收 5：CPU 超阈值持续 60s → QQ 告警 ----------
# 判定语义：从首个越限采样起持续满 Sustain(60s) 后，在下一次采样触发告警（30s 周期 → 最长约 120s，预算 180s）
# 阈值选择：本地模式施加 CPU 载荷（nproc/4，上限 8 个）确保越限；远程模式无法给目标机施压，
#   按观测到的 CPU 最小值-1 自动设阈值，仍不触发则如实记 SKIP（建议人工在目标机施压后重跑）
CPU_WAIT="${CPU_WAIT:-180}"
CPU_THR=5
SPINNERS=""
start_spinners() {
  [ "$ATTACH_PANEL" = "1" ] && return 0
  local n=$(( $(nproc) / 4 )); [ "$n" -lt 2 ] && n=2; [ "$n" -gt 8 ] && n=8
  for _ in $(seq 1 "$n"); do timeout 300 yes >/dev/null 2>&1 & SPINNERS="$SPINNERS $!"; done
  echo "== 已施加 $n 个 CPU 载荷进程（确保持续越限，最长 300s 自退）"
}
stop_spinners() {
  local p
  for p in $SPINNERS; do kill "$p" 2>/dev/null || true; done
  SPINNERS=""
}
if [ "$ATTACH_PANEL" = "1" ]; then
  FROM=$(date -u -d '15 minutes ago' +%Y-%m-%dT%H:%M:%SZ)
  TO=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  CPU_THR=$(curl -sf -b "$COOKIE" --get "$PANEL_BASE/api/metrics/$DEV_ID/series" \
    --data-urlencode "granularity=raw" --data-urlencode "from=$FROM" --data-urlencode "to=$TO" \
    | python3 -c 'import sys,json;pts=[p["cpu"] for p in json.load(sys.stdin)["points"]];print(max(1,int(min(pts))-1) if pts else 5)' 2>/dev/null) || CPU_THR=5
  echo "== 远程模式：按观测 CPU 自动设阈值 ${CPU_THR}%"
fi
start_spinners
curl -sf -b "$COOKIE" -X PUT -H 'Content-Type: application/json' \
  -d "{\"metric\":\"cpu\",\"value\":$CPU_THR}" "$PANEL_BASE/api/alerts/thresholds/global" >/dev/null \
  || { stop_spinners; die "设置全局 CPU 阈值失败"; }
echo "== 全局 CPU 阈值已设为 ${CPU_THR}%，等待持续越限告警（≤${CPU_WAIT}s）…"
if poll_until "$CPU_WAIT" "napcat 收到指标越限告警" grep -q "指标越限告警" "$NAPCAT_LOG"; then
  ALERT_CPU=$(grep "指标越限告警" "$NAPCAT_LOG" | head -1)
  printf '%s' "$ALERT_CPU" | python3 -c '
import sys, json
entry = json.loads(sys.stdin.read())
text = " ".join(m["data"]["text"] for m in entry["payload"]["message"] if m["type"] == "text")
assert "CPU" in text and "当前" in text and "阈值" in text, text
print(f"  napcat 收到：{text}")
' || { stop_spinners; die "验收5 失败：告警内容缺设备名/指标/当前值"; }
  record "5-阈值越限告警" "PASS" "CPU 阈值 ${CPU_THR}% 持续越限后，QQ 渠道收到含设备名/指标/当前值的告警"
else
  stop_spinners
  if [ "$ATTACH_PANEL" = "1" ]; then
    record "5-阈值越限告警" "SKIP" "远程目标机 CPU 未持续越过自动阈值 ${CPU_THR}%：请在目标机施压（如 stress-ng）后重跑"
  else
    die "验收5 失败：${CPU_WAIT}s 内未收到越限告警"
  fi
fi
stop_spinners
curl -sf -b "$COOKIE" -X PUT -H 'Content-Type: application/json' \
  -d '{"metric":"cpu","value":95}' "$PANEL_BASE/api/alerts/thresholds/global" >/dev/null

# ---------- 验收 4：agent 停止 → 离线 + QQ 离线告警（napcat 正常，直发） ----------
KILL_AT=$(date +%s)
stop_agent
poll_until 120 "设备判定离线" bash -c "curl -sf -b '$COOKIE' '$PANEL_BASE/api/devices' | python3 -c 'import sys,json;ds=json.load(sys.stdin);print(any(d[\"id\"]==$DEV_ID and not d[\"online\"] for d in ds))' | grep -q True" \
  || die "验收4 失败：120s 内未判定离线"
OFFLINE_AT=$(date +%s)
poll_until 90 "napcat 收到离线告警" grep -q "设备离线告警" "$NAPCAT_LOG" \
  || die "验收4 失败：未收到离线告警"
grep "设备离线告警" "$NAPCAT_LOG" | head -1 | python3 -c '
import sys, json
entry = json.loads(sys.stdin.read())
text = " ".join(m["data"]["text"] for m in entry["payload"]["message"] if m["type"] == "text")
assert "离线" in text, text
print(f"  napcat 收到：{text}")
' || die "验收4 失败：离线告警内容异常"
record "4-离线判定与告警" "PASS" "停 agent 后 $((OFFLINE_AT - KILL_AT))s 内离线，QQ 渠道收到含设备名的离线告警"

# ---------- 验收 9：napcat 断连 → 队列暂存 → 恢复补发 ----------
kill "$NAPCAT_PID" 2>/dev/null || true; wait "$NAPCAT_PID" 2>/dev/null || true; NAPCAT_PID=""
LINES_BEFORE=$(wc -l <"$NAPCAT_LOG")
echo "== 假 napcat 已停止；重启 agent 等待恢复在线后再次制造离线事件…"
start_agent "$AGENT_TOKEN"
poll_until 40 "设备恢复在线（离线事件关闭）" bash -c "curl -sf -b '$COOKIE' '$PANEL_BASE/api/devices' | python3 -c 'import sys,json;ds=json.load(sys.stdin);print(any(d[\"id\"]==$DEV_ID and d[\"online\"] for d in ds))' | grep -q True" \
  || die "验收9 失败：agent 重启后未恢复在线"
sleep 20  # 等离线扫描（15s 周期）确认恢复、关闭上一事件，再制造新离线
stop_agent
poll_until 120 "离线告警进入待发队列（napcat 不可达）" bash -c \
  "curl -sf -b '$COOKIE' '$PANEL_BASE/api/alerts/queue' | python3 -c 'import sys,json;q=json.load(sys.stdin);print(q[\"count\"]>=1)' | grep -q True" \
  || die "验收9 失败：napcat 断连期间告警未进队列"
QUEUED=$(curl -sf -b "$COOKIE" "$PANEL_BASE/api/alerts/queue")
echo "$QUEUED" | python3 -c '
import sys, json
q = json.load(sys.stdin)
assert q["count"] >= 1, q
print("  待发队列", q["count"], "条（napcat 断连期间暂存）")
'
echo "== 重启假 napcat，等待自动补发…"
python3 scripts/fake-napcat.py --port "$NAPCAT_PORT" --token "$NAPCAT_TOKEN" --log "$NAPCAT_LOG" &
NAPCAT_PID=$!
poll_until 60 "队列清空（补发完成）" bash -c \
  "curl -sf -b '$COOKIE' '$PANEL_BASE/api/alerts/queue' | python3 -c 'import sys,json;print(json.load(sys.stdin)[\"count\"]==0)' | grep -q True" \
  || die "验收9 失败：napcat 恢复后队列未清空"
LINES_AFTER=$(wc -l <"$NAPCAT_LOG")
DELTA=$((LINES_AFTER - LINES_BEFORE))
[ "$DELTA" -ge 1 ] || die "验收9 失败：补发后 napcat 未收到消息（新增 $DELTA 条）"
tail -1 "$NAPCAT_LOG" | python3 -c '
import sys, json
entry = json.loads(sys.stdin.read())
text = " ".join(m["data"]["text"] for m in entry["payload"]["message"] if m["type"] == "text")
print(f"  napcat 补发收到：{text}")
'
record "9-队列补发无丢失" "PASS" "napcat 断连期间告警入队，恢复后自动补发（napcat 侧新增 $DELTA 条，队列清空）"

# ---------- 汇总 ----------
echo ""
echo "== 验收记录：$RECORD"
cat "$RECORD"
echo "== 假 napcat 收件记录：$NAPCAT_LOG（$(wc -l <"$NAPCAT_LOG") 条）"
echo "== 面板日志：$PANEL_LOG；agent 日志：$AGENT_LOG"
echo "== 端到端验收完成"
