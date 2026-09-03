using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>指标查询 API：自动粒度选择（大跨度聚合/小跨度明细）、显式覆盖、参数校验与设备隔离。</summary>
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
    public async Task Short_Range_Uses_Raw_Details()
    {
        var deviceId = await SeedDataAsync();
        var series = await GetSeriesAsync(deviceId, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z");

        Assert.Equal("raw", series.GetProperty("granularity").GetString());
        Assert.Equal(4, series.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Multi_Day_Range_Uses_Hourly_Aggregates()
    {
        var deviceId = await SeedDataAsync();
        var series = await GetSeriesAsync(deviceId, "2026-09-01T00:00:00Z", "2026-09-03T23:59:59Z");

        Assert.Equal("hour", series.GetProperty("granularity").GetString());
        // 四个样本都落在 9/3 00:00–01:00 的同一小时桶
        Assert.Equal(1, series.GetProperty("points").GetArrayLength());
        var point = series.GetProperty("points")[0];
        Assert.Equal("2026-09-03T00:00:00Z", point.GetProperty("t").GetString());
        // 桶均值 = 明细均值（口径一致：cpu 10/20/30/40，mem 20/30/40/50，disk 30/40/50/60）
        Assert.Equal(25, point.GetProperty("cpu").GetDouble(), precision: 6);
        Assert.Equal(35, point.GetProperty("mem").GetDouble(), precision: 6);
        Assert.Equal(45, point.GetProperty("disk").GetDouble(), precision: 6);
        Assert.Equal(250, point.GetProperty("netRx").GetDouble(), precision: 6);
        Assert.Equal(2500, point.GetProperty("netTx").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Long_Range_Uses_Daily_Aggregates()
    {
        var deviceId = await SeedDataAsync();
        var series = await GetSeriesAsync(deviceId, "2026-08-01T00:00:00Z", "2026-09-03T23:59:59Z");

        Assert.Equal("day", series.GetProperty("granularity").GetString());
        var point = Assert.Single(series.GetProperty("points").EnumerateArray());
        Assert.Equal("2026-09-03T00:00:00Z", point.GetProperty("t").GetString());
        Assert.Equal(25, point.GetProperty("cpu").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Explicit_Granularity_Overrides_Auto()
    {
        var deviceId = await SeedDataAsync();
        var series = await GetSeriesAsync(deviceId, "2026-09-01T00:00:00Z", "2026-09-03T23:59:59Z", "raw");

        Assert.Equal("raw", series.GetProperty("granularity").GetString());
        Assert.Equal(4, series.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Devices_Are_Isolated_In_Series()
    {
        // 验收 5：曲线与所选设备对应，多设备数据不串
        var first = await SeedDataAsync(cpuBase: 10);
        var second = await SeedDataAsync(cpuBase: 90);

        var firstSeries = await GetSeriesAsync(first, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z");
        var secondSeries = await GetSeriesAsync(second, "2026-09-03T00:00:00Z", "2026-09-03T02:00:00Z");

        Assert.All(firstSeries.GetProperty("points").EnumerateArray(), p => Assert.True(p.GetProperty("cpu").GetDouble() < 50));
        Assert.All(secondSeries.GetProperty("points").EnumerateArray(), p => Assert.True(p.GetProperty("cpu").GetDouble() > 50));
    }

    [Fact]
    public async Task Unknown_Device_Returns_404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/metrics/999/series?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z");

        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Range_Or_Granularity_Returns_400()
    {
        var deviceId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var inverted = await client.GetAsync($"/api/metrics/{deviceId}/series?from=2026-09-03T00:00:00Z&to=2026-09-01T00:00:00Z");
        Assert.Equal(400, (int)inverted.StatusCode);

        var badGranularity = await client.GetAsync($"/api/metrics/{deviceId}/series?from=2026-09-01T00:00:00Z&to=2026-09-02T00:00:00Z&granularity=week");
        Assert.Equal(400, (int)badGranularity.StatusCode);
    }

    [Fact]
    public async Task Missing_Range_Defaults_To_Last_24h_Auto()
    {
        var deviceId = await SeedDataAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/metrics/{deviceId}/series");
        response.EnsureSuccessStatusCode();
        var series = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 默认最近 24h → 小时聚合
        Assert.Equal("hour", series.GetProperty("granularity").GetString());
        Assert.Equal(1, series.GetProperty("points").GetArrayLength());
    }

    /// <summary>种入 9/3 当天 00:10/00:20/00:30/00:40 四个样本：cpu=cpuBase+10*i，mem=20+10*i，disk=30+10*i，netRx=100*i，netTx=1000*i。</summary>
    private async Task<long> SeedDataAsync(double cpuBase = 10)
    {
        var client = await AuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/devices", new { name = $"指标设备-{Guid.NewGuid():N}"[..24], tags = Array.Empty<string>() });
        created.EnsureSuccessStatusCode();
        var deviceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        var day = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            store.Insert(deviceId, day.AddMinutes(10 * (i + 1)), new MetricsPoint(
                day.AddMinutes(10 * (i + 1)),
                cpuBase + 10 * i,
                20 + 10 * i,
                30 + 10 * i,
                100 * (i + 1),
                1000 * (i + 1)));
        }

        return deviceId;
    }

    private async Task<JsonElement> GetSeriesAsync(long deviceId, string from, string to, string? granularity = null)
    {
        var client = await AuthenticatedClientAsync();
        var url = $"/api/metrics/{deviceId}/series?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
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
}
