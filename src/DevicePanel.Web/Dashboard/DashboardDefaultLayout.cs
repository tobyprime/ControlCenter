using System.Text.Json;

namespace DevicePanel.Web.Dashboard;

/// <summary>服务端默认布局：等价一期主页概览（设备总数、在线设备、活跃告警），全部默认可见。</summary>
public static class DashboardDefaultLayout
{
    public const string CardIdTotalDevices = "overview-total-devices";
    public const string CardIdOnlineDevices = "overview-online-devices";
    public const string CardIdActiveAlerts = "overview-active-alerts";

    public const string CardTypeTotalDevices = "overview-total-devices";
    public const string CardTypeOnlineDevices = "overview-online-devices";
    public const string CardTypeActiveAlerts = "overview-active-alerts";

    public static DashboardLayout Create() => new([]);
}
