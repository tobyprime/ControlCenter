using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// HTTP/JSON 探针（面板侧，模块2）：状态/延迟/JSON 提取指标入库并喂告警引擎；
/// 连续失败阈值（默认 3 次）只在转换时写一次 status=false；恢复首个成功写回 true。
/// </summary>
public class HttpProbeWorkerTests : IDisposable
{
    private const string MapSettingsJson = """
        { "maxPlayers": 200, "players": [ { "name": "steve" }, { "name": "alex" } ] }
        """;

    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly TargetRegistry _targets;
    private readonly MetricsStore _metrics;
    private readonly AlertRuleStore _rules;
    private readonly MetricKeyRegistry _metricKeys;
    private readonly AlertOutboxStore _outbox;
    private readonly ProbeConfigStore _configs;
    private readonly StubProbeClient _client = new();
    private readonly AlertRuleEngine _engine;
    private readonly HttpProbeWorker _worker;
    private readonly long _targetId;

    public HttpProbeWorkerTests()
    {
        _targets = new TargetRegistry(_db.Factory, _clock);
        _metrics = new MetricsStore(_db.Factory);
        _rules = new AlertRuleStore(_db.Factory, _clock);
        _metricKeys = new MetricKeyRegistry(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        _configs = new ProbeConfigStore(_db.Factory, _clock);
        _engine = new AlertRuleEngine(
            _rules, _metricKeys, _metrics, _targets,
            [new ThresholdAboveRuleType(), new ThresholdBelowRuleType(), new NoDataRuleType(), new StateMismatchRuleType()],
            new AlertStateStore(_db.Factory),
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertRuleEngine>.Instance);
        _worker = new HttpProbeWorker(
            _targets, _configs, _client, _metricKeys, _metrics, _engine, new ProbeOptions(), _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpProbeWorker>.Instance);
        _targetId = _targets.Create(TargetTypes.Service, "MC 服务", ["游戏"]).Target.Id;

        foreach (var seeded in _rules.List())
        {
            _rules.Delete(seeded.Id);
        }

        // status/latency_ms 已由迁移播种为内置 key；提取指标 key 由配置保存管道注册（约束 A），此处直接注册以隔离被测对象
        _metricKeys.Register("mc.players", MetricValueType.Number, "在线玩家数", "人");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Success_Writes_Status_Latency_And_Extracted_Metric()
    {
        _client.NextBody = MapSettingsJson;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new ProbeMetricMapping("mc.players", "$.players.length()", MetricValueType.Number, "在线玩家数", "人"),
        ]);

        await _worker.RunDueOnceAsync();

        var status = _metrics.GetLatest(_targetId, MetricKeys.Status);
        Assert.NotNull(status);
        Assert.Equal("true", status.ValueText);
        Assert.Equal(1, status.ValueNum);

        var latency = _metrics.GetLatest(_targetId, MetricKeys.LatencyMs);
        Assert.NotNull(latency);
        Assert.True(latency.ValueNum is > 0);

        var players = _metrics.GetLatest(_targetId, "mc.players");
        Assert.NotNull(players);
        Assert.Equal(2, players.ValueNum);
        Assert.Null(players.ValueText);

        // 最近探测时间随之刷新
        Assert.NotNull(_targets.Get(_targetId)!.LastSeenAtUtc);
    }

