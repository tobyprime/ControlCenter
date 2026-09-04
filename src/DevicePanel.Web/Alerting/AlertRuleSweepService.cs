using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 时间驱动规则（无数据）的后台扫描宿主：按 ScanInterval 周期调用引擎 Sweep。
/// 取代一期 OfflineAlertScanner 的扫描节奏；具体规则语义全部在 IAlertRuleType 实现中（约束 B）。
/// </summary>
public sealed class AlertRuleSweepService : BackgroundService
{
    private readonly AlertRuleEngine _engine;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertRuleSweepService> _logger;

    public AlertRuleSweepService(AlertRuleEngine engine, AlertOptions options, TimeProvider clock, ILogger<AlertRuleSweepService> logger)
    {
        _engine = engine;
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
                _engine.Sweep(_clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "无数据规则扫描异常，继续下一轮");
            }
        }
    }
}
