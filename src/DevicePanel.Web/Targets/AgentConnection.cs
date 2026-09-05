using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;

namespace DevicePanel.Web.Targets;

/// <summary>面板侧单条 agent WebSocket 连接：封装信封收发与主动关闭，实现 IDeviceChannel。</summary>
public sealed class AgentConnection : IDeviceChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public AgentConnection(long deviceId, WebSocket socket)
    {
        DeviceId = deviceId;
        _socket = socket;
    }

    /// <summary>认证握手完成前为 0，认证成功后由会话回填（关联 agent = target id，未关联 = 负 agent id）。</summary>
    public long DeviceId { get; internal set; }

    /// <summary>认证通过的 agent id，握手完成前为 0，认证成功后由会话回填。</summary>
    public long AgentId { get; internal set; }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public async Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen)
            {
                return;
            }

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            if (IsOpen)
            {
                await _socket.CloseAsync((WebSocketCloseStatus)closeStatus, reason, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // 连接可能已被对端断开，忽略关闭异常
        }
    }

    /// <summary>读取一条入站信封；连接关闭或消息损坏时返回 null。</summary>
    public async Task<AgentEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var received = 0;
        while (true)
        {
            if (received >= buffer.Length)
            {
                return null; // 超长消息视为协议错误
            }

            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseFromPeerAsync(result.CloseStatus, result.CloseStatusDescription).ConfigureAwait(false);
                return null;
            }

            received += result.Count;
            if (result.EndOfMessage)
            {
                try
                {
                    return JsonSerializer.Deserialize(buffer.AsSpan(0, received).ToArray(), ProtocolJsonContext.Default.AgentEnvelope);
                }
                catch (JsonException)
                {
                    return null; // 非 JSON 帧按协议错误处理，由调用方断开
                }
            }
        }
    }

    private async Task CloseFromPeerAsync(WebSocketCloseStatus? closeStatus, string? description)
    {
        try
        {
            if (_socket.State == WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(closeStatus ?? WebSocketCloseStatus.NormalClosure, description, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
    }
}
