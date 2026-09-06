using System.Net;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// srv 公网入口收紧为 API-only（TOB-373：前端由 Cloudflare Pages 独立承载后）：
/// 配置 DevicePanel:Serving:EnableFrontend=false 时，集群后端只放行
/// /healthz、/api/*（仍走登录门禁）、/agent/ws（agent token 认证），SPA 托管整体下线。
/// </summary>
public class ApiOnlyServingTests : IClassFixture<ApiOnlyServingTests.Factory>
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            Settings["DevicePanel:Serving:EnableFrontend"] = "false";
        }
    }

    private readonly Factory _factory;

    public ApiOnlyServingTests(Factory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/index.html")]
    public async Task Frontend_Routes_Return_404_When_Spa_Hosting_Is_Disabled(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_Endpoint_Stays_Anonymously_Reachable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_Still_Gated_By_Session_Auth()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/collectors");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
