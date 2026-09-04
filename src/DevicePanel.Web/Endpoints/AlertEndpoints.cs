using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record SaveAlertSettingsRequest(string? BaseUrl, string? Token, string? TargetType, string? TargetId);

public sealed record SaveThresholdRequest(string? Metric, double? Value);

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/alerts");

        alerts.MapGet("/settings", (IAlertSettingsStore settings) =>
        {
            var current = settings.Get();
            return Results.Ok(new
            {
                napcat = new
                {
                    baseUrl = current.NapcatBaseUrl,
                    // token 只回传"是否已设置"，明文永不离开库（面板留空即保持原值）
                    tokenSet = !string.IsNullOrEmpty(current.NapcatToken),
                    targetType = current.NapcatTargetType,
                    targetId = current.NapcatTargetId,
                },
            });
        });

        alerts.MapPut("/settings", ([FromBody] SaveAlertSettingsRequest request, IAlertSettingsStore settings) =>
        {
            var current = settings.Get();
            var baseUrl = request.BaseUrl ?? current.NapcatBaseUrl;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                var validBase = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
                    && parsed.Scheme is "http" or "https";
                if (!validBase)
                {
                    return Results.BadRequest(new { error = "napcat 地址必须是有效的 http(s) URL" });
                }
            }

            var targetType = request.TargetType ?? current.NapcatTargetType;
            if (targetType is { Length: > 0 } type && type is not (NapcatNotifier.TargetPrivate or NapcatNotifier.TargetGroup))
            {
                return Results.BadRequest(new { error = "通知目标类型仅支持 private（私聊）或 group（群聊）" });
            }

            var targetId = request.TargetId ?? current.NapcatTargetId;
            if (!string.IsNullOrEmpty(targetType))
            {
                if (string.IsNullOrEmpty(targetId) || !long.TryParse(targetId, out _))
                {
                    return Results.BadRequest(new { error = "已选择通知目标类型时必须填写数字 ID（QQ 号或群号）" });
                }
            }
            else if (!string.IsNullOrEmpty(targetId))
            {
                return Results.BadRequest(new { error = "请先选择通知目标类型（私聊或群聊）" });
            }

            settings.Save(new AlertDeliverySettings(
                baseUrl,
                // 未传 token = 保持原值；传空串 = 清除
                request.Token is null ? current.NapcatToken : (request.Token.Length == 0 ? null : request.Token),
                targetType,
                targetId));
            return Results.NoContent();
        });

        alerts.MapGet("/thresholds", (IAlertThresholdStore thresholds, IDeviceRegistry devices) =>
        {
            var names = devices.List().ToDictionary(d => d.Id, d => d.Name);
            return Results.Ok(new
            {
                global = new Dictionary<string, double>
                {
                    [AlertMetrics.Cpu] = thresholds.GetGlobal(AlertMetrics.Cpu),
                    [AlertMetrics.Mem] = thresholds.GetGlobal(AlertMetrics.Mem),
                    [AlertMetrics.Disk] = thresholds.GetGlobal(AlertMetrics.Disk),
                },
                overrides = thresholds.ListOverrides()
                    .Select(o => new
                    {
                        deviceId = o.DeviceId,
                        deviceName = names.GetValueOrDefault(o.DeviceId, $"设备 {o.DeviceId}"),
                        metric = o.Metric,
                        value = o.ThresholdValue,
                    })
                    .ToList(),
            });
        });

        alerts.MapPut("/thresholds/global", ([FromBody] SaveThresholdRequest request, IAlertThresholdStore thresholds) =>
            !TryValidateThreshold(request, out var metric, out var value, out var error)
                ? Results.BadRequest(new { error })
                : SaveAndNoContent(() => thresholds.SetGlobal(metric, value)));

        alerts.MapPut("/thresholds/devices/{deviceId:long}", (
            long deviceId,
            [FromBody] SaveThresholdRequest request,
            IAlertThresholdStore thresholds,
            IDeviceRegistry devices) =>
        {
            if (devices.Get(deviceId) is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            return !TryValidateThreshold(request, out var metric, out var value, out var error)
                ? Results.BadRequest(new { error })
                : SaveAndNoContent(() => thresholds.SetOverride(deviceId, metric, value));
        });

        alerts.MapDelete("/thresholds/devices/{deviceId:long}/{metric}", (
            long deviceId,
            string metric,
            IAlertThresholdStore thresholds) =>
            thresholds.DeleteOverride(deviceId, metric)
                ? Results.NoContent()
                : Results.NotFound(new { error = "该设备没有此指标的覆盖配置" }));

        alerts.MapGet("/queue", (IAlertOutboxStore outbox) =>
        {
            var entries = outbox.List();
            return Results.Ok(new
            {
                count = entries.Count,
                items = entries.Select(e => new
                {
                    id = e.Id,
                    createdAtUtc = e.CreatedAtUtc,
                    channel = e.Channel,
                    title = e.Message.Title,
                    content = e.Message.Content,
                    attempts = e.Attempts,
                    lastError = e.LastError,
                }).ToList(),
            });
        });

        return endpoints;
    }

    private static IResult SaveAndNoContent(Action save)
    {
        save();
        return Results.NoContent();
    }

    private static bool TryValidateThreshold(SaveThresholdRequest request, out string metric, out double value, out string error)
    {
        metric = (request.Metric ?? string.Empty).Trim();
        value = 0;
        error = string.Empty;
        if (!AlertMetrics.IsKnown(metric))
        {
            error = $"不支持的指标：仅支持 {string.Join('/', AlertMetrics.Known)}";
            return false;
        }

        if (request.Value is not { } parsed || parsed <= 0 || parsed > 100)
        {
            error = "阈值必须是 0-100 之间的百分比数值";
            return false;
        }

        value = parsed;
        return true;
    }
}
