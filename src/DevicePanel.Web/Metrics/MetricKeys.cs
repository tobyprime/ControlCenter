namespace DevicePanel.Web.Metrics;

/// <summary>内置指标 key 常量（随迁移播种到 metric_keys；代码内仅供采集链路引用，不含业务语义）。</summary>
public static class MetricKeys
{
    public const string Cpu = "cpu";
    public const string Mem = "mem";
    public const string Disk = "disk";
    public const string NetRx = "net_rx";
    public const string NetTx = "net_tx";

    /// <summary>温度（°C）：agent 上报 hwmon/thermal 中 CPU 相关传感器的最大值；无传感器的设备不产出该指标。</summary>
    public const string Temp = "temp";

    /// <summary>温度传感器名（string）：与 temp 同点上报，保留读数来源。</summary>
    public const string TempSensor = "temp_sensor";

    /// <summary>磁盘读取速率（B/s）：agent 经 /proc/diskstats 整盘扇区差值计算。</summary>
    public const string DiskRx = "disk_rx";

    /// <summary>磁盘写入速率（B/s）。</summary>
    public const string DiskTx = "disk_tx";

    /// <summary>内存已用（B）：total - available。</summary>
    public const string MemUsed = "mem_used";

    /// <summary>内存总量（B）。</summary>
    public const string MemTotal = "mem_total";

    /// <summary>设备在线状态（bool）：心跳写 true，TargetStatusScanner 在判定离线时写 false。</summary>
    public const string Online = "online";

    /// <summary>服务可达状态（bool，模块2 探针）：成功写 true；连续失败达到阈值在判定异常的转换点写一次 false。</summary>
    public const string Status = "status";

    /// <summary>探针响应耗时（ms，模块2）：仅成功请求产生样本。</summary>
    public const string LatencyMs = "latency_ms";
}
