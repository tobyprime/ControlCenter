using DevicePanel.Protocol;
using DevicePanel.Web.Targets;

namespace DevicePanel.Web.Logs;

/// <summary>
/// logs.* 上行处理器：把 agent 响应（services/tail/error）交给 LogQueryService 按 (通道, seq) 完成挂起请求。
/// 三个类型各注册一个实例，分发链路零改动；无挂起请求（超时后迟到）由服务侧忽略。
/// </summary>
public abstract class LogsResponseHandler : IAgentMessageHandler
{
    private readonly LogQueryService _queries;

    protected LogsResponseHandler(LogQueryService queries)
    {
        _queries = queries;
    }

    public abstract string MessageType { get; }

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        _queries.Complete(context.Channel, context.Envelope);
        return Task.CompletedTask;
    }
}

public sealed class LogsServicesResponseHandler : LogsResponseHandler
{
    public LogsServicesResponseHandler(LogQueryService queries) : base(queries) { }

    // 协议字符串以 DevicePanel.Protocol.AgentMessageTypes 为唯一事实源，处理器不重复定义
    public override string MessageType => AgentMessageTypes.LogsServicesResponse;
}

public sealed class LogsTailResponseHandler : LogsResponseHandler
{
    public LogsTailResponseHandler(LogQueryService queries) : base(queries) { }

    public override string MessageType => AgentMessageTypes.LogsTailResponse;
}

public sealed class LogsErrorHandler : LogsResponseHandler
{
    public LogsErrorHandler(LogQueryService queries) : base(queries) { }

    public override string MessageType => AgentMessageTypes.LogsError;
}
