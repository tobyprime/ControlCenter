using System.Text.Json;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 规则评估引擎：指标入库后逐规则评估（采样驱动）+ 后台周期扫描（时间驱动）。
/// 防抖沿用一期"持续 N 时间窗口"语义并参数化到规则（sustain_seconds，0 = 判定即告警）；
/// 同一事件恢复前默认只发一次（repeat_minutes，0 = 恢复前不重发）。
/// 事件状态持久化（alert_state，键 rule:{id}），面板重启不重复告警。
/// 优先级：目标级规则（target_id 非 NULL）对同 (metric, rule_type) 的全局规则遮蔽——与一期"按设备覆盖 ?? 全局默认"一致。
/// </summary>
public interface IAlertRuleEngine
{
    /// <summary>评估一次新样本写入（指标入库成功后调用）。保证不抛异常：评估故障只丢评估，不影响入库与 WS 会话。</summary>
    void OnSample(long targetId, string metricKey, MetricSample sample, DateTimeOffset nowUtc);

    /// <summary>后台扫描一轮时间驱动规则（如无数据）。保证不抛异常。</summary>
    void Sweep(DateTimeOffset nowUtc);

    /// <summary>规则删除/参数变更后清理其事件状态（防抖与已告警记录随之失效，下次触发按全新事件计）。</summary>
    void ResetState(long ruleId);
}

public sealed class AlertRuleEngine : IAlertRuleEngine
{
    private sealed record ViolationState(DateTimeOffset FirstSeenUtc, DateTimeOffset? LastAlertedUtc);

    private readonly IAlertRuleStore _rules;
    private readonly IMetricKeyRegistry _metricKeys;
    private readonly IMetricsStore _metrics;
    private readonly ITargetRegistry _targets;
    private readonly IReadOnlyDictionary<string, IAlertRuleType> _ruleTypes;
    private readonly IAlertStateStore _states;
    private readonly AlertDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertRuleEngine> _logger;

    public AlertRuleEngine(
        IAlertRuleStore rules,
        IMetricKeyRegistry metricKeys,
        IMetricsStore metrics,
        ITargetRegistry targets,
        IEnumerable<IAlertRuleType> ruleTypes,
        IAlertStateStore states,
        AlertDispatcher dispatcher,
        TimeProvider clock,
        ILogger<AlertRuleEngine> logger)
    {
        _rules = rules;
        _metricKeys = metricKeys;
        _metrics = metrics;
        _targets = targets;
        _ruleTypes = ruleTypes.ToDictionary(t => t.TypeId, StringComparer.Ordinal);
        _states = states;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
    }

    public void OnSample(long targetId, string metricKey, MetricSample sample, DateTimeOffset nowUtc)
    {
        // 同类型下目标级规则遮蔽全局规则
        var effective = _rules.ListApplicable(targetId, metricKey)
            .GroupBy(r => r.RuleType, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(r => r.TargetId == targetId) ?? g.First());

        foreach (var rule in effective)
        {
            if (_ruleTypes.GetValueOrDefault(rule.RuleType) is not { } type)
            {
                continue;
            }

            try
            {
                if (!type.SampleDriven)
                {
                    // 时间驱动规则（无数据）：新数据到达 = 恢复
                    _states.Delete(StateKey(rule.Id));
                    continue;
                }

                if (!type.IsViolated(rule.ParametersJson, sample))
                {
                    // 未触发 = 事件恢复：关闭状态（下次触发是全新事件）
                    _states.Delete(StateKey(rule.Id));
                    continue;
                }

                FireWhenSustained(rule, type, targetId, sample, nowUtc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "目标 {TargetId} 指标 {MetricKey} 规则 {RuleId} 评估失败，本次跳过", targetId, metricKey, rule.Id);
            }
        }
    }

