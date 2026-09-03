using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevicePanel.Protocol;

/// <summary>
/// agent↔面板消息类型常量。扩展约定：新增能力只加消息类型（建议用前缀命名空间，如 metrics.*/term.*/logs.*），不改信封结构。
/// </summary>
public static class AgentMessageTypes
{
    public const string Auth = "auth";
    public const string AuthOk = "auth.ok";
    public const string AuthError = "auth.error";
    public const string Heartbeat = "heartbeat";

    /// <summary>指标上报：agent 周期采集的 CPU/内存/磁盘/网络快照（指标 issue 使用）。</summary>
    public const string MetricsReport = "metrics.report";

    /// <summary>指标上报前缀（预留，指标 issue 使用，如 metrics.report）。</summary>
    public const string MetricsPrefix = "metrics.";

    /// <summary>终端打开请求：面板 → agent，payload {sessionId, cols, rows}；agent 就绪后回 term.opened，失败回 term.error。</summary>
    public const string TermOpen = "term.open";

    /// <summary>终端打开确认：agent → 面板，payload {sessionId}。</summary>
    public const string TermOpened = "term.opened";

    /// <summary>终端输入：面板 → agent，payload {sessionId, data}（base64 UTF-8 字节，保分块边界安全）。</summary>
    public const string TermInput = "term.input";

    /// <summary>终端输出：agent → 面板，payload {sessionId, data}（base64 UTF-8 字节）。</summary>
    public const string TermOutput = "term.output";

    /// <summary>终端关闭请求：面板 → agent，payload {sessionId}。</summary>
    public const string TermClose = "term.close";

    /// <summary>终端会话结束：agent → 面板（shell 退出或关闭完成），payload {sessionId}。</summary>
    public const string TermClosed = "term.closed";

    /// <summary>终端错误：agent → 面板（如打开 PTY 失败），payload {sessionId, message}。</summary>
    public const string TermError = "term.error";

    /// <summary>终端通道前缀（预留，Web 终端 issue 使用，如 term.open/term.input/term.output/term.close）。</summary>
    public const string TermPrefix = "term.";

    /// <summary>日志拉取前缀（预留，日志 issue 使用，如 logs.request/logs.response）。</summary>
    public const string LogsPrefix = "logs.";
}

/// <summary>WebSocket 关闭码：4000-4999 为应用自定义区段，agent 依据关闭码决定是否/如何重连。</summary>
public enum WebSocketCloseCodes
{
    AuthFailed = 4001,
    DeviceDeleted = 4002,
    TokenReset = 4003,
    HeartbeatTimeout = 4004,
    DuplicateSession = 4005,
}

/// <summary>
/// agent↔面板统一消息信封：{type, seq, payload}。
/// payload 为不透明 JSON，对任意消息类型透明透传；新增能力只加 type 不改信封。
/// </summary>
[JsonConverter(typeof(AgentEnvelopeConverter))]
public sealed class AgentEnvelope
{
    public string Type { get; init; } = string.Empty;

    /// <summary>发送方内的递增序号，用于响应关联（服务端回包沿用请求的 seq）。</summary>
    public long Seq { get; set; }

    /// <summary>不透明负载；未携带时为 Null。</summary>
    public JsonElement Payload { get; set; }

    public static AgentEnvelope Create(string type, long seq, JsonElement? payload = null) => new()
    {
        Type = type,
        Seq = seq,
        Payload = payload ?? default,
    };
}

/// <summary>信封编解码：payload 按原始 JSON 透传（结构未知也不丢失），AOT/反射均可用。</summary>
public sealed class AgentEnvelopeConverter : JsonConverter<AgentEnvelope>
{
    public override AgentEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? type = null;
        long seq = 0;
        JsonElement payload = default;

        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    type = property.Value.GetString();
                    break;
                case "seq":
                    seq = property.Value.GetInt64();
                    break;
                case "payload":
                    payload = property.Value.Clone();
                    break;
            }
        }

        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            using var nullDoc = JsonDocument.Parse("null");
            payload = nullDoc.RootElement.Clone();
        }

        return new AgentEnvelope { Type = type ?? string.Empty, Seq = seq, Payload = payload };
    }

    public override void Write(Utf8JsonWriter writer, AgentEnvelope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteNumber("seq", value.Seq);
        writer.WritePropertyName("payload");
        if (value.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            writer.WriteNullValue();
        }
        else
        {
            value.Payload.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

/// <summary>协议层 JSON 上下文（源生成，兼容 NativeAOT）。</summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(AgentEnvelope))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
