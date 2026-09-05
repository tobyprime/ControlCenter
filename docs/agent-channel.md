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
src/DevicePanel.Web/Terminal Web 终端（TOB-339）
  ├─ TerminalEndpoints     浏览器 WS 入口 /api/devices/{id}/terminal + 留痕查询 API
  ├─ TerminalRelay         浏览器 ↔ agent 双向中继（会话收尾 + 留痕落库）
  ├─ TerminalSessionRegistry  sessionId → 活跃中继（term.* 下行投递）
  ├─ TerminalMessageHandlers  term.opened/output/closed/error 处理器
  └─ TerminalStore         会话元数据与命令/输出留痕（SQLite，随设备级联删除）
src/DevicePanel.Web/Logs   日志查看（TOB-340）
  ├─ LogEndpoints          服务清单 /api/devices/{id}/logs/services + 尾部拉取 /logs/tail
  ├─ LogQueryService       REST → logs.* 下行请求，按（通道, seq）关联响应（通道绑定/超时）
  └─ LogMessageHandlers    logs.services.response/logs.tail.response/logs.error 处理器
src/DevicePanel.Web/Targets   目标实体与 WS 接入：TargetRegistry 目标 CRUD、AgentWsEndpoints /agent/ws、连接注册表与心跳
src/DevicePanel.Web/Agents    Agent 实体（TOB-375）：AgentRegistry 一 agent 一 token/标签/能力持久化、AgentCapabilitiesMessageHandler
src/DevicePanel.Web/Endpoints REST 入口：TargetEndpoints 目标管理、AgentEndpoints Agent 管理（签发/重置 token、标签、删除）
src/DevicePanel.Agent      轻量 agent：出站 WSS 回连、auth、心跳；消息循环按 type 可扩展
src/DevicePanel.Agent/Terminal  终端通道（TOB-339）
  ├─ TerminalChannel       term.* 下行处理：会话登记、输入写入、输出泵
  ├─ LinuxPtySession       PTY shell 会话（openpty + posix_spawn，raw fd 读写）
  └─ AgentDownlink         每连接下行发送器（与节拍共用发送锁）
src/DevicePanel.Agent/Logs 日志通道（TOB-340）
  ├─ LogsChannel           logs.* 下行处理：请求后台化执行、按请求 seq 回包
  └─ LinuxLogsSource       服务清单发现（systemctl/docker ps）与尾部读取（journalctl/docker logs，只读）
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
| `agent.capabilities` | agent → 面板 | 能力声明（TOB-375），payload 为字符串数组（如 `["metrics","terminal","logs"]`，常量见 `AgentCapabilityNames`）；认证成功后立即上报，未上报 = 未声明（旧版 agent 兼容） |

预留前缀（后续 issue 只留扩展点，不做业务）：

- `logs.*` —— 日志拉取（TOB-340 已实现，见下节）

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

### 日志通道 logs.*（TOB-340 已实现）

请求-响应型消息（与 term.* 的会话型不同）：响应沿用请求的 seq 做关联（协议通用约定），
多个在途请求靠 seq 区分；面板侧按「设备通道 + seq」登记挂起请求，通道绑定校验防跨设备/陈旧连接串扰。
拉取只读按需进行：agent 只执行 `systemctl list-units` / `journalctl -u <unit>` / `docker ps` / `docker logs` 四类只读命令，
不改变目标机状态，零入站端口不变；面板侧不落库（明确不做全量长期存储，仅尾部查看）。

| type | 方向 | payload | 说明 |
|---|---|---|---|
| `logs.services.request` | 面板 → agent | `{}` | 请求目标机可查看日志的服务清单 |
| `logs.services.response` | agent → 面板 | `{services:[{name,kind,description}]}` | kind 为 systemd/docker；seq 沿用请求 |
| `logs.tail.request` | 面板 → agent | `{service, kind, lines}` | service 为 unit/容器名；lines 1–1000（面板默认 200） |
| `logs.tail.response` | agent → 面板 | `{lines:[{ts,level,message}]}` | ts 为 ISO-8601 UTC（缺失为空串）；seq 沿用请求 |
| `logs.error` | agent → 面板 | `{message}` | 服务不存在/命令失败/超时等；seq 沿用请求 |

实现契约：

- 服务清单发现：动态执行只读命令合并两路来源——systemd（`systemctl list-units --type=service`，journalctl 可查历史日志）与
  docker（`docker ps -a`，容器在停机状态也有日志）；单来源不可用（非 systemd 主机/未装 docker）跳过该来源。
  取舍：动态发现零配置、如实反映目标机现状，代价是每次打开日志页查询一次（频率可忽略）；
  不用静态配置（易过期）与 `list-unit-files`（含从未启动、无日志的单元）。
- 级别提取：systemd 用 journalctl `PRIORITY`（0-3→error，4→warn，5/6→info，7→debug）精确映射；
  docker 日志无级别字段，按消息关键词启发式判级（error/fatal/failed→error，warn→warn，debug/trace→debug，其余 info）。
- 慢请求不阻塞节拍：logs.* 请求处理在 agent 侧立即后台化（回归锚：AgentLogsIntegrationTests，
  TOB-338「下行信封不打断心跳/指标节拍」在日志路径上的延伸）；命令超时 20s 折算成 logs.error。
- 名称防注入：服务名按白名单校验（`[A-Za-z0-9._@:-]`），面板与 agent 双侧校验；docker logs 经 /bin/sh 合并 stdout/stderr。
- 面板 API：`GET /api/devices/{id}/logs/services`；`GET /api/devices/{id}/logs/tail?service&kind&lines`（默认 200，上限 1000）。
  设备离线 409、agent 错误 502、等待超时 504（`DevicePanel:Logs:RequestTimeoutSeconds` 默认 30）。

