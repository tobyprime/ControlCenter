using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>规则引擎集成：入库采样 → (target, metric) 规则匹配 → 处理器评估 → 分发入队/状态落地。</summary>
public class AlertRuleEngineTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly DeviceRegistry _devices;
    private readonly TargetStore _targets;
    private readonly MetricKeyRegistry _keys;
    private readonly MetricValueStore _values;
    private readonly AlertRuleStore _rules;
    private readonly AlertStateStore _states;
    private readonly AlertOutboxStore _outbox;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly AlertOptions _alertOptions = new();
    private readonly AgentOptions _agentOptions = new();

    private AlertRuleEngine Engine()
    {
        var applier = new AlertRuleDecisionApplier(_states, Dispatcher(), _clock);
        return new AlertRuleEngine(
            _targets, _rules, _keys, _states,
            new IAlertRuleTypeHandler[]
            {
                new ThresholdAboveRuleHandler(_alertOptions),
                new NoDataRuleHandler(_agentOptions),
            },
            applier,
            NullLogger<AlertRuleEngine>.Instance);
    }

    private AlertDispatcher Dispatcher() => new(_outbox, [new FakeNotifier()]);    public AlertRuleEngineTests()
    {
        _devices = new DeviceRegistry(_db.Factory, _clock);
        _targets = new TargetStore(_db.Factory, _clock);
        _keys = new MetricKeyRegistry(_db.Factory, _clock);
        _values = new MetricValueStore(_db.Factory, _keys);
        _rules = new AlertRuleStore(_db.Factory, _clock);
        _states = new AlertStateStore(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        foreach (var (key, unit, display) in new[]
                 {
                     ("cpu", "%", "CPU 使用率"), ("mem", "%", "内存使用率"), ("disk", "%", "磁盘使用率"),
                     ("net_rx", "B/s", "下行速率"), ("net_tx", "B/s", "上行速率"),
                 })
        {
            _keys.Register(key, MetricValueType.Number, unit, display);
        }
    }

    private (DeviceInfo Device, TargetInfo Target) NewDevice(string name = "engine-host")
    {
        var device = _devices.Create(name, []).Device;
        var target = _targets.ProvisionForDevice(device.Id, name);
        return (device, target);
    }

    [Fact]
    public void Device_Sample_Evaluates_Threshold_Rule_And_Fires_After_Sustain()
    {
        var (device, _) = NewDevice();
        _rules.Create(_targets.GetByDeviceId(device.Id)!.Id, "cpu", ThresholdAboveRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeThreshold(90, sustainSeconds: 0, repeatMinutes: 0), enabled: true);
        var engine = Engine();

        // 采样入库事件：CPU 95（sustain=0 立即告警）
        engine.EvaluateDeviceSample(device.Id, new MetricsPoint(_clock.GetUtcNow(), Cpu: 95, Mem: 10, Disk: 10, NetRx: 0, NetTx: 0), _clock.GetUtcNow());

        var queue = _outbox.List();
        Assert.Single(queue);
        Assert.Contains("CPU 使用率当前 95.0%，超过阈值 90.0%", queue[0].Message.Content);

        // 恢复：事件关闭
        engine.EvaluateDeviceSample(device.Id, new MetricsPoint(_clock.GetUtcNow(), Cpu: 50, Mem: 10, Disk: 10, NetRx: 0, NetTx: 0), _clock.GetUtcNow());
        Assert.Null(_states.Get($"rule:{_rules.List()[0].Id}"));
    }

    [Fact]
    public void Disabled_Rules_Never_Fire()
    {
        var (device, target) = NewDevice();
        var rule = _rules.Create(target.Id, "cpu", ThresholdAboveRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeThreshold(90, 0, 0), enabled: false);
        var engine = Engine();

        engine.EvaluateDeviceSample(device.Id, new MetricsPoint(_clock.GetUtcNow(), Cpu: 99, Mem: 10, Disk: 10, NetRx: 0, NetTx: 0), _clock.GetUtcNow());

        Assert.Empty(_outbox.List());
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void Unknown_Rule_Type_Is_Skipped_Without_Breaking_Others()
    {
        var (device, target) = NewDevice();
        _rules.Create(target.Id, "cpu", "magic_rule_v9", "{}", enabled: true);
        _rules.Create(target.Id, "cpu", ThresholdAboveRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeThreshold(90, 0, 0), enabled: true);
        var engine = Engine();

        engine.EvaluateDeviceSample(device.Id, new MetricsPoint(_clock.GetUtcNow(), Cpu: 99, Mem: 10, Disk: 10, NetRx: 0, NetTx: 0), _clock.GetUtcNow());

        var queue = _outbox.List();
        Assert.Single(queue);
    }

    [Fact]
    public void Generic_Sample_Path_Evaluates_Registered_Metrics()
    {
        var (_, target) = NewDevice();
        _keys.Register("players", MetricValueType.Number, "人", "在线玩家数");
        _rules.Create(target.Id, "players", ThresholdAboveRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeThreshold(100, 0, 0), enabled: true);
        var engine = Engine();

        engine.EvaluateSample(target.Id, "players", new MetricValue(_clock.GetUtcNow(), 120, null), _clock.GetUtcNow());

        var queue = _outbox.List();
        Assert.Single(queue);
        Assert.Contains("在线玩家数当前 120.0人，超过阈值 100.0人", queue[0].Message.Content);
    }

    [Fact]
    public void Scan_Service_Fires_NoData_Rule_From_Metric_Store()
    {
        var (device, target) = NewDevice();
        _keys.Register("players", MetricValueType.Number, "人", "在线玩家数");
        _rules.Create(target.Id, "players", NoDataRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeNoData(600), enabled: true);
        var lastPoint = _clock.GetUtcNow().AddMinutes(-1);
        _values.Insert(target.Id, "players", lastPoint, new MetricValue(lastPoint, 5, null));
        var scan = NewScanService();

        // 数据新鲜：不告警
        _clock.Advance(TimeSpan.FromSeconds(30));
        scan.ScanOnce();
        Assert.Empty(_outbox.List());

        // 超过窗口：告警一次
        _clock.Advance(TimeSpan.FromMinutes(15));
        scan.ScanOnce();
        Assert.Single(_outbox.List());
        Assert.Contains("指标 在线玩家数 已超过 600 秒无数据", _outbox.List()[0].Message.Content);

        // 重启不重复：再次扫描不重发
        _clock.Advance(TimeSpan.FromMinutes(15));
        scan.ScanOnce();
        Assert.Single(_outbox.List());

        // 恢复：状态清除
        var fresh = _clock.GetUtcNow();
        _values.Insert(target.Id, "players", fresh, new MetricValue(fresh, 6, null));
        scan.ScanOnce();
        Assert.Single(_outbox.List());
        Assert.Null(_states.Get(AlertRuleEngine.StateKey(_rules.List()[0])));
    }

    [Fact]
    public void Scan_Service_Fires_Device_Heartbeat_NoData_From_LastSeen()
    {
        var (device, target) = NewDevice();
        _rules.Create(target.Id, null, NoDataRuleHandler.TypeName,
            AlertRuleParamsSerializer.SerializeNoData(60), enabled: true);
        var scan = NewScanService();

        // 从未上报：不告警
        scan.ScanOnce();
        Assert.Empty(_outbox.List());

        // 心跳后断流超窗：离线告警（一期同款文案）
        _devices.Touch(device.Id, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(120));
        scan.ScanOnce();
        Assert.Single(_outbox.List());
        Assert.Equal("设备离线告警", _outbox.List()[0].Message.Title);
        Assert.Contains("engine-host」已离线（超过 60 秒未上报心跳）", _outbox.List()[0].Message.Content);
    }

    private AlertRuleScanService NewScanService()
    {
        return new AlertRuleScanService(
            _rules, _targets, _devices, _values, _keys, _states,
            new IAlertRuleTypeHandler[]
            {
                new ThresholdAboveRuleHandler(_alertOptions),
                new NoDataRuleHandler(_agentOptions),
            },
            new AlertRuleDecisionApplier(_states, Dispatcher(), _clock),
            new AlertOptions { ScanSeconds = 15 },
            _clock,
            new ConsoleLogger<AlertRuleScanService>());
    }

    private sealed class FakeNotifier : INotifier
    {
        public string ChannelName => "test";

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class ConsoleLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Console.WriteLine($"[{logLevel}] {typeof(T).Name}: {formatter(state, exception)}\n{exception}");
    }

    public void Dispose() => _db.Dispose();
}
