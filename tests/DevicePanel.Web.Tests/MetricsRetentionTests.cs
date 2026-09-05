using DevicePanel.Web.Targets;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>保留策略：超过保留期（默认 30 天）的明细与聚合被清理任务删除，期内数据保留。</summary>
public class MetricsRetentionTests : IDisposable
{
    private readonly TempSqliteDatabase _database = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly long _targetId;

    public MetricsRetentionTests()
    {
        _targetId = new TargetRegistry(_database.Factory, _clock).Create(TargetTypes.Device, "保留期设备", []).Id;
    }

    public void Dispose() => _database.Dispose();

    private MetricsRetentionService CreateService(int retentionDays = 30)
    {
        var options = new MetricsOptions { RetentionDays = retentionDays };
        return new MetricsRetentionService(_database.Factory, options, _clock);
    }

    [Fact]
    public async Task CleanupOnce_Deletes_Expired_Data_And_Keeps_Recent()
    {
        var store = new MetricsStore(_database.Factory);
        var recent = _clock.GetUtcNow().AddDays(-2);
        var expired = _clock.GetUtcNow().AddDays(-45);
        store.Insert(_targetId, MetricKeys.Cpu, new MetricSample(recent, 10, null));
        store.Insert(_targetId, MetricKeys.Cpu, new MetricSample(expired, 99, null));

        var service = CreateService();
        var result = await service.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.DetailDeleted);
        Assert.Equal(1, result.HourlyDeleted);
        Assert.Equal(1, result.DailyDeleted);
        Assert.Single(store.QueryRaw(_targetId, MetricKeys.Cpu, recent.AddMinutes(-1), recent.AddMinutes(1)));
        Assert.Empty(store.QueryRaw(_targetId, MetricKeys.Cpu, expired.AddMinutes(-1), expired.AddMinutes(1)));
    }

    [Fact]
    public async Task CleanupOnce_Respects_Configured_Retention_Days()
    {
        var store = new MetricsStore(_database.Factory);
        var withinDefault = _clock.GetUtcNow().AddDays(-20);
        store.Insert(_targetId, MetricKeys.Cpu, new MetricSample(withinDefault, 10, null));

        // 保留期缩到 7 天后，20 天前的数据应被清理
        var service = CreateService(retentionDays: 7);
        var result = await service.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.DetailDeleted);
        Assert.Empty(store.QueryRaw(_targetId, MetricKeys.Cpu, withinDefault.AddMinutes(-1), withinDefault.AddMinutes(1)));
    }

    [Fact]
    public async Task CleanupOnce_On_Empty_Database_Is_Noop()
    {
        var service = CreateService();

        var result = await service.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(new MetricsCleanupResult(0, 0, 0), result);
    }
}
