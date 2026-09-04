using DevicePanel.Web.Metrics;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>指标键注册表（TOB-360 约束 A）：核心不内置指标语义，新增指标 = 注册 key + 类型 + 展示元数据。</summary>
public class MetricKeyRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly MetricKeyRegistry _registry;

    public MetricKeyRegistryTests()
    {
        _registry = new MetricKeyRegistry(_db.Factory, TimeProvider.System);
    }

    [Fact]
    public void Register_And_Get_Roundtrip()
    {
        _registry.Register("players", MetricValueType.Number, "人", "在线玩家数");

        var info = _registry.Get("players");

        Assert.NotNull(info);
        Assert.Equal("players", info!.Key);
        Assert.Equal(MetricValueType.Number, info.ValueType);
        Assert.Equal("人", info.Unit);
        Assert.Equal("在线玩家数", info.DisplayName);
        Assert.True(_registry.IsRegistered("players"));
        Assert.False(_registry.IsRegistered("unknown"));
        Assert.Null(_registry.Get("unknown"));
    }

    [Fact]
    public void Register_Upserts_Metadata()
    {
        _registry.Register("players", MetricValueType.Number, "人", "在线玩家数");
        _registry.Register("players", MetricValueType.Number, null, "在线玩家");

        var info = _registry.Get("players");
        Assert.NotNull(info);
        Assert.Null(info!.Unit);
        Assert.Equal("在线玩家", info.DisplayName);
    }

    [Fact]
    public void Register_Allows_Enum_String_Bool_Types()
    {
        _registry.Register("status", MetricValueType.Enum, null, "服务状态");
        _registry.Register("version", MetricValueType.String, null, "版本");
        _registry.Register("maintenance", MetricValueType.Bool, null, "维护中");

        Assert.Equal(MetricValueType.Enum, _registry.Get("status")?.ValueType);
        Assert.Equal(MetricValueType.String, _registry.Get("version")?.ValueType);
        Assert.Equal(MetricValueType.Bool, _registry.Get("maintenance")?.ValueType);
    }

    [Fact]
    public void List_Returns_Registered_Keys_Ordered()
    {
        _registry.Register("b-key", MetricValueType.Number, null, "B");
        _registry.Register("a-key", MetricValueType.Number, null, "A");

        Assert.Equal(["a-key", "b-key"], _registry.List().Select(k => k.Key).ToArray());
    }

    [Fact]
    public void Register_Rejects_Unknown_Value_Type_Text()
    {
        Assert.Throws<ArgumentException>(() => MetricValueTypeText.Parse("float"));
    }

    public void Dispose() => _db.Dispose();
}
