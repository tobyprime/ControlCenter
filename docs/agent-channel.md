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
src/DevicePanel.Agent      轻量 agent：出站 WSS 回连、auth、心跳；消息循环按 type 可扩展
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

- `term.*` —— Web 终端（如 `term.open` / `term.input` / `term.output` / `term.close`）
- `logs.*` —— 日志拉取（如 `logs.request` / `logs.response`）

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

agent 侧：在 `AgentRunner` 消息循环的 `HandleInboundAsync` 处按 type 分发；上报类消息参照 `heartbeat`/`metrics.report` 的定时发送方式扩展（信封与连接层不动）。

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
