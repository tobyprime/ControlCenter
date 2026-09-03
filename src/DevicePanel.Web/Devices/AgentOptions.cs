namespace DevicePanel.Web.Devices;

/// <summary>agent 接入与心跳判定参数（appsettings: DevicePanel:Agent）。</summary>
public sealed class AgentOptions
{
    public const string SectionName = "DevicePanel:Agent";

    /// <summary>agent 心跳周期（秒），默认 30s。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>WS 建立后等待 agent 发送 auth 信封的超时（秒）。</summary>
    public int AuthTimeoutSeconds { get; set; } = 10;

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

    /// <summary>离线判定阈值：连续 2 个心跳周期未收到心跳即视为离线（默认 60s）。</summary>
    public TimeSpan OfflineAfter => TimeSpan.FromSeconds(2L * HeartbeatIntervalSeconds);
}
