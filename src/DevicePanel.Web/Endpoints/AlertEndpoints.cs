using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record SaveAlertSettingsRequest(string? BaseUrl, string? Token, string? TargetType, string? TargetId);

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
}
