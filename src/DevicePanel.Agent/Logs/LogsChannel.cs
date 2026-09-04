using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DevicePanel.Protocol;

namespace DevicePanel.Agent;

/// <summary>logs.services.response 负载中的一个服务条目：kind 为 systemd / docker。</summary>
internal sealed record LogsServicePayload(string Name, string Kind, string Description);

/// <summary>logs.services.response 负载：目标机当前可查看日志的服务清单。</summary>
internal sealed record LogsServicesPayload(IReadOnlyList<LogsServicePayload> Services);

/// <summary>logs.tail.request 负载：service 为 systemd unit 名或 docker 容器名；lines 为尾部行数。</summary>
internal sealed record LogsTailRequestPayload(string Service, string Kind, int Lines);

/// <summary>logs.tail.response 负载中的一行：ts 为 ISO-8601 UTC（无法解析时为空串），level 为 error/warn/info/debug。</summary>
internal sealed record LogsLinePayload(string Ts, string Level, string Message);

/// <summary>logs.tail.response 负载：按请求顺序排列的尾部日志行。</summary>
internal sealed record LogsTailPayload(IReadOnlyList<LogsLinePayload> Lines);

/// <summary>logs.error 负载：请求无法执行的原因（服务不存在、命令失败、超时等）。</summary>
internal sealed record LogsErrorPayload(string Message);

/// <summary>
/// agent 侧日志下行发送原语：logs.* 响应按请求 seq 回包（请求-响应关联，与 term.* 的 sessionId 关联不同）。
/// 连接已断开时发送为 no-op（尽力而为，不打断会话清理）。
/// </summary>
internal interface ILogsDownlink
{
    Task SendServicesResponseAsync(long seq, IReadOnlyList<LogsServicePayload> services, CancellationToken cancellationToken);

    Task SendTailResponseAsync(long seq, IReadOnlyList<LogsLinePayload> lines, CancellationToken cancellationToken);

    Task SendLogsErrorAsync(long seq, string message, CancellationToken cancellationToken);
}

/// <summary>
/// agent 侧日志通道（扩展点）：处理面板下行的 logs.* 请求型消息。
/// 实现约定与终端通道一致：HandleAsync 不向消息循环抛异常；执行（journalctl/docker logs 等外部命令）
/// 一律后台化——慢请求不阻塞心跳/指标节拍（TOB-338 回归契约）。
/// </summary>
internal interface ILogsChannel
{
    Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// 日志通道：logs.services.request → 列出目标机可查看日志的服务（systemd units / docker 容器）；
/// logs.tail.request → 按需只读拉取指定服务尾部 N 行，结构化为 {ts, level, message} 回发。
/// 请求处理立即后台化，结果（或 logs.error）按请求 seq 回包；连接断开后发送自动降级为 no-op。
/// </summary>
internal sealed class LogsChannel : ILogsChannel
{
    /// <summary>单次尾部拉取的行数上限：与面板侧一致，防止超长输出拖垮低配目标机。</summary>
    public const int MaxLines = 1000;

    private readonly ILogsDownlink _downlink;
    private readonly ILogsSource _source;
    private readonly TextWriter _output;

    public LogsChannel(ILogsDownlink downlink, ILogsSource source, TextWriter? output = null)
    {
        _downlink = downlink;
        _source = source;
        _output = output ?? TextWriter.Null;
    }

    public Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            switch (envelope.Type)
            {
                case AgentMessageTypes.LogsServicesRequest:
                    _ = ExecuteAsync(envelope.Seq, ListServicesAsync);
                    break;
                case AgentMessageTypes.LogsTailRequest:
                    var request = TryDeserialize(envelope.Payload, AgentJsonContext.Default.LogsTailRequestPayload);
                    if (request is null)
                    {
                        _ = SendErrorAsync(envelope.Seq, "日志拉取请求负载无效");
                        break;
                    }

                    _ = ExecuteAsync(envelope.Seq, seq => TailAsync(seq, request));
                    break;
            }
        }
        catch (Exception ex)
        {
            // 消息循环契约：下行处理失败绝不打断心跳/指标节拍
            _ = WriteOutputAsync($"日志消息处理失败（{envelope.Type}）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>请求执行体：在后台线程运行外部命令/读日志源，任何失败都折算成 logs.error 回包。</summary>
    private async Task ExecuteAsync(long seq, Func<long, Task> action)
    {
        try
        {
            await action(seq).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _downlink.SendLogsErrorAsync(seq, $"日志拉取失败：{ex.Message}", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task ListServicesAsync(long seq)
    {
        var services = await Task.Run(() => _source.ListServices()).ConfigureAwait(false);
        await _downlink.SendServicesResponseAsync(seq,
            services.Select(s => new LogsServicePayload(s.Name, s.Kind, s.Description)).ToList(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TailAsync(long seq, LogsTailRequestPayload request)
    {
        if (string.IsNullOrWhiteSpace(request.Service))
        {
            await _downlink.SendLogsErrorAsync(seq, "日志拉取失败：服务名不能为空", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (!LogsSourceNames.IsValidName(request.Service))
        {
            await _downlink.SendLogsErrorAsync(seq, "日志拉取失败：服务名包含非法字符", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var lines = Math.Clamp(request.Lines, 1, MaxLines);
        var entries = await Task.Run(() => _source.ReadTail(request.Service, request.Kind, lines)).ConfigureAwait(false);
        await _downlink.SendTailResponseAsync(seq,
            entries.Select(l => new LogsLinePayload(l.Timestamp, l.Level, l.Message)).ToList(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(long seq, string message)
    {
        try
        {
            await _downlink.SendLogsErrorAsync(seq, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteOutputAsync($"日志错误回包失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task WriteOutputAsync(string message) => await _output.WriteLineAsync(message).ConfigureAwait(false);

    private static T? TryDeserialize<T>(JsonElement payload, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(payload.GetRawText(), typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
