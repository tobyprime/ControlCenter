using System.Collections.Concurrent;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Devices;
using Microsoft.Extensions.Options;

namespace DevicePanel.Web.Logs;

/// <summary>
/// 面板 → agent 的日志请求服务：把 REST 查询折算成 logs.* 下行信封，按 (通道, seq) 关联 agent 响应。
/// - 请求型消息 seq 关联：响应/错误沿用请求 seq（协议约定），通道绑定防跨设备/陈旧连接串扰；
/// - 每个请求独立等待，不落库、不改变目标机状态（只读按需拉取）；
/// - 设备离线立即失败；等待超时抛 AgentTimeoutException。
/// </summary>
public sealed class LogQueryService
{
    private readonly AgentConnectionRegistry _connections;
    private readonly LogsOptions _options;
    private readonly ILogger<LogQueryService> _logger;
    private readonly ConcurrentDictionary<(IDeviceChannel Channel, long Seq), TaskCompletionSource<AgentEnvelope>> _pending = new();
    private long _seq;

    public LogQueryService(AgentConnectionRegistry connections, LogsOptions options, ILogger<LogQueryService> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LogServiceInfo>> ListServicesAsync(long deviceId, CancellationToken cancellationToken)
    {
        var payload = await RequestAsync(deviceId, AgentMessageTypes.LogsServicesRequest, new { }, cancellationToken)
            .ConfigureAwait(false);
        return ParseListAsync<LogServiceInfo>(payload, "services", ParseService);
    }

    public async Task<IReadOnlyList<LogLineInfo>> TailAsync(long deviceId, string service, string kind, int lines, CancellationToken cancellationToken)
    {
        var payload = await RequestAsync(deviceId, AgentMessageTypes.LogsTailRequest, new
        {
            service,
            kind,
            lines,
        }, cancellationToken).ConfigureAwait(false);
        return ParseListAsync<LogLineInfo>(payload, "lines", ParseLine);
    }

    /// <summary>logs.* 响应处理器入口：按 (通道, seq) 完成挂起的请求；无匹配（陈旧响应）则忽略。</summary>
    public void Complete(IDeviceChannel channel, AgentEnvelope envelope)
    {
        if (!_pending.TryRemove((channel, envelope.Seq), out var pending))
        {
            _logger.LogDebug("忽略无法关联的日志响应：type={Type}, seq={Seq}（陈旧连接或超时后迟到）", envelope.Type, envelope.Seq);
            return;
        }

        if (envelope.Type == AgentMessageTypes.LogsError)
        {
            var message = envelope.Payload.ValueKind == JsonValueKind.Object &&
                          envelope.Payload.TryGetProperty("message", out var m)
                ? m.GetString()
                : null;
            pending.TrySetException(new AgentLogException(message ?? "设备无法执行日志请求"));
            return;
        }

        pending.TrySetResult(envelope);
    }

    private async Task<JsonElement> RequestAsync(long deviceId, string type, object payload, CancellationToken cancellationToken)
    {
        var channel = _connections.GetChannel(deviceId);
        if (channel is null || !channel.IsOpen)
        {
            throw new DeviceOfflineException("设备离线，无法获取日志");
        }

        var seq = Interlocked.Increment(ref _seq);
        var pending = new TaskCompletionSource<AgentEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(channel, seq)] = pending;
        try
        {
            await channel.SendAsync(AgentEnvelope.Create(type, seq, JsonSerializer.SerializeToElement(payload)), cancellationToken)
                .ConfigureAwait(false);

            var completed = await Task.WhenAny(pending.Task,
                    Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)), cancellationToken))
                .ConfigureAwait(false);
            if (completed != pending.Task)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new AgentTimeoutException($"设备响应日志请求超时（{_options.RequestTimeoutSeconds}s）");
            }

            var envelope = await pending.Task.ConfigureAwait(false);
            return envelope.Payload;
        }
        finally
        {
            _pending.TryRemove((channel, seq), out _);
        }
    }

    private IReadOnlyList<T> ParseListAsync<T>(JsonElement payload, string field, Func<JsonElement, T> parse)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(field, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"设备日志响应格式无效（缺少 {field}）");
        }

        var items = new List<T>();
        foreach (var entry in array.EnumerateArray())
        {
            items.Add(parse(entry));
        }

        return items;
    }

    private static LogServiceInfo ParseService(JsonElement entry) => new(
        entry.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
        entry.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty,
        entry.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty);

    private static LogLineInfo ParseLine(JsonElement entry) => new(
        entry.TryGetProperty("ts", out var ts) ? ts.GetString() ?? string.Empty : string.Empty,
        entry.TryGetProperty("level", out var level) ? level.GetString() ?? string.Empty : string.Empty,
        entry.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty);}
