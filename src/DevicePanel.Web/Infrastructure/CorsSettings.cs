namespace DevicePanel.Web.Infrastructure;

/// <summary>
/// CORS 允许来源配置（appsettings: DevicePanel:Cors）。
/// 面板与前端不同源部署（如前端托管 Cloudflare Pages）时，配置前端来源列表以放行
/// 带 Cookie 的跨域 API/WS 请求；为空（默认）时不启用 CORS，保持同源内嵌形态不变。
/// </summary>
public sealed class CorsSettings
{
    public const string SectionName = "DevicePanel:Cors";

    /// <summary>允许的前端来源，分号或逗号分隔（如 https://panel.example.com,https://cc.pages.dev）。</summary>
    public string AllowedOrigins { get; set; } = string.Empty;

    /// <summary>解析来源列表：按分号/逗号拆分、去空白、去空项、去重（忽略大小写）。</summary>
    public IReadOnlyList<string> ResolvedAllowedOrigins()
    {
        return AllowedOrigins
            .Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
