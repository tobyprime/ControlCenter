using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DevicePanel.Protocol;

namespace DevicePanel.Agent;

/// <summary>term.open 请求负载（面板 → agent）。</summary>
internal sealed record TermOpenPayload(string SessionId, int Cols, int Rows);

/// <summary>term.input 输入负载：data 为 base64 UTF-8 字节（保分块边界）。</summary>
internal sealed record TermInputPayload(string SessionId, string Data);

/// <summary>term.resize 尺寸变更负载（面板 → agent）。</summary>
internal sealed record TermResizePayload(string SessionId, int Cols, int Rows);

/// <summary>term.opened 确认负载（agent → 面板）。</summary>
internal sealed record TermOpenedPayload(string SessionId);

/// <summary>term.output 输出负载：data 为 base64 字节。</summary>
internal sealed record TermOutputPayload(string SessionId, string Data);

/// <summary>term.closed 结束负载（agent → 面板；shell 退出或关闭完成）。</summary>
internal sealed record TermClosedPayload(string SessionId);

/// <summary>term.error 错误负载（agent → 面板）。</summary>
internal sealed record TermErrorPayload(string SessionId, string Message);

/// <summary>
/// agent 侧下行发送原语：面板主动消息（term.* 等）经此回发；seq 由实现方内部递增。
/// 连接已断开时发送为 no-op（尽力而为，不打断会话清理）。
/// </summary>
internal interface IAgentDownlink
{
    bool IsOpen { get; }

    Task SendOpenedAsync(string sessionId, CancellationToken cancellationToken);

    Task SendOutputAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    Task SendClosedAsync(string sessionId, CancellationToken cancellationToken);

    Task SendErrorAsync(string sessionId, string message, CancellationToken cancellationToken);
}

/// <summary>
/// agent 侧下行会话通道（扩展点）：在消息循环中按信封 type 处理面板主动消息。
/// 实现约定：HandleAsync 不得向消息循环抛异常——下行处理失败只记日志，
/// 绝不中断心跳/指标节拍（TOB-338 回归契约）；长任务（如终端输出泵）自行后台化。
/// </summary>
internal interface ITerminalChannel
{
    Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>连接断开/重连时调用：终止该连接上的全部派生资源（终端 PTY 会话等）。</summary>
    Task ShutdownAsync();
}

