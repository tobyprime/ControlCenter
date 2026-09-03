using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class MetricsStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _database = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly DeviceRegistry _devices;
    private readonly MetricsStore _store;
    private long _deviceId;

    public MetricsStoreTests()
    {
        _devices = new DeviceRegistry(_database.Factory, _clock);
        _store = new MetricsStore(_database.Factory);
        _deviceId = _devices.Create("指标设备", []).Device.Id;
    }

    public void Dispose() => _database.Dispose();

    private MetricsPoint Point(double cpu = 10, double mem = 20, double disk = 30, double netRx = 100, double netTx = 200) =>
        new(TimeUtc: default, cpu, mem, disk, netRx, netTx);

    [Fact]
    public void Insert_Stores_Detail_Sample_With_Collected_Time()
    {
        var collectedAt = new DateTimeOffset(2026, 9, 3, 11, 59, 30, TimeSpan.Zero);

        _store.Insert(_deviceId, collectedAt, Point(cpu: 12.5, mem: 40, disk: 55, netRx: 20480, netTx: 4096));

        var raw = _store.QueryRaw(_deviceId, collectedAt.AddMinutes(-1), collectedAt.AddMinutes(1));
        var sample = Assert.Single(raw);
        Assert.Equal(collectedAt, sample.TimeUtc);
        Assert.Equal(12.5, sample.Cpu, precision: 6);
        Assert.Equal(40, sample.Mem, precision: 6);
        Assert.Equal(55, sample.Disk, precision: 6);
        Assert.Equal(20480, sample.NetRx, precision: 6);
        Assert.Equal(4096, sample.NetTx, precision: 6);
    }

    [Fact]
    public void Insert_Accumulates_Hourly_And_Daily_Aggregates()
    {
        var hour = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        _store.Insert(_deviceId, hour.AddMinutes(10), Point(cpu: 10, netRx: 1000));
        _store.Insert(_deviceId, hour.AddMinutes(40), Point(cpu: 30, netRx: 3000));

        var hourly = _store.QueryHourly(_deviceId, hour, hour.AddHours(1));
        var bucket = Assert.Single(hourly);
        Assert.Equal(hour, bucket.TimeUtc);
        Assert.Equal(20, bucket.CpuAvg, precision: 6); // 平均
        Assert.Equal(2000, bucket.NetRxAvg, precision: 6);

        var daily = _store.QueryDaily(_deviceId, hour.Date, hour.Date.AddDays(1));
        var dayBucket = Assert.Single(daily);
        Assert.Equal(20, dayBucket.CpuAvg, precision: 6);
        Assert.Equal(2000, dayBucket.NetRxAvg, precision: 6);
    }

    [Fact]
    public void Insert_Tracks_Max_Values_Per_Bucket()
    {
        var hour = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        _store.Insert(_deviceId, hour.AddMinutes(10), Point(cpu: 80));
        _store.Insert(_deviceId, hour.AddMinutes(40), Point(cpu: 20));

        var bucket = Assert.Single(_store.QueryHourly(_deviceId, hour, hour.AddHours(1)));

        Assert.Equal(50, bucket.CpuAvg, precision: 6); // 平均
        Assert.Equal(80, bucket.CpuMax, precision: 6); // 峰值
    }

    [Fact]
    public void Insert_Different_Hours_Create_Separate_Buckets()
    {
        var first = new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 9, 3, 11, 30, 0, TimeSpan.Zero);
        _store.Insert(_deviceId, first, Point(cpu: 10));
        _store.Insert(_deviceId, second, Point(cpu: 50));

        Assert.Equal(
            new[] { 10.0, 50.0 },
            _store.QueryHourly(_deviceId, first.AddHours(-1), second.AddHours(1)).Select(p => p.CpuAvg).ToArray());
    }

    [Fact]
    public void QueryRaw_Filters_By_Range_And_Device()
    {
        var inRange = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var tooEarly = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var otherDeviceId = _devices.Create("另一台", []).Device.Id;

        _store.Insert(_deviceId, tooEarly, Point(cpu: 1));
        _store.Insert(_deviceId, inRange, Point(cpu: 2));
        _store.Insert(otherDeviceId, inRange, Point(cpu: 3));

        var raw = _store.QueryRaw(_deviceId, inRange.AddSeconds(-1), inRange.AddMinutes(1));

        var sample = Assert.Single(raw);
        Assert.Equal(2, sample.Cpu, precision: 6);
    }

    [Fact]
    public void Query_Hourly_Aggregates_Stay_Consistent_With_Detail_Over_Multi_Day_Data()
    {
        // 验收 3：覆盖多天的历史数据，小时/天聚合与明细口径一致（平均值 = 明细样本均值）
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var rng = new Random(20260903);
        var expectedByHour = new Dictionary<DateTimeOffset, List<double>>();
        for (var t = start; t < start.AddDays(3); t = t.AddMinutes(30))
        {
            var cpu = 5 + rng.NextDouble() * 85;
            _store.Insert(_deviceId, t, Point(cpu: cpu));
            var bucket = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
            if (!expectedByHour.TryGetValue(bucket, out var list))
            {
                expectedByHour[bucket] = list = new List<double>();
            }

            list.Add(cpu);
        }

        var hourly = _store.QueryHourly(_deviceId, start, start.AddDays(3));
        Assert.Equal(72, hourly.Count);
        foreach (var bucket in hourly)
        {
            var expected = expectedByHour[bucket.TimeUtc].Average();
            Assert.Equal(expected, bucket.CpuAvg, precision: 5);
        }

        var daily = _store.QueryDaily(_deviceId, start, start.AddDays(3));
        Assert.Equal(3, daily.Count);
        foreach (var day in daily)
        {
            var expectedDayAverage = expectedByHour
                .Where(kv => kv.Key >= day.TimeUtc && kv.Key < day.TimeUtc.AddDays(1))
                .SelectMany(kv => kv.Value)
                .Average();
            Assert.Equal(expectedDayAverage, day.CpuAvg, precision: 3);
        }
    }

    [Fact]
    public void DeleteOlderThan_Removes_Expired_From_All_Tables_Keeps_Recent()
    {
        // 验收 4：超过保留期的数据被清理任务删除
        var recent = _clock.GetUtcNow().AddDays(-1);
        var expired = _clock.GetUtcNow().AddDays(-31);

        _store.Insert(_deviceId, recent, Point(cpu: 10));
        _store.Insert(_deviceId, expired, Point(cpu: 99));

        var result = _store.DeleteOlderThan(_clock.GetUtcNow().AddDays(-30));

        Assert.Equal(1, result.DetailDeleted);
        Assert.Equal(1, result.HourlyDeleted);
        Assert.Equal(1, result.DailyDeleted);

        Assert.Empty(_store.QueryRaw(_deviceId, expired.AddMinutes(-1), expired.AddMinutes(1)));
        Assert.Empty(_store.QueryHourly(_deviceId, expired.Date, expired.Date.AddDays(1)));
        Assert.Empty(_store.QueryDaily(_deviceId, expired.Date, expired.Date.AddDays(1)));

        var kept = Assert.Single(_store.QueryRaw(_deviceId, recent.AddMinutes(-1), recent.AddMinutes(1)));
        Assert.Equal(10, kept.Cpu, precision: 6);
    }

    [Fact]
    public void Delete_Device_Cascades_To_Metrics()
    {
        var now = _clock.GetUtcNow();
        _store.Insert(_deviceId, now, Point(cpu: 10));

        Assert.True(_devices.Delete(_deviceId));

        Assert.Empty(_store.QueryRaw(_deviceId, now.AddMinutes(-1), now.AddMinutes(1)));
    }

    [Fact]
    public void Query_Empty_Range_Returns_Empty()
    {
        Assert.Empty(_store.QueryRaw(_deviceId, _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow()));
        Assert.Empty(_store.QueryHourly(_deviceId, _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow()));
        Assert.Empty(_store.QueryDaily(_deviceId, _clock.GetUtcNow().AddDays(-1), _clock.GetUtcNow()));
    }
}
