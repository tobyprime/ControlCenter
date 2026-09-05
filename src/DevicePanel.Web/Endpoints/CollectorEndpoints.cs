using DevicePanel.Protocol;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Collectors;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record CollectorAgentSummary(long Id, string Name, IReadOnlyList<string>? Capabilities, bool Online);

public sealed record CollectorResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    string Mode,
    CollectorAgentSummary? Agent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online);

public sealed record CollectorCreatedResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    string Mode,
    CollectorAgentSummary? Agent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online,
    string AgentToken);

public sealed record CreateCollectorRequest(string? Name, IReadOnlyList<string>? Tags, PullUpsertRequest? Pull);

public sealed record UpdateCollectorRequest(string? Name, IReadOnlyList<string>? Tags);

public sealed record TokenResetResponse(string AgentToken);

/// <summary>
/// 统一采集器 API（三期模块3）：push 与 pull 同一模型，无分栏。
/// 创建模式由请求推导：带 pull 配置 = pull 采集器（面板侧轮询）；否则 = push 采集器（建 agent、token 只发一次）。
/// device / service 语义经内置标签保留（服务端维护，用户传参中的 type:* 被忽略）；自定义标签自由编辑与筛选。
/// </summary>
public static class CollectorEndpoints
{
    public static IEndpointRouteBuilder MapCollectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var collectors = endpoints.MapGroup("/api/collectors");

        // 数据类型清单（验收8）：DI 收集的 ICollectorDataType 全集；新增数据类型 = 注册实现，核心管道零改动
        collectors.MapGet("/data-types", (CollectorDataTypeCatalog catalog) =>
            Results.Ok(catalog.List().Select(t => new { key = t.Key, displayName = t.DisplayName })));

        collectors.MapGet("/", (ICollectorRegistry registry, IAgentRegistry agents, AgentOptions options, TimeProvider clock, IMetricsStore metrics) =>
            Results.Ok(registry.List().Select(c => ToResponse(c, agents, options, clock, metrics))));

