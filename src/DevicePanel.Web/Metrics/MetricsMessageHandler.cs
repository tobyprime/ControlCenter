using DevicePanel.Protocol;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 指标上报处理器：解析 metrics.report 负载并以面板 UTC 接收时间入库，写入即同步更新小时/天级聚合桶，
/// 随后同步喂告警规则引擎（一期阈值评估挂接点升级为规则化引擎，评估自捕获异常不影响入库）。
/// 契约"丢点保连"：负载不合法或落库失败（存储抖动/磁盘故障）均只丢弃该点并保留连接与心跳，
/// 不允许异常穿过 DispatchAsync 结束 WS 会话。
/// </summary>
public sealed class MetricsMessageHandler : IAgentMessageHandler
{
    private readonly IMetricsStore _store;
    private readonly IAlertRuleEngine _ruleEngine;
    private readonly TimeProvider _clock;
    private readonly ILogger<MetricsMessageHandler> _logger;

    public MetricsMessageHandler(IMetricsStore store, IAlertRuleEngine ruleEngine, TimeProvider clock, ILogger<MetricsMessageHandler> logger)
    {
        _store = store;
        _ruleEngine = ruleEngine;
        _clock = clock;
        _logger = logger;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.MetricsReport;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        if (!MetricsPayloadReader.TryParse(context.Payload, out var point))
        {
            _logger.LogWarning("设备 {DeviceId} 的指标上报负载不合法，已忽略（seq={Seq}）", context.Channel.DeviceId, context.Seq);
            return Task.CompletedTask;
        }

        try
        {
            _store.Insert(context.Channel.DeviceId, _clock.GetUtcNow(), point);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 丢点保连：存储故障不结束 WS 会话（agent 将按周期重报后续点）
            _logger.LogError(ex, "设备 {DeviceId} 的指标入库失败，本点已丢弃（seq={Seq}）", context.Channel.DeviceId, context.Seq);
            return Task.CompletedTask;
        }

        // 告警规则评估挂在入库成功之后：数据源复用指标入库链路，引擎内部自捕获异常
        _ruleEngine.EvaluateDeviceSample(context.Channel.DeviceId, point, _clock.GetUtcNow());
        return Task.CompletedTask;
    }
}
