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
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(AuthOkPayload))]
[JsonSerializable(typeof(TermOpenPayload))]
[JsonSerializable(typeof(TermInputPayload))]
[JsonSerializable(typeof(TermResizePayload))]
[JsonSerializable(typeof(TermOpenedPayload))]
[JsonSerializable(typeof(TermOutputPayload))]
[JsonSerializable(typeof(TermClosedPayload))]
[JsonSerializable(typeof(TermErrorPayload))]
[JsonSerializable(typeof(LogsTailRequestPayload))]
[JsonSerializable(typeof(LogsServicesPayload))]
[JsonSerializable(typeof(LogsTailPayload))]
[JsonSerializable(typeof(LogsErrorPayload))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

internal sealed record AuthPayload(string Token);

internal sealed record HeartbeatPayload(long UptimeSec);

/// <summary>
/// 指标上报负载：百分比 0-100（cpu/mem/disk），速率字节/秒（netRx/netTx）；
/// extra 携带扩展指标（snake_case key，与服务端注册 metric key 一致，约束 A：注册后经同一管道入库）。
/// </summary>
internal sealed record MetricsPayload(double Cpu, double Mem, double Disk, double NetRx, double NetTx,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, JsonElement>? Extra = null)
{
    /// <summary>采集快照 → 上报负载：内存实际数值与磁盘读写恒上报，温度仅在传感器存在时携带。</summary>
    public static MetricsPayload From(MetricsSample sample)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["mem_used"] = Number(sample.MemUsedBytes),
            ["mem_total"] = Number(sample.MemTotalBytes),
            ["disk_rx"] = Number(sample.DiskReadBytesPerSec),
            ["disk_tx"] = Number(sample.DiskWriteBytesPerSec),
        };
        if (sample.TempCelsius is { } temp)
        {
            extra["temp"] = Number(temp);
        }

        if (sample.TempSensor is { } sensor)
        {
            extra["temp_sensor"] = String(sensor);
        }

        return new MetricsPayload(sample.CpuPercent, sample.MemPercent, sample.DiskPercent,
            sample.NetRxBytesPerSec, sample.NetTxBytesPerSec, extra);
    }

    private static JsonElement Number(double value) =>
        JsonSerializer.SerializeToElement(value, AgentJsonContext.Default.Double);

    private static JsonElement String(string value) =>
        JsonSerializer.SerializeToElement(value, AgentJsonContext.Default.String);
}

internal sealed record AuthOkPayload(long DeviceId, string Name);

/// <summary>
/// 轻量 agent：出站 WSS 回连面板 → auth 信封认证 → 每 HeartbeatIntervalSeconds 发送一次心跳与指标快照。
/// 断线按指数退避重连；token 类拒绝（认证失败/设备删除/token 重置）不重试，需更换 token 后重启。
/// 扩展点：终端（term.*）/日志（logs.*）等下行通道经 ITerminalChannel/ILogsChannel 接入消息循环，不改信封与连接层。
/// </summary>
public sealed class AgentRunner
{
    private readonly AgentOptions _options;
    private readonly TextWriter _output;
    private readonly IMetricsCollector _metricsCollector;
    private readonly Func<IAgentDownlink, ITerminalChannel>? _terminalChannelFactory;
    private readonly Func<IAgentDownlink, ILogsChannel>? _logsChannelFactory;

    public AgentRunner(AgentOptions options, TextWriter output)
        : this(options, output, new LinuxMetricsCollector(),
            downlink => new TerminalChannel(downlink, new LinuxPtySessionFactory(), output),
            downlink => new LogsChannel((ILogsDownlink)downlink, new LinuxLogsSource(new ProcessCommandRunner()), output))
    {
    }

