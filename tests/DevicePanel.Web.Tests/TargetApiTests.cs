using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>目标统一 API（TOB-360 模块 0）：目标列表、指标键注册表、统一序列查询（legacy 路由 + 通用路由）。</summary>
public class TargetApiTests : IDisposable
{
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Targets_List_Includes_Device_Target_With_Online_State()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "目标设备");

        var response = await client.GetAsync("/api/targets");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var target = payload.EnumerateArray().Single(t => t.GetProperty("deviceId").GetInt64() == deviceId);

        Assert.Equal("device", target.GetProperty("type").GetString());
        Assert.Equal("目标设备", target.GetProperty("name").GetString());
        Assert.False(target.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task MetricKeys_List_Contains_Built_In_Catalog()
    {
        var client = await AuthenticatedAsync();

        var response = await client.GetAsync("/api/metric-keys");
        response.EnsureSuccessStatusCode();
        var keys = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Contains(keys, k => k.GetProperty("key").GetString() == "cpu" && k.GetProperty("unit").GetString() == "%");
        Assert.Contains(keys, k => k.GetProperty("key").GetString() == "net_rx" && k.GetProperty("unit").GetString() == "B/s");
        Assert.All(keys, k => Assert.Contains(k.GetProperty("valueType").GetString()!, new[] { "number", "enum", "string", "bool" }));
    }

    [Fact]
    public async Task Series_Legacy_Metric_Routes_To_Device_Store()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "序列设备");
        var targetId = await GetTargetIdAsync(client, deviceId);
        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        store.Insert(deviceId, now, new MetricsPoint(now, 42, 50, 60, 100, 200));

        var response = await client.GetAsync($"/api/targets/{targetId}/series?metric=cpu&from=2026-09-03T11:00:00Z&to=2026-09-03T13:00:00Z");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("cpu", payload.GetProperty("metric").GetString());
        Assert.Equal("raw", payload.GetProperty("granularity").GetString());
        var point = Assert.Single(payload.GetProperty("points").EnumerateArray());
        Assert.Equal(42, point.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Series_Generic_Metric_Routes_To_Typed_Store()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "通用序列设备");
        var targetId = await GetTargetIdAsync(client, deviceId);
        await client.GetAsync("/healthz"); // 触发宿主就绪
        _factory.Services.GetRequiredService<IMetricKeyRegistry>()
            .Register("players", MetricValueType.Number, "人", "在线玩家数");
        var values = _factory.Services.GetRequiredService<IMetricValueStore>();
        var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        values.Insert(targetId, "players", now, new MetricValue(now, 7, null));

        var response = await client.GetAsync($"/api/targets/{targetId}/series?metric=players&from=2026-09-03T11:00:00Z&to=2026-09-03T13:00:00Z");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        var point = Assert.Single(payload.GetProperty("points").EnumerateArray());
        Assert.Equal(7, point.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Series_Rejects_Missing_Metric_And_Unknown_Target()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "校验目标");
        var targetId = await GetTargetIdAsync(client, deviceId);

        var noMetric = await client.GetAsync($"/api/targets/{targetId}/series");
        Assert.Equal(HttpStatusCode.BadRequest, noMetric.StatusCode);

        var unknownTarget = await client.GetAsync("/api/targets/99999/series?metric=cpu");
        Assert.Equal(HttpStatusCode.NotFound, unknownTarget.StatusCode);
    }

    private async Task<HttpClient> AuthenticatedAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<long> CreateDeviceAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/devices", new { name, tags = Array.Empty<string>() });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetInt64();
    }

    private async Task<long> GetTargetIdAsync(HttpClient client, long deviceId)
    {
        var payload = await (await client.GetAsync("/api/targets")).Content.ReadFromJsonAsync<JsonElement>();
        return payload.EnumerateArray().Single(t => t.GetProperty("deviceId").GetInt64() == deviceId)
            .GetProperty("id").GetInt64();
    }

    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }
}
