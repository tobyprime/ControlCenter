using System.Collections.Concurrent;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;
using DevicePanel.Web.Logs;
using Microsoft.Extensions.Options;

namespace DevicePanel.Web.Metrics;

/// <summary>按需查询返回的最新值条目：valueNum 与 valueText 二选一（与指标存储模型一致）。</summary>
public sealed record MetricLatestEntry(string Key, DateTimeOffset TimeUtc, double? ValueNum, string? ValueText);

/// <summary>
/// 面板 → agent 的指标按需查询服务（三期模块3）：把 REST 查询折算成 metrics.latest.request 下行信封，
/// 按 (通道, seq) 关联 agent 响应——与 LogQueryService 同一模式。
/// - push 采集器在线 → 即时采样回包（只读不落库，历史曲线仍走面板聚合）；
/// - pull 采集器 → 直读面板侧最新样本（探测即最新），未探测/探测失败视为离线；
/// - 离线立即失败；等待超时抛 AgentTimeoutException；agent 采样失败经 metrics.error 抛 AgentLogException。
/// </summary>
public sealed class MetricsQueryService
{
    private readonly AgentConnectionRegistry _connections;
    private readonly IMetricsStore _store;
    private readonly MetricsOptions _options;
    private readonly ILogger<MetricsQueryService> _logger;
    private readonly ConcurrentDictionary<(IDeviceChannel Channel, long Seq), TaskCompletionSource<AgentEnvelope>> _pending = new();
    private long _seq;

    public MetricsQueryService(AgentConnectionRegistry connections, IMetricsStore store, MetricsOptions options, ILogger<MetricsQueryService> logger)
    {
        _connections = connections;
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>查询采集器最新值：push 走 agent 即时采样，pull 直读面板侧最新样本（探测即最新）。</summary>
    public async Task<IReadOnlyList<MetricLatestEntry>> LatestAsync(CollectorInfo collector, CancellationToken cancellationToken)
    {
        if (collector.AgentId is null)
        {
            // pull 采集器：无 agent 连接可下行，最新值 = 上次探测入库的样本；未探测/探测失败（status ≠ true）视为离线
            if (_store.GetLatest(collector.Id, MetricKeys.Status) is not { ValueText: "true" })
            {
                throw new DeviceOfflineException("采集器离线，无法按需查询最新值");
            }

            return _store.ListReportedKeys(collector.Id)
                .Select(key => (Key: key, Sample: _store.GetLatest(collector.Id, key)))
                .Where(entry => entry.Sample is not null)
                .Select(entry => new MetricLatestEntry(entry.Key, entry.Sample!.TimeUtc, entry.Sample.ValueNum, entry.Sample.ValueText))
                .ToList();
        }

        var payload = await RequestAsync(collector.Id, cancellationToken).ConfigureAwait(false);
        if (!MetricsPayloadReader.TryParse(payload, DateTimeOffset.UtcNow, out var samples))
        {
            throw new InvalidOperationException("采集器指标响应格式无效");
        }

        return samples.Select(s => new MetricLatestEntry(s.Key, s.Sample.TimeUtc, s.Sample.ValueNum, s.Sample.ValueText)).ToList();
    }

    /// <summary>metrics.latest.* 响应处理器入口：按 (通道, seq) 完成挂起的请求；无匹配（陈旧响应）则忽略。</summary>
    public void Complete(IDeviceChannel channel, AgentEnvelope envelope)
    {
        if (!_pending.TryRemove((channel, envelope.Seq), out var pending))
        {
            _logger.LogDebug("忽略无法关联的指标响应：type={Type}, seq={Seq}（陈旧连接或超时后迟到）", envelope.Type, envelope.Seq);
            return;
        }

        if (envelope.Type == AgentMessageTypes.MetricsError)
        {
            var message = envelope.Payload.ValueKind == JsonValueKind.Object &&
                          envelope.Payload.TryGetProperty("message", out var m)
                ? m.GetString()
                : null;
            pending.TrySetException(new AgentLogException(message ?? "采集器无法执行指标查询"));
            return;
        }

        pending.TrySetResult(envelope);
    }

    private async Task<JsonElement> RequestAsync(long collectorId, CancellationToken cancellationToken)
    {
        var channel = _connections.GetChannel(collectorId);
        if (channel is null || !channel.IsOpen)
        {
            throw new DeviceOfflineException("采集器离线，无法按需查询最新值");
        }

        var seq = Interlocked.Increment(ref _seq);
        var pending = new TaskCompletionSource<AgentEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(channel, seq)] = pending;
        try
        {
            await channel.SendAsync(AgentEnvelope.Create(AgentMessageTypes.MetricsLatestRequest, seq, JsonSerializer.SerializeToElement(new { })), cancellationToken)
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

                throw new AgentTimeoutException($"采集器响应指标查询超时（{_options.RequestTimeoutSeconds}s）");
            }

            var envelope = await pending.Task.ConfigureAwait(false);
            return envelope.Payload;
        }
        finally
        {
            _pending.TryRemove((channel, seq), out _);
        }
    }
}
