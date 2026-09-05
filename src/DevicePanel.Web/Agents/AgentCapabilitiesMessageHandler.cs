using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Targets;

namespace DevicePanel.Web.Agents;

/// <summary>
/// 能力声明处理器：agent 连接后主动上报 agent.capabilities（字符串数组，如 ["metrics","terminal"]），
/// 面板持久化到 agents.capabilities_json 供管理页展示；未上报的旧版 agent 保持未声明（向后兼容）。
/// 负载不合法只忽略本条并记日志，不影响会话。重复上报以最新为准（能力随版本变化可重报）。
/// </summary>
public sealed class AgentCapabilitiesMessageHandler : IAgentMessageHandler
{
    private readonly IAgentRegistry _agents;
    private readonly ILogger<AgentCapabilitiesMessageHandler> _logger;

    public AgentCapabilitiesMessageHandler(IAgentRegistry agents, ILogger<AgentCapabilitiesMessageHandler> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.AgentCapabilities;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        if (context.Channel.AgentId <= 0 || context.Payload.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("agent.capabilities 负载不合法，已忽略（agent: {AgentId}，seq={Seq}）", context.Channel.AgentId, context.Seq);
            return Task.CompletedTask;
        }

        var capabilities = context.Payload
            .EnumerateArray()
            .Select(e => e.GetString()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .Distinct()
            .ToList();
        if (!_agents.SetCapabilities(context.Channel.AgentId, capabilities))
        {
            _logger.LogWarning("Agent {AgentId} 上报能力声明时已不存在，已忽略", context.Channel.AgentId);
        }

        return Task.CompletedTask;
    }
}
