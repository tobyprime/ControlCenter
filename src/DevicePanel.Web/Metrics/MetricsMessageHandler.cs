using DevicePanel.Protocol;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 指标上报处理器：解析 metrics.report 负载并以面板 UTC 接收时间入库（写入即同步更新小时/天级聚合桶），
/// 随后逐指标喂告警规则引擎（约束 B 挂接点，评估自捕获异常不影响入库）。
/// 仅注册过的 metric key 入库（约束 A：未注册指标拒收并告警日志提示，注册后即生效）。
/// 契约"丢点保连"：负载不合法或落库失败（存储抖动/磁盘故障）均只丢弃该点并保留连接与心跳，
/// 不允许异常穿过 DispatchAsync 结束 WS 会话。
/// </summary>
public sealed class MetricsMessageHandler : IAgentMessageHandler
{
    private readonly IMetricsStore _store;
    private readonly IMetricKeyRegistry _registry;
    private readonly IAlertRuleEngine _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<MetricsMessageHandler> _logger;

    public MetricsMessageHandler(IMetricsStore store, IMetricKeyRegistry registry, IAlertRuleEngine alerts, TimeProvider clock, ILogger<MetricsMessageHandler> logger)
    {
        _store = store;
        _registry = registry;
        _alerts = alerts;
        _clock = clock;
        _logger = logger;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.MetricsReport;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        // 未关联目标的 agent（模块2）：连接受理但指标暂不入库（无 target 维度的存储键；模块3接入 agent 维度管道）
        if (context.Channel.DeviceId <= 0)
        {
            _logger.LogInformation("未关联目标的 Agent {AgentId} 上报指标已忽略（seq={Seq}）", context.Channel.AgentId, context.Seq);
            return Task.CompletedTask;
        }

        var receivedAtUtc = _clock.GetUtcNow();
        if (!MetricsPayloadReader.TryParse(context.Payload, receivedAtUtc, out var samples))
        {
            _logger.LogWarning("目标 {TargetId} 的指标上报负载不合法，已忽略（seq={Seq}）", context.Channel.DeviceId, context.Seq);
            return Task.CompletedTask;
        }

        foreach (var (key, sample) in samples)
        {
            if (_registry.Get(key) is null)
            {
                _logger.LogWarning("目标 {TargetId} 上报了未注册指标 {MetricKey}，已忽略（注册 metric key 后自动生效）", context.Channel.DeviceId, key);
                continue;
            }

            try
            {
                _store.Insert(context.Channel.DeviceId, key, sample);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 丢点保连：存储故障不结束 WS 会话（agent 将按周期重报后续点）
                _logger.LogError(ex, "目标 {TargetId} 指标 {MetricKey} 入库失败，本点已丢弃（seq={Seq}）", context.Channel.DeviceId, key, context.Seq);
                continue;
            }

            // 规则评估挂在入库成功之后：数据源复用指标入库链路，引擎内部自捕获异常
            _alerts.OnSample(context.Channel.DeviceId, key, sample, _clock.GetUtcNow());
        }

        return Task.CompletedTask;
    }
}
