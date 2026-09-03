# Agent ↔ 面板消息通道

本文是 agent 接入通道的协议说明与扩展点约定（TOB-337 交付），供后续指标（TOB 功能点 3）、Web 终端（功能点 4）、日志（功能点 5）等 issue 复用。

## 总体结构

```
src/DevicePanel.Protocol   协议层：AgentEnvelope 信封、消息类型常量、关闭码（面板/agent/测试共享）
src/DevicePanel.Web/Devices
  ├─ AgentWsEndpoints      /agent/ws 接入：auth 握手（token 认证）→ 注册连接 → 按 type 分发
  ├─ AgentConnection       单条 WS 连接的信封收发（IDeviceChannel 实现）
  ├─ IDeviceChannel        面板侧通道抽象：SendAsync / CloseAsync / DeviceId
  ├─ IAgentMessageHandler  消息类型处理器接口（服务端扩展点）
  ├─ AgentMessageDispatcher  按 type 路由；未注册类型忽略（向前兼容）
  ├─ AgentConnectionRegistry  设备 ID → 在线连接；删除断连 / 重置断连 / 心跳超时清理
  └─ HeartbeatMessageHandler  内置心跳处理器（也是新增处理器的参考实现）
src/DevicePanel.Web/Terminal  Web 终端（TOB-339）
  ├─ TerminalEndpoints     浏览器 WS 入口 /api/devices/{id}/terminal + 留痕查询 API
  ├─ TerminalRelay         浏览器 ↔ agent 双向中继（会话收尾 + 留痕落库）
  ├─ TerminalSessionRegistry  sessionId → 活跃中继（term.* 下行投递）
  ├─ TerminalMessageHandlers  term.opened/output/closed/error 处理器
  └─ TerminalStore         会话元数据与命令/输出留痕（SQLite，随设备级联删除）
src/DevicePanel.Agent      轻量 agent：出站 WSS 回连、auth、心跳；消息循环按 type 可扩展
src/DevicePanel.Agent/Terminal  终端通道（TOB-339）
  ├─ TerminalChannel       term.* 下行处理：会话登记、输入写入、输出泵
  ├─ LinuxPtySession       PTY shell 会话（openpty + posix_spawn，raw fd 读写）
  └─ AgentDownlink         每连接下行发送器（与节拍共用发送锁）
```

## 消息信封

统一 JSON 信封 `{type, seq, payload}`（`AgentEnvelope`）：

- `type`：消息类型。**新增能力只加 type，不改信封**。
- `seq`：发送方内递增序号；对请求型消息，响应沿用请求的 seq 做关联。
- `payload`：不透明 JSON，编解码层按原样透传（`AgentEnvelopeConverter`），任何消息类型的结构变化不影响通道。

内置类型：

| type | 方向 | 说明 |
|---|---|---|
| `auth` | agent → 面板 | 握手，payload `{token}`；WS 建立后 10s 内必须发送（`AgentOptions:AuthTimeoutSeconds`） |
| `auth.ok` | 面板 → agent | 认证成功，payload `{deviceId, name}` |
| `auth.error` | 面板 → agent | 认证失败，payload `{message}`，随后以 4001 关闭 |
| `heartbeat` | agent → 面板 | 心跳（默认 30s 一个周期），payload `{uptimeSec}` |
| `metrics.report` | agent → 面板 | 指标快照（与心跳同节拍发送，默认 30s），payload `{cpu, mem, disk, netRx, netTx}`；cpu/mem/disk 为百分比（0-100，disk 为根文件系统），netRx/netTx 为字节/秒 |

预留前缀（后续 issue 只留扩展点，不做业务）：

- `logs.*` —— 日志拉取（如 `logs.request` / `logs.response`）

### 终端通道 term.*（TOB-339 已实现）

