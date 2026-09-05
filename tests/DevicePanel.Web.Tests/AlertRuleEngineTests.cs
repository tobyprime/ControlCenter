using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 规则评估引擎测试：一期阈值越限行为回归（持续窗口、防刷屏、恢复重开、目标级遮蔽全局、重启不重发）
/// + 新增规则类型（阈值下/无数据/状态不符）与可插拔语义。
/// </summary>
public class AlertRuleEngineTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));
    private readonly TargetRegistry _targets;
    private readonly MetricsStore _metrics;
    private readonly AlertRuleStore _rules;
    private readonly MetricKeyRegistry _metricKeys;
    private readonly AlertOutboxStore _outbox;
    private readonly AlertRuleEngine _engine;
    private readonly long _targetId;
    private readonly string _targetName = "压测机甲";

    public AlertRuleEngineTests()
    {
        _targets = new TargetRegistry(_db.Factory, _clock);
        _metrics = new MetricsStore(_db.Factory);
        _rules = new AlertRuleStore(_db.Factory, _clock);
        _metricKeys = new MetricKeyRegistry(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        _engine = CreateEngine();
        _targetId = _targets.Create(TargetTypes.Device, _targetName, ["机房A"]).Id;

        // 清空迁移播种的内置规则：引擎行为测试从干净状态开始，各用例自建规则
        foreach (var seeded in _rules.List())
        {
            _rules.Delete(seeded.Id);
        }
    }

    public void Dispose() => _db.Dispose();

    private AlertRuleEngine CreateEngine(int repeatMinutes = 0) =>
        new(_rules, _metricKeys, _metrics, _targets,
            [new ThresholdAboveRuleType(), new ThresholdBelowRuleType(), new NoDataRuleType(), new StateMismatchRuleType()],
            new AlertStateStore(_db.Factory),
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertRuleEngine>.Instance);

    private AlertRule CreateRule(long? targetId, string metricKey, string ruleType, string parameters, int sustain = 60, int repeat = 0, bool enabled = true) =>
        _rules.Create(targetId, metricKey, ruleType, parameters, sustain, repeat, enabled);

    private void Report(string metricKey, double value, DateTimeOffset? timeUtc = null)
    {
        var time = timeUtc ?? _clock.GetUtcNow();
        _metrics.Insert(_targetId, metricKey, new MetricSample(time, value, null));
        _engine.OnSample(_targetId, metricKey, new MetricSample(time, value, null), _clock.GetUtcNow());
    }

    // —— 一期行为回归（迁移后的全局规则 + 目标级规则与升级前一致）——

    [Fact]
    public void Samples_Below_Global_Threshold_Never_Alert()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        for (var i = 0; i < 5; i++)
        {
            Report(MetricKeys.Cpu, 89.9);
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Empty(_outbox.List());
    }

    [Fact]
    public void Violation_Shorter_Than_Sustain_Window_Does_Not_Alert()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Report(MetricKeys.Cpu, 95);

        // 30s < 默认持续 60s：尚不告警
        Assert.Empty(_outbox.List());
    }

    [Fact]
    public void Sustained_Violation_Alerts_Once_With_Target_Metric_And_Value()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        Report(MetricKeys.Cpu, 95.5);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Report(MetricKeys.Cpu, 96);
        _clock.Advance(TimeSpan.FromSeconds(31));
        Report(MetricKeys.Cpu, 97);

        Assert.Single(_outbox.List());
        var alert = _outbox.PeekOldest()!;
        Assert.Equal(NapcatNotifier.ChannelNameValue, alert.Channel);
        Assert.Equal("指标越限告警", alert.Message.Title);
        Assert.Contains(_targetName, alert.Message.Content);
        Assert.Contains("CPU 使用率", alert.Message.Content);
        Assert.Contains("97.0", alert.Message.Content);
        Assert.Contains("90.0", alert.Message.Content);

        // 防刷屏默认：同一越限事件恢复前不重发
        _clock.Advance(TimeSpan.FromSeconds(30));
        Report(MetricKeys.Cpu, 98);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Report(MetricKeys.Cpu, 99);
        Assert.Single(_outbox.List());
    }

    [Fact]
    public void Recovery_Closes_Event_And_Next_Violation_Alerts_Again()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 95);
        Assert.Single(_outbox.List());

        // 恢复：回落到阈值以下，事件关闭，并发恢复通知（本次确实告警过）
        Report(MetricKeys.Cpu, 50);
        Assert.Equal(2, _outbox.List().Count());
        var recovery = _outbox.List().Last();
        Assert.Equal("告警恢复通知", recovery.Message.Title);
        Assert.Contains(_targetName, recovery.Message.Content);
        Assert.Contains("CPU 使用率", recovery.Message.Content);
        _clock.Advance(TimeSpan.FromSeconds(30));

        // 新一轮越限：再次触发（不受上一事件防刷屏影响）
        Report(MetricKeys.Cpu, 96);
        Assert.Equal(2, _outbox.List().Count());
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 96);
        Assert.Equal(3, _outbox.List().Count());
    }

    [Fact]
    public void Recovery_Notification_Sent_Once_Per_Event()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 95);

        // 恢复后的后续正常样本不再重复发恢复通知
        Report(MetricKeys.Cpu, 50);
        Assert.Equal(2, _outbox.List().Count());
        Report(MetricKeys.Cpu, 50);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Report(MetricKeys.Cpu, 50);
        Assert.Equal(2, _outbox.List().Count());
    }

    [Fact]
    public void No_Recovery_Notification_When_Violation_Never_Alerted()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        // 越限未满持续窗口即回落：从未告警，恢复也不发通知
        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Report(MetricKeys.Cpu, 50);

        Assert.Empty(_outbox.List());
    }

    [Fact]
    public void No_Recovery_Notification_After_Rule_State_Reset()
    {
        var rule = CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");

        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 95);
        Assert.Single(_outbox.List());

        // 规则删除/参数变更加重置状态：随后的恢复是人工介入结果，不发恢复通知
        _engine.ResetState(rule.Id);
        Report(MetricKeys.Cpu, 50);

        Assert.Single(_outbox.List());
    }

    [Fact]
    public void Target_Level_Rule_Shadows_Global_For_That_Target_Only()
    {
        var otherId = _targets.Create(TargetTypes.Device, "普通设备", ["机房B"]).Id;
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");
        CreateRule(_targetId, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":50}""");

        // 55%：本机按目标级规则 50 告警
        Report(MetricKeys.Cpu, 55);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 55);
        Assert.Single(_outbox.List());
        Assert.Contains(_targetName, _outbox.PeekOldest()!.Message.Content);

        // 另一台设备同值不告警（按全局 90）——此处直接喂引擎模拟另一目标的上报
        _metrics.Insert(otherId, MetricKeys.Cpu, new MetricSample(_clock.GetUtcNow(), 55, null));
        _engine.OnSample(otherId, MetricKeys.Cpu, new MetricSample(_clock.GetUtcNow(), 55, null), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _metrics.Insert(otherId, MetricKeys.Cpu, new MetricSample(_clock.GetUtcNow(), 55, null));
        _engine.OnSample(otherId, MetricKeys.Cpu, new MetricSample(_clock.GetUtcNow(), 55, null), _clock.GetUtcNow());
        Assert.Single(_outbox.List());
    }

    [Fact]
    public void Restarted_Engine_Does_Not_ReAlert_Open_Violation()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""");
        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 95);
        Assert.Single(_outbox.List());

        // 模拟面板重启：同库新实例，越限事件仍在持续（状态持久化，不重复告警）
        var restarted = CreateEngine();
        var now = _clock.GetUtcNow();
        _metrics.Insert(_targetId, MetricKeys.Cpu, new MetricSample(now, 96, null));
        restarted.OnSample(_targetId, MetricKeys.Cpu, new MetricSample(now, 96, null), _clock.GetUtcNow());

        Assert.Single(_outbox.List());
    }

    [Fact]
    public void Repeat_Window_Allows_Re_Notify_When_Configured()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", sustain: 60, repeat: 5);

        Report(MetricKeys.Cpu, 95);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report(MetricKeys.Cpu, 95);
        Assert.Single(_outbox.List());

        // 持续越限 5 分钟后允许重发一次（防刷屏间隔按规则可调）
        _clock.Advance(TimeSpan.FromMinutes(6));
        Report(MetricKeys.Cpu, 96);
        Assert.Equal(2, _outbox.List().Count());
    }

    [Fact]
    public void Disabled_Rule_Never_Fires()
    {
        CreateRule(null, MetricKeys.Cpu, ThresholdAboveRuleType.TypeIdValue, """{"threshold":90}""", enabled: false);

        Report(MetricKeys.Cpu, 99);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Report(MetricKeys.Cpu, 99);

        Assert.Empty(_outbox.List());
    }

    // —— 新增规则类型 ——

    [Fact]
    public void Threshold_Below_Rule_Fires_When_Value_Sustained_Below()
    {
        _metricKeys.Register("battery.level", MetricValueType.Number, "电池电量", "%");
        CreateRule(_targetId, "battery.level", ThresholdBelowRuleType.TypeIdValue, """{"threshold":20}""");

        // 正常值不触发
        Report("battery.level", 50);
        Assert.Empty(_outbox.List());

        // 持续低于阈值（首个违规样本起算持续窗口）
        Report("battery.level", 15);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Report("battery.level", 15);

        Assert.Single(_outbox.List());
        Assert.Contains("低于阈值 20.0", _outbox.PeekOldest()!.Message.Content);
    }

    [Fact]
    public void State_Mismatch_Rule_Fires_On_Unexpected_State_And_Recovers()
    {
        // 设备在线状态规则（迁移播种的全局规则的等价形态）：online != true 持续即告警
        CreateRule(null, MetricKeys.Online, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", sustain: 0);

        var onlineAt = _clock.GetUtcNow();
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(onlineAt, 1, "true"));
        _engine.OnSample(_targetId, MetricKeys.Online, new MetricSample(onlineAt, 1, "true"), _clock.GetUtcNow());
        Assert.Empty(_outbox.List());

        _clock.Advance(TimeSpan.FromSeconds(15));
        // 判定离线：online=false 样本，sustain=0 判定即告警（对齐一期离线告警时机）
        var offlineAt = _clock.GetUtcNow();
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(offlineAt, 0, "false"));
        _engine.OnSample(_targetId, MetricKeys.Online, new MetricSample(offlineAt, 0, "false"), _clock.GetUtcNow());

        Assert.Single(_outbox.List());
        var alert = _outbox.PeekOldest()!;
        Assert.Contains("true", alert.Message.Content);
        Assert.Contains("false", alert.Message.Content);

        // 恢复在线：状态关闭并发恢复通知，再离线按新事件重新告警
        _clock.Advance(TimeSpan.FromSeconds(30));
        var recoverAt = _clock.GetUtcNow();
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(recoverAt, 1, "true"));
        _engine.OnSample(_targetId, MetricKeys.Online, new MetricSample(recoverAt, 1, "true"), _clock.GetUtcNow());
        Assert.Equal(2, _outbox.List().Count());
        _clock.Advance(TimeSpan.FromSeconds(90));
        var againAt = _clock.GetUtcNow();
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(againAt, 0, "false"));
        _engine.OnSample(_targetId, MetricKeys.Online, new MetricSample(againAt, 0, "false"), _clock.GetUtcNow());
        Assert.Equal(3, _outbox.List().Count());
    }

    [Fact]
    public void No_Data_Rule_Fires_After_Missing_Window_Via_Sweep()
    {
        _metricKeys.Register("player.count", MetricValueType.Number, "在线玩家数", "人");
        CreateRule(_targetId, "player.count", NoDataRuleType.TypeIdValue, """{"minutes":10}""", sustain: 0);

        // 正常上报期间扫描不触发
        Report("player.count", 12);
        _engine.Sweep(_clock.GetUtcNow());
        Assert.Empty(_outbox.List());

        // 停止上报 9 分钟：未达窗口
        _clock.Advance(TimeSpan.FromMinutes(9));
        _engine.Sweep(_clock.GetUtcNow());
        Assert.Empty(_outbox.List());

        // 停止上报 10 分钟后扫描触发（sustain=0）
        _clock.Advance(TimeSpan.FromMinutes(1));
        _engine.Sweep(_clock.GetUtcNow());
        Assert.Single(_outbox.List());
        var alert = _outbox.PeekOldest()!;
        Assert.Contains("10 分钟", alert.Message.Content);
        Assert.Contains("在线玩家数", alert.Message.Content);

        // 持续无数据不重发（防刷屏默认 0）
        _clock.Advance(TimeSpan.FromMinutes(30));
        _engine.Sweep(_clock.GetUtcNow());
        Assert.Single(_outbox.List());

        // 数据恢复上报即恢复（并发恢复通知），之后再缺失按新事件触发
        Report("player.count", 15);
        Assert.Equal(2, _outbox.List().Count());
        _clock.Advance(TimeSpan.FromMinutes(11));
        _engine.Sweep(_clock.GetUtcNow());
        Assert.Equal(3, _outbox.List().Count());
    }

    [Fact]
    public void No_Data_Rule_Skips_Targets_That_Never_Reported()
    {
        _metricKeys.Register("never.reported", MetricValueType.Number, "从未上报", "");
        CreateRule(_targetId, "never.reported", NoDataRuleType.TypeIdValue, """{"minutes":10}""", sustain: 0);

        _clock.Advance(TimeSpan.FromHours(1));
        _engine.Sweep(_clock.GetUtcNow());

        Assert.Empty(_outbox.List());
    }

    // —— 参数校验 ——

    [Theory]
    [InlineData("""{"threshold":"abc"}""")]
    [InlineData("""{}""")]
    [InlineData("not-json")]
    public void Threshold_Type_Rejects_Invalid_Parameters(string parameters)
    {
        var type = new ThresholdAboveRuleType();
        Assert.NotNull(type.ValidateParameters(parameters));
    }

    [Fact]
    public void No_Data_Type_Rejects_Out_Of_Range_Minutes()
    {
        var type = new NoDataRuleType();
        Assert.NotNull(type.ValidateParameters("""{"minutes":0}"""));
        Assert.NotNull(type.ValidateParameters("""{"minutes":1441}"""));
        Assert.Null(type.ValidateParameters("""{"minutes":10}"""));
    }

    [Fact]
    public void State_Mismatch_Type_Rejects_Empty_Expected()
    {
        var type = new StateMismatchRuleType();
        Assert.NotNull(type.ValidateParameters("""{"expected":""}"""));
        Assert.Null(type.ValidateParameters("""{"expected":"online"}"""));
    }

    private sealed class StubNotifier : INotifier
    {
        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
