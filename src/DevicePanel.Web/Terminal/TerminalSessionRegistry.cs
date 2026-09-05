using System.Collections.Concurrent;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Terminal;

/// <summary>活跃终端会话登记表：sessionId → 中继。term.* 下行处理器据此找到浏览器侧会话投递。</summary>
public sealed class TerminalSessionRegistry
{
    private readonly ConcurrentDictionary<string, TerminalRelay> _relays = new();

    public bool TryAdd(TerminalRelay relay) => _relays.TryAdd(relay.SessionId, relay);

    public bool TryRemove(TerminalRelay relay) => _relays.TryRemove(relay.SessionId, out _);

    /// <summary>会话是否仍在登记表中（观察泄漏用）。</summary>
    public bool Contains(string sessionId) => _relays.ContainsKey(sessionId);

    /// <summary>
    /// 按 sessionId 投递下行信封到对应会话。
    /// 通道绑定校验：信封必须来自会话所属设备的通道，防止跨设备伪造输出/留痕注入。
    /// </summary>
    public void Dispatch(IDeviceChannel channel, AgentEnvelope envelope)
    {
        if (envelope.Payload.ValueKind == JsonValueKind.Object &&
            envelope.Payload.TryGetProperty("sessionId", out var sessionIdElement) &&
            sessionIdElement.GetString() is { } sessionId &&
            _relays.TryGetValue(sessionId, out var relay) &&
            relay.AcceptsFrom(channel))
        {
            _ = relay.OnAgentEnvelopeAsync(envelope);
        }
    }
}
