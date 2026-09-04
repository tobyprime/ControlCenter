using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>MetricKey 注册表单元测试（约束 A：新增一种指标 = 注册 key + 类型）。</summary>
public class MetricKeyRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private MetricKeyRegistry CreateRegistry() => new(_db.Factory, _clock);

    [Fact]
    public void Builtin_Keys_Are_Seeded_By_Migrations()
    {
        var registry = CreateRegistry();

        var keys = registry.List().ToDictionary(k => k.Key);
        Assert.Equal(
        [
            MetricKeys.Cpu, MetricKeys.Disk, MetricKeys.DiskRx, MetricKeys.DiskTx, MetricKeys.LatencyMs, MetricKeys.Mem,
            MetricKeys.MemTotal, MetricKeys.MemUsed, MetricKeys.NetRx, MetricKeys.NetTx, MetricKeys.Online,
            MetricKeys.Status, MetricKeys.Temp, MetricKeys.TempSensor,
        ], keys.Keys.OrderBy(k => k).ToArray());
        Assert.All(keys.Values, k => Assert.True(k.BuiltIn));
        Assert.Equal(MetricValueType.Number, keys[MetricKeys.Cpu].ValueType);
        Assert.Equal("%", keys[MetricKeys.Cpu].Unit);
        Assert.Equal(MetricValueType.Bool, keys[MetricKeys.Online].ValueType);
        // 模块1（TOB-362）新增采集项：温度/磁盘读写/内存实际数值，随迁移播种为内置 key（约束 A）
        Assert.Equal(MetricValueType.Number, keys[MetricKeys.Temp].ValueType);
        Assert.Equal("°C", keys[MetricKeys.Temp].Unit);
        Assert.Equal(MetricValueType.String, keys[MetricKeys.TempSensor].ValueType);
        Assert.Equal("B/s", keys[MetricKeys.DiskRx].Unit);
        Assert.Equal("B", keys[MetricKeys.MemUsed].Unit);
    }

    [Fact]
    public void Register_New_Key_Makes_It_Listed_And_Gettable()
    {
        var registry = CreateRegistry();

        var registered = registry.Register("temp.cpu", MetricValueType.Number, "CPU 温度", "°C");

        Assert.Equal("temp.cpu", registered.Key);
        Assert.Equal(MetricValueType.Number, registered.ValueType);
        Assert.False(registered.BuiltIn);
        Assert.NotNull(registry.Get("temp.cpu"));
        Assert.Contains(registry.List(), k => k.Key == "temp.cpu");
    }

    [Theory]
    [InlineData("Temp")]      // 大写
    [InlineData("1abc")]      // 数字开头
    [InlineData("a-b")]       // 非法字符
    [InlineData(".abc")]      // 段为空
    [InlineData("a..b")]      // 空段
    [InlineData("")]
    public void NormalizeKey_Rejects_Invalid_Keys(string key)
    {
        Assert.Null(MetricKeyRegistry.NormalizeKey(key));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a1_b")]
    [InlineData("temp.cpu")]
    [InlineData("a.b.c")]
    public void NormalizeKey_Accepts_Valid_Keys(string key)
    {
        Assert.Equal(key, MetricKeyRegistry.NormalizeKey(key));
    }

    [Fact]
    public void Register_Duplicate_Key_Throws()
    {
        var registry = CreateRegistry();
        registry.Register("custom.metric", MetricValueType.String, "自定义", "");

        Assert.Throws<InvalidOperationException>(() => registry.Register("custom.metric", MetricValueType.String, "自定义", ""));
    }

    [Fact]
    public void UpdateDisplay_Changes_DisplayName_And_Unit_Not_ValueType()
    {
        var registry = CreateRegistry();
        registry.Register("custom.metric", MetricValueType.Enum, "旧名", "");

        var updated = registry.UpdateDisplay("custom.metric", "新名", "级");

        Assert.NotNull(updated);
        Assert.Equal("新名", updated!.DisplayName);
        Assert.Equal("级", updated.Unit);
        Assert.Equal(MetricValueType.Enum, updated.ValueType);
    }

    [Fact]
    public void Delete_Removes_User_Key_But_Refuses_Builtin()
    {
        var registry = CreateRegistry();
        registry.Register("custom.metric", MetricValueType.Number, "自定义", "");

        Assert.True(registry.Delete("custom.metric"));
        Assert.Null(registry.Get("custom.metric"));
        Assert.False(registry.Delete("custom.metric"));
        Assert.False(registry.Delete(MetricKeys.Cpu));
        Assert.NotNull(registry.Get(MetricKeys.Cpu));
    }
}
