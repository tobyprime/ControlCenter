namespace DevicePanel.Web.Infrastructure;

/// <summary>
/// wwwroot 解析：ContentRoot 与应用目录（AppContext.BaseDirectory）双候选。
/// 发布产物从仓库根目录运行时 ContentRoot 下没有 wwwroot，需回退到应用目录自带的一份，
/// 静态文件中间件与拦截中间件的壳回退共用此候选序列，保证两者一致。
/// </summary>
public static class WebRootResolver
{
    public static IEnumerable<string> CandidateRoots(string contentRootPath, string? baseDirectoryOverride = null)
    {
        yield return Path.Combine(contentRootPath, "wwwroot");
        yield return Path.Combine(baseDirectoryOverride ?? AppContext.BaseDirectory, "wwwroot");
    }

    /// <summary>返回第一个真实存在的 wwwroot 目录；均不存在时返回 null。</summary>
    public static string? ResolveWebRoot(string contentRootPath, string? baseDirectoryOverride = null)
    {
        return CandidateRoots(contentRootPath, baseDirectoryOverride).FirstOrDefault(Directory.Exists);
    }
}
