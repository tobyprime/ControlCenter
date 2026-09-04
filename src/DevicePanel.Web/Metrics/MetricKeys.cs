namespace DevicePanel.Web.Metrics;

/// <summary>内置指标 key 常量（随迁移播种到 metric_keys；代码内仅供采集链路引用，不含业务语义）。</summary>
public static class MetricKeys
{
    public const string Cpu = "cpu";
    public const string Mem = "mem";
    public const string Disk = "disk";
    public const string NetRx = "net_rx";
    public const string NetTx = "net_tx";

    /// <summary>设备在线状态（bool）：心跳写 true，TargetStatusScanner 在判定离线时写 false。</summary>
    public const string Online = "online";
}
