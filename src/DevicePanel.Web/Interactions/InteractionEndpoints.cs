using DevicePanel.Web.Devices;

namespace DevicePanel.Web.Interactions;

public sealed record InteractionModeResponse(string Key, string DisplayName, string? Description);

/// <summary>交互模式查询 API：全量注册模式清单 + 目标声明入口（目标详情页交互区的渲染数据源，/api 前缀由登录拦截统一把关）。</summary>
public static class InteractionEndpoints
{
    public static IEndpointRouteBuilder MapInteractionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/interactions/modes", (InteractionModeRegistry registry) =>
            Results.Ok(registry.Modes.Select(ToResponse).ToList()));

        endpoints.MapGet("/api/devices/{deviceId:long}/interaction-modes", (
            long deviceId,
            IDeviceRegistry devices,
            InteractionModeRegistry registry,
            IInteractionModeCatalog catalog) =>
        {
            if (devices.Get(deviceId) is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            var modes = catalog.GetDeclaredModeKeys(deviceId)
                .Select(key => registry.Find(key))
                .Where(mode => mode is not null)
                .Select(mode => ToResponse(mode!))
                .ToList();
            return Results.Ok(modes);
        });

        return endpoints;
    }

    private static InteractionModeResponse ToResponse(IInteractionMode mode) =>
        new(mode.Key, mode.DisplayName, mode.Description);
}
