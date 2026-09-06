namespace DevicePanel.Web.Infrastructure;

/// <summary>
/// 前端托管形态：默认内嵌（后端托管 SPA，同源）；公网前端独立部署（如 Cloudflare Pages）
/// 后可关闭内嵌托管，把集群公网入口收紧为 API-only——仅放行 /healthz、/api/*、/agent/ws。
/// </summary>
public sealed class ServingOptions
{
    public const string SectionName = "DevicePanel:Serving";

    /// <summary>是否由后端托管 SPA（静态文件 + 壳回退）。false 时非 API 路径一律 404。</summary>
    public bool EnableFrontend { get; set; } = true;
}
