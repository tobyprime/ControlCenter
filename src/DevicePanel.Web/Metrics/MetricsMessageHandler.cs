using DevicePanel.Protocol;
using DevicePanel.Web.Devices;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 指标上报处理器：解析 metrics.report 负载并以面板接收时间（UTC）入库，
/// 写入即同步更新小时/天级聚合桶。负载不合法时忽略该条并保留连接（agent 侧采集失败已自行跳过）。
/// </summary>
public sealed class MetricsMessageHandler : IAgentMessageHandler
{
    private readonly IMetricsStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<MetricsMessageHandler> _logger;

    public MetricsMessageHandler(IMetricsStore store, TimeProvider clock, ILogger<MetricsMessageHandler> logger)
    {
        _store = store;
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

        _store.Insert(context.Channel.DeviceId, _clock.GetUtcNow(), point);
        return Task.CompletedTask;
    }
}
