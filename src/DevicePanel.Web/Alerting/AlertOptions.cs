namespace DevicePanel.Web.Alerting;

/// <summary>告警分发参数（appsettings: DevicePanel:Alert）。</summary>
public sealed class AlertOptions
{
    public const string SectionName = "DevicePanel:Alert";

    /// <summary>离线告警扫描周期（秒）：判定离线后最多延迟一个周期发出告警。</summary>
    public int ScanSeconds { get; set; } = 15;

    /// <summary>待发队列轮询间隔（秒）：队列空闲时的检查节奏。</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>发送失败重试间隔（秒）：napcat 不可用时的退避节奏。</summary>
    public int RetrySeconds { get; set; } = 30;

    /// <summary>越限持续时长（秒）：持续超过该时长才告警（默认 60s，对应完成标准 2）。</summary>
    public int SustainSeconds { get; set; } = 60;

    /// <summary>
    /// 同一越限事件的重发间隔（分钟）。防刷屏默认值：0 = 恢复前不重发（一个事件只发一次），
    /// 调大后表示持续越限期间每隔 N 分钟提醒一次。
    /// </summary>
    public int RepeatMinutes { get; set; } = 0;

    public TimeSpan ScanInterval => TimeSpan.FromSeconds(ScanSeconds);

    public TimeSpan SustainWindow => TimeSpan.FromSeconds(SustainSeconds);

    public TimeSpan RepeatWindow => TimeSpan.FromMinutes(RepeatMinutes);
}
