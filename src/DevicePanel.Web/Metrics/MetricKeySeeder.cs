using DevicePanel.Web.Infrastructure;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 内置指标键目录：一期五键的注册表落库（类型与展示元数据，与一期告警/图表命名一致）。
/// 核心不解释指标含义——新增指标 = 注册一条（探针配置或代码目录），不改核心逻辑（TOB-360 约束 A）。
/// </summary>
public static class MetricKeyCatalog
{
    public static readonly IReadOnlyList<MetricKeyInfo> BuiltIn =
    [
        new MetricKeyInfo("cpu", MetricValueType.Number, "%", "CPU 使用率"),
        new MetricKeyInfo("mem", MetricValueType.Number, "%", "内存使用率"),
        new MetricKeyInfo("disk", MetricValueType.Number, "%", "磁盘使用率"),
        new MetricKeyInfo("net_rx", MetricValueType.Number, "B/s", "下行速率"),
        new MetricKeyInfo("net_tx", MetricValueType.Number, "B/s", "上行速率"),
    ];
}

/// <summary>内置指标键种子（幂等 upsert）：启动时注册目录中的全部指标键。</summary>
public sealed class MetricKeySeeder : IHostedService
{
    private readonly IMetricKeyRegistry _registry;

    public MetricKeySeeder(IMetricKeyRegistry registry)
    {
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var key in MetricKeyCatalog.BuiltIn)
        {
            _registry.Register(key.Key, key.ValueType, key.Unit, key.DisplayName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
