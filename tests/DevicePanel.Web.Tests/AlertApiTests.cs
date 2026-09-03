using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Alerting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>告警配置 API 测试：napcat 设置（token 不回传）、阈值（全局/按设备覆盖）校验、待发队列可见。</summary>
public class AlertApiTests : IDisposable
{
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Settings_Put_Then_Get_Hides_Token_But_Reports_Presence()
    {
        var client = await AuthenticatedAsync();

        var put = await client.PutAsJsonAsync("/api/alerts/settings", new
        {
            baseUrl = "http://127.0.0.1:3000",
            token = "napcat-secret",
            targetType = "private",
            targetId = "10001",
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/api/alerts/settings");
        get.EnsureSuccessStatusCode();
        var raw = await get.Content.ReadAsStringAsync();
        var payload = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("http://127.0.0.1:3000", payload.GetProperty("napcat").GetProperty("baseUrl").GetString());
        Assert.True(payload.GetProperty("napcat").GetProperty("tokenSet").GetBoolean());
        Assert.Equal("private", payload.GetProperty("napcat").GetProperty("targetType").GetString());
        Assert.Equal("10001", payload.GetProperty("napcat").GetProperty("targetId").GetString());
        Assert.DoesNotContain("napcat-secret", raw);
    }

    [Fact]
    public async Task Settings_Put_Without_Token_Keeps_Existing_And_Empty_Token_Clears()
    {
        var client = await AuthenticatedAsync();
        await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl = "http://a:1", token = "keep-me", targetType = "group", targetId = "42" });

        await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl = "http://b:2" });
        Assert.True(await TokenSetAsync());

        await client.PutAsJsonAsync("/api/alerts/settings", new { token = "" });
        Assert.False(await TokenSetAsync());
    }

    [Theory]
    [InlineData("ftp://a:1", "t", "private", "10001", "baseUrl")]
    [InlineData("http://a:1", "t", "channel", "10001", "targetType")]
    [InlineData("http://a:1", "t", "private", "abc", "targetId")]
    [InlineData("http://a:1", "t", "private", "", "targetId")]
    public async Task Settings_Put_Rejects_Invalid_Input(string baseUrl, string token, string targetType, string targetId, string _)
    {
        var client = await AuthenticatedAsync();
        var put = await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl, token, targetType, targetId });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Thresholds_Return_Builtin_Defaults_And_Accept_Global_Update()
    {
        var client = await AuthenticatedAsync();

        var payload = await GetThresholdsAsync();
        Assert.Equal(90, payload.GetProperty("global").GetProperty("cpu").GetDouble());
        Assert.Equal(90, payload.GetProperty("global").GetProperty("mem").GetDouble());
        Assert.Equal(90, payload.GetProperty("global").GetProperty("disk").GetDouble());
        Assert.Empty(payload.GetProperty("overrides").EnumerateArray());

        var put = await client.PutAsJsonAsync("/api/alerts/thresholds/global", new { metric = "cpu", value = 75 });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        payload = await GetThresholdsAsync();
        Assert.Equal(75, payload.GetProperty("global").GetProperty("cpu").GetDouble());
        Assert.Equal(90, payload.GetProperty("global").GetProperty("mem").GetDouble());
    }

    [Fact]
    public async Task Thresholds_Device_Override_Add_List_Delete()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "覆盖设备");

        var put = await client.PutAsJsonAsync($"/api/alerts/thresholds/devices/{deviceId}", new { metric = "cpu", value = 50 });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var payload = await GetThresholdsAsync();
        var overrides = payload.GetProperty("overrides");
        var entry = Assert.Single(overrides.EnumerateArray());
        Assert.Equal(deviceId, entry.GetProperty("deviceId").GetInt64());
        Assert.Equal("覆盖设备", entry.GetProperty("deviceName").GetString());
        Assert.Equal("cpu", entry.GetProperty("metric").GetString());
        Assert.Equal(50, entry.GetProperty("value").GetDouble());

        var delete = await client.DeleteAsync($"/api/alerts/thresholds/devices/{deviceId}/cpu");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await GetThresholdsAsync()).GetProperty("overrides").EnumerateArray());

        var missing = await client.DeleteAsync($"/api/alerts/thresholds/devices/{deviceId}/cpu");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("net", 50, HttpStatusCode.BadRequest)]
    [InlineData("cpu", 0, HttpStatusCode.BadRequest)]
    [InlineData("cpu", 101, HttpStatusCode.BadRequest)]
    public async Task Thresholds_Put_Rejects_Unknown_Metric_And_Out_Of_Range_Value(string metric, double value, HttpStatusCode expected)
    {
        var client = await AuthenticatedAsync();
        var put = await client.PutAsJsonAsync("/api/alerts/thresholds/global", new { metric, value });
        Assert.Equal(expected, put.StatusCode);
    }

    [Fact]
    public async Task Thresholds_Device_Put_Returns_NotFound_For_Unknown_Device()
    {
        var client = await AuthenticatedAsync();
        var put = await client.PutAsJsonAsync("/api/alerts/thresholds/devices/999", new { metric = "cpu", value = 50 });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Queue_Endpoint_Lists_Pending_Alerts_Visible_While_Napcat_Down()
    {
        var client = await AuthenticatedAsync();
        var outbox = _factory.Services.GetRequiredService<IAlertOutboxStore>();
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("设备离线告警", "设备「q-1」已离线"), DateTimeOffset.UtcNow);
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("指标越限告警", "设备「q-2」CPU 使用率 98.0%"), DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/alerts/queue");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, payload.GetProperty("count").GetInt64());
        var items = payload.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("设备离线告警", items[0].GetProperty("title").GetString());
        Assert.Equal(NapcatNotifier.ChannelNameValue, items[0].GetProperty("channel").GetString());
        Assert.Equal("设备「q-2」CPU 使用率 98.0%", items[1].GetProperty("content").GetString());
    }

    private async Task<bool> TokenSetAsync()
    {
        var client = await AuthenticatedAsync();
        var payload = await (await client.GetAsync("/api/alerts/settings")).Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("napcat").GetProperty("tokenSet").GetBoolean();
    }

    private async Task<JsonElement> GetThresholdsAsync()
    {
        var client = await AuthenticatedAsync();
        var response = await client.GetAsync("/api/alerts/thresholds");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<long> CreateDeviceAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/devices", new { name, tags = new[] { "告警" } });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> AuthenticatedAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }
}
