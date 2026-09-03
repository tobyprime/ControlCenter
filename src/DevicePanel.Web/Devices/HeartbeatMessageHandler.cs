using DevicePanel.Protocol;

namespace DevicePanel.Web.Devices;

/// <summary>
/// 内置心跳处理器：刷新设备 last_seen（数据库 + 在线连接登记表）。
/// 同时作为消息处理器的参考实现——指标/终端/日志等能力按同样方式接入，无需改通道代码。
/// </summary>
public sealed class HeartbeatMessageHandler : IAgentMessageHandler
{
    private readonly IDeviceRegistry _devices;
    private readonly AgentConnectionRegistry _connections;
    private readonly TimeProvider _clock;

    public HeartbeatMessageHandler(IDeviceRegistry devices, AgentConnectionRegistry connections, TimeProvider clock)
    {
        _devices = devices;
        _connections = connections;
        _clock = clock;
    }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public string MessageType => AgentMessageTypes.Heartbeat;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        _devices.Touch(context.Channel.DeviceId, nowUtc);
        _connections.Touch(context.Channel.DeviceId, nowUtc);
        return Task.CompletedTask;
    }
}
