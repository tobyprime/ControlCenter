using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>阈值越限规则测试：持续时长、防刷屏、恢复重开、按设备覆盖、重启不重发。</summary>
public class ThresholdAlertEvaluatorTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));
    private readonly DeviceRegistry _devices;
    private readonly AlertOutboxStore _outbox;
    private readonly ThresholdAlertEvaluator _evaluator;
    private readonly long _deviceId;
    private readonly string _deviceName = "压测机甲";

    public ThresholdAlertEvaluatorTests()
    {
        _devices = new DeviceRegistry(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        var dispatcher = new AlertDispatcher(_outbox, [new StubNotifier()]);
        var options = new AlertOptions();
        _evaluator = new ThresholdAlertEvaluator(
            _devices,
            new AlertThresholdStore(_db.Factory),
            new AlertStateStore(_db.Factory),
            dispatcher,
            options,
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ThresholdAlertEvaluator>.Instance);
        _deviceId = _devices.Create(_deviceName, ["机房A"]).Device.Id;
    }

    public void Dispose() => _db.Dispose();

    private MetricsPoint Point(double cpu = 10, double mem = 20, double disk = 30) =>
        new(_clock.GetUtcNow(), cpu, mem, disk, 1024, 2048);

    [Fact]
    public void Samples_Below_Threshold_Never_Alert()
    {
        for (var i = 0; i < 5; i++)
        {
            _evaluator.Evaluate(_deviceId, Point(cpu: 89.9), _clock.GetUtcNow());
            _clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(0, _outbox.Count());
    }

    [Fact]
    public void Violation_Shorter_Than_Sustain_Window_Does_Not_Alert()
    {
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(30));
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());

        // 30s < 默认持续 60s：尚不告警
        Assert.Equal(0, _outbox.Count());
    }

    [Fact]
    public void Sustained_Violation_Alerts_Once_With_Device_Metric_And_Value()
    {
        _evaluator.Evaluate(_deviceId, Point(cpu: 95.5), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(30));
        _evaluator.Evaluate(_deviceId, Point(cpu: 96), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(31));
        _evaluator.Evaluate(_deviceId, Point(cpu: 97), _clock.GetUtcNow());

        Assert.Equal(1, _outbox.Count());
        var alert = _outbox.PeekOldest()!;
        Assert.Equal(NapcatNotifier.ChannelNameValue, alert.Channel);
        Assert.Contains(_deviceName, alert.Message.Content);
        Assert.Contains("CPU", alert.Message.Content);
        Assert.Contains("97", alert.Message.Content);
        Assert.Contains("90", alert.Message.Content);

        // 防刷屏默认：同一越限事件恢复前不重发
        _clock.Advance(TimeSpan.FromSeconds(30));
        _evaluator.Evaluate(_deviceId, Point(cpu: 98), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(10));
        _evaluator.Evaluate(_deviceId, Point(cpu: 99), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());
    }

    [Fact]
    public void Recovery_Closes_Event_And_Next_Violation_Alerts_Again()
    {
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());

        // 恢复：回落到阈值以下，事件关闭
        _evaluator.Evaluate(_deviceId, Point(cpu: 50), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(30));

        // 新一轮越限：再次触发（不受上一事件防刷屏影响）
        _evaluator.Evaluate(_deviceId, Point(cpu: 96), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(_deviceId, Point(cpu: 96), _clock.GetUtcNow());
        Assert.Equal(2, _outbox.Count());
    }

    [Fact]
    public void Per_Device_Override_Overrides_Global_For_That_Device_Only()
    {
        var otherId = _devices.Create("普通设备", ["机房B"]).Device.Id;
        new AlertThresholdStore(_db.Factory).SetOverride(_deviceId, AlertMetrics.Cpu, 50);

        // 55%：高于全局 90 的设备按覆盖值 50 告警
        _evaluator.Evaluate(_deviceId, Point(cpu: 55), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(_deviceId, Point(cpu: 55), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());

        // 另一台设备同值不告警（按全局 90）
        _evaluator.Evaluate(otherId, Point(cpu: 55), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(otherId, Point(cpu: 55), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());
    }

    [Fact]
    public void Restarted_Evaluator_Does_Not_ReAlert_Open_Violation()
    {
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());

        // 模拟面板重启：同库新实例，越限事件仍在持续
        var restarted = new ThresholdAlertEvaluator(
            _devices,
            new AlertThresholdStore(_db.Factory),
            new AlertStateStore(_db.Factory),
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            new AlertOptions(),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ThresholdAlertEvaluator>.Instance);
        restarted.Evaluate(_deviceId, Point(cpu: 96), _clock.GetUtcNow());

        Assert.Equal(1, _outbox.Count());
    }

    [Fact]
    public void Repeat_Window_Allows_Re_Notify_When_Configured()
    {
        var evaluator = new ThresholdAlertEvaluator(
            _devices,
            new AlertThresholdStore(_db.Factory),
            new AlertStateStore(_db.Factory),
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            new AlertOptions { RepeatMinutes = 5 },
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ThresholdAlertEvaluator>.Instance);

        evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        evaluator.Evaluate(_deviceId, Point(cpu: 95), _clock.GetUtcNow());
        Assert.Equal(1, _outbox.Count());

        // 持续越限 5 分钟后允许重发一次（防刷屏间隔可调）
        _clock.Advance(TimeSpan.FromMinutes(6));
        evaluator.Evaluate(_deviceId, Point(cpu: 96), _clock.GetUtcNow());
        Assert.Equal(2, _outbox.Count());
    }

    [Fact]
    public void Deleted_Device_Is_Skipped_Without_Alert()
    {
        var tempId = _devices.Create("临时设备", []).Device.Id;
        _devices.Delete(tempId);
        _evaluator.Evaluate(tempId, Point(cpu: 99), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(61));
        _evaluator.Evaluate(tempId, Point(cpu: 99), _clock.GetUtcNow());
        Assert.Equal(0, _outbox.Count());
    }

    private sealed class StubNotifier : INotifier
    {
        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
