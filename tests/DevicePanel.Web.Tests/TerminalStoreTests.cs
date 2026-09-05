using DevicePanel.Web.Collectors;
using DevicePanel.Web.Terminal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class TerminalStoreTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempSqliteDatabase _database = new();
    private readonly FakeTimeProvider _clock = new(Base);
    private readonly CollectorRegistry _targets;
    private readonly TerminalStore _store;
    private readonly long _deviceId;

    public TerminalStoreTests()
    {
        _targets = new CollectorRegistry(_database.Factory, _clock);
        _store = new TerminalStore(_database.Factory);
        _deviceId = _targets.Create("终端设备", [CollectorBuiltinTags.Device]).Id;
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void OpenSession_Persists_Metadata_And_Query_Returns_It()
    {
        _store.OpenSession("s1", _deviceId, "admin", Base);

        var sessions = _store.QuerySessions(_deviceId, null, null);
        var session = Assert.Single(sessions);
        Assert.Equal("s1", session.Id);
        Assert.Equal(_deviceId, session.DeviceId);
        Assert.Equal("admin", session.Operator);
        Assert.Equal(Base, session.OpenedAtUtc);
        Assert.Null(session.ClosedAtUtc);
        Assert.Null(session.CloseReason);
    }

    [Fact]
    public void Append_Persists_Input_And_Output_Entries_In_Order()
    {
        _store.OpenSession("s1", _deviceId, "admin", Base);
        _store.Append("s1", TerminalEntryDirections.Input, "ls -l\n", Base.AddSeconds(1));
        _store.Append("s1", TerminalEntryDirections.Output, "total 0\n", Base.AddSeconds(2));

        var entries = _store.QueryEntries("s1");
        Assert.Equal(2, entries.Count);
        Assert.Equal(TerminalEntryDirections.Input, entries[0].Direction);
        Assert.Equal("ls -l\n", entries[0].Data);
        Assert.Equal(Base.AddSeconds(1), entries[0].RecordedAtUtc);
        Assert.Equal(TerminalEntryDirections.Output, entries[1].Direction);
        Assert.Equal("total 0\n", entries[1].Data);
    }

    [Fact]
    public void CloseSession_Records_End_Time_And_Reason()
    {
        _store.OpenSession("s1", _deviceId, "admin", Base);

        _store.CloseSession("s1", Base.AddMinutes(5), TerminalCloseReasons.Operator);

        var session = Assert.Single(_store.QuerySessions(_deviceId, null, null));
        Assert.Equal(Base.AddMinutes(5), session.ClosedAtUtc);
        Assert.Equal(TerminalCloseReasons.Operator, session.CloseReason);
    }

    [Fact]
    public void QuerySessions_Filters_By_Device_And_Time_Range()
    {
        _store.OpenSession("s1", _deviceId, "admin", Base);
        var otherDeviceId = _targets.Create("另一台", [CollectorBuiltinTags.Device]).Id;
        _store.OpenSession("s2", otherDeviceId, "admin", Base.AddMinutes(1));
        _store.OpenSession("s3", _deviceId, "admin", Base.AddMinutes(30));

        Assert.Single(_store.QuerySessions(otherDeviceId, null, null));

        // 时间窗过滤按 opened_at_utc
        var window = _store.QuerySessions(null, Base.AddMinutes(10), Base.AddMinutes(40));
        var inWindow = Assert.Single(window);
        Assert.Equal("s3", inWindow.Id);

        Assert.Equal(2, _store.QuerySessions(_deviceId, null, null).Count);
    }

    [Fact]
    public void QuerySessions_Returns_Newest_First()
    {
        _store.OpenSession("older", _deviceId, "admin", Base);
        _store.OpenSession("newer", _deviceId, "admin", Base.AddMinutes(1));

        var sessions = _store.QuerySessions(_deviceId, null, null);

        Assert.Equal("newer", sessions[0].Id);
        Assert.Equal("older", sessions[1].Id);
    }

    [Fact]
    public void Delete_Device_Cascades_Sessions_And_Entries()
    {
        _store.OpenSession("s1", _deviceId, "admin", Base);
        _store.Append("s1", TerminalEntryDirections.Input, "echo hi", Base.AddSeconds(1));

        _targets.Delete(_deviceId);

        Assert.Empty(_store.QuerySessions(_deviceId, null, null));
        Assert.Empty(_store.QueryEntries("s1"));
    }
}
