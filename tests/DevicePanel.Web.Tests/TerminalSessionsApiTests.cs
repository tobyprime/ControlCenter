using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>终端留痕查询 API：会话列表（含设备名/操作者/起止/关闭原因）与单会话记录。</summary>
public class TerminalSessionsApiTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    public sealed class Factory : TestAppFactory
    {
        public FakeTimeProvider Clock { get; } = new(Base);

        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            TestServices = services => services.AddSingleton<TimeProvider>(Clock);
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;
    private readonly long _deviceId;

    public TerminalSessionsApiTests()
    {
        _client = _factory.CreateClient();
        var login = _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" })
            .GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<DevicePanel.Web.Collectors.ICollectorRegistry>();
        _deviceId = registry.Create("留痕设备", ["机房A", DevicePanel.Web.Collectors.CollectorBuiltinTags.Device]).Id;
    }

    public void Dispose() => _factory.Dispose();

    private ITerminalStore Store() => _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ITerminalStore>();

    [Fact]
    public async Task Sessions_List_Enriches_Device_Name_And_Supports_Filters()
    {
        Store().OpenSession("s1", _deviceId, "admin", Base);
        Store().OpenSession("s2", _deviceId, "root", Base.AddHours(2));
        Store().CloseSession("s1", Base.AddMinutes(30), TerminalCloseReasons.Operator);

        var list = await ListAsync("/api/terminal/sessions");
        Assert.Equal(2, list.Count);

        var closed = list.Single(s => s.GetProperty("id").GetString() == "s1");
        Assert.Equal("留痕设备", closed.GetProperty("deviceName").GetString());
        Assert.Equal("admin", closed.GetProperty("operator").GetString());
        Assert.NotNull(closed.GetProperty("closedAtUtc").GetString());
        Assert.Equal(TerminalCloseReasons.Operator, closed.GetProperty("closeReason").GetString());

        var open = list.Single(s => s.GetProperty("id").GetString() == "s2");
        Assert.True(open.GetProperty("closedAtUtc").ValueKind is JsonValueKind.Null);
        Assert.Null(open.GetProperty("closeReason").GetString());

        // deviceId 过滤 + from/to 过滤（按打开时间）
        var unknownDevice = await _client.GetAsync($"/api/terminal/sessions?deviceId={_deviceId + 100}");
        Assert.Equal(HttpStatusCode.NotFound, unknownDevice.StatusCode);

        var byWindow = await ListAsync($"/api/terminal/sessions?from={Uri.EscapeDataString(Base.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}");
        var inWindow = Assert.Single(byWindow);
        Assert.Equal("s2", inWindow.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Sessions_Unknown_Device_Filter_Returns_404()
    {
        var response = await _client.GetAsync("/api/terminal/sessions?deviceId=424242");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Records_Returns_Session_Entries_In_Order()
    {
        Store().OpenSession("s1", _deviceId, "admin", Base);
        Store().Append("s1", TerminalEntryDirections.Input, "whoami", Base.AddSeconds(1));
        Store().Append("s1", TerminalEntryDirections.Output, "root", Base.AddSeconds(2));

        var records = await ListAsync("/api/terminal/sessions/s1/records");

        Assert.Equal(2, records.Count);
        Assert.Equal("input", records[0].GetProperty("direction").GetString());
        Assert.Equal("whoami", records[0].GetProperty("data").GetString());
        Assert.Equal("output", records[1].GetProperty("direction").GetString());
        Assert.Equal("root", records[1].GetProperty("data").GetString());
    }

    [Fact]
    public async Task Records_Unknown_Session_Returns_404()
    {
        var response = await _client.GetAsync("/api/terminal/sessions/no-such-session/records");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Requires_Login()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/terminal/sessions");
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
