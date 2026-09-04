using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>告警规则实例存储单元测试：CRUD、(target, metric, type) 唯一、生效规则合成（目标级 + 全局）。</summary>
public class AlertRuleStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

    public AlertRuleStoreTests()
    {
        // 清空迁移播种的内置规则：存储行为测试从干净状态开始
        var store = CreateStore();
        foreach (var seeded in store.List())
        {
            store.Delete(seeded.Id);
        }
    }

    public void Dispose() => _db.Dispose();

    private AlertRuleStore CreateStore() => new(_db.Factory, _clock);

    private long CreateTarget() =>
        new TargetRegistry(_db.Factory, _clock).Create(TargetTypes.Device, "规则目标", []).Target.Id;

    [Fact]
    public void Create_And_Get_RoundTrip_Preserves_Fields()
    {
        var store = CreateStore();
        var targetId = CreateTarget();

        var created = store.Create(targetId, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":75}""", 30, 5, true);

        Assert.True(created.Id > 0);
        var fetched = store.Get(created.Id)!;
        Assert.Equal(targetId, fetched.TargetId);
        Assert.Equal(MetricKeys.Cpu, fetched.MetricKey);
        Assert.Equal(ThresholdAboveRuleType.TypeIdValue, fetched.RuleType);
        Assert.True(fetched.Enabled);
        Assert.Equal(30, fetched.SustainSeconds);
        Assert.Equal(5, fetched.RepeatMinutes);
    }

    [Fact]
    public void Create_Global_Rule_Stores_Null_Target()
    {
        var store = CreateStore();

        var created = store.Create(null, MetricKeys.Mem, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);

        Assert.Null(created.TargetId);
        Assert.Null(store.Get(created.Id)!.TargetId);
    }

    [Fact]
    public void Create_Duplicate_Target_Metric_Type_Throws()
    {
        var store = CreateStore();
        var targetId = CreateTarget();
        store.Create(targetId, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);

        // 目标级重复
        Assert.Throws<InvalidOperationException>(
            () => store.Create(targetId, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":50}""", 60, 0, true));
        // 全局规则与目标级可并存
        store.Create(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);
        // 全局重复同样拒绝
        Assert.Throws<InvalidOperationException>(
            () => store.Create(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":80}""", 60, 0, true));
    }

    [Fact]
    public void Update_Changes_Parameters_Sustain_Repeat_And_Enabled()
    {
        var store = CreateStore();
        var created = store.Create(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);

        var updated = store.Update(created.Id, """{"threshold":70}""", 120, 10, false);

        Assert.NotNull(updated);
        Assert.Equal("""{"threshold":70}""", updated!.ParametersJson);
        Assert.Equal(120, updated.SustainSeconds);
        Assert.Equal(10, updated.RepeatMinutes);
        Assert.False(updated.Enabled);
    }

    [Fact]
    public void ListApplicable_Returns_Target_And_Global_Rules_Enabled_Only()
    {
        var store = CreateStore();
        var targetId = CreateTarget();
        store.Create(targetId, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":50}""", 60, 0, true);
        store.Create(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);
        store.Create(null, MetricKeys.Disk, ThresholdAboveRuleType.TypeIdValue, """{"threshold":10}""", 60, 0, enabled: false);
        store.Create(null, MetricKeys.Mem, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", 60, 0, true);

        var applicable = store.ListApplicable(targetId, MetricKeys.Cpu);

        Assert.Equal(2, applicable.Count);
        Assert.All(applicable, r => Assert.True(r.Enabled));
        Assert.Single(applicable, r => r.TargetId == targetId);
        Assert.Single(applicable, r => r.TargetId is null);
    }

    [Fact]
    public void Find_Matches_Null_Target_Exactly()
    {
        var store = CreateStore();
        var targetId = CreateTarget();
        store.Create(null, MetricKeys.Cpu, NoDataRuleType.TypeIdValue, """{"minutes":10}""", 60, 0, true);
        store.Create(targetId, MetricKeys.Cpu, NoDataRuleType.TypeIdValue, """{"minutes":5}""", 60, 0, true);

        Assert.NotNull(store.Find(null, MetricKeys.Cpu, NoDataRuleType.TypeIdValue));
        Assert.NotNull(store.Find(targetId, MetricKeys.Cpu, NoDataRuleType.TypeIdValue));
        Assert.Null(store.Find(targetId, MetricKeys.Mem, NoDataRuleType.TypeIdValue));
    }

    [Fact]
    public void Delete_Removes_Rule_And_CountByMetricKey_Reflects_It()
    {
        var store = CreateStore();
        var targetId = CreateTarget();
        var rule = store.Create(targetId, "custom.key", ThresholdAboveRuleType.TypeIdValue, """{"threshold":1}""", 60, 0, true);
        Assert.Equal(1, store.CountByMetricKey("custom.key"));

        Assert.True(store.Delete(rule.Id));
        Assert.False(store.Delete(rule.Id));
        Assert.Equal(0, store.CountByMetricKey("custom.key"));
    }
}
