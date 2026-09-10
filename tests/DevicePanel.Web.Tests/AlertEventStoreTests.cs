using DevicePanel.Web.Alerting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 告警事件历史存储测试（TOB-403 F2）：触发/恢复事件落库（快照规则与目标信息）、
/// 按目标与时间窗口筛选、投递状态回写（pending → delivered / failed）。
/// </summary>
public class AlertEventStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
    private readonly AlertEventStore _store;

    public AlertEventStoreTests() => _store = new AlertEventStore(_db.Factory);

    public void Dispose() => _db.Dispose();

    private AlertEventDraft Draft(
        string kind = AlertEventKinds.Trigger,
        long? targetId = 7,
        string targetName = "压测机甲") =>
        new(3, "threshold_above", targetId, targetName, "cpu", "CPU 使用率", kind,
            "指标越限告警", "目标「压测机甲」CPU 使用率 95.0%，超过阈值 90.0%",
            new AlertEventSample(_clock.GetUtcNow(), 95, null));

    [Fact]
    public void Append_Assigns_Id_And_Reads_Back_All_Fields()
    {
        var eventId = _store.Append(Draft(), _clock.GetUtcNow());

        var listed = _store.List(null, null, null, 100);
        Assert.Single(listed);
        var row = listed[0];
        Assert.Equal(eventId, row.Id);
        Assert.Equal(3, row.RuleId);
        Assert.Equal("threshold_above", row.RuleType);
        Assert.Equal(7, row.TargetId);
        Assert.Equal("压测机甲", row.TargetName);
        Assert.Equal("cpu", row.MetricKey);
        Assert.Equal("CPU 使用率", row.MetricDisplayName);
        Assert.Equal(AlertEventKinds.Trigger, row.Kind);
        Assert.Equal("指标越限告警", row.Title);
        Assert.Contains("95.0%", row.Content);
        Assert.NotNull(row.Sample);
        Assert.Equal(95, row.Sample!.ValueNum);
        // 投递状态默认待发送，由分发链路回写
        Assert.Equal(AlertDeliveryStatuses.Pending, row.DeliveryStatus);
        Assert.Null(row.DeliveredAtUtc);
        Assert.Null(row.DeliveryError);
    }

    [Fact]
    public void Append_Supports_Sampleless_Recovery_And_Global_Rule()
    {
        var draft = new AlertEventDraft(null, "threshold_above", null, "（全局）", "cpu", "CPU 使用率",
            AlertEventKinds.Recover, "告警恢复通知", "目标「压测机甲」CPU 使用率已恢复正常", null);
        _store.Append(draft, _clock.GetUtcNow());

        var row = Assert.Single(_store.List(null, null, null, 100));
        Assert.Equal(AlertEventKinds.Recover, row.Kind);
        Assert.Null(row.RuleId);
        Assert.Null(row.TargetId);
        Assert.Null(row.Sample);
    }

    [Fact]
    public void List_Filters_By_Target_And_Time_Window()
    {
        var early = _store.Append(Draft(targetId: 1, targetName: "甲"), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(10));
        var later = _store.Append(Draft(targetId: 2, targetName: "乙"), _clock.GetUtcNow());
        var windowStart = _clock.GetUtcNow().AddMinutes(-5);

        Assert.DoesNotContain(later, _store.List(targetId: 1, null, null, 100).Select(e => e.Id));
        Assert.Equal([early], _store.List(targetId: 1, null, null, 100).Select(e => e.Id));
        Assert.Equal([later], _store.List(targetId: 2, null, null, 100).Select(e => e.Id));
        // 时间窗口：from 之后只命中后一条
        Assert.Equal([later], _store.List(null, windowStart, null, 100).Select(e => e.Id));
        // to 之前只命中前一条
        Assert.Equal([early], _store.List(null, null, _clock.GetUtcNow().AddMinutes(-5), 100).Select(e => e.Id));
    }

    [Fact]
    public void List_Returns_Newest_First_And_Respects_Limit()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Append(Draft(), _clock.GetUtcNow());
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var page = _store.List(null, null, null, 3);
        Assert.Equal(3, page.Count);
        Assert.True(page[0].CreatedAtUtc >= page[1].CreatedAtUtc && page[1].CreatedAtUtc >= page[2].CreatedAtUtc);
    }

    [Fact]
    public void Delivery_Status_Transitions_Pending_To_Delivered_Or_Failed()
    {
        var delivered = _store.Append(Draft(), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(5));
        var failed = _store.Append(Draft(), _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromSeconds(5));
        _store.MarkDelivered(delivered, _clock.GetUtcNow());
        _store.RecordDeliveryFailure(failed, "napcat 连接被拒绝", _clock.GetUtcNow());

        var rows = _store.List(null, null, null, 100).ToDictionary(e => e.Id);
        Assert.Equal(AlertDeliveryStatuses.Delivered, rows[delivered].DeliveryStatus);
        Assert.Equal(_clock.GetUtcNow(), rows[delivered].DeliveredAtUtc);
        Assert.Equal(AlertDeliveryStatuses.Failed, rows[failed].DeliveryStatus);
        Assert.Equal("napcat 连接被拒绝", rows[failed].DeliveryError);
    }
}
