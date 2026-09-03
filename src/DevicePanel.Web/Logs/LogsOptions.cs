namespace DevicePanel.Web.Logs;

/// <summary>日志查看配置：拉取为面板按需下发（logs.*），无面板侧落库。</summary>
public sealed class LogsOptions
{
    public const string SectionName = "DevicePanel:Logs";

    /// <summary>等待 agent 响应日志请求的超时秒数（journalctl/docker logs 为一次性只读命令，超时按网关超时返回）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>服务清单中的一个条目：kind 为 systemd / docker。</summary>
public sealed record LogServiceInfo(string Name, string Kind, string Description);

/// <summary>尾部日志中的一行：ts 为 ISO-8601 UTC（缺失时为空串），level ∈ error/warn/info/debug。</summary>
public sealed record LogLineInfo(string Ts, string Level, string Message);

/// <summary>设备不在线，无法下发日志请求。</summary>
public sealed class DeviceOfflineException(string message) : Exception(message);

/// <summary>agent 明确返回 logs.error（服务不存在/命令失败等）。</summary>
public sealed class AgentLogException(string message) : Exception(message);

/// <summary>等待 agent 响应超时。</summary>
public sealed class AgentTimeoutException(string message) : Exception(message);
