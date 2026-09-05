using DevicePanel.Web.Alerting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>告警存储单元测试：待发队列 FIFO/失败记账/持久化、napcat 设置往返、规则状态。</summary>
public class AlertStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Outbox_Enqueue_Peek_MarkSent_Follows_Fifo_Order()
    {
        var store = new AlertOutboxStore(_db.Factory);
        store.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("标题一", "内容一"), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(5));
        store.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("标题二", "内容二"), _clock.GetUtcNow());

        var first = store.PeekOldest();
        Assert.NotNull(first);
        Assert.Equal("标题一", first!.Message.Title);
        Assert.Equal(0, first.Attempts);

        store.MarkSent(first.Id);
        var second = store.PeekOldest();
        Assert.NotNull(second);
        Assert.Equal("标题二", second!.Message.Title);
        Assert.Single(store.List());
    }

    [Fact]
    public void Outbox_RecordFailure_Keeps_Row_And_Accumulates_Attempts()
    {
        var store = new AlertOutboxStore(_db.Factory);
        store.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("标题", "内容"), _clock.GetUtcNow());
        var entry = store.PeekOldest()!;

        _clock.Advance(TimeSpan.FromSeconds(30));
        store.RecordFailure(entry.Id, "napcat 返回 500", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(30));
        store.RecordFailure(entry.Id, "连接被拒绝", _clock.GetUtcNow());

        var after = store.PeekOldest()!;
        Assert.Equal(2, after.Attempts);
        Assert.Equal("连接被拒绝", after.LastError);
        Assert.Single(store.List());
    }

    [Fact]
    public void Outbox_Rows_Survive_New_Store_Instance_On_Same_Database()
    {
        var store = new AlertOutboxStore(_db.Factory);
        store.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("断线期间", "告警一"), _clock.GetUtcNow());
        store.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("断线期间", "告警二"), _clock.GetUtcNow());
        store.RecordFailure(store.PeekOldest()!.Id, "不可用", _clock.GetUtcNow());

        // 模拟面板重启：同一数据库上的新实例必须看到全部待发行（无丢失契约）
        var reopened = new AlertOutboxStore(_db.Factory);
        Assert.Equal(2, reopened.List().Count());
        Assert.Equal("告警一", reopened.PeekOldest()!.Message.Content);
        Assert.Equal(1, reopened.PeekOldest()!.Attempts);
    }

    [Fact]
    public void Settings_RoundTrip_Preserves_Napcat_Fields()
    {
        var store = new AlertSettingsStore(_db.Factory);
        Assert.Equal(new AlertDeliverySettings(null, null, null, null), store.Get());

        store.Save(new AlertDeliverySettings("http://127.0.0.1:3000", "secret-token", "group", "123456"));
        Assert.Equal(new AlertDeliverySettings("http://127.0.0.1:3000", "secret-token", "group", "123456"), store.Get());

        store.Save(new AlertDeliverySettings("http://127.0.0.1:3001", null, "private", "10001"));
        var updated = store.Get();
        Assert.Equal("http://127.0.0.1:3001", updated.NapcatBaseUrl);
        Assert.Equal("private", updated.NapcatTargetType);
        Assert.Equal("10001", updated.NapcatTargetId);
    }

    [Fact]
    public void State_Store_Set_Get_Delete_RoundTrip()
    {
        var store = new AlertStateStore(_db.Factory);
        Assert.Null(store.Get("offline:1"));

        store.Set("offline:1", """{"alertedAtUtc":"2026-09-04T08:00:00.0000000+00:00"}""", _clock.GetUtcNow());
        Assert.NotNull(store.Get("offline:1"));

        store.Delete("offline:1");
        Assert.Null(store.Get("offline:1"));
    }
}
