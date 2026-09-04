using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 一期告警 → 规则实例迁移（TOB-360 验收 4：升级后告警行为一致；验收 7：数据无损、零重装）。
/// </summary>
public class AlertRuleMigrationTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly DeviceRegistry _devices;
    private readonly TargetStore _targets;
    private readonly AlertRuleStore _rules;
    private readonly AlertThresholdStore _thresholds;
    private readonly AlertStateStore _states;
    private readonly PanelSettingsStore _panelSettings;
    private readonly AlertOptions _alertOptions = new();
    private readonly AgentOptions _agentOptions = new();

    public AlertRuleMigrationTests()
    {
        _devices = new DeviceRegistry(_db.Factory, _clock);
        _targets = new TargetStore(_db.Factory, _clock);
        _rules = new AlertRuleStore(_db.Factory, _clock);
        _thresholds = new AlertThresholdStore(_db.Factory, _clock);
        _states = new AlertStateStore(_db.Factory, _clock);
        _panelSettings = new PanelSettingsStore(_db.Factory, _clock);
    }

    private AlertRuleMigrator NewMigrator() => new(
        _devices, _targets, NewSeeder(), _rules, _states, _panelSettings, _db.Factory, _clock,
        NullLogger<AlertRuleMigrator>.Instance);

    private AlertRuleSeeder NewSeeder() => new(
        _targets, _rules, _thresholds, _alertOptions, _agentOptions, _clock);

    private (DeviceInfo Device, TargetInfo Target) NewDevice(string name)
    {
        var device = _devices.Create(name, []).Device;
        return (device, _targets.ProvisionForDevice(device.Id, name));
    }

    [Fact]
    public void Legacy_Thresholds_Materialize_As_Rule_Instances_With_Effective_Values()
    {
        // 一期配置：全局默认 cpu=85（alert_thresholds device_id=0），设备覆盖 mem=70，disk 无配置（内置 90）
        var (device, target) = NewDevice("legacy-host");
        _thresholds.SetGlobal(AlertMetrics.Cpu, 85);
        _thresholds.SetOverride(device.Id, AlertMetrics.Mem, 70);

        NewMigrator().StartAsync(CancellationToken.None);

        var rules = _rules.List(targetId: target.Id, ruleType: ThresholdAboveRuleHandler.TypeName);
        Assert.Equal(3, rules.Count);

        var cpu = rules.Single(r => r.Metric == "cpu");
        var mem = rules.Single(r => r.Metric == "mem");
        var disk = rules.Single(r => r.Metric == "disk");
        Assert.Contains("\"threshold\":85", cpu.ParamsJson);
        Assert.Contains("\"threshold\":70", mem.ParamsJson);
        Assert.Contains("\"threshold\":90", disk.ParamsJson);
        // 防抖参数化：迁移时取一期运行默认（60s 持续 / 0 重发）
        Assert.Contains("\"sustainSeconds\":60", cpu.ParamsJson);
        Assert.Contains("\"repeatMinutes\":0", cpu.ParamsJson);
        Assert.All(rules, r => Assert.True(r.Enabled));
    }

    [Fact]
    public void Legacy_Offline_Behavior_Migrates_As_Heartbeat_NoData_Rule()
    {
        var (device, target) = NewDevice("offline-host");

        NewMigrator().StartAsync(CancellationToken.None);

        var rule = _rules.Find(target.Id, null, NoDataRuleHandler.TypeName);
        Assert.NotNull(rule);
        Assert.Contains("\"windowSeconds\":60", rule!.ParamsJson);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void In_Flight_Alert_States_Are_Remapped_To_Rule_Keys()
    {
        var (device, target) = NewDevice("state-host");
        // 一期在途状态：cpu 越限事件等待中、设备已离线告警
        var cpuViolation = """{"FirstSeenUtc":"2026-09-01T00:00:00+00:00","LastAlertedUtc":null}""";
        var offlineAlerted = """{"AlertedAtUtc":"2026-09-01T00:05:00+00:00"}""";
        _states.Set($"threshold:{device.Id}:cpu", cpuViolation, _clock.GetUtcNow());
        _states.Set($"offline:{device.Id}", offlineAlerted, _clock.GetUtcNow());

        NewMigrator().StartAsync(CancellationToken.None);

        var cpuRule = _rules.Find(target.Id, "cpu", ThresholdAboveRuleHandler.TypeName)!;
        var offlineRule = _rules.Find(target.Id, null, NoDataRuleHandler.TypeName)!;
        Assert.Equal(cpuViolation, _states.Get($"rule:{cpuRule.Id}"));
        Assert.Equal(offlineAlerted, _states.Get($"rule:{offlineRule.Id}"));
        Assert.Null(_states.Get($"threshold:{device.Id}:cpu"));
        Assert.Null(_states.Get($"offline:{device.Id}"));

        // 搬运后的防抖状态可被新版处理器无缝续接：持续 61s 后告警
        var handler = new ThresholdAboveRuleHandler(_alertOptions);
        var rule = cpuRule with { ParamsJson = cpuRule.ParamsJson };
        var now = DateTimeOffset.Parse("2026-09-01T00:01:01Z");
        var context = new AlertRuleContext(rule, target, null, now, _states.Get($"rule:{cpuRule.Id}"), SampleNum: 95);
        var decision = handler.Evaluate(context);
        Assert.Equal(AlertRuleAction.Fire, decision.Action);
        Assert.Contains("已持续 61 秒", decision.Message!.Content);
    }

    [Fact]
    public void Migration_Is_Idempotent_Across_Restarts()
    {
        NewDevice("twice-host");
        NewMigrator().StartAsync(CancellationToken.None);
        var countAfterFirst = _rules.List().Count;

        NewMigrator().StartAsync(CancellationToken.None);

        Assert.Equal(countAfterFirst, _rules.List().Count);
        Assert.NotNull(_panelSettings.Get(AlertRuleMigrator.MigrationFlagKey));
    }

    [Fact]
    public void New_Devices_Get_Default_Rules_After_Migration()
    {
        NewMigrator().StartAsync(CancellationToken.None);

        var (device, target) = NewDevice("fresh-host");
        NewSeeder().EnsureForDevice(device.Id, device.Name);

        Assert.Equal(3, _rules.List(targetId: target.Id, ruleType: ThresholdAboveRuleHandler.TypeName).Count);
        Assert.NotNull(_rules.Find(target.Id, null, NoDataRuleHandler.TypeName));
    }

    [Fact]
    public void Unset_Global_Falls_Back_To_Built_In_Default_90()
    {
        var (_, target) = NewDevice("default-host");

        NewMigrator().StartAsync(CancellationToken.None);

        var cpu = _rules.Find(target.Id, "cpu", ThresholdAboveRuleHandler.TypeName)!;
        Assert.Contains("\"threshold\":90", cpu.ParamsJson);
    }

    public void Dispose() => _db.Dispose();
}
