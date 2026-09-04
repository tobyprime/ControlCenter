using DevicePanel.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 指标过期清理任务：启动即清理一次，之后按 CleanupIntervalMinutes 周期执行。
/// CleanupOnceAsync 公开可调：测试与验证可直接触发（验收：过期数据被清理，支持手工触发或可等待验证）。
/// </summary>
public sealed class MetricsRetentionService : BackgroundService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IMetricValueStore _metricValues;
    private readonly MetricsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MetricsRetentionService> _logger;

    public MetricsRetentionService(
        SqliteConnectionFactory connectionFactory,
        IMetricValueStore metricValues,
        MetricsOptions options,
        TimeProvider clock,
        ILogger<MetricsRetentionService>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _metricValues = metricValues;
        _options = options;
        _clock = clock;
        _logger = logger ?? NullLogger<MetricsRetentionService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "指标过期清理任务执行失败，将在下个周期重试");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<MetricsCleanupResult> CleanupOnceAsync(CancellationToken cancellationToken)
    {
        var store = new MetricsStore(_connectionFactory);
        var cutoff = _clock.GetUtcNow().AddDays(-Math.Max(1, _options.RetentionDays));
        var result = store.DeleteOlderThan(cutoff);
        // 通用指标序列（metric_values，TOB-360）与一期明细同保留期
        var genericDeleted = _metricValues.DeleteOlderThan(cutoff);
        if (result is { DetailDeleted: > 0 } or { HourlyDeleted: > 0 } or { DailyDeleted: > 0 } || genericDeleted > 0)
        {
            _logger.LogInformation(
                "指标过期清理完成：明细 {Detail} 条、小时聚合 {Hourly} 条、天聚合 {Daily} 条、通用序列 {Generic} 条（保留 {Days} 天）",
                result.DetailDeleted, result.HourlyDeleted, result.DailyDeleted, genericDeleted, _options.RetentionDays);
        }

        return await Task.FromResult(result);
    }
}
