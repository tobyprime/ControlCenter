using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 规则引擎：指标点入库后按 (target, metric) 找启用规则，分发到对应类型的处理器并落结论。
/// 契约与一期一致：评估故障只丢评估，不影响入库与 WS 会话；未知规则类型跳过并记日志（向前兼容）。
/// </summary>
public interface IAlertRuleEngine
{
    /// <summary>legacy 五键管道（metric_samples）：cpu/mem/disk/net_rx/net_tx 逐键进入通用评估。</summary>
    void EvaluateDeviceSample(long deviceId, MetricsPoint point, DateTimeOffset nowUtc);

    /// <summary>通用管道：任意目标的任意注册指标点（探针/agent 新采集项）。</summary>
    void EvaluateSample(long targetId, string metric, MetricValue value, DateTimeOffset nowUtc);
}

public sealed class AlertRuleEngine : IAlertRuleEngine
{
    /// <summary>规则状态键：与一期 threshold:*/offline:* 键命名空间隔离，迁移时由 AlertRuleMigrator 搬运。</summary>
    public static string StateKey(AlertRule rule) => $"rule:{rule.Id}";

    private readonly ITargetStore _targets;
    private readonly IAlertRuleStore _rules;
    private readonly IMetricKeyRegistry _metricKeys;
    private readonly IAlertStateStore _states;
    private readonly IReadOnlyDictionary<string, IAlertRuleTypeHandler> _handlers;
    private readonly AlertRuleDecisionApplier _applier;
    private readonly ILogger<AlertRuleEngine> _logger;
    private readonly HashSet<string> _unknownTypesLogged = new(StringComparer.Ordinal);

    public AlertRuleEngine(
        ITargetStore targets,
        IAlertRuleStore rules,
        IMetricKeyRegistry metricKeys,
        IAlertStateStore states,
        IEnumerable<IAlertRuleTypeHandler> handlers,
        AlertRuleDecisionApplier applier,
        ILogger<AlertRuleEngine> logger)
    {
        _targets = targets;
        _rules = rules;
        _metricKeys = metricKeys;
        _states = states;
        _handlers = handlers.ToDictionary(h => h.RuleType, StringComparer.Ordinal);
        _applier = applier;
        _logger = logger;
    }

    public void EvaluateDeviceSample(long deviceId, MetricsPoint point, DateTimeOffset nowUtc)
    {
        var target = _targets.GetByDeviceId(deviceId);
        if (target is null)
        {
            _logger.LogDebug("设备 {DeviceId} 尚无对应目标，跳过规则评估", deviceId);
            return;
        }

        var value = new MetricValue(nowUtc, 0, null);
        EvaluateQuietly(target, "cpu", value with { NumValue = point.Cpu }, nowUtc);
        EvaluateQuietly(target, "mem", value with { NumValue = point.Mem }, nowUtc);
        EvaluateQuietly(target, "disk", value with { NumValue = point.Disk }, nowUtc);
        EvaluateQuietly(target, "net_rx", value with { NumValue = point.NetRx }, nowUtc);
        EvaluateQuietly(target, "net_tx", value with { NumValue = point.NetTx }, nowUtc);
    }

    public void EvaluateSample(long targetId, string metric, MetricValue value, DateTimeOffset nowUtc)
    {
        var target = _targets.Get(targetId);
        if (target is null)
        {
            return;
        }

        EvaluateQuietly(target, metric, value, nowUtc);
    }

    private void EvaluateQuietly(TargetInfo target, string metric, MetricValue value, DateTimeOffset nowUtc)
    {
        try
        {
            EvaluateRules(target, metric, value, nowUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "目标 {TargetId} 指标 {Metric} 规则评估失败，本点跳过", target.Id, metric);
        }
    }

    private void EvaluateRules(TargetInfo target, string metric, MetricValue value, DateTimeOffset nowUtc)
    {
        var ruleList = _rules.ListForTargetMetric(target.Id, metric);
        if (ruleList.Count == 0)
        {
            return;
        }

        var metricInfo = _metricKeys.Get(metric);
        foreach (var rule in ruleList)
        {
            var stateKey = StateKey(rule);
            try
            {
                if (!_handlers.TryGetValue(rule.RuleType, out var handler))
                {
                    if (_unknownTypesLogged.Add(rule.RuleType))
                    {
                        _logger.LogWarning("规则 {RuleId} 的类型 {RuleType} 未注册处理器，已跳过", rule.Id, rule.RuleType);
                    }

                    continue;
                }

                var context = new AlertRuleContext(
                    rule, target, metricInfo, nowUtc,
                    _states.Get(stateKey), SampleNum: value.NumValue, SampleText: value.TextValue);
                _applier.Apply(rule, stateKey, handler.Evaluate(context));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "规则 {RuleId}（{RuleType}）评估失败，本点跳过", rule.Id, rule.RuleType);
            }
        }
    }
}

