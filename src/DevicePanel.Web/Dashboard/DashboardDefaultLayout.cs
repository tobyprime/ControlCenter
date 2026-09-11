using System.Text.Json;

namespace DevicePanel.Web.Dashboard;

/// <summary>服务端默认布局（TOB-408 F10）：一期概览三卡 + 最近告警卡 + 指标摘要卡，全部默认可见。</summary>
public static class DashboardDefaultLayout
{
    public const string CardIdTotalDevices = "overview-total-devices";
    public const string CardIdOnlineDevices = "overview-online-devices";
    public const string CardIdActiveAlerts = "overview-active-alerts";
    public const string CardIdRecentAlerts = "recent-alerts";
    public const string CardIdMetricsSummary = "metrics-summary";

    public const string CardTypeTotalDevices = DashboardCardCatalog.TypeTotalDevices;
    public const string CardTypeOnlineDevices = DashboardCardCatalog.TypeOnlineDevices;
    public const string CardTypeActiveAlerts = DashboardCardCatalog.TypeActiveAlerts;
    public const string CardTypeRecentAlerts = DashboardCardCatalog.TypeRecentAlerts;
    public const string CardTypeMetricsSummary = DashboardCardCatalog.TypeMetricsSummary;

    public static DashboardLayout Create()
    {
        var config = JsonDocument.Parse("{}").RootElement.Clone();
        return new DashboardLayout(
        [
            new DashboardCard(CardIdTotalDevices, CardTypeTotalDevices, 0, Visible: true, config),
            new DashboardCard(CardIdOnlineDevices, CardTypeOnlineDevices, 1, Visible: true, config),
            new DashboardCard(CardIdActiveAlerts, CardTypeActiveAlerts, 2, Visible: true, config),
            new DashboardCard(CardIdRecentAlerts, CardTypeRecentAlerts, 3, Visible: true, config),
            new DashboardCard(CardIdMetricsSummary, CardTypeMetricsSummary, 4, Visible: true, config),
        ]);
    }
}
