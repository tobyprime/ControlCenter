using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Collectors;

/// <summary>
/// push 采集器在线状态采样器（三期模块3 泛化）：关联 agent 的采集器超过 OfflineAfter（连续 2 个心跳周期）未心跳
/// 即写入 online=false 样本（每次离线转换只写一次，不重复刷样本）；online=true 样本由心跳处理器写入。
/// pull 采集器不扫（状态由面板侧轮询产出 status 样本）。
/// 在线状态由此成为类型化指标序列，离线告警 = "状态不符 online != true" 规则实例（约束 B，不再硬编码）。
/// </summary>
public sealed class CollectorStatusScanner : BackgroundService
{
    private readonly ICollectorRegistry _collectors;
    private readonly IMetricsStore _metrics;
    private readonly IAlertRuleEngine _alerts;
    private readonly AgentOptions _agentOptions;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CollectorStatusScanner> _logger;

    public CollectorStatusScanner(
        ICollectorRegistry collectors,
        IMetricsStore metrics,
        IAlertRuleEngine alerts,
        AgentOptions agentOptions,
        AlertOptions options,
        TimeProvider clock,
        ILogger<CollectorStatusScanner> logger)
    {
        _collectors = collectors;
        _metrics = metrics;
        _alerts = alerts;
        _agentOptions = agentOptions;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ScanInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                ScanOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "目标在线状态扫描异常，继续下一轮");
            }
        }
    }

    /// <summary>执行一轮在线状态扫描（暴露为公开方法便于测试）。</summary>
    public void ScanOnce()
    {
        var nowUtc = _clock.GetUtcNow();
        foreach (var collector in _collectors.List())
        {
            // 只扫 push 采集器（关联 agent 走心跳）；pull 采集器状态由面板侧轮询产出（status 样本），没有"心跳掉线"可言
            if (collector.AgentId is null || collector.LastSeenAtUtc is null)
            {
                continue;
            }

            if (collector.IsOnline(_clock, _agentOptions))
            {
                continue;
            }

            var latest = _metrics.GetLatest(collector.Id, MetricKeys.Online);
            if (latest is { } sample && sample.TimeUtc > collector.LastSeenAtUtc && sample.ValueText == "false")
            {
                // 已标记离线：等待心跳恢复（true 由心跳处理器写入）
                continue;
            }

            var offlineSample = new MetricSample(nowUtc, 0, "false");
            _metrics.Insert(collector.Id, MetricKeys.Online, offlineSample);
            _alerts.OnSample(collector.Id, MetricKeys.Online, offlineSample, nowUtc);
            _logger.LogInformation("采集器 {CollectorId} 判定离线，已写入 online=false 样本", collector.Id);
        }
    }
}
