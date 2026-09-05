namespace DevicePanel.Web.Control;

/// <summary>控制下发配置（appsettings "DevicePanel:Control"）。</summary>
public sealed class ControlOptions
{
    public const string SectionName = "DevicePanel:Control";

    /// <summary>控制下发等待 agent 回执的超时秒数（PRD 约定 10s 级；离线不受此影响，立即失败）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}

/// <summary>控制下发结果状态（与控制留痕 status 同源：success / failure / timeout）。</summary>
public static class ControlLogStatuses
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Timeout = "timeout";
}

/// <summary>
/// 一次控制下发的最终结论（回执/错误/超时/离线），留痕与 HTTP 映射共用。
/// 离线在留痕里折算为 failure（三态口径），DeviceOffline 单独标注供 HTTP 映射 409（对齐日志按需查询）。
/// </summary>
public sealed record ControlInvokeOutcome(string Status, string? Message, bool DeviceOffline = false)
{
    public bool Success => Status == ControlLogStatuses.Success;
}

/// <summary>控制器不存在（采集器未关联 agent、agent 未声明该 key）。</summary>
public sealed class ControlNotFoundException(string message) : Exception(message);

/// <summary>下发参数不满足该控制类型的声明 schema。</summary>
public sealed class ControlValidationException(string message) : Exception(message);
