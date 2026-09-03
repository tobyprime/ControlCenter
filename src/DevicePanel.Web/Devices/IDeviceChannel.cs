using System.Text.Json;
using DevicePanel.Protocol;

namespace DevicePanel.Web.Devices;

/// <summary>面板侧设备通道抽象：一帧 = 一个信封。指标/终端/日志等后续通道复用同一收发原语。</summary>
public interface IDeviceChannel
{
    long DeviceId { get; }

    bool IsOpen { get; }

    Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>主动断开（删除设备/token 重置/心跳超时等），closeStatus 使用 WebSocketCloseCodes。</summary>
    Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken);
}

/// <summary>单条入站消息的处理上下文：处理方可通过 Channel 回包、按 Seq 关联请求。</summary>
public sealed record AgentChannelContext(IDeviceChannel Channel, AgentEnvelope Envelope)
{
    public long Seq => Envelope.Seq;

    public JsonElement Payload => Envelope.Payload;
}

/// <summary>
/// 消息类型处理器（扩展点）：为通道新增一种消息能力 = 实现本接口 + 注册 DI（services.AddSingleton&lt;IAgentMessageHandler&gt;(...)），
/// 无需改动信封、WS 接入与分发链路。内置处理器可参考 HeartbeatMessageHandler。
/// </summary>
public interface IAgentMessageHandler
{
    /// <summary>本处理器负责的信封 type（完整匹配，如 heartbeat / metrics.report）。</summary>
    string MessageType { get; }

    Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken);
}

/// <summary>按信封 type 路由到已注册的处理器；未注册的类型忽略（向前兼容：新 agent 旧面板不炸）。</summary>
public sealed class AgentMessageDispatcher
{
    private readonly Dictionary<string, IAgentMessageHandler> _handlers;

    public AgentMessageDispatcher(IEnumerable<IAgentMessageHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.MessageType, StringComparer.Ordinal);
    }

    public Task DispatchAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        return _handlers.TryGetValue(context.Envelope.Type, out var handler)
            ? handler.HandleAsync(context, cancellationToken)
            : Task.CompletedTask;
    }
}
