using System.Text.Json;
using DevicePanel.Protocol;

namespace DevicePanel.Agent;

/// <summary>
/// 终端通道：处理面板下行的 term.* 消息，维护 sessionId → PTY 会话。
/// - term.open：创建 PTY shell，后台泵持续把输出按 term.output 流式回发；就绪后回 term.opened
/// - term.input：base64 解码写入 PTY；term.close：终止 PTY（泵随之发 term.closed）
/// - PTY 退出/EOF：回 term.closed；打开失败：回 term.error
/// 约定：任何异常都不外抛（消息循环契约），term.output 的发送失败仅意味着连接已断。
/// </summary>
internal sealed class TerminalChannel : ITerminalChannel
{
    private sealed class SessionState
    {
        public required string Id { get; init; }
        public required IPtySession Pty { get; init; }
        public required Task Pump { get; init; }
    }

    private readonly IAgentDownlink _downlink;
    private readonly IPtySessionFactory _ptyFactory;
    private readonly TextWriter _output;
    private readonly object _lock = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public TerminalChannel(IAgentDownlink downlink, IPtySessionFactory ptyFactory, TextWriter? output = null)
    {
        _downlink = downlink;
        _ptyFactory = ptyFactory;
        _output = output ?? TextWriter.Null;
    }

    public async Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            switch (envelope.Type)
            {
                case AgentMessageTypes.TermOpen:
                    await HandleOpenAsync(envelope.Payload).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.TermInput:
                    await HandleInputAsync(envelope.Payload).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.TermResize:
                    HandleResize(envelope.Payload);
                    break;
                case AgentMessageTypes.TermClose:
                    HandleClose(envelope.Payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 消息循环契约：下行处理失败绝不打断心跳/指标节拍
            await _output.WriteLineAsync($"终端消息处理失败（{envelope.Type}）：{ex.Message}").ConfigureAwait(false);
        }
    }

    public async Task ShutdownAsync()
    {
        SessionState[] sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Pty.Kill();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.Pump.ConfigureAwait(false);
            }
            catch
            {
                // 泵的收尾异常在连接断开场景下无关紧要
            }
        }
    }

    private async Task HandleOpenAsync(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize(payload.GetRawText(), AgentJsonContext.Default.TermOpenPayload);
        if (request is null || string.IsNullOrEmpty(request.SessionId))
        {
            return;
        }

        var sessionId = request.SessionId;
        IPtySession pty;
        try
        {
            lock (_lock)
            {
                if (_sessions.ContainsKey(sessionId))
                {
                    return; // 同 ID 重复打开：忽略（面板保证 sessionId 唯一）
                }
            }

            pty = _ptyFactory.Create(Math.Clamp(request.Cols, 2, 500), Math.Clamp(request.Rows, 2, 200));
        }
        catch (Exception ex)
        {
            await _downlink.SendErrorAsync(sessionId, $"打开终端失败：{ex.Message}", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        Register(sessionId, pty);
        await _downlink.SendOpenedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleInputAsync(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize(payload.GetRawText(), AgentJsonContext.Default.TermInputPayload);
        if (request?.SessionId is not { } sessionId || request.Data is null)
        {
            return;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(request.Data);
        }
        catch (FormatException)
        {
            await _output.WriteLineAsync($"终端 {sessionId} 收到非法 base64 输入，已忽略").ConfigureAwait(false);
            return;
        }

        SessionState? state;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out state);
        }

        if (state is null)
        {
            return;
        }

        state.Pty.Write(data);
    }

    private void HandleResize(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize(payload.GetRawText(), AgentJsonContext.Default.TermResizePayload);
        if (request?.SessionId is not { } sessionId)
        {
            return;
        }

        SessionState? state;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out state);
        }

        state?.Pty.SetWindowSize(Math.Clamp(request.Cols, 2, 500), Math.Clamp(request.Rows, 2, 200));
    }

    private void HandleClose(JsonElement payload)
    {
        var request = JsonSerializer.Deserialize(payload.GetRawText(), AgentJsonContext.Default.TermClosedPayload);
        if (request?.SessionId is not { } sessionId)
        {
            return;
        }

        SessionState? state;
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out state))
            {
                _sessions.Remove(sessionId);
            }
        }

        // 终止后泵检测到 EOF 发送 term.closed（本会话已从登记表移除，重发会话不存在）
        state?.Pty.Kill();
    }

    private void Register(string sessionId, IPtySession pty)
    {
        var state = new SessionState
        {
            Id = sessionId,
            Pty = pty,
            Pump = PumpAsync(sessionId, pty),
        };
        lock (_lock)
        {
            _sessions[sessionId] = state;
        }
    }

    private async Task PumpAsync(string sessionId, IPtySession pty)
    {
        // 关键：先让出调用线程再进入阻塞读——否则泵会在 Register 的调用线程（消息循环）上
        // 同步执行阻塞 Read，令牌节拍与 term.opened 确认全部停摆
        await Task.Yield();
        var buffer = new byte[4096];
        try
        {
            while (_downlink.IsOpen)
            {
                // 同步阻塞读放在泵任务里：每个终端会话一个专用泵，不影响消息循环与节拍
                var read = pty.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break; // EOF：shell 退出
                }

                await _downlink.SendOutputAsync(sessionId, buffer.AsMemory(0, read), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 读失败（连接断开/会话被杀导致句柄释放）一律按会话结束处理
        }
        finally
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var state) && ReferenceEquals(state.Pty, pty))
                {
                    _sessions.Remove(sessionId);
                }
            }

            pty.Kill();
            // 连接已断则 no-op；连接仍在（shell 退出）则通知面板收尾
            await _downlink.SendClosedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
