using DevicePanel.Protocol;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record TargetResponse(
    long Id,
    string Type,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online);

public sealed record TargetCreatedResponse(
    long Id,
    string Type,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online,
    string AgentToken);

public sealed record CreateTargetRequest(string? Type, string? Name, IReadOnlyList<string>? Tags);

public sealed record UpdateTargetRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record TokenResetResponse(string AgentToken);

public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var targets = endpoints.MapGroup("/api/targets");

        targets.MapGet("/", (ITargetRegistry registry, AgentOptions options, TimeProvider clock) =>
            Results.Ok(registry.List().Select(t => ToResponse(t, options, clock))));

        targets.MapPost("/", (
            [FromBody] CreateTargetRequest request,
            ITargetRegistry registry) =>
        {
            if (!TryNormalizeType(request.Type, out var type, out var typeError))
            {
                return Results.BadRequest(new { error = typeError });
            }

            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var created = registry.Create(type, name, tags);
            return Results.Json(ToCreatedResponse(created.Target, created.AgentToken), statusCode: StatusCodes.Status201Created);
        });

        targets.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateTargetRequest request,
            ITargetRegistry registry,
            AgentOptions options,
            TimeProvider clock) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var updated = registry.Update(id, name, tags);
            return updated is null
                ? Results.NotFound(new { error = "目标不存在" })
                : Results.Ok(ToResponse(updated, options, clock));
        });

        targets.MapDelete("/{id:long}", (
            long id,
            ITargetRegistry registry,
            AgentConnectionRegistry connections) =>
        {
            // 先删台账与 token（此后用该 token 的新认证即被拒），再断开已注册的在线连接；
            // 「认证后、注册前」落入窗口的连接由注册后复核（AgentConnectionRegistry.TryRegister）兜底关闭
            if (!registry.Delete(id))
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            connections.TryDisconnect(id, WebSocketCloseCodes.DeviceDeleted, "目标已删除");
            return Results.NoContent();
        });

        targets.MapPost("/{id:long}/token", (
            long id,
            ITargetRegistry registry,
            AgentConnectionRegistry connections) =>
        {
            var token = registry.ResetToken(id);
            if (token is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            // 旧 token 立即失效：断开用旧 token 建立的在线连接，重连即被拒
            connections.TryDisconnect(id, WebSocketCloseCodes.TokenReset, "token 已重置");
            return Results.Ok(new TokenResetResponse(token));
        });

        return endpoints;
    }

    private static TargetResponse ToResponse(TargetInfo target, AgentOptions options, TimeProvider clock) =>
        new(target.Id, target.Type, target.Name, target.Tags, target.CreatedAtUtc, target.UpdatedAtUtc, target.LastSeenAtUtc,
            target.Type == TargetTypes.Device && target.IsOnline(clock, options));

    private static TargetCreatedResponse ToCreatedResponse(TargetInfo target, string agentToken) =>
        new(target.Id, target.Type, target.Name, target.Tags, target.CreatedAtUtc, target.UpdatedAtUtc, target.LastSeenAtUtc, false, agentToken);

    private static bool TryNormalizeType(string? type, out string normalized, out string error)
    {
        normalized = string.IsNullOrWhiteSpace(type) ? TargetTypes.Device : type.Trim();
        if (!TargetTypes.IsValid(normalized))
        {
            error = "目标类型仅支持 device（设备）或 service（服务）";
            return false;
        }

        if (normalized == TargetTypes.Service)
        {
            // service 目标的探针采集在服务监测模块（模块 2）开放，本模块先只放开 device
            error = "服务目标将在服务监测模块开放，当前请创建 device 目标";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryNormalize(string? name, IReadOnlyList<string>? tags, out string normalizedName, out List<string> normalizedTags, out string error)
    {
        normalizedName = (name ?? string.Empty).Trim();
        normalizedTags = (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        error = normalizedName.Length switch
        {
            0 => "请填写目标名称",
            > 100 => "目标名称不能超过 100 个字符",
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