    [Fact]
    public async Task Success_Feeds_Alert_Engine_Rule_On_Extracted_Metric()
    {
        _client.NextBody = MapSettingsJson;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new ProbeMetricMapping("mc.players", "$.players.length()", MetricValueType.Number, "在线玩家数", "人"),
        ]);
        _rules.Create(_targetId, "mc.players", ThresholdAboveRuleType.TypeIdValue, """{"threshold":1}""", 0, 0, true);

        await _worker.RunDueOnceAsync();

        var alert = Assert.Single(_outbox.List());
        Assert.Contains("在线玩家数", alert.Message.Content);
        Assert.Contains("2", alert.Message.Content);
    }

    [Fact]
    public async Task Three_Consecutive_Failures_Write_Status_False_Once_And_Trigger_Alert()
    {
        _client.Fail = true;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/unreachable", 60, []);
        _rules.Create(_targetId, MetricKeys.Status, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", 0, 0, true);

        await _worker.RunDueOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Empty(_outbox.List());
        Assert.Null(_metrics.GetLatest(_targetId, MetricKeys.Status));

        // 第 3 次连续失败：判定异常，写一次 status=false 并触发状态不符规则
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        var status = _metrics.GetLatest(_targetId, MetricKeys.Status);
        Assert.NotNull(status);
        Assert.Equal("false", status.ValueText);
        var alert = Assert.Single(_outbox.List());
        Assert.Contains("服务状态", alert.Message.Content);

        // 第 4 次失败：不重复刷样本、不重发
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Single(_metrics.QueryRaw(_targetId, MetricKeys.Status, _clock.GetUtcNow().AddMinutes(-5), _clock.GetUtcNow().AddMinutes(1)));
        Assert.Single(_outbox.List());
    }

    [Fact]
    public async Task Failure_Counter_Resets_And_Status_Recovers_On_Next_Success()
    {
        _configs.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60, []);
        _rules.Create(_targetId, MetricKeys.Status, StateMismatchRuleType.TypeIdValue, """{"expected":"true"}""", 0, 0, true);

        _client.Fail = true;
        await _worker.RunDueOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Single(_outbox.List());

        // 恢复：首个成功写回 status=true，并产生恢复通知
        _client.Fail = false;
        _client.NextBody = MapSettingsJson;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Equal("true", _metrics.GetLatest(_targetId, MetricKeys.Status)!.ValueText);
        Assert.Equal(2, _outbox.List().Count());
        Assert.Contains("恢复", _outbox.List().Last().Message.Title);

        // 1 次失败（未达阈值）不清空 status、不告警
        _client.Fail = true;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Equal("true", _metrics.GetLatest(_targetId, MetricKeys.Status)!.ValueText);
        Assert.Equal(2, _outbox.List().Count());
    }

    [Fact]
    public async Task Extraction_Failure_Does_Not_Mark_Probe_Down()
    {
        // 路径未命中：该指标丢点，但服务本身可达（status/latency 照常写入）
        _client.NextBody = MapSettingsJson;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new ProbeMetricMapping("mc.players", "$.missing.length()", MetricValueType.Number, "在线玩家数", "人"),
        ]);

        await _worker.RunDueOnceAsync();

        Assert.Equal("true", _metrics.GetLatest(_targetId, MetricKeys.Status)!.ValueText);
        Assert.NotNull(_metrics.GetLatest(_targetId, MetricKeys.LatencyMs));
        Assert.Null(_metrics.GetLatest(_targetId, "mc.players"));
    }

    [Fact]
    public async Task Probe_Not_ReRun_Before_Interval_Elapses()
    {
        _client.NextBody = MapSettingsJson;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/tiles/settings.json", 60, []);

        await _worker.RunDueOnceAsync();
        await _worker.RunDueOnceAsync();
        Assert.Equal(1, _client.CallCount);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        Assert.Equal(2, _client.CallCount);
    }

    [Fact]
    public async Task Non_Success_Status_Code_Counts_As_Failure()
    {
        _client.NextStatusCode = 503;
        _configs.Save(_targetId, "https://mc.zenoxs.cn/degraded", 60, []);

        await _worker.RunDueOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _worker.RunDueOnceAsync();

        Assert.Equal("false", _metrics.GetLatest(_targetId, MetricKeys.Status)!.ValueText);
        Assert.Null(_metrics.GetLatest(_targetId, MetricKeys.LatencyMs));
    }

    private sealed class StubProbeClient : IProbeHttpClient
    {
        public bool Fail;
        public int CallCount;
        public string? NextBody;
        public int NextStatusCode = 200;

        public Task<ProbeFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
        {
            CallCount++;
            if (Fail || NextStatusCode >= 400)
            {
                return Task.FromResult(new ProbeFetchResult(false, null, null));
            }

            return Task.FromResult(new ProbeFetchResult(true, 42.5, NextBody));
        }
    }

    private sealed class StubNotifier : INotifier
    {
        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
