namespace DevicePanel.Web.Dashboard;

/// <summary>
/// 服务端卡片类型目录（与前端 cards.ts 的 BUILTIN_CARD_DEFS 对齐）：
/// PUT 布局按此校验卡片 type 与指标卡 config 的来源结构（targetId/key/windowHours），
/// 未知类型与缺来源的指标卡拒绝入库——前端对未知类型是静默丢弃，脏数据入库等于卡片消失。
/// </summary>
public static class DashboardCardCatalog
{
    public const string TypeTotalDevices = "overview-total-devices";
    public const string TypeOnlineDevices = "overview-online-devices";
    public const string TypeActiveAlerts = "overview-active-alerts";
    public const string TypeMetricValue = "metric-value";
    public const string TypeMetricStatus = "metric-status";
    public const string TypeMetricChart = "metric-chart";
    public const string TypeControl = "control-card";

    /// <summary>指标卡类型：config 必须携带 { targetId, key, windowHours? }（windowHours 可选，缺省语义在前端）。</summary>
    public static bool IsMetricType(string type) =>
        type is TypeMetricValue or TypeMetricStatus or TypeMetricChart;

    public static bool IsKnownType(string type) =>
        type is TypeTotalDevices or TypeOnlineDevices or TypeActiveAlerts
            or TypeMetricValue or TypeMetricStatus or TypeMetricChart
            or TypeControl;
}
