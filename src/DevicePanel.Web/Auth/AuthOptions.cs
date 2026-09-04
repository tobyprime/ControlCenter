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

    /// <summary>
    /// 会话 Cookie SameSite 策略（Lax/Strict/None，忽略大小写），默认 Lax。
    /// 前端与面板不同源部署（如 Cloudflare Pages 独立域名）时按部署形态选择：
    /// 同站（同一 eTLD+1 的子域）保持 Lax；跨站（*.pages.dev）需 None。
    /// None 时自动附带 Secure（浏览器拒绝不带 Secure 的 SameSite=None）。
    /// </summary>
    public string SessionCookieSameSite { get; set; } = "Lax";

    /// <summary>解析会话 Cookie 的 SameSite 策略；非法值快速失败并给出配置键提示。</summary>
    public SameSiteMode ResolvedSessionSameSite()
    {
        switch (SessionCookieSameSite.Trim().ToLowerInvariant())
        {
            case "lax":
                return SameSiteMode.Lax;
            case "strict":
                return SameSiteMode.Strict;
            case "none":
                return SameSiteMode.None;
            default:
                throw new InvalidOperationException(
                    $"配置 DevicePanel:Auth:SessionCookieSameSite 的值 \"{SessionCookieSameSite}\" 无效：只接受 Lax / Strict / None（忽略大小写）。");
        }
    }

    /// <summary>会话 Cookie 是否需要 Secure 标记：SameSite=None 时必须（跨站 Cookie 安全要求）。</summary>
    public bool SessionCookieRequiresSecure => ResolvedSessionSameSite() == SameSiteMode.None;
}
