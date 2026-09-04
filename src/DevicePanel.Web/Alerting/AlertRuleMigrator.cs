using DevicePanel.Web.Devices;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 设备默认告警规则种子：每个设备目标自带 cpu/mem/disk 阈值上限规则（阈值 = 一期全局默认语义：
/// alert_thresholds 全局行 ?? 内置 90）与心跳无数据（离线）规则。幂等：已有规则不重复建。
/// 一期数据迁移（AlertRuleMigrator）与新建设备（DeviceEndpoints）共用此入口，保证行为一致。
/// </summary>
public sealed class AlertRuleSeeder
{
    private readonly ITargetStore _targets;
    private readonly IAlertRuleStore _rules;
    private readonly IAlertThresholdStore _thresholds;
    private readonly AlertOptions _alertOptions;
    private readonly AgentOptions _agentOptions;
    private readonly TimeProvider _timeProvider;

    public AlertRuleSeeder(
        ITargetStore targets,
        IAlertRuleStore rules,
        IAlertThresholdStore thresholds,
        AlertOptions alertOptions,
        AgentOptions agentOptions,
        TimeProvider? timeProvider = null)
    {
        _targets = targets;
        _rules = rules;
        _thresholds = thresholds;
        _alertOptions = alertOptions;
        _agentOptions = agentOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>确保设备目标与其默认规则存在；返回目标。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="deviceName">设备名。</param>
    /// <param name="useEffectiveThresholds">
    /// true = 按设备生效阈值实例化（覆盖 ?? 全局 ?? 内置，一期迁移口径）；
    /// false = 全局默认阈值（新建设备口径）。
    /// </param>
    public TargetInfo EnsureForDevice(long deviceId, string deviceName, bool useEffectiveThresholds = false)
    {
        var target = _targets.ProvisionForDevice(deviceId, deviceName);

        foreach (var metric in AlertMetrics.Known)
        {
            if (_rules.Find(target.Id, metric, ThresholdAboveRuleHandler.TypeName) is not null)
            {
                continue;
            }

            var threshold = useEffectiveThresholds
                ? _thresholds.GetEffective(deviceId, metric)
                : _thresholds.GetGlobal(metric);
            _rules.Create(
                target.Id,
                metric,
                ThresholdAboveRuleHandler.TypeName,
                AlertRuleParamsSerializer.SerializeThreshold(threshold, _alertOptions.SustainSeconds, _alertOptions.RepeatMinutes),
                enabled: true);
        }

        if (_rules.Find(target.Id, null, NoDataRuleHandler.TypeName) is null)
        {
            _rules.Create(
                target.Id,
                null,
                NoDataRuleHandler.TypeName,
                AlertRuleParamsSerializer.SerializeNoData((int)_agentOptions.OfflineAfter.TotalSeconds),
                enabled: true);
        }

        return target;
    }
}

/// <summary>规则参数序列化（库内 params_json 的唯一写出口，保证字段命名一致）。</summary>
public static class AlertRuleParamsSerializer
{
    public static string SerializeThreshold(double threshold, int sustainSeconds, int repeatMinutes) =>
        System.Text.Json.JsonSerializer.Serialize(new { threshold, sustainSeconds, repeatMinutes });

    public static string SerializeNoData(int windowSeconds) =>
        System.Text.Json.JsonSerializer.Serialize(new { windowSeconds });

    public static string SerializeStatusMismatch(string expectedValue, int sustainSeconds, int repeatMinutes) =>
        System.Text.Json.JsonSerializer.Serialize(new { expectedValue, sustainSeconds, repeatMinutes });
}

/// <summary>一期告警数据 → 规则实例的一次性迁移（TOB-360 验收 4/7：行为不变、历史无损）。</summary>
public sealed class AlertRuleMigrator : IHostedService
{
    /// <summary>迁移完成标记（panel_settings）：只跑一次，重启幂等。</summary>
    public const string MigrationFlagKey = "alert_rules_migrated_v1";

    private readonly IDeviceRegistry _devices;
    private readonly ITargetStore _targets;
    private readonly AlertRuleSeeder _seeder;
    private readonly IAlertRuleStore _rules;
    private readonly IAlertStateStore _states;
    private readonly IPanelSettingsStore _panelSettings;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AlertRuleMigrator> _logger;

    public AlertRuleMigrator(
        IDeviceRegistry devices,
        ITargetStore targets,
        AlertRuleSeeder seeder,
        IAlertRuleStore rules,
        IAlertStateStore states,
        IPanelSettingsStore panelSettings,
        SqliteConnectionFactory connectionFactory,
        TimeProvider? timeProvider = null,
        ILogger<AlertRuleMigrator>? logger = null)
    {
        _devices = devices;
        _targets = targets;
        _seeder = seeder;
        _rules = rules;
        _states = states;
        _panelSettings = panelSettings;
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AlertRuleMigrator>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_panelSettings.Get(MigrationFlagKey) is not null)
        {
            return Task.CompletedTask;
        }

        var deviceCount = 0;
        foreach (var device in _devices.List())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _seeder.EnsureForDevice(device.Id, device.Name, useEffectiveThresholds: true);
            deviceCount++;
        }

        var remapped = RemapLegacyStates();
        _panelSettings.Set(MigrationFlagKey, _timeProvider.GetUtcNow().ToString("O"));
        _logger.LogInformation(
            "一期告警迁移完成：{DeviceCount} 台设备的阈值与离线规则已实例化，{Remapped} 条在途告警状态已搬运",
            deviceCount, remapped);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 在途告警状态搬运：threshold:{deviceId}:{metric} / offline:{deviceId} → rule:{id}。
    /// 状态 JSON 形状与新版一致（防抖状态/离线已告警标记），升级瞬间不重复告警、不丢事件。
    /// </summary>
    public int RemapLegacyStates()
    {
        var remapped = 0;
        using var connection = _connectionFactory.CreateOpenConnection();
        var legacyKeys = new List<(string Key, string StateJson)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT rule_key, state_json FROM alert_state";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                legacyKeys.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (key, stateJson) in legacyKeys)
        {
            var ruleId = ResolveLegacyStateRule(key);
            if (ruleId is null)
            {
                continue;
            }

            _states.Set($"rule:{ruleId}", stateJson, _timeProvider.GetUtcNow());
            _states.Delete(key);
            remapped++;
        }

        return remapped;
    }

    private long? ResolveLegacyStateRule(string legacyKey)
    {
        if (legacyKey.StartsWith("threshold:", StringComparison.Ordinal))
        {
            var parts = legacyKey["threshold:".Length..].Split(':', 2);
            if (parts.Length == 2
                && long.TryParse(parts[0], out var thresholdDeviceId)
                && _targets.GetByDeviceId(thresholdDeviceId) is { } thresholdTarget)
            {
                return _rules.Find(thresholdTarget.Id, parts[1], ThresholdAboveRuleHandler.TypeName)?.Id;
            }
        }
        else if (legacyKey.StartsWith("offline:", StringComparison.Ordinal))
        {
            if (long.TryParse(legacyKey["offline:".Length..], out var offlineDeviceId)
                && _targets.GetByDeviceId(offlineDeviceId) is { } offlineTarget)
            {
                return _rules.Find(offlineTarget.Id, null, NoDataRuleHandler.TypeName)?.Id;
            }
        }

        return null;
    }
}
