using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DevicePanel.Protocol;

namespace DevicePanel.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthPayload))]
[JsonSerializable(typeof(HeartbeatPayload))]
[JsonSerializable(typeof(MetricsPayload))]
[JsonSerializable(typeof(AuthOkPayload))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

internal sealed record AuthPayload(string Token);

internal sealed record HeartbeatPayload(long UptimeSec);

/// <summary>指标上报负载：百分比 0-100（cpu/mem/disk），网络速率字节/秒（netRx/netTx）。</summary>
internal sealed record MetricsPayload(double Cpu, double Mem, double Disk, double NetRx, double NetTx);

internal sealed record AuthOkPayload(long DeviceId, string Name);

/// <summary>
/// 轻量 agent：出站 WSS 回连面板 → auth 信封认证 → 每 HeartbeatIntervalSeconds 发送一次心跳与指标快照。
/// 断线按指数退避重连；token 类拒绝（认证失败/设备删除/token 重置）不重试，需更换 token 后重启。
/// 扩展点：后续终端/日志通道在消息循环中按信封 type 接入处理器即可，不改信封与连接层。
/// </summary>
public sealed class AgentRunner
{
    private readonly AgentOptions _options;
    private readonly TextWriter _output;
    private readonly IMetricsCollector _metricsCollector;

    public AgentRunner(AgentOptions options, TextWriter output)
        : this(options, output, new LinuxMetricsCollector())
    {
    }

    internal AgentRunner(AgentOptions options, TextWriter output, IMetricsCollector metricsCollector)
    {
        _options = options;
        _output = output;
        _metricsCollector = metricsCollector;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsValid(out var error))
        {
            await _output.WriteLineAsync(error).ConfigureAwait(false);
            return 2;
        }

        var backoff = TimeSpan.FromSeconds(1);
        var startedAt = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await ConnectOnceAsync(startedAt, cancellationToken).ConfigureAwait(false);
                if (result == ConnectResult.TokenRejected)
                {
                    await _output.WriteLineAsync("token 已被面板拒绝（无效/已重置/设备已删除），请更换 token 后重新启动。").ConfigureAwait(false);
                    return 3;
                }

                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await _output.WriteLineAsync($"连接断开：{ex.Message}，{backoff.TotalSeconds}s 后重连").ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (backoff < TimeSpan.FromSeconds(30))
            {
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }

        return 0;
    }

    private async Task<ConnectResult> ConnectOnceAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _output.WriteLineAsync($"正在连接 {_options.Url} …").ConfigureAwait(false);
        await socket.ConnectAsync(new Uri(_options.Url), cancellationToken).ConfigureAwait(false);

        var seq = 0L;
        await SendAsync(socket, AgentMessageTypes.Auth, ++seq, new AuthPayload(_options.Token), AgentJsonContext.Default.AuthPayload, cancellationToken)
            .ConfigureAwait(false);
        var reply = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (reply is null || reply.Type != AgentMessageTypes.AuthOk)
        {
            var message = reply is { Type: AgentMessageTypes.AuthError, Payload.ValueKind: JsonValueKind.Object } &&
                          reply.Payload.TryGetProperty("message", out var m)
                ? m.GetString()
                : null;
            await _output.WriteLineAsync($"认证失败：{message ?? "面板拒绝接入"}").ConfigureAwait(false);
            return ConnectResult.TokenRejected;
        }

        var authOk = JsonSerializer.Deserialize(reply.Payload.GetRawText(), AgentJsonContext.Default.AuthOkPayload);
        await _output.WriteLineAsync($"认证成功：设备 #{authOk?.DeviceId}（{authOk?.Name}），心跳与指标上报周期 {_options.HeartbeatIntervalSeconds}s")
            .ConfigureAwait(false);

        await MessageLoopAsync(socket, startedAt, cancellationToken).ConfigureAwait(false);
        return ConnectResult.ConnectionLost;
    }

    /// <summary>
    /// 消息循环：下行接收与节拍等待都用跨迭代的持久任务（PeriodicTimer 不允许并发等待，
    /// 下行先到时未决的 tick 任务必须保留到下轮观察），心跳/指标按节拍发送（ClientWebSocket 允许一收一发并行）。
    /// 关键约束：不能取消挂起的 ReceiveAsync——取消会直接中止 ClientWebSocket 连接（State=Aborted），
    /// 心跳与指标将永远不会发出（回归锚：AgentRunnerLoopTests）。
    /// </summary>
    private async Task MessageLoopAsync(ClientWebSocket socket, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds);
        using var heartbeatTimer = new PeriodicTimer(heartbeatInterval);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seq = 0L;
        Task<AgentEnvelope?>? inboundTask = null;
        Task<bool>? tickTask = null;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                inboundTask ??= ReceiveAsync(socket, sessionCts.Token);
                tickTask ??= heartbeatTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(inboundTask, tickTask).ConfigureAwait(false);
                if (completed == inboundTask)
                {
                    var inbound = await inboundTask.ConfigureAwait(false);
                    inboundTask = null;
                    if (inbound is null)
                    {
                        break; // 连接关闭或异常断开
                    }

                    await HandleInboundAsync(inbound).ConfigureAwait(false);
                    continue; // 未决的 tick 任务保留，下轮继续观察
                }

                await tickTask.ConfigureAwait(false); // 节拍到期（观察并消费本次 tick）
                tickTask = null;
                await SendAsync(socket, AgentMessageTypes.Heartbeat, ++seq,
                    new HeartbeatPayload((long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
                    AgentJsonContext.Default.HeartbeatPayload, cancellationToken).ConfigureAwait(false);
                await SendMetricsReportAsync(socket, ++seq, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // 收尾：取消仍挂起的接收并等待其结束（取消在连接关闭场景才发生，中止无需顾虑）；
            // 未决任务一律观察，避免未观察异常（tick 收尾异常/取消与连接已无关）
            sessionCts.Cancel();
            if (inboundTask is not null)
            {
                try
                {
                    await inboundTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 收尾排空：连接已无关紧要
                }
            }

            if (tickTask is not null)
            {
                try
                {
                    await tickTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 收尾排空：timer 取消/释放的异常无需上抛
                }
            }
        }
    }

    /// <summary>采集指标并按 metrics.report 上报；采集失败时跳过本周期，不影响心跳与连接。</summary>
    private async Task SendMetricsReportAsync(ClientWebSocket socket, long seq, CancellationToken ct)
    {
        MetricsSample sample;
        try
        {
            sample = _metricsCollector.Sample();
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync($"指标采集失败，本周期跳过：{ex.Message}").ConfigureAwait(false);
            return;
        }

        await SendAsync(socket, AgentMessageTypes.MetricsReport, seq,
            new MetricsPayload(sample.CpuPercent, sample.MemPercent, sample.DiskPercent,
                sample.NetRxBytesPerSec, sample.NetTxBytesPerSec),
            AgentJsonContext.Default.MetricsPayload, ct).ConfigureAwait(false);
    }

    private Task HandleInboundAsync(AgentEnvelope envelope)
    {
        // 扩展点：终端（term.*）、日志（logs.*）、指标下行（metrics.*）消息在此按 type 接入处理器
        return _output.WriteLineAsync($"收到面板消息：{envelope.Type}（seq={envelope.Seq}）");
    }

    private static async Task SendAsync<T>(
        ClientWebSocket socket,
        string type,
        long seq,
        T payload,
        JsonTypeInfo<T> payloadTypeInfo,
        CancellationToken ct)
    {
        var envelope = AgentEnvelope.Create(type, seq, JsonSerializer.SerializeToElement(payload, payloadTypeInfo));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task<AgentEnvelope?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var received = 0;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), ct)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                received += result.Count;
                if (result.EndOfMessage)
                {
                    return JsonSerializer.Deserialize(buffer.AsSpan(0, received).ToArray(), ProtocolJsonContext.Default.AgentEnvelope);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null; // 会话收尾时被取消
        }
        catch (WebSocketException)
        {
            return null; // 连接异常断开，由外层按断线处理
        }
    }
}

internal enum ConnectResult
{
    Closed,
    ConnectionLost,
    TokenRejected,
}