    internal AgentRunner(AgentOptions options, TextWriter output, IMetricsCollector metricsCollector,
        Func<IAgentDownlink, ITerminalChannel>? terminalChannelFactory = null,
        Func<IAgentDownlink, ILogsChannel>? logsChannelFactory = null)
    {
        _options = options;
        _output = output;
        _metricsCollector = metricsCollector;
        _terminalChannelFactory = terminalChannelFactory;
        _logsChannelFactory = logsChannelFactory;
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

        // 发送器随连接创建：auth 与后续节拍/下行共用（同一把发送锁）
        var downlink = new AgentDownlink(socket);
        await downlink.SendAsync(AgentMessageTypes.Auth, new AuthPayload(_options.Token), AgentJsonContext.Default.AuthPayload, cancellationToken)
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

        // 下行通道按连接创建：断开/重连时随连接销毁（终端会话等派生资源一并终止）；
        // 节拍发送也经 downlink 走同一把发送锁（ClientWebSocket 不允许并发发送）
        ITerminalChannel? channel = _terminalChannelFactory?.Invoke(downlink);
        ILogsChannel? logsChannel = _logsChannelFactory?.Invoke(downlink);

        // 能力声明（三期模块2）：认证成功后上报本连接实际可提供的通道，面板持久化并在管理页展示
        var capabilities = new List<string> { AgentCapabilityNames.Metrics };
        if (channel is not null)
        {
            capabilities.Add(AgentCapabilityNames.Terminal);
        }

        if (logsChannel is not null)
        {
            capabilities.Add(AgentCapabilityNames.Logs);
        }

        await downlink.SendAsync(AgentMessageTypes.AgentCapabilities,
            capabilities.ToArray(), AgentJsonContext.Default.StringArray, cancellationToken).ConfigureAwait(false);
        try
        {
            await MessageLoopAsync(socket, downlink, channel, logsChannel, startedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (channel is not null)
            {
                await channel.ShutdownAsync().ConfigureAwait(false);
            }
        }

        return ConnectResult.ConnectionLost;
    }

    /// <summary>
    /// 消息循环：下行接收与节拍等待都用跨迭代的持久任务（PeriodicTimer 不允许并发等待，
    /// 下行先到时未决的 tick 任务必须保留到下轮观察），心跳/指标按节拍发送（ClientWebSocket 允许一收一发并行）。
    /// 关键约束：不能取消挂起的 ReceiveAsync——取消会直接中止 ClientWebSocket 连接（State=Aborted），
    /// 心跳与指标将永远不会发出（回归锚：AgentRunnerLoopTests）。
    /// </summary>
    private async Task MessageLoopAsync(ClientWebSocket socket, AgentDownlink downlink, ITerminalChannel? channel,
        ILogsChannel? logsChannel, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds);
        using var heartbeatTimer = new PeriodicTimer(heartbeatInterval);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

                    await HandleInboundAsync(inbound, downlink, channel, logsChannel).ConfigureAwait(false);
                    continue; // 未决的 tick 任务保留，下轮继续观察
                }

                await tickTask.ConfigureAwait(false); // 节拍到期（观察并消费本次 tick）
                tickTask = null;
                await downlink.SendAsync(AgentMessageTypes.Heartbeat,
                    new HeartbeatPayload((long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
                    AgentJsonContext.Default.HeartbeatPayload, cancellationToken).ConfigureAwait(false);
                await SendMetricsReportAsync(downlink, cancellationToken).ConfigureAwait(false);
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
    private async Task SendMetricsReportAsync(AgentDownlink downlink, CancellationToken ct)
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

        await downlink.SendAsync(AgentMessageTypes.MetricsReport,
            MetricsPayload.From(sample), AgentJsonContext.Default.MetricsPayload, ct).ConfigureAwait(false);
    }

    private async Task HandleInboundAsync(AgentEnvelope envelope, AgentDownlink downlink, ITerminalChannel? channel, ILogsChannel? logsChannel)
    {
        // 扩展点：按前缀路由到对应通道；各通道内部兜异常——下行失败不打断心跳/指标节拍（回归锚：AgentRunnerLoopTests）
        try
        {
            if (envelope.Type == AgentMessageTypes.MetricsLatestRequest)
            {
                // 按需查询（三期模块3）：即时采样一次并按请求 seq 回包；失败回 metrics.error（采样快速，仍后台化保节拍）
                _ = HandleMetricsLatestAsync(envelope.Seq, downlink);
                return;
            }

            if (channel is not null &&
                envelope.Type.StartsWith(AgentMessageTypes.TermPrefix, StringComparison.Ordinal))
            {
                await channel.HandleAsync(envelope, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (logsChannel is not null &&
                envelope.Type.StartsWith(AgentMessageTypes.LogsPrefix, StringComparison.Ordinal))
            {
                await logsChannel.HandleAsync(envelope, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync($"下行消息处理异常（{envelope.Type}）：{ex.Message}").ConfigureAwait(false);
            return;
        }

        await _output.WriteLineAsync($"收到面板消息：{envelope.Type}（seq={envelope.Seq}）").ConfigureAwait(false);
    }

    /// <summary>metrics.latest.request 处理体：即时采样一次回 metrics.latest.response；采样失败回 metrics.error（连接断开时发送降级为 no-op）。</summary>
    private async Task HandleMetricsLatestAsync(long seq, AgentDownlink downlink)
    {
        try
        {
            await downlink.SendMetricsLatestResponseAsync(seq, MetricsPayload.From(_metricsCollector.Sample()), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await downlink.SendMetricsErrorAsync(seq, $"指标采样失败：{ex.Message}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception sendEx)
            {
                await _output.WriteLineAsync($"指标查询错误回包失败：{sendEx.Message}").ConfigureAwait(false);
            }
        }
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
