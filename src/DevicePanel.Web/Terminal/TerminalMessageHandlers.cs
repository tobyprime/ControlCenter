using DevicePanel.Protocol;
using DevicePanel.Web.Devices;

namespace DevicePanel.Web.Terminal;

/// <summary>
/// term.* 下行处理器：把信封投递到对应会话的中继（TerminalSessionRegistry）。
/// 三个类型各注册一个实例，分发链路零改动；会话不存在（已关闭/陈旧输出）则静默忽略。
/// </summary>
public abstract class TerminalAgentMessageHandler : IAgentMessageHandler
{
    private readonly TerminalSessionRegistry _sessions;

    protected TerminalAgentMessageHandler(TerminalSessionRegistry sessions)
    {
        _sessions = sessions;
    }

    public abstract string MessageType { get; }

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        _sessions.Dispatch(context.Envelope);
        return Task.CompletedTask;
    }
}

public sealed class TermOutputHandler : TerminalAgentMessageHandler
{
    public TermOutputHandler(TerminalSessionRegistry sessions) : base(sessions) { }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public override string MessageType => AgentMessageTypes.TermOutput;
}

public sealed class TermClosedHandler : TerminalAgentMessageHandler
{
    public TermClosedHandler(TerminalSessionRegistry sessions) : base(sessions) { }

    public override string MessageType => AgentMessageTypes.TermClosed;
}

public sealed class TermErrorHandler : TerminalAgentMessageHandler
{
    public TermErrorHandler(TerminalSessionRegistry sessions) : base(sessions) { }

    public override string MessageType => AgentMessageTypes.TermError;
}

public sealed class TermOpenedHandler : TerminalAgentMessageHandler
{
    public TermOpenedHandler(TerminalSessionRegistry sessions) : base(sessions) { }

    public override string MessageType => AgentMessageTypes.TermOpened;
}
