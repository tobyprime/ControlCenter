# 真实环境部署指南（TOB-357：TOB-352 k8s + Cloudflare Pages）

与 [deploy-k3s.md](deploy-k3s.md) 的通用交付不同，本文面向**当前真实环境**的一次性落地：
后端跑在 TOB-352（EnvOps）的 k3s 集群，前端独立托管 Cloudflare Pages，公网走 Cloudflare Tunnel，
agent 经公网回连。集群基础组件与 Cloudflare 账号侧操作不在业务部署范围内，分别走 EnvOps 配合
节点（§6）与用户清单（§5）。

## 1. 目标拓扑

```
浏览器 ──https──▶ Cloudflare Pages（前端静态站点，SPA）
                    │  fetch / WebSocket（跨域，凭据模式 CORS + 会话 Cookie）
                    ▼
用户 ──https/wss──▶ Cloudflare Tunnel：https://<Tunnel 域名>
                    │  Public Hostname → http://device-panel.controlcenter.svc.cluster.local:80
                    ▼
        k3s（toby-laptop-debian）：device-panel Deployment（单副本，PVC 持久化 SQLite）
                    ▲ wss://<Tunnel 域名>/agent/ws（出站回连，token 认证）
        外网服务器：DevicePanel.Agent（零入站端口）
```

两条前端入口并存：Tunnel 域名直接打开的是后端内嵌前端（同源，行为与集成验收一致）；
Pages 域名打开的是独立托管前端（跨域配置见 §3）。

## 2. 部署适配改动（本 issue 任务分支交付）

| 改动 | 说明 |
|---|---|
| 会话 Cookie SameSite/Secure 可配置 | `DevicePanel:Auth:SessionCookieSameSite`（默认 `Lax` 不变；`None` 自动加 `Secure`，供 `*.pages.dev` 跨站形态使用） |
| CORS 允许来源可配置 | `DevicePanel:Cors:AllowedOrigins`（分号/逗号分隔；凭据模式、回显具体来源；未配置完全不启用，同源形态零变化） |
| 前端 API/WSS 基址可配置 | `VITE_API_BASE_URL` 构建期注入；默认空串=同源，内嵌形态零变化 |
| Cloudflare Pages 构建模式 | `npm run build:pages` → `dist/`（含 `_redirects` SPA 回退） |
| 部署包推送通道 | `scripts/push-deploy-bundle.sh`：构建镜像 → docker save → SSH 推送节点 → sha256 校验 |

## 3. 部署形态与 Cookie/CORS 策略

| 形态 | Cookie SameSite | CORS 来源 | 说明 |
|---|---|---|---|
| 仅 Tunnel（内嵌前端） | Lax（默认） | 不启用 | 最简形态，行为与集成验收完全一致 |
| Pages（`*.pages.dev`）+ Tunnel 后端 | `None`（自动 Secure） | `https://<项目名>.pages.dev` | 跨站 Cookie；Chrome 可用，Firefox/Safari 严格隐私模式可能拦第三方 Cookie |
| Pages 绑自定义域（与后端同站，如 `panel.example.com` + `cc.example.com`） | `Lax`（默认） | 两个 Pages 来源 | **推荐**：同站 Cookie 无第三方限制，浏览器兼容性最好 |

## 4. 后端部署（集群节点侧）

集群业务负载部署由部署包完成（镜像 tar + manifests + 一键脚本），包内容与步骤见
[deploy/real-deploy/README.md](../deploy/real-deploy/README.md)（该手册随包推送到节点 `/home/toby/controlcenter/`）。

## 5. 用户操作清单（Cloudflare 侧，一次性）

> 占位符约定：`<Tunnel 域名>`=后端公网域名（如 `cc.example.com`）；`<Pages 域名>`=Pages 分配的
> `https://<项目名>.pages.dev`。完成后逐项验证。清单按顺序执行。
>
> **本次实际值（TOB-357 已落地）**：`<Tunnel 域名>`=`srv-control-panel.tobylinas.top`（清单 B 已完成）；
> `<Pages 域名>`=`https://controlcenter-5qk.pages.dev`（清单 A 由部署方经 `wrangler pages` 完成，重名分配 -5qk 后缀）；
> 用户已绑自定义域 `control-center.tobylinas.top`（与预留名 `control-panel` 拼写不同，后端 CORS 随实际域名单独更新过一次）。
> 后端 CORS 当前允许：`https://controlcenter-5qk.pages.dev,https://control-center.tobylinas.top`；
> 会话 Cookie 维持 `None+Secure`（pages.dev 跨站与自定义域同站两形态均可用；确认仅用自定义域后可切 `Lax` 并收窄 CORS）。

### A. 建前端 Pages 项目

> 本次由部署方以 CLI 完成（`wrangler pages project create controlcenter --production-branch=main` +
> `wrangler pages deploy dist --project-name controlcenter --branch main`），结果即上注实际值；
> 手动路径保留如下，供重建/迁移时参考。

1. **操作位置**：Cloudflare Dashboard → Workers & Pages → Create → Pages → **Upload assets（直接上传）**；
   项目名填 **`controlcenter`**（若重名则换名，并通知部署方同步改后端 CORS 配置）。