    public void Sweep(DateTimeOffset nowUtc)
    {
        foreach (var group in _rules.ListEnabledByType(NoDataRuleType.TypeIdValue).GroupBy(r => r.RuleType))
        foreach (var rule in group)
        {
            if (_ruleTypes.GetValueOrDefault(rule.RuleType) is not { } type || type.SampleDriven)
            {
                continue;
            }

            var targetIds = rule.TargetId is { } targetId ? [targetId] : _metrics.ListTargetsReporting(rule.MetricKey);
            foreach (var id in targetIds)
            {
                try
                {
                    SweepRule(rule, type, id, nowUtc);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "无数据规则 {RuleId} 扫描目标 {TargetId} 失败，本次跳过", rule.Id, id);
                }
            }
        }
    }

    private void SweepRule(AlertRule rule, IAlertRuleType type, long targetId, DateTimeOffset nowUtc)
    {
        var latest = _metrics.GetLatest(targetId, rule.MetricKey);
        var dataAge = latest is null ? (TimeSpan?)null : nowUtc - latest.TimeUtc;
        var stateKey = StateKey(rule.Id);
        if (!type.IsSweepViolated(rule.ParametersJson, dataAge))
        {
            _states.Delete(stateKey);
            return;
        }

        // 首见时间取"数据恰好缺失满窗口"的时刻：缺失满窗口 + 防抖窗口后告警，语义精确
        var state = AlertStateStore.Read<ViolationState>(_states.Get(stateKey))
                    ?? new ViolationState(latest!.TimeUtc + TimeSpan.FromMinutes(ReadMinutes(rule.ParametersJson)), null);
        FireWhenSustained(rule, type, targetId, latest, nowUtc, state);
    }

    private void FireWhenSustained(AlertRule rule, IAlertRuleType type, long targetId, MetricSample? sample, DateTimeOffset nowUtc, ViolationState? existing = null)
    {
        var stateKey = StateKey(rule.Id);
        var state = existing ?? AlertStateStore.Read<ViolationState>(_states.Get(stateKey)) ?? new ViolationState(nowUtc, null);

        if (state.LastAlertedUtc is { } lastAlerted)
        {
            var repeatWindow = TimeSpan.FromMinutes(rule.RepeatMinutes);
            if (repeatWindow <= TimeSpan.Zero || nowUtc - lastAlerted < repeatWindow)
            {
                return;
            }
        }
        else if (nowUtc - state.FirstSeenUtc < TimeSpan.FromSeconds(rule.SustainSeconds))
        {
            // 尚未持续满一个窗口：继续等待
            _states.Set(stateKey, Serialize(state), nowUtc);
            return;
        }

        // 告警主体是"上报数据的目标"（全局规则作用于多个目标，但事件总由具体目标的样本触发）
        var targetName = _targets.Get(targetId)?.Name ?? $"目标 {targetId}";
        var metric = _metricKeys.Get(rule.MetricKey);
        var sustained = nowUtc - state.FirstSeenUtc;
        var content = $"目标「{targetName}」{metric?.DisplayName ?? rule.MetricKey} {type.DescribeViolation(rule.ParametersJson, sample, metric?.Unit ?? string.Empty, sustained)}";
        _dispatcher.Enqueue(new AlertMessage(type.AlertTitle, content), nowUtc);
        _states.Set(stateKey, Serialize(state with { LastAlertedUtc = nowUtc }), nowUtc);
        _logger.LogInformation("规则 {RuleId}（{RuleType}）触发告警：目标 {TargetId} 指标 {MetricKey}", rule.Id, rule.RuleType, rule.TargetId, rule.MetricKey);
    }

    private static int ReadMinutes(string parametersJson)
    {
        try
        {
            if (JsonDocument.Parse(parametersJson).RootElement.TryGetProperty("minutes", out var element)
                && element.ValueKind == JsonValueKind.Number)
            {
                return element.GetInt32();
            }
        }
        catch (JsonException)
        {
        }

        return 0;
    }

    public void ResetState(long ruleId) => _states.Delete(StateKey(ruleId));

    private static string StateKey(long ruleId) => $"rule:{ruleId}";

    private static string Serialize(ViolationState state) => System.Text.Json.JsonSerializer.Serialize(state);
}
