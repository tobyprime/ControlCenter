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

    /// <summary>能力声明：agent 认证成功后主动上报，payload 为字符串数组（如 ["metrics","terminal"]）；未上报 = 未声明（旧版兼容）。</summary>
    public const string AgentCapabilities = "agent.capabilities";

    /// <summary>指标上报：agent 周期采集的 CPU/内存/磁盘/网络快照（指标 issue 使用）。</summary>
    public const string MetricsReport = "metrics.report";

    /// <summary>指标按需查询请求：面板 → agent，payload 为空对象；agent 即时采样一次后回 metrics.latest.response（seq 沿用请求）。</summary>
    public const string MetricsLatestRequest = "metrics.latest.request";

    /// <summary>指标按需查询响应：agent → 面板，负载结构与 metrics.report 一致；seq 沿用请求（三期模块3 按需查询）。</summary>
    public const string MetricsLatestResponse = "metrics.latest.response";

    /// <summary>指标按需查询错误：agent → 面板（采样失败等），payload {message}；seq 沿用请求。</summary>
    public const string MetricsError = "metrics.error";

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

    /// <summary>终端尺寸变更：面板 → agent，payload {sessionId, cols, rows}；agent 调整 PTY winsize。</summary>
    public const string TermResize = "term.resize";

    /// <summary>终端关闭请求：面板 → agent，payload {sessionId}。</summary>
    public const string TermClose = "term.close";

    /// <summary>终端会话结束：agent → 面板（shell 退出或关闭完成），payload {sessionId}。</summary>
    public const string TermClosed = "term.closed";

    /// <summary>终端错误：agent → 面板（如打开 PTY 失败），payload {sessionId, message}。</summary>
    public const string TermError = "term.error";

    /// <summary>终端通道前缀（预留，Web 终端 issue 使用，如 term.open/term.input/term.output/term.close）。</summary>
    public const string TermPrefix = "term.";

    /// <summary>日志服务清单请求：面板 → agent，payload 为空对象；agent 回 logs.services.response（seq 沿用请求）。</summary>
    public const string LogsServicesRequest = "logs.services.request";

    /// <summary>日志服务清单响应：agent → 面板，payload {services:[{name,kind,description}]}；seq 沿用请求。</summary>
    public const string LogsServicesResponse = "logs.services.response";

    /// <summary>日志尾部拉取请求：面板 → agent，payload {service, kind, lines}；agent 回 logs.tail.response（seq 沿用请求）。</summary>
    public const string LogsTailRequest = "logs.tail.request";

    /// <summary>日志尾部响应：agent → 面板，payload {lines:[{ts,level,message}]}；seq 沿用请求。</summary>
    public const string LogsTailResponse = "logs.tail.response";

    /// <summary>日志拉取错误：agent → 面板（请求无法执行，如服务不存在/命令失败），payload {message}；seq 沿用请求。</summary>
    public const string LogsError = "logs.error";

    /// <summary>日志拉取前缀（日志 issue 使用：logs.services.request/response、logs.tail.request/response、logs.error）。</summary>
    public const string LogsPrefix = "logs.";
}

/// <summary>agent.capabilities 内置能力名（三期模块2 起上报；具体能力类型由模块 3/4 扩充）。</summary>
public static class AgentCapabilityNames
{
    public const string Metrics = "metrics";
    public const string Terminal = "terminal";
    public const string Logs = "logs";
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