/// <summary>评估结论的统一落地：Fire 入队分发并更新状态，Clear 清状态，None 按需持久化（防抖等待中）。</summary>
public sealed class AlertRuleDecisionApplier
{
    private readonly IAlertStateStore _states;
    private readonly AlertDispatcher _dispatcher;
    private readonly TimeProvider _clock;

    public AlertRuleDecisionApplier(IAlertStateStore states, AlertDispatcher dispatcher, TimeProvider clock)
    {
        _states = states;
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public void Apply(AlertRule rule, string stateKey, AlertRuleDecision decision)
    {
        switch (decision.Action)
        {
            case AlertRuleAction.Fire:
                if (decision.Message is { } message)
                {
                    _dispatcher.Enqueue(message, _clock.GetUtcNow());
                }

                if (decision.StateJson is not null)
                {
                    _states.Set(stateKey, decision.StateJson, _clock.GetUtcNow());
                }

                break;
            case AlertRuleAction.Clear:
                _states.Delete(stateKey);
                break;
            default:
                if (decision.StateJson is not null)
                {
                    _states.Set(stateKey, decision.StateJson, _clock.GetUtcNow());
                }

                break;
        }
    }
}

/// <summary>
/// 规则周期扫描（BackgroundService）：驱动 ScansOnSchedule 类型（无数据等）按 AlertOptions.ScanInterval 评估。
/// metric 规则取通用指标存储最近数据时间；无指标规则（设备心跳离线）取 devices.last_seen。
/// </summary>
public sealed class AlertRuleScanService : BackgroundService
{
    private readonly IAlertRuleStore _rules;
    private readonly ITargetStore _targets;
    private readonly IDeviceRegistry _devices;
    private readonly IMetricValueStore _metricValues;
    private readonly IMetricKeyRegistry _metricKeys;
    private readonly IAlertStateStore _states;
    private readonly IReadOnlyDictionary<string, IAlertRuleTypeHandler> _handlers;
    private readonly AlertRuleDecisionApplier _applier;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertRuleScanService> _logger;

    public AlertRuleScanService(
        IAlertRuleStore rules,
        ITargetStore targets,
        IDeviceRegistry devices,
        IMetricValueStore metricValues,
        IMetricKeyRegistry metricKeys,
        IAlertStateStore states,
        IEnumerable<IAlertRuleTypeHandler> handlers,
        AlertRuleDecisionApplier applier,
        AlertOptions options,
        TimeProvider clock,
        ILogger<AlertRuleScanService> logger)
    {
        _rules = rules;
        _targets = targets;
        _devices = devices;
        _metricValues = metricValues;
        _metricKeys = metricKeys;
        _states = states;
        _handlers = handlers.ToDictionary(h => h.RuleType, StringComparer.Ordinal);
        _applier = applier;
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
                ScanOnce(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "规则扫描异常，继续下一轮");
            }
        }
    }

    /// <summary>执行一轮扫描（公开便于测试）。</summary>
    public void ScanOnce(CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.GetUtcNow();
        foreach (var rule in _rules.List(enabled: true))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (!_handlers.TryGetValue(rule.RuleType, out var handler) || !handler.ScansOnSchedule)
                {
                    continue;
                }

                var target = _targets.Get(rule.TargetId);
                if (target is null)
                {
                    continue;
                }

                DateTimeOffset? lastDataUtc;
                MetricKeyInfo? metricInfo;
                if (rule.Metric is { } metric)
                {
                    lastDataUtc = _metricValues.TryGetLatest(target.Id, metric)?.TimeUtc;
                    metricInfo = _metricKeys.Get(metric);
                }
                else if (target.IsDevice && target.DeviceId is { } deviceId)
                {
                    lastDataUtc = _devices.Get(deviceId)?.LastSeenAtUtc;
                    metricInfo = null;
                }
                else
                {
                    // 服务目标没有心跳，无指标的无数据规则不可评估（创建入口已限制）
                    continue;
                }

                var stateKey = AlertRuleEngine.StateKey(rule);
                var context = new AlertRuleContext(rule, target, metricInfo, nowUtc, _states.Get(stateKey), LastDataUtc: lastDataUtc);
                _applier.Apply(rule, stateKey, handler.Evaluate(context));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "规则 {RuleId}（{RuleType}）扫描评估失败", rule.Id, rule.RuleType);
            }
        }
    }
}
