using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>服务探针配置持久化：一采集器一配置（upsert）、随采集器删除级联清理。</summary>
public class PullCollectorConfigStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly CollectorRegistry _collectors;
    private readonly PullCollectorConfigStore _store;
    private readonly long _collectorId;

    public PullCollectorConfigStoreTests()
    {
        _collectors = new CollectorRegistry(_db.Factory, _clock);
        _store = new PullCollectorConfigStore(_db.Factory, _clock);
        _collectorId = _collectors.Create("MC 服务", ["游戏", CollectorBuiltinTags.Service]).Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Save_Then_Get_Returns_Url_Interval_And_Mappings()
    {
        _store.Save(_collectorId, "https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new PullMetricMapping("mc.players", "$.players.length()", MetricValueType.Number, "在线玩家数", "人"),
        ]);

        var config = _store.Get(_collectorId);
        Assert.NotNull(config);
        Assert.Equal(_collectorId, config.CollectorId);
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
        _store.Save(_collectorId, "https://a.example.com/health", 30, []);
        _store.Save(_collectorId, "https://b.example.com/health", 45,
        [
            new PullMetricMapping("svc.version", "$.version", MetricValueType.String, "版本", ""),
        ]);

        var config = _store.Get(_collectorId);
        Assert.NotNull(config);
        Assert.Equal("https://b.example.com/health", config.Url);
        Assert.Equal(45, config.IntervalSeconds);
        Assert.Equal("svc.version", Assert.Single(config.Mappings).MetricKey);
    }

    [Fact]
    public void List_Returns_Configs_For_All_Service_Targets()
    {
        var otherId = _collectors.Create("另一个服务", [CollectorBuiltinTags.Service]).Id;
        _store.Save(_collectorId, "https://a.example.com", 30, []);
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
        _store.Save(_collectorId, "https://a.example.com", 30, []);

        _collectors.Delete(_collectorId);

        Assert.Null(_store.Get(_collectorId));
    }
}
