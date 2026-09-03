using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Devices;

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
    private const int BrowserFrameLimit = 64 * 1024;

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

    /// <summary>主循环：浏览器 → agent 泵；浏览器断开即结束会话（operator 关闭）。</summary>
    public async Task RunAsync()
    {
        _connections.ConnectionClosed += OnConnectionClosed;
        try
        {
            RecordOpen();
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
        while (_browser.State == WebSocketState.Open)
        {
            var result = await _browser.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), CancellationToken.None)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return; // operator 关闭，finally 兜底收尾
            }

            received += result.Count;
            if (!result.EndOfMessage)
            {
                if (received >= buffer.Length)
                {
                    throw new InvalidOperationException("浏览器消息超长");
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
            !message.TryGetProperty("type", out var type) ||
            type.GetString() != "input" ||
            !message.TryGetProperty("data", out var data) ||
            data.GetString() is not { } text)
        {
            return;
        }

        await SendToAgentAsync(AgentMessageTypes.TermInput, new
        {
            sessionId = SessionId,
            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        }).ConfigureAwait(false);
        Record(TerminalEntryDirections.Input, text);
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

        var text = Encoding.UTF8.GetString(bytes);
        Record(TerminalEntryDirections.Output, text);
        await SendToBrowserAsync(new { type = "output", data = text }).ConfigureAwait(false);
    }

    private async Task CloseAsync(string reason, bool sendTermClose)
    {
        if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
        {
            return;
        }

        RecordClose(reason);
        if (sendTermClose && _agentChannel.IsOpen)
        {
            await SendToAgentAsync(AgentMessageTypes.TermClose, new { sessionId = SessionId }).ConfigureAwait(false);
        }

        try
        {
            if (_browser.State == WebSocketState.Open)
            {
                await _browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "会话结束", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // 浏览器侧已断开，忽略
        }

        _sessions.TryRemove(this);
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
