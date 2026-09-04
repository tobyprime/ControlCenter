# k3s 部署指南（容器化 + Cloudflare Tunnel）

交付物对应 Multica issue TOB-342：面板容器镜像、k3s manifests、部署文档。目标形态：k3s 单副本部署，SQLite 数据 PVC 持久化，经 Cloudflare Tunnel 提供外网访问（登录认证由面板自带），轻量 agent 在目标设备上出站回连接入。

## 架构

```
浏览器 ── Cloudflare Tunnel（外网入口，未登录 302 → /login）
           │
           ▼
   cloudflared（用户环境已有，方案 A；或随项目部署，方案 B）
           │  http://device-panel.controlcenter.svc:80
           ▼
   k3s: device-panel Deployment（单副本，非 root，:8080 /healthz）
           │  PVC device-panel-data（/data）
           ▼
   SQLite（WAL）：设备台账 / 指标 / 终端留痕 / 告警设置

目标设备：DevicePanel.Agent（amd64/arm64 静态单二进制，零入站端口）
          ── wss://<面板域名>/agent/ws 出站回连 ──▶ device-panel
```

## 前置条件

- k3s 集群，本机 `kubectl` 可管理（`k3s kubectl` 或已导入 kubeconfig）
- 构建机装有 Docker（构建镜像）；或直接使用已推送仓库的镜像
- （QQ 告警）napcat 服务地址 + token + 通知目标（QQ 号/群号）
- 外网入口：已有 Cloudflare Tunnel（方案 A），或按方案 B 部署

## 1. 构建镜像

```bash
# 前端构建 + 后端发布均在镜像内多阶段完成，可重复执行
scripts/build-image.sh controlcenter-panel:local
# 构建容器无法直连外网的环境（npm/NuGet 拉取失败时）：
DOCKER_BUILD_NETWORK=host scripts/build-image.sh controlcenter-panel:local
```

导入 k3s（无镜像仓库时）：

```bash
docker save controlcenter-panel:local | sudo k3s ctr images import -
```

或推送到镜像仓库后，修改 `deploy/k3s/30-deployment.yaml` 的 `image` 字段。

## 2. 创建 Secret

敏感配置不落仓库，用 `kubectl` 直接创建（`deploy/k3s/21-secret.yaml` 仅为字段模板）：

```bash
kubectl -n controlcenter create secret generic device-panel-secret \
  --from-literal=DevicePanel__Auth__InitialUsername=admin \
  --from-literal=DevicePanel__Auth__InitialPassword='<强密码>' \
  --from-literal=DevicePanel__Alert__Napcat__BaseUrl='http://<napcat 地址>:3000' \
  --from-literal=DevicePanel__Alert__Napcat__Token='<napcat token>' \
  --from-literal=DevicePanel__Alert__Napcat__TargetType=group \
  --from-literal=DevicePanel__Alert__Napcat__TargetId='<QQ 群号或私聊 QQ>'
```

注入语义：

- 初始账号/密码：仅用户表为空（首次启动）时创建；之后改 Secret 不影响已有账号。
- napcat 地址/token/目标：仅面板 KV 设置为空时种子写入，之后以「告警设置」页 UI 保存为准；Secret 只填 BaseUrl/Token 时可只种对应项（TargetType 取值 `private`/`group`）。

## 3. 部署到 k3s

```bash
kubectl apply -f deploy/k3s/
kubectl -n controlcenter get pods -w   # 等待 device-panel Running 且 READY 1/1
```

集群内验证：

```bash
kubectl -n controlcenter port-forward svc/device-panel 18080:80
curl -s http://localhost:18080/healthz        # {"status":"ok"}
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:18080/api/devices/   # 401 = 未登录被拒
# 浏览器打开 http://localhost:18080 → 302 到 /login → 用 Secret 里的账号登录
```

## 4. 接入 Cloudflare Tunnel

### 方案 A（默认交付形态）：使用用户环境已有的 cloudflared

在现有 cloudflared 配置中增加一条 ingress 指向集群内 Service：

```yaml
ingress:
  - hostname: panel.example.com
    service: http://device-panel.controlcenter.svc.cluster.local:80
  - service: http_status:404
```

（cloudflared 与 k3s 同主机时，也可用 `http://localhost:<NodePort>` 形式；Service 已按 ClusterIP 交付，跨主机场景建议 cloudflared 以 token 方式运行在该 k3s 集群内。）

### 方案 B（备选）：cloudflared 随项目容器化部署

适用于用户环境尚无 Tunnel，或希望 Tunnel 与面板同生命周期管理：

```bash
kubectl -n controlcenter create secret generic cloudflared-token \
  --from-literal=TUNNEL_TOKEN='<Cloudflare Zero Trust 控制台获取>'
```

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: cloudflared
  namespace: controlcenter