会话由面板侧发起并生成 `sessionId`（GUID 文本），agent 按 sessionId 维护 PTY shell；
`data` 一律为 base64 编码字节（UTF-8 分块边界安全）。seq 约定：term.* 各端均使用自身单调自增序号，
不做请求-响应 seq 关联（会话消息靠 sessionId 关联，与 auth 这类请求型消息不同）。
面板中继对 `term.output` 做增量 UTF-8 解码，多字节字符跨分块不产生乱码；下行投递校验来源设备通道，
仅接受会话所属通道的信封（防跨设备注入）。

| type | 方向 | payload | 说明 |
|---|---|---|---|
| `term.open` | 面板 → agent | `{sessionId, cols, rows}` | 请求打开 PTY shell（/bin/sh -i 或 $SHELL） |
| `term.opened` | agent → 面板 | `{sessionId}` | PTY 就绪确认 |
| `term.input` | 面板 → agent | `{sessionId, data}` | 键盘输入（base64） |
| `term.output` | agent → 面板 | `{sessionId, data}` | shell 输出（base64，≤4KB/帧流式回发） |
| `term.resize` | 面板 → agent | `{sessionId, cols, rows}` | 浏览器视口变更时调整 PTY winsize（TIOCSWINSZ） |
| `term.close` | 面板 → agent | `{sessionId}` | 关闭会话（PTY 主端释放 + SIGTERM→SIGKILL 回收） |
| `term.closed` | agent → 面板 | `{sessionId}` | 会话结束（shell 退出或关闭完成） |
| `term.error` | agent → 面板 | `{sessionId, message}` | 打开失败等错误（不中断连接与节拍） |

会话生命周期契约：

- 目标设备零入站端口不变：终端数据全部走既有出站 WS 通道。
- agent 断连/重连：该连接上的全部 PTY 随 `ITerminalChannel.ShutdownAsync` 终止并回收，重连后是全新会话（无跨连接残留）。
- 节拍契约延伸：term.* 处理与输出泵全部兜异常——终端故障不打断心跳/指标（TOB-338 回归锚的延伸场景，见 AgentTerminalIntegrationTests）。
- 留痕：面板侧对每个会话记录元数据（设备/操作者/起止/关闭原因）与输入输出留档（`terminal_sessions`/`terminal_entries`，随设备级联删除）；
  存储故障只丢留痕不断会话（沿用「存储故障不杀 WS 会话」契约）。
- 浏览器入口：`WS /api/devices/{deviceId}/terminal?cols&rows`（会话 Cookie 认证）→ 面板中继到 agent；
  留痕查询：`GET /api/terminal/sessions`、`GET /api/terminal/sessions/{id}/records`。

后续扩展方式（预留，未实现）：

- **预设命令**：面板侧定义命令模板，下发即转发为 `term.input`（无需新消息类型）；如需服务端执行语义，可扩 `term.preset` `{sessionId, commandId}`。
- **文件传输**：沿用同一信封扩展 `file.*` 前缀（如 `file.offer` / `file.chunk` / `file.ack`，payload 带 sessionId 关联终端会话），复用 term.input 的通道与留痕模式。
- **会话参数**：`term.open` 负载追加字段（如环境变量、初始目录），信封与既有消费者不受影响（payload 不透明原则）。

## WebSocket 关闭码

| 码 | 常量 | 场景 |
|---|---|---|
| 4001 | `AuthFailed` | token 无效 / 已重置 / 认证超时 |
| 4002 | `DeviceDeleted` | 设备已删除 |
| 4003 | `TokenReset` | token 已重置 |
| 4004 | `HeartbeatTimeout` | 连续 2 个心跳周期无消息 |
| 4005 | `DuplicateSession` | 同设备新连接顶替旧连接 |

agent 侧重连策略：网络类断开按指数退避（1s 起、30s 封顶）自动重连；token 类（4001/4002/4003）不重试，退出提示更换 token。

## 认证与生命周期

- token 由面板在创建设备/重置 token 时签发（`dpk_` 前缀），**明文只出现一次**，库中仅存 SHA-256。
- 重置 token / 删除设备：面板立即以 4003/4002 关闭该设备在线连接，旧 token 重新连接即被拒。
- 在线判定：`last_seen_at_utc` 距当前不超过连续 2 个心跳周期（`DevicePanel:Agent:HeartbeatIntervalSeconds` 默认 30s → 离线阈值 60s）；`HeartbeatMonitor` 每 15s 清理超时连接（4004）。
- `/agent/ws` 独立于 Web 会话认证（token 信封认证），面板登录拦截中间件对该路径放行。

