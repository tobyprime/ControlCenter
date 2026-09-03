# ControlCenter

设备与环境统一管理面板（一期）。集中管理所有计算设备与服务的状态、指标曲线、Web 终端、日志查看，异常经 napcat 主动推送 QQ；远程服务器通过轻量 agent 出站回连接入。

- 需求与验收：Multica issue TOB-336（父 PRD，一期共 8 个功能点）
- 架构：.NET 单服务（ASP.NET Core 8 LTS + 内嵌前端产物）+ SQLite（WAL），时间统一 UTC 存储
- 前端：Vue 3 + Vite + TypeScript，全站中文、响应式
- 部署：k3s 容器化 + Cloudflare Tunnel 外网入口（一期功能点 8，后续 issue 交付）

## 分支模型

- `main`：稳定基线
- `dev`：集成开发分支（工作分支 `issue/TOB-336` 由 Leader 合并至此）
- `issue/TOB-336`：一期工作分支，各任务分支 `feature/TOB-<编号>-<desc>` 经 PR 合入

## 快速开始

依赖：.NET 8 SDK、Node.js 20+。

```bash
# 一键构建（前端 + 后端）
scripts/build.sh

# 运行
dotnet artifacts/publish/DevicePanel.Web.dll --urls http://0.0.0.0:5000
```

开发调试：

```bash
dotnet run --project src/DevicePanel.Web     # 后端，默认 http://localhost:5000
cd frontend && npm run dev                   # 前端热更新，/api 代理到 5000
dotnet test                                  # 后端单元 + 集成测试
cd e2e && npm test                           # Playwright 端到端验证
```

首次启动自动初始化数据库（WAL + 迁移）并创建初始账号，浏览器打开 `http://localhost:5000` 即可登录。

## 账号与会话

- 初始账号用户名默认 `admin`（`DevicePanel:Auth:InitialUsername`）
- 初始密码：优先读配置 `DevicePanel:Auth:InitialPassword`；未配置则首次启动生成 16 位随机密码并打印到启动日志（仅一次，WARN 级别）
- 密码仅存 PBKDF2-SHA256 哈希（10 万次迭代、随机盐），不落明文
- 会话为服务端会话：Cookie（HttpOnly、SameSite=Lax）只存 token，库里存 SHA-256(token)；登出即删，立即失效
- 会话有效期默认 24 小时（绝对过期，`DevicePanel:Auth:SessionHours`）
- 登录失败限速：默认连续失败 5 次锁定 600 秒（10 分钟），`DevicePanel:Auth:MaxFailedAttempts` / `DevicePanel:Auth:LockoutSeconds` 可调

## 配置项

| 配置键 | 环境变量 | 默认值 | 说明 |
|---|---|---|---|
| `DevicePanel:DataDir` | `DevicePanel__DataDir` | `data` | SQLite 文件目录 |
| `DevicePanel:Auth:InitialUsername` | `DevicePanel__Auth__InitialUsername` | `admin` | 初始账号用户名 |
| `DevicePanel:Auth:InitialPassword` | `DevicePanel__Auth__InitialPassword` | 空（自动生成） | 初始账号密码 |
| `DevicePanel:Auth:MaxFailedAttempts` | `DevicePanel__Auth__MaxFailedAttempts` | `5` | 登录失败锁定阈值 |
| `DevicePanel:Auth:LockoutSeconds` | `DevicePanel__Auth__LockoutSeconds` | `600` | 锁定时长（秒） |
| `DevicePanel:Auth:SessionHours` | `DevicePanel__Auth__SessionHours` | `24` | 会话有效期（小时） |
| `DevicePanel:Agent:HeartbeatIntervalSeconds` | `DevicePanel__Agent__HeartbeatIntervalSeconds` | `30` | agent 心跳周期（秒），离线阈值 = 2×该值 |
| `DevicePanel:Metrics:RetentionDays` | `DevicePanel__Metrics__RetentionDays` | `30` | 指标保留天数（明细与聚合），过期清理任务删除 |
| `DevicePanel:Metrics:CleanupIntervalMinutes` | `DevicePanel__Metrics__CleanupIntervalMinutes` | `360` | 指标过期清理任务执行间隔（分钟） |

数据库约定：SQLite 以 WAL 模式运行；所有时间列 UTC 存储（ISO-8601 文本）；表结构变更走 `src/DevicePanel.Web/Infrastructure/Migrations/` 手写迁移。

## 设备与 Agent

- 设备台账：登录后在「设备管理」页登记/编辑/删除设备（名称、多标签），创建/重置时签发 agent token（明文仅显示一次）
- 轻量 agent：`scripts/build-agent.sh` 构建 Linux amd64/arm64 静态单二进制，目标机仅出站 WSS 回连 `/agent/ws`，30s 心跳与指标上报
- 指标曲线：「指标曲线」页选择设备与时间范围查看 CPU/内存/磁盘/网络历史曲线；30s 明细 + 小时/天级预聚合（长跨度自动切换），保留约 30 天，删除设备级联清理
- 通道协议与扩展点：见 [docs/agent-channel.md](docs/agent-channel.md)
