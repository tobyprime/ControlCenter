using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var targets = endpoints.MapGroup("/api/targets");

        // 目标列表（设备与服务统一视图）：设备目标的在线态复用一期判定（心跳超时即离线）
        targets.MapGet("/", (ITargetStore store, IDeviceRegistry devices, AgentOptions options, TimeProvider clock) =>
        {
            var deviceMap = devices.List().ToDictionary(d => d.Id);
            return Results.Ok(store.List().Select(t => new
            {
                id = t.Id,
                type = t.Type,
                name = t.Name,
                deviceId = t.DeviceId,
                online = t.IsDevice && t.DeviceId is { } deviceId
                    && deviceMap.GetValueOrDefault(deviceId)?.IsOnline(clock, options) == true,
            }).ToList());
        });

        // 指标键注册表（约束 A：核心不解释含义，注册表提供类型与展示元数据）
        var metricKeys = endpoints.MapGroup("/api/metric-keys");
        metricKeys.MapGet("/", (IMetricKeyRegistry registry) =>
            Results.Ok(registry.List().Select(k => new
            {
                key = k.Key,
                valueType = MetricValueTypeText.Format(k.ValueType),
                unit = k.Unit,
                displayName = k.DisplayName,
            }).ToList()));

        return endpoints;
    }
}
