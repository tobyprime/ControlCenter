using DevicePanel.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DevicePanel.Web.Tests;

public class AppShellPathTests
{
    [Fact]
    public void Resolves_Shell_From_WebRootPath()
    {
        var (contentRoot, shellPath) = CreateDirWithShell();
        var env = new FakeEnv(webRootPath: Path.Combine(contentRoot, "wwwroot"), contentRootPath: "/nonexistent");

        var path = AuthenticationGateMiddleware.ResolveShellPath(env);

        Assert.Equal(shellPath, path);
    }

    [Fact]
    public void Falls_Back_To_ContentRoot_When_WebRoot_Missing()
    {
        var (contentRoot, _) = CreateDirWithShell();
        var env = new FakeEnv(webRootPath: null, contentRootPath: contentRoot);

        var path = AuthenticationGateMiddleware.ResolveShellPath(env);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Falls_Back_To_BaseDirectory_When_ContentRoot_Has_No_wwwroot()
    {
        // 模拟发布布局：ContentRoot（CWD）下无 wwwroot，但应用目录（BaseDirectory）自带 wwwroot
        var (contentRoot, _) = CreateDirWithShell();
        var env = new FakeEnv(webRootPath: null, contentRootPath: "/nonexistent");

        var path = AuthenticationGateMiddleware.ResolveShellPath(env, baseDirectoryOverride: contentRoot);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    private static (string ContentRoot, string ShellPath) CreateDirWithShell()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "device-panel-shell-tests", Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(contentRoot, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        var shellPath = Path.Combine(wwwroot, "index.html");
        File.WriteAllText(shellPath, "<html lang=\"zh-CN\"></html>");
        return (contentRoot, shellPath);
    }

    private sealed class FakeEnv(string? webRootPath, string contentRootPath) : IWebHostEnvironment
    {
        public string? WebRootPath { get; set; } = webRootPath;
        public string ContentRootPath { get; set; } = contentRootPath;
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Development";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
