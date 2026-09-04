using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

public interface IThresholdAlertEvaluator
{
    /// <summary>
    /// 评估一次指标采样（指标入库后同步调用）。保证不抛异常：评估故障只丢评估，不影响入库与 WS 会话。
    /// </summary>
    void Evaluate(long deviceId, MetricsPoint point, DateTimeOffset nowUtc);
}

/// <summary>
/// 阈值越限规则：阈值 = 按设备覆盖 ?? 全局 ?? 内置默认；
/// 持续越限超过 SustainWindow（默认 60s）才告警，同一越限事件恢复前默认只发一次（RepeatMinutes 可调）；
/// 事件状态持久化，面板重启不重复告警。
/// </summary>
public sealed class ThresholdAlertEvaluator : IThresholdAlertEvaluator
{
    private sealed record ViolationState(DateTimeOffset FirstSeenUtc, DateTimeOffset? LastAlertedUtc);

    private readonly IDeviceRegistry _devices;
    private readonly IAlertThresholdStore _thresholds;
    private readonly IAlertStateStore _states;
    private readonly AlertDispatcher _dispatcher;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ThresholdAlertEvaluator> _logger;

    public ThresholdAlertEvaluator(
        IDeviceRegistry devices,
        IAlertThresholdStore thresholds,
        IAlertStateStore states,
        AlertDispatcher dispatcher,
        AlertOptions options,
        TimeProvider clock,
        ILogger<ThresholdAlertEvaluator> logger)
    {
        _devices = devices;
        _thresholds = thresholds;
        _states = states;
        _dispatcher = dispatcher;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public void Evaluate(long deviceId, MetricsPoint point, DateTimeOffset nowUtc)
    {
        foreach (var metric in AlertMetrics.Known)
        {
            var value = metric switch
            {
                AlertMetrics.Cpu => point.Cpu,
                AlertMetrics.Mem => point.Mem,
                AlertMetrics.Disk => point.Disk,
                _ => double.NaN,
            };

            try
            {
                EvaluateMetric(deviceId, metric, value, nowUtc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "设备 {DeviceId} 指标 {Metric} 越限评估失败，本点跳过", deviceId, metric);
            }
        }
    }

    private void EvaluateMetric(long deviceId, string metric, double value, DateTimeOffset nowUtc)
    {
        var ruleKey = $"threshold:{deviceId}:{metric}";
        var state = AlertStateStore.Read<ViolationState>(_states.Get(ruleKey));

        if (value <= _thresholds.GetEffective(deviceId, metric))
        {
            // 回落到阈值以下 = 事件恢复，关闭状态（下次越限是全新事件）
            if (state is not null)
            {
                _states.Delete(ruleKey);
            }

            return;
        }

        var device = _devices.Get(deviceId);
        if (device is null)
        {
            return;
        }

        state ??= new ViolationState(nowUtc, null);
        if (state.LastAlertedUtc is { } lastAlerted)
        {
            var repeatWindow = _options.RepeatWindow;
            if (repeatWindow <= TimeSpan.Zero || nowUtc - lastAlerted < repeatWindow)
            {
                return;
            }
        }
        else if (nowUtc - state.FirstSeenUtc < _options.SustainWindow)
        {
            // 尚未持续满一个窗口：继续等待
            _states.Set(ruleKey, Serialize(state), nowUtc);
            return;
        }

        var sustainedSeconds = Math.Round((nowUtc - state.FirstSeenUtc).TotalSeconds);
        var threshold = _thresholds.GetEffective(deviceId, metric);
        _dispatcher.Enqueue(
            new AlertMessage(
                "指标越限告警",
                $"设备「{device.Name}」{AlertMetrics.DisplayName(metric)}当前 {value:F1}%，超过阈值 {threshold:F1}%（已持续 {sustainedSeconds:F0} 秒）"),
            nowUtc);
        _states.Set(ruleKey, Serialize(state with { LastAlertedUtc = nowUtc }), nowUtc);
        _logger.LogInformation("设备 {DeviceId} 指标 {Metric} 持续越限，已入队告警", deviceId, metric);
    }

    private static string Serialize(ViolationState state) => System.Text.Json.JsonSerializer.Serialize(state);
}
