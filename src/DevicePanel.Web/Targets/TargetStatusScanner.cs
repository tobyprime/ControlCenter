using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Targets;

/// <summary>
/// 目标在线状态采样器：device 类目标超过 OfflineAfter（连续 2 个心跳周期）未心跳即写入 online=false 样本
/// （每次离线转换只写一次，不重复刷样本）；online=true 样本由心跳处理器写入。
/// 在线状态由此成为类型化指标序列，离线告警 = "状态不符 online != true" 规则实例（约束 B，不再硬编码）。
/// </summary>
public sealed class TargetStatusScanner : BackgroundService
{
    private readonly ITargetRegistry _targets;
    private readonly IMetricsStore _metrics;
    private readonly IAlertRuleEngine _alerts;
    private readonly AgentOptions _agentOptions;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TargetStatusScanner> _logger;

    public TargetStatusScanner(
        ITargetRegistry targets,
        IMetricsStore metrics,
        IAlertRuleEngine alerts,
        AgentOptions agentOptions,
        AlertOptions options,
        TimeProvider clock,
        ILogger<TargetStatusScanner> logger)
    {
        _targets = targets;
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
        foreach (var target in _targets.List())
        {
            if (target.Type != TargetTypes.Device || target.LastSeenAtUtc is null)
            {
                // 服务目标不走 agent 心跳（状态来源后续模块接入）；从未接入的目标没有"掉线"可言
                continue;
            }

            if (target.IsOnline(_clock, _agentOptions))
            {
                continue;
            }

            var latest = _metrics.GetLatest(target.Id, MetricKeys.Online);
            if (latest is { } sample && sample.TimeUtc > target.LastSeenAtUtc && sample.ValueText == "false")
            {
                // 已标记离线：等待心跳恢复（true 由心跳处理器写入）
                continue;
            }

            var offlineSample = new MetricSample(nowUtc, 0, "false");
            _metrics.Insert(target.Id, MetricKeys.Online, offlineSample);
            _alerts.OnSample(target.Id, MetricKeys.Online, offlineSample, nowUtc);
            _logger.LogInformation("目标 {TargetId} 判定离线，已写入 online=false 样本", target.Id);
        }
    }
}
