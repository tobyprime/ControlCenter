using DevicePanel.Protocol;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;

namespace DevicePanel.Web.Targets;

/// <summary>
/// 内置心跳处理器：刷新 agent 与目标 last_seen（数据库 + 在线连接登记表），并写入 online=true 样本
/// （在线状态成为类型化指标序列，"状态不符"规则的数据来源；离线侧由 TargetStatusScanner 补 false）。
/// 未关联目标的 agent（连接键为负 agent id）只刷新 agent 侧 last_seen——target 指标/告警链路仅对关联 agent 生效。
/// 同时作为消息处理器的参考实现——指标/终端/日志等能力按同样方式接入，无需改通道代码。
/// </summary>
public sealed class HeartbeatMessageHandler : IAgentMessageHandler
{
    private readonly ITargetRegistry _targets;
    private readonly IAgentRegistry _agents;
    private readonly AgentConnectionRegistry _connections;
    private readonly IMetricsStore _metrics;
    private readonly IAlertRuleEngine _alerts;
    private readonly TimeProvider _clock;

    public HeartbeatMessageHandler(ITargetRegistry targets, IAgentRegistry agents, AgentConnectionRegistry connections, IMetricsStore metrics, IAlertRuleEngine alerts, TimeProvider clock)
    {
        _targets = targets;
        _agents = agents;
        _connections = connections;
        _metrics = metrics;
        _alerts = alerts;
        _clock = clock;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.Heartbeat;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        if (context.Channel.AgentId > 0)
        {
            _agents.Touch(context.Channel.AgentId, nowUtc);
        }

        _connections.Touch(context.Channel.DeviceId, nowUtc);
        if (context.Channel.DeviceId <= 0)
        {
            // 未关联 agent：无 target 台账可刷，online 样本留待模块3按 agent 维度接入
            return Task.CompletedTask;
        }

        _targets.Touch(context.Channel.DeviceId, nowUtc);

        // 在线样本 best-effort：写入失败不影响心跳与会话（状态扫描器会在下次转换时补齐）
        try
        {
            var sample = new MetricSample(nowUtc, 1, "true");
            _metrics.Insert(context.Channel.DeviceId, MetricKeys.Online, sample);
            _alerts.OnSample(context.Channel.DeviceId, MetricKeys.Online, sample, nowUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return Task.CompletedTask;
    }
}
