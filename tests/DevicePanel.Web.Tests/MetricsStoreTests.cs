using DevicePanel.Web.Collectors;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class MetricsStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _database = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly CollectorRegistry _targets;
    private readonly MetricsStore _store;
    private readonly long _targetId;

    public MetricsStoreTests()
    {
        _targets = new CollectorRegistry(_database.Factory, _clock);
        _store = new MetricsStore(_database.Factory);
        _targetId = _targets.Create("指标设备", [CollectorBuiltinTags.Device]).Id;
    }

    public void Dispose() => _database.Dispose();

    private static MetricSample Num(double value) => new(default, value, null);

    private static MetricSample Text(string value, double? num = null) => new(default, num, value);

    [Fact]
    public void Insert_Stores_Detail_Sample_With_Time()
    {
        var collectedAt = new DateTimeOffset(2026, 9, 3, 11, 59, 30, TimeSpan.Zero);

        _store.Insert(_targetId, MetricKeys.Cpu, new MetricSample(collectedAt, 12.5, null));

        var raw = _store.QueryRaw(_targetId, MetricKeys.Cpu, collectedAt.AddMinutes(-1), collectedAt.AddMinutes(1));
        var sample = Assert.Single(raw);
        Assert.Equal(collectedAt, sample.TimeUtc);
        Assert.Equal(12.5, sample.ValueNum!.Value, precision: 6);
        Assert.Null(sample.ValueText);
    }

    [Fact]
    public void Insert_Accumulates_Hourly_And_Daily_Aggregates()
    {
        var hour = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(hour.AddMinutes(10), 10));
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(hour.AddMinutes(40), 30));

        var bucket = Assert.Single(_store.QueryHourly(_targetId, MetricKeys.Cpu, hour, hour.AddHours(1)));
        Assert.Equal(hour, bucket.TimeUtc);
        Assert.Equal(20, bucket.Avg, precision: 6); // 平均
        Assert.Equal(30, bucket.Max!.Value, precision: 6); // 峰值

        var dayBucket = Assert.Single(_store.QueryDaily(_targetId, MetricKeys.Cpu, hour.Date, hour.Date.AddDays(1)));
        Assert.Equal(20, dayBucket.Avg, precision: 6);
    }

    [Fact]
    public void Insert_Text_Metrics_Store_Value_Without_Number_Aggregates()
    {
        var now = new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);
        _store.Insert(_targetId, "service.status", new MetricSample(now, null, "online"));

        var sample = Assert.Single(_store.QueryRaw(_targetId, "service.status", now.AddMinutes(-1), now.AddMinutes(1)));
        Assert.Equal("online", sample.ValueText);
        Assert.Null(sample.ValueNum);

        // 非数值指标不参与聚合
        Assert.Empty(_store.QueryHourly(_targetId, "service.status", now.AddHours(-1), now.AddHours(1)));
    }

    [Fact]
    public void GetLatest_Returns_Newest_Sample_Per_Metric_Key()
    {
        var first = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero);
        _store.Insert(_targetId, MetricKeys.Mem, new MetricSample(first, 40, null));
        _store.Insert(_targetId, MetricKeys.Mem, new MetricSample(second, 55, null));
        _store.Insert(_targetId, MetricKeys.Online, new MetricSample(first, 1, "true"));

        var latest = _store.GetLatest(_targetId, MetricKeys.Mem);
        Assert.NotNull(latest);
        Assert.Equal(second, latest!.TimeUtc);
        Assert.Equal(55, latest.ValueNum);

        var online = _store.GetLatest(_targetId, MetricKeys.Online);
        Assert.NotNull(online);
        Assert.Equal("true", online!.ValueText);

        Assert.Null(_store.GetLatest(_targetId, MetricKeys.Disk));
    }

    [Fact]
    public void ListReportedKeys_And_ListTargetsReporting_Support_Registry_Queries()
    {
        var otherId = _targets.Create("另一台", [CollectorBuiltinTags.Device]).Id;
        var now = _clock.GetUtcNow();
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(now, 10));
        _store.Insert(_targetId, MetricKeys.Online, new MetricSample(now, 1, "true"));
        _store.Insert(otherId, MetricKeys.Cpu, Sample(now, 20));

        Assert.Equal(new[] { "cpu", "online" }, _store.ListReportedKeys(_targetId));
        Assert.Equal(new[] { _targetId, otherId }, _store.ListTargetsReporting(MetricKeys.Cpu));
        Assert.Equal([_targetId], _store.ListTargetsReporting(MetricKeys.Online));
        Assert.True(_store.HasAnySample(MetricKeys.Cpu));
        Assert.False(_store.HasAnySample(MetricKeys.NetRx));
    }

    [Fact]
    public void QueryRaw_Filters_By_Range_And_Target()
    {
        var inRange = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var tooEarly = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var otherTargetId = _targets.Create("另一台", [CollectorBuiltinTags.Device]).Id;

        _store.Insert(_targetId, MetricKeys.Cpu, Sample(tooEarly, 1));
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(inRange, 2));
        _store.Insert(otherTargetId, MetricKeys.Cpu, Sample(inRange, 3));

        var raw = _store.QueryRaw(_targetId, MetricKeys.Cpu, inRange.AddSeconds(-1), inRange.AddMinutes(1));

        var sample = Assert.Single(raw);
        Assert.Equal(2, sample.ValueNum!.Value, precision: 6);
    }

    [Fact]
    public void Query_Hourly_Aggregates_Stay_Consistent_With_Detail_Over_Multi_Day_Data()
    {
        // 验收：覆盖多天的历史数据，小时/天聚合与明细口径一致（平均值 = 明细样本均值）
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var rng = new Random(20260903);
        var expectedByHour = new Dictionary<DateTimeOffset, List<double>>();
        for (var t = start; t < start.AddDays(3); t = t.AddMinutes(30))
        {
            var cpu = 5 + rng.NextDouble() * 85;
            _store.Insert(_targetId, MetricKeys.Cpu, Sample(t, cpu));
            var bucket = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
            if (!expectedByHour.TryGetValue(bucket, out var list))
            {
                expectedByHour[bucket] = list = new List<double>();
            }

            list.Add(cpu);
        }

        var hourly = _store.QueryHourly(_targetId, MetricKeys.Cpu, start, start.AddDays(3));
        Assert.Equal(72, hourly.Count);
        foreach (var bucket in hourly)
        {
            Assert.Equal(expectedByHour[bucket.TimeUtc].Average(), bucket.Avg, precision: 5);
        }

        var daily = _store.QueryDaily(_targetId, MetricKeys.Cpu, start, start.AddDays(3));
        Assert.Equal(3, daily.Count);
        foreach (var day in daily)
        {
            var expectedDayAverage = expectedByHour
                .Where(kv => kv.Key >= day.TimeUtc && kv.Key < day.TimeUtc.AddDays(1))
                .SelectMany(kv => kv.Value)
                .Average();
            Assert.Equal(expectedDayAverage, day.Avg, precision: 3);
        }
    }

    [Fact]
    public void DeleteOlderThan_Removes_Expired_From_All_Tables_Keeps_Recent()
    {
        var recent = _clock.GetUtcNow().AddDays(-1);
        var expired = _clock.GetUtcNow().AddDays(-31);

        _store.Insert(_targetId, MetricKeys.Cpu, Sample(recent, 10));
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(expired, 99));

        var result = _store.DeleteOlderThan(_clock.GetUtcNow().AddDays(-30));

        Assert.Equal(1, result.DetailDeleted);
        Assert.Equal(1, result.HourlyDeleted);
        Assert.Equal(1, result.DailyDeleted);

        Assert.Empty(_store.QueryRaw(_targetId, MetricKeys.Cpu, expired.AddMinutes(-1), expired.AddMinutes(1)));
        Assert.Empty(_store.QueryHourly(_targetId, MetricKeys.Cpu, expired.Date, expired.Date.AddDays(1)));
        Assert.Empty(_store.QueryDaily(_targetId, MetricKeys.Cpu, expired.Date, expired.Date.AddDays(1)));

        var kept = Assert.Single(_store.QueryRaw(_targetId, MetricKeys.Cpu, recent.AddMinutes(-1), recent.AddMinutes(1)));
        Assert.Equal(10, kept.ValueNum!.Value, precision: 6);
    }

    [Fact]
    public void Delete_Target_Cascades_To_Metrics()
    {
        var now = _clock.GetUtcNow();
        _store.Insert(_targetId, MetricKeys.Cpu, Sample(now, 10));

        Assert.True(_targets.Delete(_targetId));

        Assert.Empty(_store.QueryRaw(_targetId, MetricKeys.Cpu, now.AddMinutes(-1), now.AddMinutes(1)));
    }

    [Fact]
    public void Query_Empty_Range_Returns_Empty()
    {
        Assert.Empty(_store.QueryRaw(_targetId, MetricKeys.Cpu, _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow()));
        Assert.Empty(_store.QueryHourly(_targetId, MetricKeys.Cpu, _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow()));
        Assert.Empty(_store.QueryDaily(_targetId, MetricKeys.Cpu, _clock.GetUtcNow().AddDays(-1), _clock.GetUtcNow()));
    }

    private static MetricSample Sample(DateTimeOffset time, double value) => new(time, value, null);
}
