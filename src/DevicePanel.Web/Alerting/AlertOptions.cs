namespace DevicePanel.Web.Alerting;

/// <summary>告警分发与规则扫描参数（appsettings: DevicePanel:Alert）。</summary>
public sealed class AlertOptions
{
    public const string SectionName = "DevicePanel:Alert";

    /// <summary>规则扫描周期（秒）：在线状态采样器与无数据规则扫描共用该节奏。</summary>
    public int ScanSeconds { get; set; } = 15;

    /// <summary>待发队列轮询间隔（秒）：队列空闲时的检查节奏。</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>发送失败重试间隔（秒）：napcat 不可用时的退避节奏。</summary>
    public int RetrySeconds { get; set; } = 30;

    /// <summary>新建规则时防抖窗口的默认值（秒）：持续超过该时长才告警；规则实例可单独覆盖（sustain_seconds）。</summary>
    public int SustainSeconds { get; set; } = 60;

    /// <summary>
    /// 新建规则时同一事件重发间隔的默认值（分钟）：0 = 恢复前不重发（一个事件只发一次）；
    /// 规则实例可单独覆盖（repeat_minutes），调大后表示持续触发期间每隔 N 分钟提醒一次。
    /// </summary>
    public int RepeatMinutes { get; set; } = 0;

    public TimeSpan ScanInterval => TimeSpan.FromSeconds(ScanSeconds);

    public TimeSpan SustainWindow => TimeSpan.FromSeconds(SustainSeconds);

    public TimeSpan RepeatWindow => TimeSpan.FromMinutes(RepeatMinutes);
}
