using DevicePanel.Protocol;
using DevicePanel.Web.Devices;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record DeviceResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online);

public sealed record DeviceCreatedResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online,
    string AgentToken);

public sealed record CreateDeviceRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record UpdateDeviceRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record TokenResetResponse(string AgentToken);

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/devices");

        devices.MapGet("/", (IDeviceRegistry registry, AgentOptions options, TimeProvider clock) =>
            Results.Ok(registry.List().Select(d => ToResponse(d, options, clock))));

        devices.MapPost("/", (
            [FromBody] CreateDeviceRequest request,
            IDeviceRegistry registry,
            Alerting.AlertRuleSeeder alertRuleSeeder) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var created = registry.Create(name, tags);
            try
            {
                // 设备目标与默认告警规则（阈值上限 ×3 + 心跳无数据）随创建落地，与一期离线/阈值行为对齐
                alertRuleSeeder.EnsureForDevice(created.Device.Id, created.Device.Name);
            }
            catch (Exception)
            {
                // 种子失败不阻断设备创建：设备台账先行，规则缺失仅告警侧降级（迁移器/重启后可补齐）
            }

            return Results.Json(ToCreatedResponse(created.Device, created.AgentToken), statusCode: StatusCodes.Status201Created);
        });

        devices.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateDeviceRequest request,
            IDeviceRegistry registry,
            AgentOptions options,
            TimeProvider clock) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var updated = registry.Update(id, name, tags);
            return updated is null
                ? Results.NotFound(new { error = "设备不存在" })
                : Results.Ok(ToResponse(updated, options, clock));
        });

        devices.MapDelete("/{id:long}", (
            long id,
            IDeviceRegistry registry,
            AgentConnectionRegistry connections) =>
        {
            // 先删台账与 token（此后用该 token 的新认证即被拒），再断开已注册的在线连接；
            // 「认证后、注册前」落入窗口的连接由注册后复核（AgentConnectionRegistry.TryRegister）兜底关闭
            if (!registry.Delete(id))
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            connections.TryDisconnect(id, WebSocketCloseCodes.DeviceDeleted, "设备已删除");
            return Results.NoContent();
        });

        devices.MapPost("/{id:long}/token", (
            long id,
            IDeviceRegistry registry,
            AgentConnectionRegistry connections) =>
        {
            var token = registry.ResetToken(id);
            if (token is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            // 旧 token 立即失效：断开用旧 token 建立的在线连接，重连即被拒
            connections.TryDisconnect(id, WebSocketCloseCodes.TokenReset, "token 已重置");
            return Results.Ok(new TokenResetResponse(token));
        });

        return endpoints;
    }

    private static DeviceResponse ToResponse(DeviceInfo device, AgentOptions options, TimeProvider clock) =>
        new(device.Id, device.Name, device.Tags, device.CreatedAtUtc, device.UpdatedAtUtc, device.LastSeenAtUtc, device.IsOnline(clock, options));

    private static DeviceCreatedResponse ToCreatedResponse(DeviceInfo device, string agentToken) =>
        new(device.Id, device.Name, device.Tags, device.CreatedAtUtc, device.UpdatedAtUtc, device.LastSeenAtUtc, false, agentToken);

    private static bool TryNormalize(string? name, IReadOnlyList<string>? tags, out string normalizedName, out List<string> normalizedTags, out string error)
    {
        normalizedName = (name ?? string.Empty).Trim();
        normalizedTags = (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        error = normalizedName.Length switch
        {
            0 => "请填写设备名称",
            > 100 => "设备名称不能超过 100 个字符",
            _ => string.Empty,
        };
        if (error.Length > 0)
        {
            return false;
        }

        if (normalizedTags.Count > 20)
        {
            error = "标签数量不能超过 20 个";
            return false;
        }

        if (normalizedTags.Any(t => t.Length > 50))
        {
            error = "单个标签不能超过 50 个字符";
            return false;
        }

        return true;
    }
}
