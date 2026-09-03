using System.Collections.Concurrent;
using System.Text.Json;
using DevicePanel.Protocol;

namespace DevicePanel.Web.Terminal;

/// <summary>活跃终端会话登记表：sessionId → 中继。term.* 下行处理器据此找到浏览器侧会话投递。</summary>
public sealed class TerminalSessionRegistry
{
    private readonly ConcurrentDictionary<string, TerminalRelay> _relays = new();

    public bool TryAdd(TerminalRelay relay) => _relays.TryAdd(relay.SessionId, relay);

    public bool TryRemove(TerminalRelay relay) => _relays.TryRemove(relay.SessionId, out _);

    public void Dispatch(AgentEnvelope envelope)
    {
        if (envelope.Payload.ValueKind == JsonValueKind.Object &&
            envelope.Payload.TryGetProperty("sessionId", out var sessionIdElement) &&
            sessionIdElement.GetString() is { } sessionId &&
            _relays.TryGetValue(sessionId, out var relay))
        {
            _ = relay.OnAgentEnvelopeAsync(envelope);
        }
    }
}
