using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Probing;

/// <summary>探针 HTTP 抓取抽象（测试替换点）：一次抓取 = 成败 + 耗时 + 原始响应体。</summary>
public interface IProbeHttpClient
{
    Task<ProbeFetchResult> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>HTTP GET 抓取结果：2xx 视为成功；网络异常/超时/非 2xx 均计失败（不计延迟样本）。</summary>
public sealed record ProbeFetchResult(bool Success, double? LatencyMs, string? BodyJson);

/// <summary>默认抓取实现：共享 HttpClient + 单请求超时，耗时用 Stopwatch 计量（毫秒，1 位小数）。</summary>
public sealed class HttpClientProbeClient : IProbeHttpClient
{
    private readonly HttpClient _http;
    private readonly ProbeOptions _options;

    public HttpClientProbeClient(HttpClient http, ProbeOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<ProbeFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            if (response.StatusCode < HttpStatusCode.OK || (int)response.StatusCode >= 300)
            {
                return new ProbeFetchResult(false, null, null);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new ProbeFetchResult(true, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeFetchResult(false, null, null);
        }
        catch (HttpRequestException)
        {
            return new ProbeFetchResult(false, null, null);
        }
    }
}

/// <summary>
/// 面板侧 HTTP/JSON 探针（模块2，不改 agent）：按目标配置的间隔轮询服务 URL。
/// 成功：写 status=true（每次）+ latency_ms + 按 mappings 提取的指标样本，并刷新目标最近探测时间；
/// 失败：连续 FailureThreshold 次仅在判定异常的转换点写一次 status=false（对齐 CollectorStatusScanner 的转换语义）。
/// 全部样本走指标管道入库并喂告警引擎（约束 A/B：可见性与通知均由 metric key 注册与告警规则实例决定）。
/// </summary>
public sealed class PullCollectorWorker : BackgroundService
{
    private readonly ICollectorRegistry _targets;
    private readonly IPullCollectorConfigStore _configs;
    private readonly IProbeHttpClient _client;
    private readonly IMetricKeyRegistry _metricKeys;
    private readonly IMetricsStore _metrics;
    private readonly IAlertRuleEngine _alerts;
    private readonly ProbeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PullCollectorWorker> _logger;
    private readonly Dictionary<long, ProbeRuntime> _runtimes = new();

    private sealed record ProbeRuntime(DateTimeOffset NextRunUtc, int ConsecutiveFailures);

    public PullCollectorWorker(
        ICollectorRegistry targets,
        IPullCollectorConfigStore configs,
        IProbeHttpClient client,
        IMetricKeyRegistry metricKeys,
        IMetricsStore metrics,
        IAlertRuleEngine alerts,
        ProbeOptions options,
        TimeProvider clock,
        ILogger<PullCollectorWorker> logger)
    {
        _targets = targets;
        _configs = configs;
        _client = client;
        _metricKeys = metricKeys;
        _metrics = metrics;
        _alerts = alerts;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "服务探针调度轮异常，继续下一轮");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>执行一轮到期探针（暴露为公开方法便于测试）；新建目标首轮即探测。</summary>
    public Task RunDueOnceAsync() => RunDueOnceAsync(CancellationToken.None);

    private async Task RunDueOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        var configs = _configs.List();

        // 目标已删除的运行时状态随轮清理（配置本身由外键级联删除）
        var liveIds = configs.Select(c => c.CollectorId).ToHashSet();
        foreach (var stale in _runtimes.Keys.Where(id => !liveIds.Contains(id)).ToList())
        {
            _runtimes.Remove(stale);
        }

        foreach (var config in configs)
        {
            var failures = 0;
            if (_runtimes.TryGetValue(config.CollectorId, out var runtime))
            {
                if (runtime.NextRunUtc > nowUtc)
                {
                    continue;
                }

                failures = runtime.ConsecutiveFailures;
            }

            try
            {
                failures = await ProbeAsync(config, failures, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 丢点保连：单目标探针故障不影响其他目标与调度循环
                _logger.LogWarning(ex, "目标 {TargetId} 探针执行异常，本次跳过", config.CollectorId);
            }

            var interval = TimeSpan.FromSeconds(Math.Clamp(config.IntervalSeconds, _options.MinIntervalSeconds, _options.MaxIntervalSeconds));
            _runtimes[config.CollectorId] = new ProbeRuntime(nowUtc + interval, failures);
        }
    }