/// <summary>
/// 面板下行（agent 侧）默认实现：单条 WS 连接上的信封发送器。
/// ClientWebSocket 不允许并发发送：节拍（心跳/指标）与终端泵的发送共用一把锁串行化。
/// </summary>
internal sealed class AgentDownlink : IAgentDownlink, ILogsDownlink, IControllersDownlink
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _seq;

    public AgentDownlink(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>节拍发送（心跳/指标）：连接已断时抛出，由重连循环按断线处理。</summary>
    public Task SendAsync<T>(string type, T payload, JsonTypeInfo<T> payloadTypeInfo, CancellationToken ct) =>
        SendCoreAsync(type, seq => AgentEnvelope.Create(type, seq, JsonSerializer.SerializeToElement(payload, payloadTypeInfo)), ct,
            throwWhenClosed: true);

    public Task SendOpenedAsync(string sessionId, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.TermOpened,
            seq => AgentEnvelope.Create(AgentMessageTypes.TermOpened, seq,
                JsonSerializer.SerializeToElement(new TermOpenedPayload(sessionId), AgentJsonContext.Default.TermOpenedPayload)),
            cancellationToken, throwWhenClosed: false);

    public Task SendOutputAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.TermOutput,
            seq => AgentEnvelope.Create(AgentMessageTypes.TermOutput, seq,
                JsonSerializer.SerializeToElement(
                    new TermOutputPayload(sessionId, Convert.ToBase64String(data.Span)), AgentJsonContext.Default.TermOutputPayload)),
            cancellationToken, throwWhenClosed: false);

    public Task SendClosedAsync(string sessionId, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.TermClosed,
            seq => AgentEnvelope.Create(AgentMessageTypes.TermClosed, seq,
                JsonSerializer.SerializeToElement(new TermClosedPayload(sessionId), AgentJsonContext.Default.TermClosedPayload)),
            cancellationToken, throwWhenClosed: false);

    public Task SendErrorAsync(string sessionId, string message, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.TermError,
            seq => AgentEnvelope.Create(AgentMessageTypes.TermError, seq,
                JsonSerializer.SerializeToElement(new TermErrorPayload(sessionId, message), AgentJsonContext.Default.TermErrorPayload)),
            cancellationToken, throwWhenClosed: false);

    // logs.* 响应按请求 seq 回包（请求-响应关联），与节拍发送共用同一把发送锁
    public Task SendServicesResponseAsync(long seq, IReadOnlyList<LogsServicePayload> services, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.LogsServicesResponse,
            () => AgentEnvelope.Create(AgentMessageTypes.LogsServicesResponse, seq,
                JsonSerializer.SerializeToElement(new LogsServicesPayload(services), AgentJsonContext.Default.LogsServicesPayload)),
            cancellationToken);

    public Task SendTailResponseAsync(long seq, IReadOnlyList<LogsLinePayload> lines, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.LogsTailResponse,
            () => AgentEnvelope.Create(AgentMessageTypes.LogsTailResponse, seq,
                JsonSerializer.SerializeToElement(new LogsTailPayload(lines), AgentJsonContext.Default.LogsTailPayload)),
            cancellationToken);

    public Task SendLogsErrorAsync(long seq, string message, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.LogsError,
            () => AgentEnvelope.Create(AgentMessageTypes.LogsError, seq,
                JsonSerializer.SerializeToElement(new LogsErrorPayload(message), AgentJsonContext.Default.LogsErrorPayload)),
            cancellationToken);

    // metrics.latest.* 响应按请求 seq 回包（请求-响应关联，与 logs.* 一致）
    public Task SendMetricsLatestResponseAsync(long seq, MetricsPayload payload, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.MetricsLatestResponse,
            () => AgentEnvelope.Create(AgentMessageTypes.MetricsLatestResponse, seq,
                JsonSerializer.SerializeToElement(payload, AgentJsonContext.Default.MetricsPayload)),
            cancellationToken);

    public Task SendMetricsErrorAsync(long seq, string message, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.MetricsError,
            () => AgentEnvelope.Create(AgentMessageTypes.MetricsError, seq,
                JsonSerializer.SerializeToElement(new LogsErrorPayload(message), AgentJsonContext.Default.LogsErrorPayload)),
            cancellationToken);

    // ctrl.* 响应按请求 seq 回包（请求-响应关联，与 logs.* 一致）
    public Task SendInvokeResponseAsync(long seq, string? message, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.ControlInvokeResponse,
            () => AgentEnvelope.Create(AgentMessageTypes.ControlInvokeResponse, seq,
                JsonSerializer.SerializeToElement(new ControlInvokeResponsePayload(message), AgentJsonContext.Default.ControlInvokeResponsePayload)),
            cancellationToken);

    public Task SendControlErrorAsync(long seq, string message, CancellationToken cancellationToken) =>
        SendCoreAsync(AgentMessageTypes.ControlError,
            () => AgentEnvelope.Create(AgentMessageTypes.ControlError, seq,
                JsonSerializer.SerializeToElement(new ControlErrorPayload(message), AgentJsonContext.Default.ControlErrorPayload)),
            cancellationToken);

    private Task SendCoreAsync(string type, Func<AgentEnvelope> envelopeFactory, CancellationToken ct) =>
        SendCoreAsync(type, _ => envelopeFactory(), ct, throwWhenClosed: false);

    private async Task SendCoreAsync(string type, Func<long, AgentEnvelope> envelopeFactory, CancellationToken ct, bool throwWhenClosed)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsOpen)
            {
                if (throwWhenClosed)
                {
                    throw new WebSocketException($"连接已断开，无法发送 {type}");
                }

                return;
            }

            var envelope = envelopeFactory(Interlocked.Increment(ref _seq));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
