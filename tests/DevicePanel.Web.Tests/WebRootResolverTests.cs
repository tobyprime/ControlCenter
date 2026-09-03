using DevicePanel.Web.Auth;
using DevicePanel.Web.Infrastructure;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// WebRoot 解析与拦截中间件的壳回退必须同源：
/// 发布产物从仓库根目录运行（README 快速开始）时，ContentRoot 下无 wwwroot，
/// 需回退到应用目录（AppContext.BaseDirectory）自带的 wwwroot，静态文件才可用。
/// </summary>
public class WebRootResolverTests
{
    [Fact]
    public void Prefers_ContentRoot_wwwroot_When_Present()
    {
        var contentRoot = CreateTreeWithWebRoot();

        var resolved = WebRootResolver.ResolveWebRoot(contentRoot, baseDirectoryOverride: "/nonexistent");

        Assert.Equal(Path.Combine(contentRoot, "wwwroot"), resolved);
    }

    [Fact]
    public void Falls_Back_To_BaseDirectory_wwwroot_When_ContentRoot_Has_None()
    {
        var baseDirectory = CreateTreeWithWebRoot();

        var resolved = WebRootResolver.ResolveWebRoot("/nonexistent", baseDirectoryOverride: baseDirectory);

        Assert.Equal(Path.Combine(baseDirectory, "wwwroot"), resolved);
    }

    [Fact]
    public void Returns_Null_When_No_wwwroot_Anywhere()
    {
        var resolved = WebRootResolver.ResolveWebRoot("/nonexistent", baseDirectoryOverride: "/also-nonexistent");

        Assert.Null(resolved);
    }

    [Fact]
    public void Middleware_Shell_Resolution_Uses_Same_Candidate_Order()
    {
        var contentRoot = CreateTreeWithWebRoot();
        var env = new AppShellPathTests.FakeEnv(webRootPath: null, contentRootPath: contentRoot);

        var shell = AuthenticationGateMiddleware.ResolveShellPath(env, baseDirectoryOverride: "/nonexistent");

        Assert.NotNull(shell);
        Assert.True(File.Exists(shell));
    }

    private static string CreateTreeWithWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "device-panel-webroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html lang=\"zh-CN\"></html>");
        return root;
    }
}
