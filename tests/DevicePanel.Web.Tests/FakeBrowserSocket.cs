using System.Net.WebSockets;
using System.Text.Json;

namespace DevicePanel.Web.Tests;

/// <summary>测试用浏览器侧 WebSocket 替身：可配置 Close 行为与 Receive 返回，用于驱动中继收尾路径。</summary>
public sealed class FakeBrowserSocket : WebSocket
{
    private TaskCompletionSource<WebSocketReceiveResult> _pendingReceive =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>CloseAsync 被调用时抛出的异常（模拟与在途发送并发触发的失败）；null 表示正常关闭。</summary>
    public Exception? CloseException { get; set; }

    /// <summary>置为 true 时，ReceiveAsync 立即返回浏览器主动关闭帧（模拟 abrupt/正常断开）。</summary>
    public bool ReceiveReturnsClose { get; set; }

    public List<ReadOnlyMemory<byte>> Sent { get; } = new();

    private WebSocketCloseStatus? _closeStatus;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription { get; } = null;
    private WebSocketState _state = WebSocketState.Open;
    public override WebSocketState State => _state;
    public override string? SubProtocol { get; } = null;

    public override void Abort() => CompletePendingReceiveOnClose();

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (CloseException is not null)
        {
            throw CloseException;
        }

        // 与真实 WS 一致：服务端关闭后挂起的 Receive 以 Close 帧完成，泵得以退出
        CompletePendingReceiveOnClose();
        await Task.CompletedTask;
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        if (CloseException is not null)
        {
            throw CloseException;
        }

        CompletePendingReceiveOnClose();
        await Task.CompletedTask;
    }

    public override void Dispose() => CompletePendingReceiveOnClose();

    private void CompletePendingReceiveOnClose()
    {
        _state = WebSocketState.Closed;
        _closeStatus ??= WebSocketCloseStatus.NormalClosure;
        _pendingReceive.TrySetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (ReceiveReturnsClose)
        {
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true));
        }

        return _pendingReceive.Task;
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Sent.Add(buffer.Array!.AsMemory(buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }
}

/// <summary>测试用 agent 通道替身：记录发往 agent 的信封，可控制在线状态。</summary>
public sealed class FakeAgentChannel : DevicePanel.Web.Targets.IDeviceChannel
{
    public FakeAgentChannel(long deviceId = 1, bool isOpen = true)
    {
        DeviceId = deviceId;
        IsOpen = isOpen;
    }

    public long DeviceId { get; }

    public long AgentId { get; }
    public bool IsOpen { get; set; }

    public List<(string Type, long Seq, JsonElement Payload)> Sent { get; } = new();

    public Task SendAsync(DevicePanel.Protocol.AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        Sent.Add((envelope.Type, envelope.Seq, envelope.Payload.Clone()));
        return Task.CompletedTask;
    }

    public Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