    /// <summary>执行一次探针并返回新的连续失败计数。</summary>
    private async Task<int> ProbeAsync(PullCollectorConfig config, int consecutiveFailures, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        var result = await _client.FetchAsync(config.Url, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var failures = consecutiveFailures + 1;
            if (failures == _options.FailureThreshold)
            {
                // 判定异常的转换点：只写一次 status=false，后续持续失败不再刷样本
                WriteSample(config.CollectorId, MetricKeys.Status, new MetricSample(nowUtc, 0, "false"), nowUtc);
                _logger.LogWarning("目标 {TargetId} 探针连续 {Failures} 次失败，判定服务异常", config.CollectorId, failures);
            }

            return failures;
        }

        _targets.Touch(config.CollectorId, nowUtc);
        WriteSample(config.CollectorId, MetricKeys.Status, new MetricSample(nowUtc, 1, "true"), nowUtc);
        if (result.LatencyMs is { } latency)
        {
            WriteSample(config.CollectorId, MetricKeys.LatencyMs, new MetricSample(nowUtc, latency, null), nowUtc);
        }

        ExtractAndStore(config, result.BodyJson, nowUtc);
        return 0;
    }

    private void ExtractAndStore(PullCollectorConfig config, string? body, DateTimeOffset nowUtc)
    {
        if (body is null || config.Mappings.Count == 0)
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("目标 {TargetId} 探针响应非 JSON，跳过提取：{Message}", config.CollectorId, ex.Message);
            return;
        }

        using (document)
        {
            foreach (var mapping in config.Mappings)
            {
                if (_metricKeys.Get(mapping.MetricKey) is null)
                {
                    // 约束 A：只有注册过的 metric key 才进入管道
                    _logger.LogWarning("目标 {TargetId} 提取映射的指标 {Key} 未注册，跳过", config.CollectorId, mapping.MetricKey);
                    continue;
                }

                try
                {
                    if (JsonPath.Evaluate(document.RootElement, mapping.JsonPath) is not { } value)
                    {
                        _logger.LogWarning("目标 {TargetId} JSONPath 未命中（{Path}），指标 {Key} 本轮丢点", config.CollectorId, mapping.JsonPath, mapping.MetricKey);
                        continue;
                    }

                    if (ToSample(nowUtc, value, mapping) is { } sample)
                    {
                        WriteSample(config.CollectorId, mapping.MetricKey, sample, nowUtc);
                    }
                    else
                    {
                        _logger.LogWarning("目标 {TargetId} 提取值与指标 {Key} 声明类型 {Type} 不符，本轮丢点", config.CollectorId, mapping.MetricKey, mapping.ValueType);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 提取失败 = 该指标本轮丢点，不影响 status/latency 与其他映射
                    _logger.LogWarning(ex, "目标 {TargetId} 提取指标 {Key} 失败（{Path}）", config.CollectorId, mapping.MetricKey, mapping.JsonPath);
                }
            }
        }
    }

    private static MetricSample? ToSample(DateTimeOffset timeUtc, JsonElement value, PullMetricMapping mapping) => mapping.ValueType switch
    {
        MetricValueType.Number => value.ValueKind == JsonValueKind.Number ? new MetricSample(timeUtc, value.GetDouble(), null) : null,
        MetricValueType.Enum or MetricValueType.String => value.ValueKind switch
        {
            JsonValueKind.String => new MetricSample(timeUtc, null, value.GetString()),
            JsonValueKind.Number => new MetricSample(timeUtc, null, value.GetRawText()),
            JsonValueKind.True => new MetricSample(timeUtc, null, "true"),
            JsonValueKind.False => new MetricSample(timeUtc, null, "false"),
            _ => null,
        },
        _ => null,
    };

    private void WriteSample(long targetId, string metricKey, MetricSample sample, DateTimeOffset nowUtc)
    {
        // 入库成功即喂告警引擎；引擎评估异常只丢评估（与 MetricsMessageHandler 同一纪律）
        _metrics.Insert(targetId, metricKey, sample);
        _alerts.OnSample(targetId, metricKey, sample, nowUtc);
    }
}
