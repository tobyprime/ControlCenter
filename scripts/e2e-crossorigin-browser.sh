#!/usr/bin/env bash
# 跨站浏览器端到端（TOB-357 审查问题 1 的回归锚点）：以「独立前端源 + 独立后端源」
# 两个不同站点（https://panel.test:5173 前端静态源 / https://api.test:5443 后端，
# HTTPS 自签证书，等价 Cloudflare Pages + Tunnel 目标形态）在真实 Chrome 中跑
# 登录 → 设备 → 指标 → 终端 → 日志全链路，验证跨站会话 Cookie（SameSite=None;
# Secure）的存储与携带——fetch 不带凭据时本测试必红（登录后所有 /api 401）。
# 与 scripts/e2e-acceptance.sh（同源内嵌形态）互补。
#
# 前置：dotnet SDK、node、google-chrome（或 CHROME_PATH 指定）、openssl、python3、curl；
#       首次运行需要写 /etc/hosts（root，结束时自动移除）。
# 用法：scripts/e2e-crossorigin-browser.sh
# 可调：SKIP_BUILD=1 跳过前端 pages 构建；WORK=<dir> 保留现场目录。
set -euo pipefail
cd "$(dirname "$0")/.."

PANEL_HOST=panel.test; PANEL_PORT=5173
API_HOST=api.test; API_PORT=5443
PANEL_ORIGIN="https://$PANEL_HOST:$PANEL_PORT"
API_ORIGIN="https://$API_HOST:$API_PORT"
AGENT_PORT=5500
DEV_NAME=${DEV_NAME:-跨站E2E设备}
ADMIN_PASS=cross-e2e-pass-1
WORK=${WORK:-$(mktemp -d /tmp/dp-cross-e2e.XXXXXX)}
mkdir -p "$WORK"
HOSTS_MARK="dp-cross-e2e"

echo "== 现场：$WORK"

# 测试站点必须直连本机：绕开环境代理（https_proxy 对未列入 NO_PROXY 的主机名生效）
export NO_PROXY="127.0.0.1,localhost,$PANEL_HOST,$API_HOST${NO_PROXY:+,$NO_PROXY}"
export no_proxy="$NO_PROXY"

# ---- /etc/hosts：两个测试站点指向本机（结束时还原；只看文件本身，不做 DNS 查询）----
restore_hosts() { sed -i "\#$HOSTS_MARK#d" /etc/hosts 2>/dev/null || true; }
if ! grep -q "$HOSTS_MARK" /etc/hosts; then
  echo "127.0.0.1 $PANEL_HOST $API_HOST $HOSTS_MARK" >> /etc/hosts
  restore_hosts_on_exit=1
fi
cleanup() {
  [ "${restore_hosts_on_exit:-0}" = 1 ] && restore_hosts
  kill "${AGENT_PID:-0}" "${PANEL_PID:-0}" 2>/dev/null || true
}
trap cleanup EXIT

# ---- 自签证书（SAN 覆盖两个站点）----
openssl req -x509 -newkey rsa:2048 -keyout "$WORK/key.pem" -out "$WORK/cert.pem" \
  -days 2 -nodes -subj "/CN=device-panel-cross-e2e" \
  -addext "subjectAltName=DNS:$API_HOST,DNS:$PANEL_HOST" 2>/dev/null

# ---- 前端 pages 构建（VITE_API_BASE_URL 指向后端站点，绝对地址注入）----
if [ "${SKIP_BUILD:-0}" != 1 ]; then
  echo "== 前端 pages 构建（VITE_API_BASE_URL=$API_ORIGIN）"
  ( cd frontend
    if [ ! -d node_modules ]; then
      npm ci --no-audit --no-fund || npm ci --no-audit --no-fund \
        --proxy http://127.0.0.1:7897 --https-proxy http://127.0.0.1:7897
    fi
    VITE_API_BASE_URL="$API_ORIGIN" npm run build:pages )
fi

# ---- 后端：HTTPS api.test:5443（浏览器走，锁 Http1）+ HTTP 127.0.0.1:5500（agent 回连走，免自签信任）----
# HTTPS 端点锁 Http1：Chrome 对 h2 连接用扩展 CONNECT 发起 WebSocket（RFC 8441），Kestrel
# 不支持会 405；生产形态经 cloudflared 以 HTTP 回源不存在该问题，测试环境锁协议即可。
# 用 Kestrel 配置式命名端点：ASPNETCORE_URLS 端点不认 EndpointDefaults 的协议覆盖。
echo "== 启动后端（Cookie SameSite=None + CORS 允许 $PANEL_ORIGIN）"
dotnet build src/DevicePanel.Web -c Debug --nologo -v q
DevicePanel__DataDir="$WORK/data" DevicePanel__Auth__InitialPassword="$ADMIN_PASS" \
DevicePanel__Auth__SessionCookieSameSite=None \
DevicePanel__Cors__AllowedOrigins="$PANEL_ORIGIN" \
Kestrel__Endpoints__Https__Url="https://$API_HOST:$API_PORT" Kestrel__Endpoints__Https__Protocols=Http1 \
Kestrel__Endpoints__Agent__Url="http://127.0.0.1:$AGENT_PORT" Kestrel__Endpoints__Agent__Protocols=Http1 \
ASPNETCORE_Kestrel__Certificates__Default__Path="$WORK/cert.pem" \
ASPNETCORE_Kestrel__Certificates__Default__KeyPath="$WORK/key.pem" \
  dotnet run --project src/DevicePanel.Web --no-build -c Debug >"$WORK/panel.log" 2>&1 &
