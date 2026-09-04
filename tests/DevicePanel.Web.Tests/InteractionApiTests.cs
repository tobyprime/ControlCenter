using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>交互模式查询 API：全量注册模式清单与目标声明入口（目标详情页交互区渲染数据源）。</summary>
public class InteractionApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    /// <summary>声明引用未注册模式的目录：验证清单渲染跳过未注册 key（向前兼容）。</summary>
    public sealed class GhostDeclarationFactory : TestAppFactory
    {
        public GhostDeclarationFactory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            TestServices = services => services.AddSingleton<IInteractionModeCatalog>(new DeclaringCatalog(["shell", "ghost-mode"]));
        }
    }

    private sealed class DeclaringCatalog(IReadOnlyList<string> keys) : IInteractionModeCatalog
    {
        public IReadOnlyList<string> GetDeclaredModeKeys(long targetId) => keys;
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;
    private readonly long _deviceId;

    public InteractionApiTests()
    {
        _client = _factory.CreateClient();
        var login = _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" })
            .GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DevicePanel.Web.Devices.IDeviceRegistry>();
        _deviceId = devices.Create("交互设备", ["机房A"]).Device.Id;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Modes_Lists_Registered_Shell_Mode()
    {
        var modes = await ListAsync("/api/interactions/modes");

        var shell = Assert.Single(modes, m => m.GetProperty("key").GetString() == ShellInteractionMode.ModeKey);
        Assert.Equal("Shell 终端", shell.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.String, shell.GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task DeviceModes_Existing_Device_Returns_Shell_Entry()
    {
        var modes = await ListAsync($"/api/devices/{_deviceId}/interaction-modes");

        var shell = Assert.Single(modes);
        Assert.Equal(ShellInteractionMode.ModeKey, shell.GetProperty("key").GetString());
        Assert.Equal("Shell 终端", shell.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task DeviceModes_Unknown_Device_Returns_404()
    {
        var response = await _client.GetAsync($"/api/devices/{_deviceId + 100}/interaction-modes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeviceModes_Skips_Unregistered_Declared_Keys()
    {
        using var factory = new GhostDeclarationFactory();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<DevicePanel.Web.Devices.IDeviceRegistry>();
        var deviceId = devices.Create("声明设备", []).Device.Id;

        var response = await client.GetAsync($"/api/devices/{deviceId}/interaction-modes");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        var mode = Assert.Single(payload.EnumerateArray());
        Assert.Equal(ShellInteractionMode.ModeKey, mode.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Requires_Login()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/interactions/modes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<List<JsonElement>> ListAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.EnumerateArray().ToList();
    }
}
