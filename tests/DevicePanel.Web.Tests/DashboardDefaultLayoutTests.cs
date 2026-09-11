using System.Text.Json;
using DevicePanel.Web.Dashboard;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>服务端默认布局单测（TOB-408 F10）：一期概览三卡 + 最近告警 + 指标摘要，全部默认可见。</summary>
public class DashboardDefaultLayoutTests
{
    [Fact]
    public void Create_Returns_Overview_And_F10_Cards_All_Visible()
    {
        var layout = DashboardDefaultLayout.Create();

        Assert.Equal(5, layout.Cards.Count);
        Assert.Equal(
            new[]
            {
                DashboardDefaultLayout.CardIdTotalDevices,
                DashboardDefaultLayout.CardIdOnlineDevices,
                DashboardDefaultLayout.CardIdActiveAlerts,
                DashboardDefaultLayout.CardIdRecentAlerts,
                DashboardDefaultLayout.CardIdMetricsSummary,
            },
            layout.Cards.Select(c => c.Id).ToArray());
        Assert.Equal(
            new[]
            {
                DashboardDefaultLayout.CardTypeTotalDevices,
                DashboardDefaultLayout.CardTypeOnlineDevices,
                DashboardDefaultLayout.CardTypeActiveAlerts,
                DashboardDefaultLayout.CardTypeRecentAlerts,
                DashboardDefaultLayout.CardTypeMetricsSummary,
            },
            layout.Cards.Select(c => c.Type).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, layout.Cards.Select(c => c.Sort).ToArray());
        Assert.All(layout.Cards, c => Assert.True(c.Visible));
    }

    [Fact]
    public void F10_Card_Types_Are_Known_Types_Accepted_By_Catalog()
    {
        Assert.True(DashboardCardCatalog.IsKnownType(DashboardCardCatalog.TypeRecentAlerts));
        Assert.True(DashboardCardCatalog.IsKnownType(DashboardCardCatalog.TypeMetricsSummary));
        Assert.False(DashboardCardCatalog.IsMetricType(DashboardCardCatalog.TypeRecentAlerts));
        Assert.False(DashboardCardCatalog.IsMetricType(DashboardCardCatalog.TypeMetricsSummary));
    }

    [Fact]
    public void Create_Returns_Empty_Object_Config_For_Every_Card()
    {
        var layout = DashboardDefaultLayout.Create();

        Assert.All(layout.Cards, c =>
        {
            Assert.Equal(JsonValueKind.Object, c.Config.ValueKind);
            Assert.Equal("{}", c.Config.GetRawText());
        });
    }

    [Fact]
    public void Default_Layout_Survives_Store_Round_Trip()
    {
        var database = new TempSqliteDatabase();
        try
        {
            var store = new DashboardLayoutStore(database.Factory);
            store.SaveLayout(DashboardDefaultLayout.Create());

            var loaded = store.GetLayout();

            Assert.NotNull(loaded);
            Assert.Equal(DashboardDefaultLayout.Create().Cards.Count, loaded.Cards.Count);
            for (var i = 0; i < loaded.Cards.Count; i++)
            {
                var expected = DashboardDefaultLayout.Create().Cards[i];
                var actual = loaded.Cards[i];
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Type, actual.Type);
                Assert.Equal(expected.Sort, actual.Sort);
                Assert.Equal(expected.Visible, actual.Visible);
                JsonElementAssertions.JsonEquals(expected.Config, actual.Config);
            }
        }
        finally
        {
            database.Dispose();
        }
    }
}
