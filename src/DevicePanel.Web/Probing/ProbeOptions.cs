namespace DevicePanel.Web.Probing;

/// <summary>探针调度与判定参数（默认对齐 PRD 技术默认值：间隔 60 秒、连续 3 次失败判定服务异常）。</summary>
public sealed class ProbeOptions
{
    public const string SectionName = "DevicePanel:Probe";

    public int DefaultIntervalSeconds { get; set; } = 60;

    public int MinIntervalSeconds { get; set; } = 10;

    public int MaxIntervalSeconds { get; set; } = 3600;

    /// <summary>连续失败次数达到该值判定服务异常（仅在转换点写一次 status=false）。</summary>
    public int FailureThreshold { get; set; } = 3;

    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>调度轮询节拍：到期探针的扫描间隔。</summary>
    public int PollSeconds { get; set; } = 5;
}