        collectors.MapPost("/", (
            [FromBody] CreateCollectorRequest request,
            ICollectorRegistry registry,
            IAgentRegistry agents,
            IPullCollectorConfigStore pulls,
            IMetricKeyRegistry metricKeys,
            ProbeOptions pullOptions) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            // pull 采集器：pull 配置必带，创建即生效；push 采集器忽略 pull 字段
            List<PullMetricMapping> mappings = [];
            var pullUrl = string.Empty;
            var pullInterval = 0;
            var isPull = request.Pull is not null;
            if (isPull
                && !PullCollectorRequests.TryNormalize(request.Pull, pullOptions, metricKeys, out pullUrl, out pullInterval, out mappings, out var pullError))
            {
                return Results.BadRequest(new { error = pullError });
            }

            // push 采集器先建 agent（token 宿主），采集器携带 agentId 镜像其 hash；pull 不生成 agent
            string agentToken = string.Empty;
            long? agentId = null;
            if (!isPull)
            {
                var agentCreated = agents.Create(name, []);
                agentId = agentCreated.Agent.Id;
                agentToken = agentCreated.Token;
            }

            var builtinTag = isPull ? CollectorBuiltinTags.Service : CollectorBuiltinTags.Device;
            CollectorInfo collector;
            try
            {
                collector = registry.Create(name, [.. tags, builtinTag], agentId);
            }
            catch
            {
                // 补偿：collectors 落库失败时回收刚签发的 agent，避免遗留带有效 token 的孤儿行
                if (agentId is not null)
                {
                    agents.Delete(agentId.Value);
                }

                throw;
            }

            if (isPull)
            {
                pulls.Save(collector.Id, pullUrl, pullInterval, mappings);
            }

            return Results.Json(ToCreatedResponse(collector, agentToken), statusCode: StatusCodes.Status201Created);
        });

        collectors.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateCollectorRequest request,
            ICollectorRegistry registry,
            IAgentRegistry agents,
            AgentOptions options,
            TimeProvider clock,
            IMetricsStore metrics) =>
        {
            if (!TryNormalize(request.Name, request.Tags, out var name, out var tags, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var existing = registry.Get(id);
            if (existing is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            // 内置 type:* 标签语义服务端所有：按采集器既有模式重挂，用户传参中的同名标签被忽略
            var builtinTag = existing.AgentId is not null ? CollectorBuiltinTags.Device : CollectorBuiltinTags.Service;
            var updated = registry.Update(id, name, [.. tags, builtinTag]);
            return updated is null
                ? Results.NotFound(new { error = "采集器不存在" })
                : Results.Ok(ToResponse(updated, agents, options, clock, metrics));
        });

        collectors.MapDelete("/{id:long}", (
            long id,
            ICollectorRegistry registry,
            IAgentRegistry agents,
            AgentConnectionRegistry connections) =>
        {
            // 先取关联 agent，再删台账与 agent（此后用该 token 的新认证即被拒），最后断开已注册的在线连接；
            // 「认证后、注册前」落入窗口的连接由注册后复核（AgentConnectionRegistry.TryRegister）兜底关闭
            var agentId = agents.FindAgentIdByCollectorId(id);
            if (!registry.Delete(id))
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            if (agentId is not null)
            {
                agents.Delete(agentId.Value);
            }

            connections.TryDisconnect(id, WebSocketCloseCodes.DeviceDeleted, "采集器已删除");
            return Results.NoContent();
        });

        collectors.MapPost("/{id:long}/token", (
            long id,
            ICollectorRegistry registry,
            IAgentRegistry agents,
            AgentConnectionRegistry connections) =>
        {
            var collector = registry.Get(id);
            if (collector is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            // token 归 agent 实体所有：push 采集器路由到 agent 重置；pull 采集器无 token
            if (collector.AgentId is null)
            {
                return Results.BadRequest(new { error = "pull 采集器没有 agent token" });
            }

            var token = agents.ResetToken(collector.AgentId.Value);
            if (token is null)
            {
                return Results.NotFound(new { error = "采集器没有关联的 agent" });
            }

            // 旧 token 立即失效：断开用旧 token 建立的在线连接，重连即被拒
            connections.TryDisconnect(id, WebSocketCloseCodes.TokenReset, "token 已重置");
            return Results.Ok(new TokenResetResponse(token));
        });

        return endpoints;
    }

    private static CollectorResponse ToResponse(
        CollectorInfo collector, IAgentRegistry agents, AgentOptions options, TimeProvider clock, IMetricsStore metrics)
    {
        CollectorAgentSummary? agent = null;
        var online = false;
        if (collector.AgentId is { } agentId)
        {
            // push 采集器：在线 = 心跳新鲜度；能力声明随 agent 带出（模块2 schema，决定日志/终端等入口）
            var agentInfo = agents.Get(agentId);
            if (agentInfo is not null)
            {
                agent = new CollectorAgentSummary(agentInfo.Id, agentInfo.Name, agentInfo.Capabilities, agentInfo.IsOnline(clock, options));
            }

            online = collector.IsOnline(clock, options);
        }
        else
        {
            // pull 采集器：在线 = 最近 status 样本为 true（面板侧探测产出）；从未探测时 online=false，由前端结合 lastSeenAtUtc 区分"未探测"
            online = metrics.GetLatest(collector.Id, MetricKeys.Status) is { ValueText: "true" };
        }

        return new CollectorResponse(
            collector.Id, collector.Name, collector.Tags, collector.AgentId is not null ? "push" : "pull",
            agent, collector.CreatedAtUtc, collector.UpdatedAtUtc, collector.LastSeenAtUtc, online);
    }

    private static CollectorCreatedResponse ToCreatedResponse(CollectorInfo collector, string agentToken) =>
        new(collector.Id, collector.Name, collector.Tags, collector.AgentId is not null ? "push" : "pull",
            null, collector.CreatedAtUtc, collector.UpdatedAtUtc, collector.LastSeenAtUtc, false, agentToken);

    private static bool TryNormalize(string? name, IReadOnlyList<string>? tags, out string normalizedName, out List<string> normalizedTags, out string error)
    {
        normalizedName = (name ?? string.Empty).Trim();
        normalizedTags = CollectorBuiltinTags.Strip((tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList());
        error = normalizedName.Length switch
        {
            0 => "请填写采集器名称",
            > 100 => "采集器名称不能超过 100 个字符",
            _ => string.Empty,
        };
        if (error.Length > 0)
        {
            return false;
        }

        // 自定义标签为自由文本（PRD 技术默认值）：仅去除首尾空白与空串，不限数量与长度
        return true;
    }
}
