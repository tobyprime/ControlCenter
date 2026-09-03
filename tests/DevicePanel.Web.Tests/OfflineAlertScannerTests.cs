using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>离线告警规则测试：复用 TOB-337 离线判定（连续 2 个心跳周期），状态转换驱动、防重发、恢复重开。</summary>
public class OfflineAlertScannerTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));
    private readonly DeviceRegistry _devices;
    private readonly AlertOutboxStore _outbox;
    private readonly OfflineAlertScanner _scanner;
    private readonly AgentOptions _agentOptions = new();

    public OfflineAlertScannerTests()
    {
        _devices = new DeviceRegistry(_db.Factory, _clock);
        _outbox = new AlertOutboxStore(_db.Factory);
        _scanner = new OfflineAlertScanner(
            _devices,
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            new AlertStateStore(_db.Factory),
            _agentOptions,
            new AlertOptions(),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OfflineAlertScanner>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Device_Going_Offline_Alerts_Once_With_Device_Name()
    {
        var deviceId = await CreateOnlineDeviceAsync("边界路由");

        // 复用离线判定：连续 2 个心跳周期（默认 60s）无心跳即离线
        _clock.Advance(_agentOptions.OfflineAfter + TimeSpan.FromSeconds(1));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, _outbox.Count());
        var alert = _outbox.PeekOldest()!;
        Assert.Contains("边界路由", alert.Message.Content);
        Assert.Equal(NapcatNotifier.ChannelNameValue, alert.Channel);

        // 持续离线：不重复发送
        _clock.Advance(TimeSpan.FromMinutes(30));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, _outbox.Count());
    }

    [Fact]
    public async Task Device_Coming_Back_Online_Clears_State_And_Next_Offline_Alerts_Again()
    {
        var deviceId = await CreateOnlineDeviceAsync("NAS-01");

        _clock.Advance(_agentOptions.OfflineAfter + TimeSpan.FromSeconds(1));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, _outbox.Count());

        // 恢复在线
        _devices.Touch(deviceId, _clock.GetUtcNow());
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, _outbox.Count());

        // 再次离线：新事件重新告警
        _clock.Advance(_agentOptions.OfflineAfter + TimeSpan.FromSeconds(1));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, _outbox.Count());
    }

    [Fact]
    public async Task Device_Never_Seen_Does_Not_Alert()
    {
        _devices.Create("从未接入的设备", ["待部署"]);
        _clock.Advance(TimeSpan.FromHours(1));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(0, _outbox.Count());
    }

    [Fact]
    public async Task Online_Device_Does_Not_Alert()
    {
        await CreateOnlineDeviceAsync("在线设备");
        _clock.Advance(TimeSpan.FromSeconds(30));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(0, _outbox.Count());
    }

    [Fact]
    public async Task Restart_Does_Not_ReAlert_Still_Offline_Device()
    {
        await CreateOnlineDeviceAsync("重启存活设备");
        _clock.Advance(_agentOptions.OfflineAfter + TimeSpan.FromSeconds(1));
        await _scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, _outbox.Count());

        var restarted = new OfflineAlertScanner(
            _devices,
            new AlertDispatcher(_outbox, [new StubNotifier()]),
            new AlertStateStore(_db.Factory),
            _agentOptions,
            new AlertOptions(),
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OfflineAlertScanner>.Instance);
        await restarted.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(1, _outbox.Count());
    }

    private async Task<long> CreateOnlineDeviceAsync(string name)
    {
        var device = _devices.Create(name, ["机房X"]);
        _devices.Touch(device.Device.Id, _clock.GetUtcNow());
        await Task.CompletedTask;
        return device.Device.Id;
    }

    private sealed class StubNotifier : INotifier
    {
        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
