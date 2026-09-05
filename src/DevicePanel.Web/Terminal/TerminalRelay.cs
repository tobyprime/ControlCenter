using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Terminal;

/// <summary>
/// 单个终端会话的浏览器 ↔ agent 双向中继。
/// 浏览器 JSON 消息 {type:"input", data} → term.input（base64 保分块边界）；
/// agent 下行 term.opened/output/closed/error → 浏览器 JSON 消息。
/// 留痕随中继落库（输入/输出都记）；存储故障只丢留痕不断会话（沿用 TOB-338 契约）。
/// 关闭路径：浏览器断开（operator）→ 回发 term.close；agent 断开（connection-lost）、
/// shell 退出（agent-exit）、打开失败（error）→ 通知浏览器并收尾。
/// </summary>
public sealed class TerminalRelay
{
    /// <summary>单帧上限：覆盖一次性粘贴的场景（base64 膨胀后仍有余量）；超限帧整条丢弃，不终止会话。</summary>
    private const int BrowserFrameLimit = 256 * 1024;

    private readonly long _deviceId;
    private readonly string _operatorName;
    private readonly IDeviceChannel _agentChannel;
    private readonly WebSocket _browser;
    private readonly ITerminalStore _store;
    private readonly TerminalSessionRegistry _sessions;
    private readonly AgentConnectionRegistry _connections;
    private readonly int _cols;
    private readonly int _rows;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _browserSendLock = new(1, 1);
    /// <summary>增量 UTF-8 解码器：term.output 按字节分块，跨块切断的多字节序列由此暂存，避免 U+FFFD 乱码。</summary>
    private readonly Decoder _outputDecoder = Encoding.UTF8.GetDecoder();
    private long _seq;
    private int _closed;

    public TerminalRelay(
        string sessionId,
        long deviceId,
        string operatorName,
        int cols,
        int rows,
        IDeviceChannel agentChannel,
        WebSocket browser,
        ITerminalStore store,
        TerminalSessionRegistry sessions,
        AgentConnectionRegistry connections,
        TimeProvider clock,
        ILogger logger)
    {
        SessionId = sessionId;
        _deviceId = deviceId;
        _operatorName = operatorName ?? string.Empty;
        _cols = cols;
        _rows = rows;
        _agentChannel = agentChannel;
        _browser = browser;
        _store = store;
        _sessions = sessions;
        _connections = connections;
        _clock = clock;
        _logger = logger;
    }

    public string SessionId { get; }

    /// <summary>通道绑定校验：仅接受本会话所属设备通道的下行信封（防跨设备注入）。</summary>
    public bool AcceptsFrom(IDeviceChannel channel) => ReferenceEquals(channel, _agentChannel);

