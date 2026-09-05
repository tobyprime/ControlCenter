using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 采集器数据类型注册表（三期模块3 验收8）：新增一种采集器数据类型 = 注册一个 ICollectorDataType 实现，
/// 清单 API 随 DI 自动纳入，核心管道（信封/入库/告警/曲线）零改动。
/// </summary>
public class CollectorDataTypeTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task DataTypes_Lists_Builtin_Metrics_And_Logs()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/collectors/data-types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var types = await response.Content.ReadFromJsonAsync<JsonElement>();
        var byKey = types.EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!);
        Assert.True(byKey.ContainsKey("metrics"), "内置数据类型 metrics 应在清单中");
        Assert.True(byKey.ContainsKey("logs"), "内置数据类型 logs 应在清单中");
        Assert.Equal(JsonValueKind.String, byKey["metrics"].GetProperty("displayName").ValueKind);
    }

    [Fact]
    public async Task Requires_Login()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/collectors/data-types");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Catalog_Collects_All_Registered_Types_From_Di()
    {
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CollectorDataTypeCatalog>();

        var keys = catalog.List().Select(t => t.Key).ToList();

        Assert.Contains("metrics", keys);
        Assert.Contains("logs", keys);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }
}
