using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 前端送达链路缓存策略（TOB-373 发版后公网仍现旧界面）：
/// - HTML 壳与 index.html：no-cache，浏览器每次导航回源校验，发版即换新；
/// - 带 hash 的 /assets/*：内容寻址，长缓存 immutable。
/// </summary>
public class StaticFileCacheHeadersTests : IClassFixture<StaticFileCacheHeadersTests.Factory>
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private readonly Factory _factory;

    public StaticFileCacheHeadersTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task App_Shell_Is_No_Cache_So_New_Deploys_Are_Picked_Up_Immediately()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache, "SPA 壳应带 Cache-Control: no-cache");
        Assert.Null(response.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Index_Html_Via_Static_Files_Is_Also_No_Cache()
    {
        // /index.html 带扩展名，走静态文件中间件而非壳回退，是浏览器可能直取的另一入口
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache, "index.html 应带 Cache-Control: no-cache");
    }

    [Fact]
    public async Task Hashed_Assets_Are_Long_Cached_Immutable()
    {
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var assetFile = Directory.EnumerateFiles(Path.Combine(webRoot!, "assets"), "*.js").First();
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/assets/" + Path.GetFileName(assetFile));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("max-age=31536000", cacheControl);
        Assert.Contains("immutable", cacheControl);
    }
}
