using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>指标查询与 MetricKey 注册 API：自动粒度、key 参数校验、注册表 CRUD、目标指标总览、按来源可用指标。</summary>
public class MetricsApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            TestServices = services => services.AddSingleton<TimeProvider>(Clock);
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Builtin_Keys_Are_Listed_Via_Api()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/metrics/keys");
        response.EnsureSuccessStatusCode();
        var keys = await response.Content.ReadFromJsonAsync<JsonElement>();

        var names = keys.EnumerateArray().Select(k => k.GetProperty("key").GetString()).ToHashSet();
        Assert.Subset(names, new HashSet<string?> { "cpu", "mem", "disk", "net_rx", "net_tx", "online" });
        Assert.All(keys.EnumerateArray(), k => Assert.True(k.GetProperty("builtIn").GetBoolean()));
    }

    [Fact]
    public async Task Register_Key_Then_Visible_In_List_And_Duplicate_Rejected()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "temp.cpu", valueType = "number", displayName = "CPU 温度", unit = "°C" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "temp.cpu", valueType = "number", displayName = "重复" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var badKey = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "Bad Key", valueType = "number", displayName = "非法" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        var badType = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "temp.gpu", valueType = "float", displayName = "非法类型" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

        var list = await (await client.GetAsync("/api/metrics/keys")).Content.ReadFromJsonAsync<JsonElement>();
        var registered = list.EnumerateArray().Single(k => k.GetProperty("key").GetString() == "temp.cpu");
        Assert.False(registered.GetProperty("builtIn").GetBoolean());
        Assert.Equal("°C", registered.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task Update_Key_Display_And_Delete_Protections()
    {
        var client = await AuthenticatedClientAsync();
        await client.PostAsJsonAsync("/api/metrics/keys", new { key = "custom.k", valueType = "enum", displayName = "自定义", unit = "" });

        var update = await client.PutAsJsonAsync("/api/metrics/keys/custom.k", new { displayName = "改 名", unit = "级" });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("改 名", updated.GetProperty("displayName").GetString());

        // 内置指标不可删除
        var builtinDelete = await client.DeleteAsync("/api/metrics/keys/cpu");
        Assert.Equal(HttpStatusCode.BadRequest, builtinDelete.StatusCode);

        var delete = await client.DeleteAsync("/api/metrics/keys/custom.k");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var missing = await client.DeleteAsync("/api/metrics/keys/custom.k");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Short_Range_Uses_Raw_Details()
    {
        var targetId = await SeedDataAsync();
        var series = await GetSeriesAsync(targetId, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z");

        Assert.Equal("raw", series.GetProperty("granularity").GetString());
        var cpuPoints = series.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "cpu").GetProperty("points");
        Assert.Equal(4, cpuPoints.GetArrayLength());
    }

    [Fact]
    public async Task Multi_Day_Range_Uses_Hourly_Aggregates()
    {
        var targetId = await SeedDataAsync();
        var series = await GetSeriesAsync(targetId, "2026-09-01T00:00:00Z", "2026-09-03T23:59:59Z");

        Assert.Equal("hour", series.GetProperty("granularity").GetString());
        // 四个样本都落在 9/3 00:00–01:00 的同一小时桶
        var cpuSeries = series.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "cpu");
        var point = Assert.Single(cpuSeries.GetProperty("points").EnumerateArray());
        Assert.Equal("2026-09-03T00:00:00Z", point.GetProperty("t").GetString());
        // 桶均值 = 明细均值（口径一致：cpu 10/20/30/40 → 25）
        Assert.Equal(25, point.GetProperty("v").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Long_Range_Uses_Daily_Aggregates()
    {
        var targetId = await SeedDataAsync();
        var series = await GetSeriesAsync(targetId, "2026-08-01T00:00:00Z", "2026-09-03T23:59:59Z", keys: "cpu");

        Assert.Equal("day", series.GetProperty("granularity").GetString());
        var cpuSeries = series.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "cpu");
        var point = Assert.Single(cpuSeries.GetProperty("points").EnumerateArray());
        Assert.Equal("2026-09-03T00:00:00Z", point.GetProperty("t").GetString());
        Assert.Equal(25, point.GetProperty("v").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Targets_Are_Isolated_In_Series()
    {
        var first = await SeedDataAsync(cpuBase: 10);
        var second = await SeedDataAsync(cpuBase: 90);

        var firstSeries = await GetSeriesAsync(first, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z", keys: "cpu");
        var secondSeries = await GetSeriesAsync(second, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z", keys: "cpu");

        Assert.All(firstSeries.GetProperty("series")[0].GetProperty("points").EnumerateArray(), p => Assert.True(p.GetProperty("v").GetDouble() < 50));
        Assert.All(secondSeries.GetProperty("series")[0].GetProperty("points").EnumerateArray(), p => Assert.True(p.GetProperty("v").GetDouble() > 50));
    }

    [Fact]
    public async Task Unregistered_Key_In_Series_Request_Returns_400()
    {
        var targetId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/metrics/{targetId}/series?keys=not.registered&from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_Lists_Reported_Keys_With_Latest_Values()
    {
        var targetId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/metrics/{targetId}/overview");
        response.EnsureSuccessStatusCode();
        var overview = await response.Content.ReadFromJsonAsync<JsonElement>();

        var byKey = overview.EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!);
        Assert.Equal(5, byKey.Count);
        Assert.Equal("CPU 使用率", byKey["cpu"].GetProperty("displayName").GetString());
        Assert.Equal("%", byKey["cpu"].GetProperty("unit").GetString());
        Assert.Equal(40, byKey["cpu"].GetProperty("latestValueNum").GetDouble(), precision: 6);
        Assert.Equal("number", byKey["cpu"].GetProperty("valueType").GetString());
    }

    [Fact]
    public async Task Unknown_Target_Returns_404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/metrics/999/series?keys=cpu&from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z");

        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Range_Or_Granularity_Returns_400()
    {
        var targetId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var inverted = await client.GetAsync($"/api/metrics/{targetId}/series?keys=cpu&from=2026-09-03T00:00:00Z&to=2026-09-01T00:00:00Z");
        Assert.Equal(400, (int)inverted.StatusCode);

        var badGranularity = await client.GetAsync($"/api/metrics/{targetId}/series?keys=cpu&from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&granularity=week");
        Assert.Equal(400, (int)badGranularity.StatusCode);
    }

    // --- 按来源可用指标（TOB-374 ①：选来源→选指标 只列该来源可用的指标） ---

    [Fact]
    public async Task Available_Without_Reported_Data_Falls_Back_To_Builtin_Keys_By_Type()
    {
        var client = await AuthenticatedClientAsync();
        var deviceId = await CreateTargetAsync(client, "可用指标设备", "device");
        var serviceId = CreateServiceTargetViaRegistry("可用指标服务");

        var deviceKeys = (await AvailableKeysAsync(client, deviceId)).EnumerateArray()
            .Select(k => k.GetProperty("key").GetString()).ToHashSet();
        Assert.Subset(deviceKeys, new HashSet<string?> { "cpu", "mem", "disk", "net_rx", "net_tx", "online" });
        Assert.DoesNotContain(deviceKeys, k => k == "status");
        Assert.DoesNotContain(deviceKeys, k => k == "latency_ms");

        var serviceKeys = (await AvailableKeysAsync(client, serviceId)).EnumerateArray()
            .Select(k => k.GetProperty("key").GetString()).ToHashSet();
        Assert.Subset(serviceKeys, new HashSet<string?> { "status", "latency_ms" });
        Assert.DoesNotContain(serviceKeys, k => k == "cpu");
        Assert.DoesNotContain(serviceKeys, k => k == "online");
    }

    [Fact]
    public async Task Available_Prefers_Reported_Keys_And_Carries_Registry_Metadata()
    {
        var targetId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var keys = (await AvailableKeysAsync(client, targetId)).EnumerateArray()
            .ToDictionary(k => k.GetProperty("key").GetString()!, k => k);

        Assert.Equal(new HashSet<string> { "cpu", "mem", "disk", "net_rx", "net_tx" }, keys.Keys.ToHashSet());
        Assert.Equal("CPU 使用率", keys["cpu"].GetProperty("displayName").GetString());
        Assert.Equal("%", keys["cpu"].GetProperty("unit").GetString());
        Assert.Equal("number", keys["cpu"].GetProperty("valueType").GetString());
        Assert.True(keys["cpu"].GetProperty("builtIn").GetBoolean());
    }

    [Fact]
    public async Task Available_Service_Reported_Keys_Win_Over_Fallback()
    {
        var serviceId = CreateServiceTargetViaRegistry("探针服务");
        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        store.Insert(serviceId, MetricKeys.LatencyMs,
            new MetricSample(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero), 42, null));

        var client = await AuthenticatedClientAsync();
        var names = (await AvailableKeysAsync(client, serviceId)).EnumerateArray()
            .Select(k => k.GetProperty("key").GetString()).ToHashSet();

        Assert.Equal(new HashSet<string?> { "latency_ms" }, names);
    }

    [Fact]
    public async Task Available_Unknown_Target_Returns_404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/metrics/999/available");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>种入 9/3 当天 00:10/00:20/00:30/00:40 四个样本：cpu=cpuBase+10*i，mem=20+10*i，disk=30+10*i，net_rx=100*(i+1)，net_tx=1000*(i+1)。</summary>
    private async Task<long> SeedDataAsync(double cpuBase = 10)
    {
        var client = await AuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/targets", new { name = $"指标设备-{Guid.NewGuid():N}"[..24], tags = Array.Empty<string>() });
        created.EnsureSuccessStatusCode();
        var targetId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        var day = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            var at = day.AddMinutes(10 * (i + 1));
            store.Insert(targetId, MetricKeys.Cpu, new MetricSample(at, cpuBase + 10 * i, null));
            store.Insert(targetId, MetricKeys.Mem, new MetricSample(at, 20 + 10 * i, null));
            store.Insert(targetId, MetricKeys.Disk, new MetricSample(at, 30 + 10 * i, null));
            store.Insert(targetId, MetricKeys.NetRx, new MetricSample(at, 100.0 * (i + 1), null));
            store.Insert(targetId, MetricKeys.NetTx, new MetricSample(at, 1000.0 * (i + 1), null));
        }

        return targetId;
    }

    private async Task<JsonElement> GetSeriesAsync(long targetId, string from, string to, string? granularity = null, string keys = "cpu,mem,disk,net_rx,net_tx")
    {
        var client = await AuthenticatedClientAsync();
        var url = $"/api/metrics/{targetId}/series?keys={Uri.EscapeDataString(keys)}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
        if (granularity is not null)
        {
            url += $"&granularity={granularity}";
        }

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<long> CreateTargetAsync(HttpClient client, string name, string type)
    {
        var created = await client.PostAsJsonAsync("/api/targets", new { type, name, tags = Array.Empty<string>() });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private long CreateServiceTargetViaRegistry(string name) =>
        _factory.Services.GetRequiredService<ITargetRegistry>().Create(TargetTypes.Service, name, Array.Empty<string>()).Id;

    private async Task<JsonElement> AvailableKeysAsync(HttpClient client, long targetId)
    {
        var response = await client.GetAsync($"/api/metrics/{targetId}/available");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