### 能力声明 agent.capabilities（TOB-375 已实现）

agent 在收到 auth.ok 之后、进入消息循环之前，主动上报一次本连接实际可提供的通道（字符串数组，
常量见 `AgentCapabilityNames`：metrics/terminal/logs）。面板 `AgentCapabilitiesMessageHandler` 持久化到
`agents.capabilities_json`，Agent 管理页展示（metrics→指标上报、terminal→Web 终端、logs→日志拉取）。

- 未上报的连接不写该字段（保持 NULL）——旧版 agent 没有这条消息也照常接入（向后兼容）。
- 每次连接（含重连）上报覆盖旧值，始终反映当前在线会话的实际能力；不更新 `updated_at_utc`（标签编辑语义专用）。
- 具体能力的类型扩充（如指标项、终端参数）由三期模块 3/4 在各自 issue 内扩展，本通道只承载能力名。

## WebSocket 关闭码

| 码 | 常量 | 场景 |
|---|---|---|
| 4001 | `AuthFailed` | token 无效 / 已重置 / 认证超时 |
| 4002 | `DeviceDeleted` | 设备已删除 |
| 4003 | `TokenReset` | token 已重置 |
| 4004 | `HeartbeatTimeout` | 连续 2 个心跳周期无消息 |
| 4005 | `DuplicateSession` | 同设备新连接顶替旧连接 |

agent 侧重连策略：网络类断开按指数退避（1s 起、30s 封顶）自动重连；token 类（4001/4002/4003）不重试，退出提示更换 token。

## Agent 实体与注册（TOB-375）

`agents` 表是连接身份与能力声明的唯一宿主：一 agent 一 token、自定义标签（自由文本、不限量）、能力声明（上节）。
存量 device target 由迁移 `013_agents.sql` 自动建出同名 agent（token hash 平移，零重装、PANEL_TOKEN 不变），
双写期 targets 与 agents 并存且一一关联：

- 表结构：`agents(id, name, token_hash UNIQUE, labels_json, capabilities_json, last_seen_at_utc, …)`；
  `targets` 新增 `agent_id`（FK → agents.id，NULL = 台账直建未关联）；`targets.agent_token_hash` 保留为已关联 agent 的**直写镜像列**
  （满足历史 NOT NULL UNIQUE 约束），认证只读 `agents.token_hash`。
- 连接键沿用 target 语义，现有 target 功能全部照常：已关联 agent 的连接键 = 其 targetId（指标/终端/日志/在线判定零改动）；
  Agent 管理页直建、未关联 target 的 agent 连接键 = **负 agent id**（不与任何 target 混淆）。
- 重置 token 同步刷新镜像列并按连接键以 4003 断开在线连接；删除关联 target 同步删 agent（级联）；未关联 agent 可单独删除（4002 断连）。
- 管理 API（会话认证）：`GET /api/agents?label=`（标签服务端筛选，json_each 展开 labels_json）、`POST /api/agents`（签发 token，明文只出现一次）、
  `PUT /api/agents/{id}/labels`、`POST /api/agents/{id}/token`、`DELETE /api/agents/{id}`（已关联目标返回 400，引导到目标管理页）。
- 管理页：前端 `/agents`「Agent 管理」——创建并签发 token（仅显示一次）、编辑标签、按标签筛选、重置 Token、删除、在线状态与能力展示。
- service 型 target 无 token 语义，不建 agent；device 型 target 创建时同步建 agent 并落 `targets.agent_id`。

## 认证与生命周期

- token 由面板签发（`dpk_` 前缀），**明文只出现一次**，库中仅存 SHA-256：双写期入口为目标创建（同步建 agent）与 Agent 管理页，认证只读 `agents.token_hash`（`targets.agent_token_hash` 仅为镜像，见上节）。
- 重置 token / 删除设备（或未关联 agent）：面板立即以 4003/4002 关闭该连接，旧 token 重新连接即被拒。
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

agent 侧：按消息形态实现对应通道接口——会话型实现 `ITerminalChannel`（如 `TerminalChannel` 对 term.* 的处理），
请求-响应型实现 `ILogsChannel`（如 `LogsChannel` 对 logs.* 的处理，响应按请求 seq 回包），
均由构造注入 AgentRunner 的对应工厂参数接入消息循环（下行按 type 前缀路由）；下行主动发送经每连接的 `AgentDownlink`
（与节拍共用一把发送锁，ClientWebSocket 不允许并发发送）。上报类消息参照 `heartbeat`/`metrics.report` 的定时发送方式扩展（信封与连接层不动）。

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
| `DevicePanel:Logs:RequestTimeoutSeconds` | `30` | 面板等待 agent 日志响应的超时（超时返回 504） |

## Agent 构建与运行

```bash
scripts/build-agent.sh    # 产出 artifacts/agent/linux-{amd64,arm64}/DevicePanel.Agent
```

- NativeAOT + musl 静态链接（内嵌静态 OpenSSL）：`file` 显示 `static-pie linked`，目标机零依赖（无 .NET 运行时、无共享库要求、无入站端口，仅出站 WSS，支持 wss:// TLS）。
- 运行：`DevicePanel.Agent --url wss://<面板地址>/agent/ws --token <设备token>`
  - 参数可被环境变量 `PANEL_URL` / `PANEL_TOKEN` / `PANEL_INTERVAL_SECONDS` 替代。
- 验收基线（TOB-337）：token 正确 30s 内面板显示在线；停止 agent 60–90s 内变离线；错误/重置 token 拒绝接入；重置后旧 token 立即失效；删除设备断开连接并从列表移除。