## 如何新增一种消息能力（后续 issue 的接入方式）

服务端（面板）：

```csharp
// 1. 实现 IAgentMessageHandler（MessageType 完整匹配信封 type）
public sealed class MetricsReportHandler : IAgentMessageHandler
{
    public string MessageType => "metrics.report";
    public Task HandleAsync(AgentChannelContext context, CancellationToken ct)
    {
        // context.Payload 即上报内容；需要回包时 context.Channel.SendAsync(...)
    }
}

// 2. 注册 DI，分发链路零改动
services.AddSingleton<IAgentMessageHandler, MetricsReportHandler>();
```

面板下行主动消息：从 `AgentConnectionRegistry` 取设备连接（或经自己的会话管理），`IDeviceChannel.SendAsync(AgentEnvelope.Create("term.open", seq, payload), ct)`。

agent 侧：实现 `ITerminalChannel`（如 `TerminalChannel` 对 term.* 的处理），由构造注入 AgentRunner 的 `terminalChannelFactory` 接入消息循环；下行主动发送经每连接的 `AgentDownlink`（与节拍共用一把发送锁，ClientWebSocket 不允许并发发送）。上报类消息参照 `heartbeat`/`metrics.report` 的定时发送方式扩展（信封与连接层不动）。

## 指标上报与面板存储（TOB-338）

- agent 侧：`LinuxMetricsCollector` 每个心跳周期采集一次——CPU（/proc/stat 增量）、内存（/proc/meminfo）、磁盘（根文件系统用量）、网络（/proc/net/dev 增量，排除 lo，字节/秒）；首个周期 CPU/网络无增量基准报 0，采集失败跳过本周期不影响心跳。
- 面板侧：`MetricsMessageHandler` 以面板 UTC 接收时间入库（明细 `metric_samples`），写入即增量更新小时/天级聚合桶（sum/count 均值 + max 峰值，`metric_samples_hourly` / `metric_samples_daily`）。
- 查询：`GET /api/metrics/{deviceId}/series?from&to&granularity`；granularity=auto 时 ≤6h 明细 / ≤10 天小时聚合 / 更长天聚合，可显式指定 raw/hour/day 覆盖；聚合均值与明细均值口径一致。
- 保留策略：`MetricsRetentionService` 启动即清理一次，之后每 6 小时清理超过保留期（`DevicePanel:Metrics:RetentionDays` 默认 30 天）的明细与聚合；删除设备级联删除其指标。

## 配置

| 配置键 | 默认值 | 说明 |
|---|---|---|
| `DevicePanel:Agent:HeartbeatIntervalSeconds` | `30` | 心跳周期；离线阈值 = 2×该值 |
| `DevicePanel:Agent:AuthTimeoutSeconds` | `10` | WS 建立后等待 auth 信封的超时 |

## Agent 构建与运行

```bash
scripts/build-agent.sh    # 产出 artifacts/agent/linux-{amd64,arm64}/DevicePanel.Agent
```

- NativeAOT + musl 静态链接（内嵌静态 OpenSSL）：`file` 显示 `static-pie linked`，目标机零依赖（无 .NET 运行时、无共享库要求、无入站端口，仅出站 WSS，支持 wss:// TLS）。
- 运行：`DevicePanel.Agent --url wss://<面板地址>/agent/ws --token <设备token>`
  - 参数可被环境变量 `PANEL_URL` / `PANEL_TOKEN` / `PANEL_INTERVAL_SECONDS` 替代。
- 验收基线（TOB-337）：token 正确 30s 内面板显示在线；停止 agent 60–90s 内变离线；错误/重置 token 拒绝接入；重置后旧 token 立即失效；删除设备断开连接并从列表移除。
