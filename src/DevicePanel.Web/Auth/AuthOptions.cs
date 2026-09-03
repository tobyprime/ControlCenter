namespace DevicePanel.Web.Auth;

/// <summary>认证与会话相关默认值/配置项（appsettings: DevicePanel:Auth）。</summary>
public sealed class AuthOptions
{
    public const string SectionName = "DevicePanel:Auth";

    /// <summary>初始账号用户名（仅当用户表为空时用于初始化）。</summary>
    public string InitialUsername { get; set; } = "admin";

    /// <summary>
    /// 初始密码。未配置时首次启动自动生成随机密码并打印到日志。
    /// </summary>
    public string? InitialPassword { get; set; }

    /// <summary>连续登录失败次数达到该值后锁定，默认 5 次。</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>登录失败锁定时长（秒），默认 600 秒（10 分钟）。</summary>
    public int LockoutSeconds { get; set; } = 600;

    /// <summary>会话有效期（小时，绝对过期），默认 24 小时。</summary>
    public int SessionHours { get; set; } = 24;

    /// <summary>会话 Cookie 名称。</summary>
    public string SessionCookieName { get; set; } = "device_panel_session";
}
