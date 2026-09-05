using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Targets;
using DevicePanel.Web.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 终端中继收尾路径单元测试（审查问题 2/4）：
/// - 问题 2：浏览器关闭与发送并发失败时，登记表必须兜底移除，异常不得外泄
/// - 问题 4：订阅前通道已被替换/移除的窗口，会话须按 connection-lost 立即收尾
/// </summary>
public class TerminalRelayTests : IDisposable
{
    private readonly TempSqliteDatabase _database = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
    private readonly TerminalSessionRegistry _registry = new();
    private readonly AgentConnectionRegistry _connections = new();
    private readonly ITerminalStore _store;
    private readonly long _deviceId;

    public TerminalRelayTests()
    {
        _store = new TerminalStore(_database.Factory);
        // terminal_sessions.device_id 带 FK：留痕测试需真实设备行
        var targets = new TargetRegistry(_database.Factory, _clock);
        _deviceId = targets.Create(TargetTypes.Device, "中继测试设备", []).Id;
    }

    public void Dispose() => _database.Dispose();

    private TerminalRelay CreateRelay(
        string sessionId,
        FakeAgentChannel channel,
        FakeBrowserSocket browser)
    {
        return new TerminalRelay(
            sessionId,
            _deviceId,
            "admin",
            80,
            24,
            channel,
            browser,
            _store,
            _registry,
            _connections,
            _clock,
            NullLogger.Instance);
    }

    [Fact]
    public async Task Close_Path_Always_Unregisters_Even_When_Browser_Close_Conflicts_With_Send()
    {
        // 浏览器 Close 与在途发送并发时（托管 WS 抛 InvalidOperationException），
        // 旧实现 catch(WebSocketException) 漏接 → TryRemove 被跳过 → 登记表泄漏
        var channel = new FakeAgentChannel();
        var browser = new FakeBrowserSocket
        {
            ReceiveReturnsClose = true,
            CloseException = new InvalidOperationException("已有一个未完成的发送操作"),
        };
        _connections.TryAdd(_deviceId, channel); // 在线通道正常登记：本用例走 operator 收尾路径
        var relay = CreateRelay("s1", channel, browser);
        Assert.True(_registry.TryAdd(relay));

        var run = relay.RunAsync(); // 泵读到浏览器关闭帧 → finally 按 operator 收尾 → Close 抛 IOE
        var exception = await Record.ExceptionAsync(() => run);

        Assert.Null(exception); // 收尾异常不得冲出主循环
        Assert.False(_registry.Contains("s1"), "登记表条目必须兜底移除");
        var session = _store.GetSession("s1");
        Assert.NotNull(session);
        Assert.Equal(TerminalCloseReasons.Operator, session!.CloseReason);
        // term.close 仍须发往 agent（浏览器先走的场景）
        Assert.Contains(channel.Sent, s => s.Type == AgentMessageTypes.TermClose);
    }

    [Fact]
    public async Task Agent_Channel_Already_Gone_At_Run_Start_Closes_Session_As_ConnectionLost()
    {
        // 端点 GetChannel 之后、RunAsync 订阅事件之前 agent 恰好断开：
        // 注册表当前登记的已不是本会话的通道 → 应按 connection-lost 立即收尾，不悬挂
        var staleChannel = new FakeAgentChannel(deviceId: _deviceId, isOpen: false);
        var currentChannel = new FakeAgentChannel(deviceId: _deviceId, isOpen: true);
        _connections.TryAdd(_deviceId, currentChannel); // 注册表已登记"新"通道

        var browser = new FakeBrowserSocket(); // ReceiveAsync 永远挂起：若未走断连分支将悬挂
        var relay = CreateRelay("s2", staleChannel, browser);
        Assert.True(_registry.TryAdd(relay));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = relay.RunAsync();
        var finished = await Task.WhenAny(run, Task.Delay(Seconds(4), cts.Token));

        Assert.Equal(run, finished);
        await run;
        Assert.Equal(TerminalCloseReasons.ConnectionLost, _store.GetSession("s2")!.CloseReason);
        Assert.False(_registry.Contains("s2"));
    }

    [Fact]
    public async Task Agent_Channel_Removed_While_Session_Running_Notifies_And_Cleans_Up()
    {
        // 会话进行中通道被移除（删设备/心跳超时路径）→ 浏览器收到 closed、登记表清空
        var channel = new FakeAgentChannel(deviceId: _deviceId, isOpen: true);
        _connections.TryAdd(_deviceId, channel);
        var browser = new FakeBrowserSocket();
        var relay = CreateRelay("s3", channel, browser);
        Assert.True(_registry.TryAdd(relay));

        var run = relay.RunAsync();
        // 等待泵挂起后模拟连接移除（断开事件）
        await Task.Delay(100);
        _connections.Remove(_deviceId, channel);
        await run;

        Assert.Equal(TerminalCloseReasons.ConnectionLost, _store.GetSession("s3")!.CloseReason);
        Assert.False(_registry.Contains("s3"));
        Assert.Contains(
            browser.Sent,
            sent => JsonSerializer.Deserialize<JsonElement>(sent.Span).TryGetProperty("type", out var t) && t.GetString() == "closed");
    }

    private static TimeSpan Seconds(int v) => TimeSpan.FromSeconds(v);
}
