using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;
using DevicePanel.Web.Control;

namespace DevicePanel.Web.Agents;

/// <summary>
/// 能力声明处理器：agent 连接后主动上报 agent.capabilities。
/// 两种负载形态（三期模块4 扩充，向后兼容）：
/// - 字符串数组（旧版形态，如 ["metrics","terminal"]）：仅能力名，控制器清空；
/// - 对象形态 { capabilities: [...], controllers: [{key,type,label,tags,paramsSchema}] }：能力名 + 控制器实体。
/// 控制器逐条清洗（缺 key/type、类型不在注册表、声明 schema 不合法 → 丢弃该条并记日志）；
/// 负载不合法只忽略本条并记日志，不影响会话。重复上报以最新为准（能力与控制器随版本变化可重报）。
/// </summary>
public sealed class AgentCapabilitiesMessageHandler : IAgentMessageHandler
{
    private readonly IAgentRegistry _agents;
    private readonly ControlTypeCatalog _controlTypes;
    private readonly ILogger<AgentCapabilitiesMessageHandler> _logger;

    public AgentCapabilitiesMessageHandler(IAgentRegistry agents, ControlTypeCatalog controlTypes,
        ILogger<AgentCapabilitiesMessageHandler> logger)
    {
        _agents = agents;
        _controlTypes = controlTypes;
        _logger = logger;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.AgentCapabilities;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        List<string> capabilities;
        IReadOnlyList<ControllerDeclaration> controllers;
        if (context.Payload.ValueKind == JsonValueKind.Array)
        {
            // 旧版形态：仅能力名（旧版 agent 未声明控制器，重报即覆盖为空）
            capabilities = NormalizeNames(context.Payload);
            controllers = [];
        }
        else if (context.Payload.ValueKind == JsonValueKind.Object)
        {
            capabilities = context.Payload.TryGetProperty("capabilities", out var names) && names.ValueKind == JsonValueKind.Array
                ? NormalizeNames(names)
                : [];
            controllers = NormalizeControllers(context.Channel.AgentId, context.Payload);
        }
        else
        {
            _logger.LogWarning("agent.capabilities 负载不合法，已忽略（agent: {AgentId}，seq={Seq}）", context.Channel.AgentId, context.Seq);
            return Task.CompletedTask;
        }

        if (!_agents.SetCapabilities(context.Channel.AgentId, capabilities, controllers))
        {
            _logger.LogWarning("Agent {AgentId} 上报能力声明时已不存在，已忽略", context.Channel.AgentId);
        }

        return Task.CompletedTask;
    }

    private List<string> NormalizeNames(JsonElement array) =>
        array.EnumerateArray()
            .Select(e => e.GetString()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .Distinct()
            .ToList();

    /// <summary>控制器声明清洗：类型须在注册表内且声明 schema 合法，否则整条丢弃（记 warning，不拒绝整条上报）。</summary>
    private IReadOnlyList<ControllerDeclaration> NormalizeControllers(long agentId, JsonElement payload)
    {
        if (!payload.TryGetProperty("controllers", out var controllers) || controllers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var valid = new List<JsonElement>();
        foreach (var entry in controllers.EnumerateArray())
        {
            var type = entry.ValueKind == JsonValueKind.Object &&
                       entry.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()?.Trim()
                : null;
            var controlType = type is null ? null : _controlTypes.Find(type);
            if (controlType is null)
            {
                _logger.LogWarning("Agent {AgentId} 上报的控制器类型未注册，已丢弃：{Type}", agentId, type ?? "(缺失)");
                continue;
            }

            if (!entry.TryGetProperty("paramsSchema", out var schema) || schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                controlType.ValidateDeclarationSchema(schema) is not { } schemaError)
            {
                valid.Add(entry);
                continue;
            }

            _logger.LogWarning("Agent {AgentId} 上报的控制器声明 schema 不合法，已丢弃（type={Type}）：{Error}", agentId, type, schemaError);
        }

        // key 去重保首条（NormalizeControllers 内部处理），未知/不合法条目已剔除
        return ControllerDeclarationList.Normalize(
            JsonSerializer.SerializeToElement(valid), typeKnown: _ => true);
    }
}