PANEL_PID=$!

for i in $(seq 1 60); do
  curl -skf -m 3 "https://127.0.0.1:$API_PORT/healthz" >/dev/null 2>&1 && break
  sleep 1
done
curl -skf -m 5 "$API_ORIGIN/healthz" >/dev/null
curl -sf -m 5 "http://127.0.0.1:$AGENT_PORT/healthz" >/dev/null
echo "== 后端就绪（https + http 双端点）"

# ---- 登记设备并启动真实 agent（HTTP 端点回连）----
curl -skf -m 5 -c "$WORK/admin.jar" -H 'Content-Type: application/json' \
  -d "{\"username\":\"admin\",\"password\":\"$ADMIN_PASS\"}" "$API_ORIGIN/api/auth/login" >/dev/null
DEV_JSON=$(curl -skf -m 5 -b "$WORK/admin.jar" -H 'Content-Type: application/json' \
  -d "{\"name\":\"$DEV_NAME\",\"tags\":[\"跨站E2E\"]}" "$API_ORIGIN/api/devices")
TOKEN=$(echo "$DEV_JSON" | python3 -c 'import sys,json;print(json.load(sys.stdin)["agentToken"])')

AGENT_BIN=${AGENT_BIN:-}
if [ -n "$AGENT_BIN" ] && [ -x "$AGENT_BIN" ]; then
  "$AGENT_BIN" --url "ws://127.0.0.1:$AGENT_PORT/agent/ws" --token "$TOKEN" >"$WORK/agent.log" 2>&1 &
else
  dotnet build src/DevicePanel.Agent -c Debug --nologo -v q
  dotnet src/DevicePanel.Agent/bin/Debug/net8.0/DevicePanel.Agent.dll \
    --url "ws://127.0.0.1:$AGENT_PORT/agent/ws" --token "$TOKEN" >"$WORK/agent.log" 2>&1 &
fi
AGENT_PID=$!

echo "== 等待 agent 在线（≤45s）"
AGENT_ONLINE=0
for i in $(seq 1 45); do
  ONLINE=$(curl -skf -m 3 -b "$WORK/admin.jar" "$API_ORIGIN/api/devices" | \
    python3 -c "import sys,json;d=[x for x in json.load(sys.stdin) if x['name']=='$DEV_NAME'];print(d[0]['online'] if d else 'missing')" 2>/dev/null || echo error)
  if [ "$ONLINE" = "True" ]; then AGENT_ONLINE=1; break; fi
  sleep 1
done
[ "$AGENT_ONLINE" = 1 ] || { echo "✗ agent 未上线"; tail -20 "$WORK/agent.log"; exit 1; }
echo "== agent 在线"

# ---- playwright 可用性：全局安装优先，缺失则临时装 playwright-core ----
export NODE_PATH=/usr/local/lib/node_modules
if ! node -e "require('playwright')" >/dev/null 2>&1; then
  echo "== 全局无 playwright，临时安装 playwright-core 到 $WORK/pw"
  npm i --prefix "$WORK/pw" playwright-core --no-audit --no-fund >/dev/null 2>&1 \
    || npm i --prefix "$WORK/pw" playwright-core --no-audit --no-fund \
      --proxy http://127.0.0.1:7897 --https-proxy http://127.0.0.1:7897
  export PW_CORE_DIR="$WORK/pw"
fi
CHROME=${CHROME_PATH:-$(command -v google-chrome-stable || command -v google-chrome || true)}
[ -n "$CHROME" ] || { echo "✗ 未找到 Chrome（可 CHROME_PATH= 指定）"; exit 2; }

# ---- 浏览器全链路 ----
PANEL_ORIGIN="$PANEL_ORIGIN" API_ORIGIN="$API_ORIGIN" PANEL_PORT="$PANEL_PORT" \
DIST_DIR="$PWD/frontend/dist" TLS_CERT="$WORK/cert.pem" TLS_KEY="$WORK/key.pem" \
DEV_NAME="$DEV_NAME" ADMIN_PASS="$ADMIN_PASS" WORK_DIR="$WORK" CHROME_PATH="$CHROME" \
  node scripts/e2e-crossorigin-browser.mjs

echo "== 跨站浏览器端到端完成，现场保留：$WORK"
