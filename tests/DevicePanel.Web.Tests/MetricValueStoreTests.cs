using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>通用类型化指标序列存储（TOB-360 约束 A）：任意注册指标按类型落库与查询，与 legacy 五键管道并存。</summary>
public class MetricValueStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly DeviceRegistry _devices;
    private readonly TargetStore _targets;
    private readonly MetricKeyRegistry _keys;
    private readonly MetricValueStore _store;

    public MetricValueStoreTests()
    {
        _devices = new DeviceRegistry(_db.Factory, TimeProvider.System);
        _targets = new TargetStore(_db.Factory, TimeProvider.System);
        _keys = new MetricKeyRegistry(_db.Factory, TimeProvider.System);
        _store = new MetricValueStore(_db.Factory, _keys);
        _keys.Register("players", MetricValueType.Number, "人", "在线玩家数");
        _keys.Register("status", MetricValueType.Enum, null, "服务状态");
        _keys.Register("note", MetricValueType.String, null, "备注");
        _keys.Register("maintenance", MetricValueType.Bool, null, "维护中");
    }

    private long NewDeviceTarget()
    {
        var device = _devices.Create("t-target", []).Device;
        return _targets.ProvisionForDevice(device.Id, device.Name).Id;
    }

    [Fact]
    public void Insert_Number_And_QueryRaw_Ordered()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "players", t0.AddSeconds(60), new MetricValue(t0.AddSeconds(60), 7, null));
        _store.Insert(targetId, "players", t0, new MetricValue(t0, 3, null));

        var points = _store.QueryRaw(targetId, "players", t0.AddMinutes(-1), t0.AddMinutes(5));

        Assert.Equal([3, 7], points.Select(p => p.NumValue).ToArray());
        Assert.All(points, p => Assert.Null(p.TextValue));
    }

    [Fact]
    public void Insert_Text_Types_Stores_Text_Value()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "status", t0, new MetricValue(t0, null, "online"));
        _store.Insert(targetId, "note", t0.AddSeconds(1), new MetricValue(t0.AddSeconds(1), null, "hello"));
        _store.Insert(targetId, "maintenance", t0.AddSeconds(2), new MetricValue(t0.AddSeconds(2), null, "TRUE"));

        Assert.Equal("online", _store.TryGetLatest(targetId, "status")?.TextValue);
        Assert.Equal("hello", _store.TryGetLatest(targetId, "note")?.TextValue);
        // bool 归一化为小写 true/false
        Assert.Equal("true", _store.TryGetLatest(targetId, "maintenance")?.TextValue);
    }

    [Fact]
    public void Insert_Rejects_Value_Not_Matching_Registered_Type()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

        Assert.Throws<InvalidOperationException>(() =>
            _store.Insert(targetId, "status", t0, new MetricValue(t0, 1.5, null)));
        Assert.Throws<InvalidOperationException>(() =>
            _store.Insert(targetId, "players", t0, new MetricValue(t0, null, "3")));
        Assert.Throws<InvalidOperationException>(() =>
            _store.Insert(targetId, "not-registered", t0, new MetricValue(t0, 1, null)));
    }

    [Fact]
    public void Same_Second_Reinsert_Overwrites()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "players", t0, new MetricValue(t0, 1, null));
        _store.Insert(targetId, "players", t0, new MetricValue(t0, 2, null));

        Assert.Equal(2, _store.TryGetLatest(targetId, "players")?.NumValue);
        Assert.Single(_store.QueryRaw(targetId, "players", t0.AddMinutes(-1), t0.AddMinutes(1)));
    }

    [Fact]
    public void QueryBucketed_Hour_Averages_Numbers()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "players", t0.AddMinutes(10), new MetricValue(t0.AddMinutes(10), 2, null));
        _store.Insert(targetId, "players", t0.AddMinutes(20), new MetricValue(t0.AddMinutes(20), 4, null));
        _store.Insert(targetId, "players", t0.AddHours(1).AddMinutes(10), new MetricValue(t0.AddHours(1).AddMinutes(10), 10, null));

        var buckets = _store.QueryBucketed(targetId, "players", "hour", t0, t0.AddHours(3));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(3, buckets[0].AvgNum);
        Assert.Equal(10, buckets[1].AvgNum);
        Assert.Equal(2, buckets[0].SampleCount);
    }

    [Fact]
    public void QueryBucketed_Takes_Last_Text_Value_In_Bucket()
    {
        var targetId = NewDeviceTarget();
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "status", t0.AddMinutes(10), new MetricValue(t0.AddMinutes(10), null, "online"));
        _store.Insert(targetId, "status", t0.AddMinutes(30), new MetricValue(t0.AddMinutes(30), null, "offline"));

        var buckets = _store.QueryBucketed(targetId, "status", "hour", t0, t0.AddHours(1));

        Assert.Single(buckets);
        Assert.Equal("offline", buckets[0].LastText);
    }

    [Fact]
    public void DeleteOlderThan_Removes_Only_Old_Rows()
    {
        var targetId = NewDeviceTarget();
        var old = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var fresh = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "players", old, new MetricValue(old, 1, null));
        _store.Insert(targetId, "players", fresh, new MetricValue(fresh, 2, null));

        var deleted = _store.DeleteOlderThan(DateTimeOffset.Parse("2026-06-01T00:00:00Z"));

        Assert.Equal(1, deleted);
        Assert.Single(_store.QueryRaw(targetId, "players", old, fresh));
    }

    [Fact]
    public void Delete_Device_Cascades_Metric_Values()
    {
        var device = _devices.Create("t-cascade", []).Device;
        var targetId = _targets.ProvisionForDevice(device.Id, device.Name).Id;
        var t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        _store.Insert(targetId, "players", t0, new MetricValue(t0, 1, null));

        _devices.Delete(device.Id);

        Assert.Empty(_store.QueryRaw(targetId, "players", t0.AddMinutes(-1), t0.AddMinutes(1)));
    }

    public void Dispose() => _db.Dispose();
}
