using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>目标在线状态采样器测试：判定离线即写一次 online=false 样本并喂规则引擎；恢复由心跳链路写 true。</summary>
public class TargetStatusScannerTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));
    private readonly TargetRegistry _targets;
    private readonly MetricsStore _metrics;
    private readonly AlertRuleStore _rules;
    private readonly AlertOutboxStore _outbox;
    private readonly TargetStatusScanner _scanner;
    private readonly AlertRuleEngine _engine;
    private readonly long _targetId;
    private readonly AgentOptions _agentOptions = new() { HeartbeatIntervalSeconds = 30 };

    public TargetStatusScannerTests()
    {
        _targets = new TargetRegistry(_db.Factory, _clock);
        _metrics = new MetricsStore(_db.Factory);
        _rules = new AlertRuleStore(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        var metricKeys = new MetricKeyRegistry(_db.Factory, _clock);
        _engine = new AlertRuleEngine(
            _rules, metricKeys, _metrics, _targets,
            [new ThresholdAboveRuleType(), new ThresholdBelowRuleType(), new NoDataRuleType(), new StateMismatchRuleType()],
            new AlertStateStore(_db.Factory),
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertRuleEngine>.Instance);
        _scanner = new TargetStatusScanner(
            _targets, _metrics, _engine, _agentOptions, new AlertOptions(), _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TargetStatusScanner>.Instance);
        _targetId = _targets.Create(TargetTypes.Device, "在线设备", []).Target.Id;

        // 清空迁移播种的内置规则：只保留用例自建的状态不符规则，断言不串扰
        foreach (var seeded in _rules.List())
        {
            _rules.Delete(seeded.Id);
        }
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Offline_Transition_Writes_Online_False_Sample_Once_And_Rule_Fires()
    {
        _rules.Create(null, MetricKeys.Online, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", sustainSeconds: 0, repeatMinutes: 0, enabled: true);

        // 在线：心跳写入 true 样本（心跳处理器的行为）
        _targets.Touch(_targetId, _clock.GetUtcNow());
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(_clock.GetUtcNow(), 1, "true"));

        // 掉线：超过 OfflineAfter（连续 2 个周期）未心跳
        _clock.Advance(TimeSpan.FromSeconds(61));
        _scanner.ScanOnce();

        var latest = _metrics.GetLatest(_targetId, MetricKeys.Online);
        Assert.NotNull(latest);
        Assert.Equal("false", latest!.ValueText);

        // 状态不符规则已触发（sustain=0 判定即告警，对齐一期离线告警时机）
        var entry = Assert.Single(_outbox.List());
        Assert.Contains("在线设备", entry.Message.Content);

        // 再次扫描不重复写样本、不重发（防刷屏默认 0）
        _clock.Advance(TimeSpan.FromSeconds(15));
        _scanner.ScanOnce();
        Assert.Single(_outbox.List());
        Assert.Equal("false", _metrics.GetLatest(_targetId, MetricKeys.Online)!.ValueText);
    }

    [Fact]
    public void Online_Target_Produces_No_Sample()
    {
        _targets.Touch(_targetId, _clock.GetUtcNow());

        _scanner.ScanOnce();

        Assert.Null(_metrics.GetLatest(_targetId, MetricKeys.Online));
        Assert.Empty(_outbox.List());
    }

    [Fact]
    public void Never_Seen_Target_Is_Skipped()
    {
        _rules.Create(null, MetricKeys.Online, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", sustainSeconds: 0, repeatMinutes: 0, enabled: true);

        _clock.Advance(TimeSpan.FromHours(1));
        _scanner.ScanOnce();

        // 从未接入（无 last_seen）：无样本、不告警（与一期一致）
        Assert.Null(_metrics.GetLatest(_targetId, MetricKeys.Online));
        Assert.Empty(_outbox.List());
    }

    [Fact]
    public void Recovery_Via_Heartbeat_True_Sample_Closes_Event()
    {
        _rules.Create(null, MetricKeys.Online, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", sustainSeconds: 0, repeatMinutes: 0, enabled: true);
        _targets.Touch(_targetId, _clock.GetUtcNow());
        _metrics.Insert(_targetId, MetricKeys.Online, new MetricSample(_clock.GetUtcNow(), 1, "true"));
        _clock.Advance(TimeSpan.FromSeconds(61));
        _scanner.ScanOnce();
        Assert.Single(_outbox.List());

        // 恢复在线：心跳写回 true（心跳处理器入库 + 喂引擎，事件关闭；下次离线是新事件）
        _targets.Touch(_targetId, _clock.GetUtcNow());
        var recoveryAt = _clock.GetUtcNow();
        var trueSample = new MetricSample(recoveryAt, 1, "true");
        _metrics.Insert(_targetId, MetricKeys.Online, trueSample);
        _engine.OnSample(_targetId, MetricKeys.Online, trueSample, recoveryAt);
        _clock.Advance(TimeSpan.FromSeconds(61));
        _scanner.ScanOnce();

        Assert.Equal(2, _outbox.List().Count());
        Assert.Equal("false", _metrics.GetLatest(_targetId, MetricKeys.Online)!.ValueText);
    }

    [Fact]
    public void Service_Targets_Are_Ignored()
    {
        var serviceId = _targets.Create(TargetTypes.Service, "MC 服务", []).Target.Id;
        _targets.Touch(serviceId, _clock.GetUtcNow().AddHours(-1));

        _scanner.ScanOnce();

        // 服务目标不走 agent 心跳：不写 online 样本（状态来源由后续模块接入）
        Assert.Null(_metrics.GetLatest(serviceId, MetricKeys.Online));
    }

    private sealed class StubNotifier : INotifier
    {
        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