2. **上传内容**：部署方提供的 `dist/` 目录全部文件（含 `_redirects`；`_redirects` 是隐藏文件，
   直接拖 `dist` 文件夹即可整包带入）。
3. **验证**：创建完成后打开 `<Pages 域名>`，应显示登录页；按 F12 → Network 随意输入账号登录，
   请求发往 `<Tunnel 域名>/api/auth/login` 即为前端配置正确（此时若后端未就绪，登录失败属预期）。

### B. 后端公网域名（Tunnel Public Hostname）

> 若集群内 cloudflared 为本地 config 管理，此步由 EnvOps 在集群内完成（见部署包手册 §6.2），
> 用户可跳过。若为 Zero Trust 控制台远程管理，按下面执行。

1. **操作位置**：Cloudflare Dashboard → Zero Trust → Networks → Tunnels → 选中现有 Tunnel →
   Public Hostname → **Add a public hostname**。
2. **填什么值**：Subdomain=准备作 `<Tunnel 域名>` 的子域（如 `cc`），Domain=你的根域；
   Type=**HTTP**；URL=**`device-panel.controlcenter.svc.cluster.local`**，端口 **`80`**。
3. **验证**：浏览器打开 `https://<Tunnel 域名>` → 未登录自动跳 `/login`（302），即后端已通。

### C.（推荐，可选）Pages 绑自定义域，消除跨站 Cookie 限制

1. **操作位置**：Workers & Pages → controlcenter → Custom domains → **Set up a custom domain**，
   填 `panel.<根域>`（与后端同站），确认 DNS 自动创建。
2. **同步配置**：告知部署方「Pages 已用自定义域」→ 后端 CORS 增加 `https://panel.<根域>`、
   `DevicePanel__Auth__SessionCookieSameSite` 改回 `Lax`（给出配置后重启 Deployment 即可）。
3. **验证**：`https://panel.<根域>` 打开登录页；登录成功且 F12 → Application → Cookies 中
   `device_panel_session` 的 SameSite 为 `Lax`/空、Secure 勾选，无控制台 Cookie 警告。

### D. Agent 安装（接入一台外网服务器）

1. **获取二进制**：从交付评论附件下载对应架构的 `DevicePanel.Agent`（`file` 名含 amd64/arm64），
   `scp` 到目标机 `/opt/device-panel/`。
2. **面板登记设备**：登录面板 → 设备管理 → 新建设备 → 复制 agent token（仅显示一次）。
3. **systemd 常驻** `/etc/systemd/system/device-panel-agent.service`：

   ```ini
   [Unit]
   Description=DevicePanel Agent
   After=network-online.target

   [Service]
   ExecStart=/opt/device-panel/DevicePanel.Agent
   Environment=PANEL_URL=wss://<Tunnel 域名>/agent/ws
   Environment=PANEL_TOKEN=<设备 token>
   Restart=always
   RestartSec=5

   [Install]
   WantedBy=multi-user.target
   ```

   ```bash
   sudo systemctl daemon-reload && sudo systemctl enable --now device-panel-agent
   ```

4. **验证**：面板设备列表 30s 内显示「在线」；指标曲线页 1 分钟内出现数据点；
   Web 终端可执行 `echo ok`；日志页可见该机服务列表。

### E. 整体验证（对照完成标准）

| 项 | 验证 |
|---|---|
| 公网登录门禁 | 隐身窗口开 `https://<Tunnel 域名>`：未登录被拒（跳登录页；直接 GET `/api/devices` 得 401）；登录后各页正常 |
| 前端 Pages | `<Pages 域名>`（或自定义域）登录后功能正常 |
| agent 回连 | 上一步 D.4 全绿 |
| QQ 告警链路 | 停某设备 agent，60–90s 面板离线 + QQ 收到离线告警 |

## 6. EnvOps 配合节点（业务部署之外的集群侧确认）

1. 导入两个镜像 tar（`controlcenter-panel`、`busybox:1.36`）到 containerd `k8s.io` 命名空间（手册 §1）。
2. 参数/Secret 填写（napcat 建议用集群内 Service 地址）与 `./deploy.sh` 执行（手册 §2–3）。
3. PVC 删 Pod 重建实测（手册 §5，完成标准 1 的证据）。
4. napcat 集内地址与可达性回报（手册 §6.1）。
5. cloudflared 管理模式确认；若本地 config 管理，由 EnvOps 加 ingress 规则（手册 §6.2）。

## 7. 失败排查速查

| 现象 | 排查 |
|---|---|
| Pages 登录提示「请求失败」且 Network 无请求发出 | 前端构建未注入 `VITE_API_BASE_URL`（检查部署方提供的 dist 构建参数） |
| Pages 登录 401/CORS 报错 | 后端 `DevicePanel__Cors__AllowedOrigins` 未含当前前端域名（ConfigMap 改后自动滚动重启） |
| 登录成功但刷新即掉线 | Cookie SameSite 与部署形态不匹配（`*.pages.dev` 需 `None`；同站自定义域用 `Lax`） |
| Tunnel 打不开 | EnvOps 侧确认 cloudflared Public Hostname/ingress 已指向 `device-panel.controlcenter.svc.cluster.local:80` |
| agent 不在线 | 目标机 `journalctl -u device-panel-agent`：看 WSS 是否到 `<Tunnel 域名>/agent/ws`、token 是否有效 |
