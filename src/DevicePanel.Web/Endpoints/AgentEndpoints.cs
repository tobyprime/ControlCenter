using DevicePanel.Protocol;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record AgentResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string>? Capabilities,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    bool Online,
    long? TargetId);

public sealed record AgentCreatedResponse(
    long Id,
    string Name,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string>? Capabilities,
    string AgentToken);

public sealed record CreateAgentRequest(string? Name, IReadOnlyList<string>? Labels);

public sealed record UpdateAgentLabelsRequest(IReadOnlyList<string>? Labels);

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var agents = endpoints.MapGroup("/api/agents");

        agents.MapGet("/", (IAgentRegistry registry, AgentOptions options, TimeProvider clock, string? label) =>
            Results.Ok(registry.List(label).Select(a => ToResponse(a, options, clock))));

        agents.MapPost("/", ([FromBody] CreateAgentRequest request, IAgentRegistry registry) =>
        {
            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Results.BadRequest(new { error = "请填写 agent 名称" });
            }

            if (name.Length > 100)
            {
                return Results.BadRequest(new { error = "Agent 名称不能超过 100 个字符" });
            }

            // token 明文只在创建响应出现一次
            var created = registry.Create(name, request.Labels ?? []);
            return Results.Json(ToCreatedResponse(created), statusCode: StatusCodes.Status201Created);
        });

        agents.MapPut("/{id:long}/labels", (
            long id,
            [FromBody] UpdateAgentLabelsRequest request,
            IAgentRegistry registry,
            AgentOptions options,
            TimeProvider clock) =>
        {
            var updated = registry.UpdateLabels(id, request.Labels ?? []);
            return updated is null
                ? Results.NotFound(new { error = "Agent 不存在" })
                : Results.Ok(ToResponse(updated, options, clock));
        });

        agents.MapPost("/{id:long}/token", (long id, IAgentRegistry registry, AgentConnectionRegistry connections) =>
        {
            var token = registry.ResetToken(id);
            if (token is null)
            {
                return Results.NotFound(new { error = "Agent 不存在" });
            }

            // 旧 token 立即失效：断开用旧 token 建立的在线连接（关联 agent 的连接键仍是 target id）
            var targetId = registry.FindTargetIdByAgentId(id);
            connections.TryDisconnect(targetId ?? -id, WebSocketCloseCodes.TokenReset, "token 已重置");
            return Results.Ok(new TokenResetResponse(token));
        });

        agents.MapDelete("/{id:long}", (long id, IAgentRegistry registry, AgentConnectionRegistry connections) =>
        {
            // 关联目标的 agent 从目标页删除（目标删除级联 agent）；此处删除会破坏双写期关联，拒绝
            if (registry.FindTargetIdByAgentId(id) is not null)
            {
                return Results.BadRequest(new { error = "该 Agent 已关联目标，请在目标管理页删除" });
            }

            if (!registry.Delete(id))
            {
                return Results.NotFound(new { error = "Agent 不存在" });
            }

            connections.TryDisconnect(-id, WebSocketCloseCodes.DeviceDeleted, "Agent 已删除");
            return Results.NoContent();
        });

        return endpoints;
    }

    private static AgentResponse ToResponse(AgentInfo agent, AgentOptions options, TimeProvider clock) =>
        new(agent.Id, agent.Name, agent.Labels, agent.Capabilities, agent.CreatedAtUtc, agent.UpdatedAtUtc, agent.LastSeenAtUtc,
            agent.IsOnline(clock, options), agent.TargetId);

    private static AgentCreatedResponse ToCreatedResponse(AgentCreated created) =>
        new(created.Agent.Id, created.Agent.Name, created.Agent.Labels, created.Agent.Capabilities, created.Token);
}
