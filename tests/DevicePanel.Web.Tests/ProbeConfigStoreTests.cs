using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>服务探针配置持久化：一目标一配置（upsert）、随目标删除级联清理。</summary>
public class ProbeConfigStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly TargetRegistry _targets;
    private readonly ProbeConfigStore _store;
    private readonly long _targetId;

    public ProbeConfigStoreTests()
    {
        _targets = new TargetRegistry(_db.Factory, _clock);
        _store = new ProbeConfigStore(_db.Factory, _clock);
        _targetId = _targets.Create(TargetTypes.Service, "MC 服务", ["游戏"]).Target.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Save_Then_Get_Returns_Url_Interval_And_Mappings()
    {
        _store.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new ProbeMetricMapping("mc.players", "$.players.length()", MetricValueType.Number, "在线玩家数", "人"),
        ]);

        var config = _store.Get(_targetId);
        Assert.NotNull(config);
        Assert.Equal(_targetId, config.TargetId);
        Assert.Equal("https://mc.zenoxs.cn/tiles/settings.json", config.Url);
        Assert.Equal(60, config.IntervalSeconds);
        var mapping = Assert.Single(config.Mappings);
        Assert.Equal("mc.players", mapping.MetricKey);
        Assert.Equal("$.players.length()", mapping.JsonPath);
        Assert.Equal(MetricValueType.Number, mapping.ValueType);
        Assert.Equal("在线玩家数", mapping.DisplayName);
        Assert.Equal("人", mapping.Unit);
    }

    [Fact]
    public void Save_Twice_Upserts_Single_Config()
    {
        _store.Save(_targetId, "https://a.example.com/health", 30, []);
        _store.Save(_targetId, "https://b.example.com/health", 45,
        [
            new ProbeMetricMapping("svc.version", "$.version", MetricValueType.String, "版本", ""),
        ]);

        var config = _store.Get(_targetId);
        Assert.NotNull(config);
        Assert.Equal("https://b.example.com/health", config.Url);
        Assert.Equal(45, config.IntervalSeconds);
        Assert.Equal("svc.version", Assert.Single(config.Mappings).MetricKey);
    }

    [Fact]
    public void List_Returns_Configs_For_All_Service_Targets()
    {
        var otherId = _targets.Create(TargetTypes.Service, "另一个服务", []).Target.Id;
        _store.Save(_targetId, "https://a.example.com", 30, []);
        _store.Save(otherId, "https://b.example.com", 60, []);

        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void Get_Missing_Target_Returns_Null()
    {
        Assert.Null(_store.Get(987654));
    }

    [Fact]
    public void Target_Delete_Cascades_Config()
    {
        _store.Save(_targetId, "https://a.example.com", 30, []);

        _targets.Delete(_targetId);

        Assert.Null(_store.Get(_targetId));
    }
}
