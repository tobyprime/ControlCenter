using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Control;

/// <summary>ctrl.invoke.response 处理器：按 (通道, seq) 完成挂起的控制下发（模式对齐 LogsResponseHandler）。</summary>
public sealed class ControlInvokeResponseHandler : IAgentMessageHandler
{
    private readonly ControlInvokeService _service;

    public ControlInvokeResponseHandler(ControlInvokeService service) => _service = service;

    public string MessageType => AgentMessageTypes.ControlInvokeResponse;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        _service.Complete(context.Channel, context.Envelope);
        return Task.CompletedTask;
    }
}

/// <summary>ctrl.error 处理器：agent 报告无法执行（控制器不存在/执行失败等），按 seq 关联后由服务折算为 failure 结论。</summary>
public sealed class ControlErrorHandler : IAgentMessageHandler
{
    private readonly ControlInvokeService _service;

    public ControlErrorHandler(ControlInvokeService service) => _service = service;

    public string MessageType => AgentMessageTypes.ControlError;

    public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
    {
        _service.Complete(context.Channel, context.Envelope);
        return Task.CompletedTask;
    }
}
