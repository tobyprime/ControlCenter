namespace DevicePanel.Web.Metrics;

/// <summary>指标存储与保留策略参数（appsettings: DevicePanel:Metrics）。</summary>
public sealed class MetricsOptions
{
    public const string SectionName = "DevicePanel:Metrics";

    /// <summary>指标保留天数（明细与聚合一致），默认约 30 天。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>过期清理任务的执行间隔（分钟），默认 6 小时。</summary>
    public int CleanupIntervalMinutes { get; set; } = 360;
}
