using DevicePanel.Protocol;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
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

public sealed record CreateTargetRequest(string? Type, string? Name, IReadOnlyList<string>? Tags, ProbeUpsertRequest? Probe);

public sealed record UpdateTargetRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record TokenResetResponse(string AgentToken);

public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var targets = endpoints.MapGroup("/api/targets");

        targets.MapGet("/", (ITargetRegistry registry, AgentOptions options, TimeProvider clock, IMetricsStore metrics) =>
            Results.Ok(registry.List().Select(t => ToResponse(t, options, clock, metrics))));

        targets.MapPost("/", (
            [FromBody] CreateTargetRequest request,
            ITargetRegistry registry,
            IAgentRegistry agents,
            IProbeConfigStore probes,
            IMetricKeyRegistry metricKeys,
            ProbeOptions probeOptions) =>
        {
            if (!TryNormalizeType(request.Type, out var type, out var typeError))
            {
                return Results.BadRequest(new { error = typeError });
            }

            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            // service 目标（模块2）：探针配置必带，创建即生效；device 目标忽略 probe 字段
            List<ProbeMetricMapping> mappings = [];
            var probeUrl = string.Empty;
            var probeInterval = 0;
            if (type == TargetTypes.Service
                && !ProbeRequests.TryNormalize(request.Probe, probeOptions, metricKeys, out probeUrl, out probeInterval, out mappings, out var probeError))
            {
                return Results.BadRequest(new { error = probeError });
            }

            // 三期模块2：device 目标先建 agent（token 宿主），target 携带 agentId 镜像其 hash；service 不生成 agent
            string agentToken;
            long? agentId = null;
            if (type == TargetTypes.Device)
            {
                var agentCreated = agents.Create(name, []);
                agentId = agentCreated.Agent.Id;
                agentToken = agentCreated.Token;
            }
            else
            {
                agentToken = string.Empty; // service 目标无 agent 通道，无 token 可发
            }

            var target = registry.Create(type, name, tags, agentId);
            if (type == TargetTypes.Service)
            {
                probes.Save(target.Id, probeUrl, probeInterval, mappings);
            }

            return Results.Json(ToCreatedResponse(target, agentToken), statusCode: StatusCodes.Status201Created);
        });

        targets.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateTargetRequest request,
            ITargetRegistry registry,
            AgentOptions options,
            TimeProvider clock,
            IMetricsStore metrics) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var updated = registry.Update(id, name, tags);
            return updated is null
                ? Results.NotFound(new { error = "目标不存在" })
                : Results.Ok(ToResponse(updated, options, clock, metrics));
        });

        targets.MapDelete("/{id:long}", (
            long id,
            ITargetRegistry registry,
            IAgentRegistry agents,
            AgentConnectionRegistry connections) =>
        {
            // 先取关联 agent，再删台账与 agent（此后用该 token 的新认证即被拒），最后断开已注册的在线连接；
            // 「认证后、注册前」落入窗口的连接由注册后复核（AgentConnectionRegistry.TryRegister）兜底关闭
            var agentId = agents.FindAgentIdByTargetId(id);
            if (!registry.Delete(id))
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            if (agentId is not null)
            {
                agents.Delete(agentId.Value);
            }

            connections.TryDisconnect(id, WebSocketCloseCodes.DeviceDeleted, "目标已删除");
            return Results.NoContent();
        });

        targets.MapPost("/{id:long}/token", (
            long id,
            ITargetRegistry registry,
            IAgentRegistry agents,
            AgentConnectionRegistry connections) =>
        {
            var target = registry.Get(id);
            if (target is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            // token 归 agent 实体所有（三期模块2）：device 目标路由到 agent 重置；service 目标无 token
            if (target.Type != TargetTypes.Device)
            {
                return Results.BadRequest(new { error = "service 目标没有 agent token" });
            }

            var agentId = agents.FindAgentIdByTargetId(id);
            if (agentId is null)
            {
                return Results.NotFound(new { error = "目标没有关联的 agent" });
            }

            var token = agents.ResetToken(agentId.Value);
            if (token is null)
            {
                return Results.NotFound(new { error = "目标没有关联的 agent" });
            }

            // 旧 token 立即失效：断开用旧 token 建立的在线连接，重连即被拒
            connections.TryDisconnect(id, WebSocketCloseCodes.TokenReset, "token 已重置");
            return Results.Ok(new TokenResetResponse(token));
        });

        return endpoints;
    }

    private static TargetResponse ToResponse(TargetInfo target, AgentOptions options, TimeProvider clock, IMetricsStore metrics) =>
        new(target.Id, target.Type, target.Name, target.Tags, target.CreatedAtUtc, target.UpdatedAtUtc, target.LastSeenAtUtc,
            target.Type == TargetTypes.Device
                ? target.IsOnline(clock, options)
                // service 目标在线 = 最近 status 样本为 true（探针产出）；从未探测时 online=false，由前端结合 lastSeenAtUtc 区分"未探测"
                : metrics.GetLatest(target.Id, MetricKeys.Status) is { ValueText: "true" });

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
