using System.Text.Json;
using DevicePanel.Web.Dashboard;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>DashboardLayoutStore 持久化单测：读写往返、整份替换、与设备数据解耦。</summary>
public class DashboardLayoutStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _database = new();
    private readonly DashboardLayoutStore _store;

    public DashboardLayoutStoreTests()
    {
        _store = new DashboardLayoutStore(_database.Factory);
    }

    [Fact]
    public void GetLayout_On_Fresh_Database_Returns_Null()
    {
        Assert.Null(_store.GetLayout());
    }

    [Fact]
    public void Save_Then_Get_RoundTrips_Layout_With_Config_Passthrough()
    {
        var config = JsonDocument.Parse("""
            {
                "source": "agent",
                "windowMinutes": 30,
                "threshold": 1.50,
                "tags": ["cpu", "内存"],
                "note": null,
                "nested": { "enabled": true, "list": [1, null, "x"] }
            }
            """).RootElement.Clone();
        var layout = new DashboardLayout(
        [
            new DashboardCard("card-metric", "metric-line", 3, Visible: false, config),
            new DashboardCard("card-total", "overview-total-devices", 0, Visible: true, EmptyObject()),
        ]);

        _store.SaveLayout(layout);
        var loaded = _store.GetLayout();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Cards.Count);
        // 存储层按保存顺序原样往返（不按 sort 重排，排序归 API 层）
        Assert.Equal("card-metric", loaded.Cards[0].Id);
        Assert.Equal("metric-line", loaded.Cards[0].Type);
        Assert.Equal(3, loaded.Cards[0].Sort);
        Assert.False(loaded.Cards[0].Visible);
        Assert.True(loaded.Cards[0].Config.ValueKind == JsonValueKind.Object);
        JsonElementAssertions.JsonEquals(config, loaded.Cards[0].Config);
        Assert.Equal("内存", loaded.Cards[0].Config.GetProperty("tags")[1].GetString());
        Assert.Equal(1.50, loaded.Cards[0].Config.GetProperty("threshold").GetDouble());

        var card = loaded.Cards[1];
        Assert.Equal("card-total", card.Id);
        Assert.Equal("overview-total-devices", card.Type);
        Assert.Equal(0, card.Sort);
        Assert.True(card.Visible);
        JsonElementAssertions.JsonEquals(EmptyObject(), card.Config);
    }

    [Fact]
    public void Save_Replaces_Previous_Layout_Entirely()
    {
        var first = new DashboardLayout(
        [
            new DashboardCard("card-a", "overview-total-devices", 0, Visible: true, EmptyObject()),
        ]);
        var second = new DashboardLayout(
        [
            new DashboardCard("card-b", "overview-online-devices", 0, Visible: true, EmptyObject()),
            new DashboardCard("card-c", "overview-active-alerts", 1, Visible: false, EmptyObject()),
        ]);

        _store.SaveLayout(first);
        _store.SaveLayout(second);

        var loaded = _store.GetLayout();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Cards.Count);
        Assert.Equal("card-b", loaded.Cards[0].Id);
        Assert.Equal("card-c", loaded.Cards[1].Id);
    }

    [Fact]
    public void Saved_Layout_Independent_Of_Device_Data()
    {
        var layout = new DashboardLayout(
        [
            new DashboardCard("card-1", "overview-total-devices", 0, Visible: true, EmptyObject()),
        ]);
        _store.SaveLayout(layout);

        using (var connection = _database.CreateOpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM collectors;";
            command.ExecuteNonQuery();
        }

        var loaded = _store.GetLayout();
        Assert.NotNull(loaded);
        JsonElementAssertions.JsonEquals(layout.Cards[0].Config, loaded.Cards[0].Config);
        Assert.Equal("card-1", loaded.Cards[0].Id);
    }

    [Fact]
    public void GetLayout_With_Corrupt_Stored_Json_Returns_Null()
    {
        using (var connection = _database.CreateOpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dashboard_layouts(id, layout_json, updated_at_utc) VALUES (1, '{ not-json', '2026-01-01T00:00:00.000Z');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Null(_store.GetLayout());
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    public void Dispose() => _database.Dispose();
}