    /// <summary>主循环：浏览器 → agent 泵；浏览器断开即结束会话（operator 关闭）。</summary>
    public async Task RunAsync()
    {
        _connections.ConnectionClosed += OnConnectionClosed;
        try
        {
            // 留痕先行：无论后续走哪条收尾路径，会话元数据先落库
            RecordOpen();

            // 订阅后复核通道仍是在线通道：封住端点 GetChannel → 此处的断连窗口（agent 恰先断开）
            if (!ReferenceEquals(_connections.GetChannel(_deviceId), _agentChannel))
            {
                await OnAgentDisconnectedAsync().ConfigureAwait(false);
                return;
            }

            await SendToAgentAsync(AgentMessageTypes.TermOpen, new { sessionId = SessionId, cols = _cols, rows = _rows })
                .ConfigureAwait(false);

            await PumpBrowserAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "终端会话 {SessionId}（设备 {DeviceId}）中继异常结束", SessionId, _deviceId);
        }
        finally
        {
            _connections.ConnectionClosed -= OnConnectionClosed;
            // 浏览器先走（operator）或异常退出：补一个 agent 侧关闭，幂等
            await CloseAsync(TerminalCloseReasons.Operator, sendTermClose: true).ConfigureAwait(false);
        }
    }

    /// <summary>agent 下行投递入口（由 term.* 处理器经登记表调用）。</summary>
    public async Task OnAgentEnvelopeAsync(AgentEnvelope envelope)
    {
        try
        {
            switch (envelope.Type)
            {
                case AgentMessageTypes.TermOpened:
                    await SendToBrowserAsync(new { type = "opened", sessionId = SessionId }).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.TermOutput:
                    await HandleOutputAsync(envelope.Payload).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.TermClosed:
                    await SendToBrowserAsync(new { type = "closed" }).ConfigureAwait(false);
                    await CloseAsync(TerminalCloseReasons.AgentExit, sendTermClose: false).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.TermError:
                    var message = envelope.Payload.ValueKind == JsonValueKind.Object &&
                                  envelope.Payload.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : null;
                    await SendToBrowserAsync(new { type = "error", message = message ?? "终端会话异常" }).ConfigureAwait(false);
                    await CloseAsync(TerminalCloseReasons.Error, sendTermClose: false).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "终端会话 {SessionId} 处理下行 {Type} 失败", SessionId, envelope.Type);
        }
    }

    /// <summary>agent 通道断开（离线/被顶替/心跳超时）：通知浏览器并按 connection-lost 收尾。</summary>
    public async Task OnAgentDisconnectedAsync()
    {
        try
        {
            await SendToBrowserAsync(new { type = "closed" }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 浏览器可能已断开，忽略
        }

        await CloseAsync(TerminalCloseReasons.ConnectionLost, sendTermClose: false).ConfigureAwait(false);
    }

    private void OnConnectionClosed(long deviceId, IDeviceChannel channel)
    {
        if (deviceId == _deviceId && ReferenceEquals(channel, _agentChannel))
        {
            _ = OnAgentDisconnectedAsync();
        }
    }

    private async Task PumpBrowserAsync()
    {
        var buffer = new byte[BrowserFrameLimit];
        var received = 0;
        var draining = false;
        var drainedBytes = 0;
        while (_browser.State == WebSocketState.Open)
        {
            var result = await _browser.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), CancellationToken.None)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return; // operator 关闭，finally 兜底收尾
            }

            if (draining)
            {
                // 超长帧的剩余分段继续读完并整条丢弃，会话不受影响
                drainedBytes += result.Count;
                if (result.EndOfMessage)
                {
                    _logger.LogWarning("终端会话 {SessionId} 丢弃超长浏览器消息（{Bytes} 字节），会话继续", SessionId, received + drainedBytes);
                    draining = false;
                    drainedBytes = 0;
                    received = 0;
                }

                continue;
            }

            received += result.Count;
            if (!result.EndOfMessage)
            {
                if (received >= buffer.Length)
                {
                    draining = true; // 超出上限：本条消息作废
                }

                continue;
            }

            await HandleBrowserMessageAsync(Encoding.UTF8.GetString(buffer, 0, received)).ConfigureAwait(false);
            received = 0;
        }
    }

    private async Task HandleBrowserMessageAsync(string json)
    {
        JsonElement message;
        try
        {
            message = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            _logger.LogWarning("终端会话 {SessionId} 收到非 JSON 浏览器消息，已忽略", SessionId);
            return;
        }

        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out var type))
        {
            return;
        }

        switch (type.GetString())
        {
            case "input" when message.TryGetProperty("data", out var data) && data.GetString() is { } text:
                await SendToAgentAsync(AgentMessageTypes.TermInput, new
                {
                    sessionId = SessionId,
                    data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                }).ConfigureAwait(false);
                Record(TerminalEntryDirections.Input, text);
                break;
            case "resize" when message.TryGetProperty("cols", out var cols) && message.TryGetProperty("rows", out var rows):
                await SendToAgentAsync(AgentMessageTypes.TermResize, new
                {
                    sessionId = SessionId,
                    cols = cols.ValueKind == JsonValueKind.Number ? cols.GetInt32() : 80,
                    rows = rows.ValueKind == JsonValueKind.Number ? rows.GetInt32() : 24,
                }).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleOutputAsync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("data", out var data) ||
            data.GetString() is not { } base64)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            _logger.LogWarning("终端会话 {SessionId} 收到非法 base64 输出，已忽略", SessionId);
            return;
        }

        // 增量解码：分块边界上的多字节 UTF-8 残缺尾序列由解码器保留到下一帧。
        // 注意 GetCharCount/GetChars 必须成对调用（即使 count 为 0），否则暂存的残缺序列会被丢弃
        var charCount = _outputDecoder.GetCharCount(bytes, 0, bytes.Length, flush: false);
        var chars = new char[charCount];
        _ = _outputDecoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
        if (charCount == 0)
        {
            return; // 整帧都是残缺序列的前缀
        }

        var text = new string(chars);
        Record(TerminalEntryDirections.Output, text);
        await SendToBrowserAsync(new { type = "output", data = text }).ConfigureAwait(false);
    }

    private async Task CloseAsync(string reason, bool sendTermClose)
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
        {
            return;
        }

        try
        {
            RecordClose(reason);
            if (sendTermClose && _agentChannel.IsOpen)
            {
                try
                {
                    await SendToAgentAsync(AgentMessageTypes.TermClose, new { sessionId = SessionId }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // agent 侧关闭失败（通道已死）不阻断收尾
                    _logger.LogWarning(ex, "终端会话 {SessionId} 发送 term.close 失败", SessionId);
                }
            }

            // 关闭与在途浏览器发送经同一把锁串行化：托管 WS 同一时刻只允许一个 send/close 操作
            await _browserSendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    await _browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "会话结束", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _browserSendLock.Release();
            }
        }
        catch (Exception ex)
        {
            // 任何收尾异常（并发操作冲突/WS 异常）都不得影响登记表清理
            _logger.LogWarning(ex, "终端会话 {SessionId} 浏览器侧关闭失败（会话已按 {Reason} 收尾）", SessionId, reason);
        }
        finally
        {
            _sessions.TryRemove(this);
        }
    }

    private void RecordOpen()
    {
        try
        {
            _store.OpenSession(SessionId, _deviceId, _operatorName, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "终端会话 {SessionId} 元数据入库失败，留痕将不完整（会话不受影响）", SessionId);
        }
    }

    private void Record(string direction, string data)
    {
        try
        {
            _store.Append(SessionId, direction, data, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "终端会话 {SessionId} 的 {Direction} 留痕入库失败，本条丢弃（会话不受影响）", SessionId, direction);
        }
    }

    private void RecordClose(string reason)
    {
        try
        {
            _store.CloseSession(SessionId, _clock.GetUtcNow(), reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "终端会话 {SessionId} 关闭状态入库失败", SessionId);
        }
    }

    /// <summary>面板登录拦截中间件会把会话用户名放进 Items；拿不到时留空。</summary>
    private async Task SendToAgentAsync(string type, object payload)
    {
        var envelope = AgentEnvelope.Create(type, Interlocked.Increment(ref _seq), JsonSerializer.SerializeToElement(payload));
        await _agentChannel.SendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendToBrowserAsync(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _browserSendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_browser.State == WebSocketState.Open)
            {
                await _browser.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _browserSendLock.Release();
        }
    }
}
