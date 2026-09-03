# ControlCenter

设备与环境统一管理面板（一期）。集中管理所有计算设备与服务的状态、指标曲线、Web 终端、日志查看，异常经 napcat 主动推送 QQ；远程服务器通过轻量 agent 出站回连接入。

- 需求与验收：Multica issue TOB-336（父 PRD）
- 架构：.NET 单服务（ASP.NET Core + 内嵌前端）+ SQLite（WAL）；agent 为 Linux amd64/arm64 静态单二进制，出站 WSS 回连；k3s 容器化部署 + Cloudflare Tunnel 外网入口

## 分支模型

- `main`：稳定基线
- `dev`：集成开发分支（工作分支 `issue/TOB-336` 由 Leader 合并至此）
- `issue/TOB-336`：一期工作分支，各任务分支 `feature/TOB-<编号>-<desc>` 经 PR 合入