spec:
  replicas: 1
  selector:
    matchLabels: { app: cloudflared }
  template:
    metadata:
      labels: { app: cloudflared }
    spec:
      containers:
        - name: cloudflared
          image: cloudflare/cloudflared:latest
          args: ["tunnel", "--no-autoupdate", "run"]
          env:
            - name: TUNNEL_TOKEN
              valueFrom:
                secretKeyRef: { name: cloudflared-token, key: TUNNEL_TOKEN }
```

Tunnel 的公网域名与路由（`panel.example.com` → `http://device-panel.controlcenter.svc:80`）在 Cloudflare Zero Trust 控制台以 Public Hostname 配置。用户环境已有 Tunnel 时无需重复建设（一期非范围）。

## 5. Agent 获取与接入新设备

获取 agent 产物（Linux amd64/arm64 静态单二进制，目标机零依赖）：

```bash
scripts/build-release.sh agent        # 产物：artifacts/agent/linux-{amd64,arm64}/DevicePanel.Agent
```

（需交叉编译工具链，前置见 `scripts/build-agent.sh` 头注；发布物如附带二进制可直接取用。）

接入步骤：

1. 面板「设备管理」→ 登记设备（名称/标签）→ 复制签发的 agent token（仅显示一次）。
2. 按目标机架构分发二进制：

```bash
scp artifacts/agent/linux-arm64/DevicePanel.Agent user@device:/opt/device-panel/
```

3. systemd 常驻（`/etc/systemd/system/device-panel-agent.service`）：

```ini
[Unit]
Description=DevicePanel Agent
After=network-online.target

[Service]
ExecStart=/opt/device-panel/DevicePanel.Agent
Environment=PANEL_URL=wss://panel.example.com/agent/ws
Environment=PANEL_TOKEN=<设备 token>
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now device-panel-agent
```

4. 面板「设备管理」确认设备 `online`（心跳 30s，离线阈值 60s）。接入地址 = Tunnel 域名 + `/agent/ws`（ConfigMap `PANEL_PUBLIC_BASE_URL` / `PANEL_AGENT_WSS_PATH` 所记即此值）。

## 6. 常见运维

### SQLite 备份

面板为单写者（SQLite WAL）。推荐借挂载同一 PVC 的临时 Pod 文件级备份；如需绝对一致的冷备，先停写再备（窗口秒级）：

```bash
# （可选冷备）kubectl -n controlcenter scale deploy/device-panel --replicas=0
kubectl -n controlcenter apply -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: data-backup
  namespace: controlcenter
spec:
  containers:
    - name: backup
      image: busybox:1.36
      command: ["sh", "-c", "cp /data/device-panel.db /backup/ && sync"]
      volumeMounts:
        - { name: data, mountPath: /data }
        - { name: backup, mountPath: /backup }
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: device-panel-data }
    - name: backup
      emptyDir: {}
EOF
kubectl -n controlcenter cp data-backup:/backup/device-panel.db ./device-panel.bak
kubectl -n controlcenter delete pod data-backup
# （冷备时）kubectl -n controlcenter scale deploy/device-panel --replicas=1
```

说明：WAL 模式下热备单拷 `.db` 文件存在极小的不一致风险，重要备份建议先 `scale --replicas=0` 停写（连同 `-wal`/`-shm` 一起拷贝更稳）。

### 升级

```bash
scripts/build-image.sh controlcenter-panel:v2 && docker save controlcenter-panel:v2 | sudo k3s ctr images import -
kubectl -n controlcenter set image deploy/device-panel device-panel=controlcenter-panel:v2
kubectl -n controlcenter rollout status deploy/device-panel
```

更新策略为 `Recreate`：旧 Pod 完全退出再起新 Pod，避免 SQLite 双写；表结构迁移由应用启动时自动执行（`Infrastructure/Migrations`），数据与配置保留在 PVC。

## 7. 完成标准对照与验证命令

| # | 标准 | 验证方式 |
|---|---|---|
| 1 | k3s 部署成功、Pod Running、服务健康 | `kubectl -n controlcenter get pods` + `curl /healthz`（§3） |
| 2 | 删 Pod 重建后设备与指标不丢 | `kubectl -n controlcenter delete pod <pod>` 后查设备列表/指标曲线（PVC 持久化） |
| 3 | Tunnel 域名外网访问：未登录被拒、登录正常 | 浏览器访问 `https://panel.example.com`：未登录 302 → `/login`；登录后功能正常 |
| 4 | agent 按文档获取并在新设备接入 | §5 步骤 + 设备页 `online` |
| 5 | 镜像构建可重复执行 | `scripts/build-release.sh`（前端构建 + 后端发布 + agent 交叉编译 amd64/arm64 + 镜像） |

已在本仓库环境验证：镜像多阶段构建成功、容器非 root 运行、`/healthz` 健康、未登录 401/302 拦截、Secret 注入登录密码与 napcat 配置、同卷重建容器后设备与设置数据不丢（PVC 语义的容器级验证）。

需用户环境实测：标准 1/2/3（真实 k3s + Tunnel 域名）与标准 4（目标设备），按 §3–§5 命令执行即可。
