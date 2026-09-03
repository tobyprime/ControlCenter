using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Auth;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 浏览器 ↔ 面板 ↔ agent 终端中继集成测试：
/// 假 agent 用测试版 WS 客户端接入 /agent/ws，浏览器侧连 /api/devices/{id}/terminal（会话 Cookie 认证）。
/// </summary>
public class TerminalWsTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            TestServices = services => services.AddSingleton<TimeProvider>(Clock);
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Open_Sends_term_open_To_Agent_And_Confirms_To_Browser()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);

        var opened = await ReceiveBrowserMessageAsync(browser);

        Assert.Equal("opened", opened.GetProperty("type").GetString());
        var sessionId = opened.GetProperty("sessionId").GetString()!;
        Assert.False(string.IsNullOrEmpty(sessionId));

        var termOpen = await agent.ReceiveUntilAsync(AgentMessageTypes.TermOpen);
        Assert.Equal(sessionId, termOpen.Payload.GetProperty("sessionId").GetString());
        Assert.True(termOpen.Payload.GetProperty("cols").GetInt32() > 0);
        Assert.True(termOpen.Payload.GetProperty("rows").GetInt32() > 0);

        var session = Assert.Single(Store().QuerySessions(deviceId, null, null));
        Assert.Equal(sessionId, session.Id);
        Assert.Equal("admin", session.Operator);
        Assert.Null(session.ClosedAtUtc);
    }

    [Fact]
    public async Task Browser_Input_Is_Relayed_As_term_input_And_Session_Closes_On_Disconnect()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        await ReceiveBrowserMessageAsync(browser); // opened
        var sessionId = (await agent.ReceiveUntilAsync(AgentMessageTypes.TermOpen)).Payload.GetProperty("sessionId").GetString()!;

        await SendBrowserMessageAsync(browser, new { type = "input", data = "echo hi\n" });

        var input = await agent.ReceiveUntilAsync(AgentMessageTypes.TermInput);
        Assert.Equal(sessionId, input.Payload.GetProperty("sessionId").GetString());
        var decoded = Convert.FromBase64String(input.Payload.GetProperty("data").GetString()!);
        Assert.Equal("echo hi\n", Encoding.UTF8.GetString(decoded));

        // 浏览器关闭（TestServer 的客户端优雅关闭不产生 Close 帧，用 Abort 模拟 abrupt 断开，
        // 与真实浏览器关标签页一致）：agent 收到 term.close，会话以 operator 关闭
        browser.Abort();
        var close = await agent.ReceiveUntilAsync(AgentMessageTypes.TermClose);
        Assert.Equal(sessionId, close.Payload.GetProperty("sessionId").GetString());

        var session = Assert.Single(Store().QuerySessions(deviceId, null, null));
        Assert.NotNull(session.ClosedAtUtc);
        Assert.Equal(TerminalCloseReasons.Operator, session.CloseReason);
    }

    [Fact]
    public async Task Agent_Output_Is_Relayed_To_Browser_And_Recorded()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        var opened = await ReceiveBrowserMessageAsync(browser);
        var sessionId = opened.GetProperty("sessionId").GetString()!;

        var output = Convert.ToBase64String(Encoding.UTF8.GetBytes("hi\r\n"));
        await agent.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermOutput, 1,
            JsonSerializer.SerializeToElement(new { sessionId, data = output })));

        var relayed = await ReceiveBrowserMessageAsync(browser);
        Assert.Equal("output", relayed.GetProperty("type").GetString());
        Assert.Equal("hi\r\n", relayed.GetProperty("data").GetString());

        var entries = Store().QueryEntries(sessionId);
        var entry = Assert.Single(entries);
        Assert.Equal(TerminalEntryDirections.Output, entry.Direction);
        Assert.Equal("hi\r\n", entry.Data);
    }

    [Fact]
    public async Task Multi_Byte_Utf8_Split_Across_Output_Frames_Is_Reassembled()
    {
        // 审查问题 1：多字节 UTF-8 字符被 term.output 分块切断时，显示与留痕都不得出现 U+FFFD
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        var sessionId = (await ReceiveBrowserMessageAsync(browser)).GetProperty("sessionId").GetString()!;

        // "终端ok" 的 UTF-8 字节在第一个字符中间切开（"终" = E7 BB 88）
        var bytes = Encoding.UTF8.GetBytes("终端ok");
        var frame1 = Convert.ToBase64String(bytes[..2]);
        var frame2 = Convert.ToBase64String(bytes[2..]);
        await agent.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermOutput, 1,
            JsonSerializer.SerializeToElement(new { sessionId, data = frame1 })));
        await agent.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermOutput, 2,
            JsonSerializer.SerializeToElement(new { sessionId, data = frame2 })));

        var outputs = new List<string>();
        while (string.Join("", outputs) != "终端ok")
        {
            var message = await ReceiveBrowserMessageAsync(browser);
            if (message.GetProperty("type").GetString() == "output")
            {
                outputs.Add(message.GetProperty("data").GetString() ?? string.Empty);
            }
        }

        Assert.Equal("终端ok", string.Join("", outputs));
        var parts = Store().QueryEntries(sessionId)
            .Where(e => e.Direction == TerminalEntryDirections.Output)
            .Select(e => e.Data);
        var recorded = string.Join("", parts);
        Assert.Equal("终端ok", recorded);
        Assert.DoesNotContain('\uFFFD', recorded);
    }

    [Fact]
    public async Task Term_Output_From_Other_Device_Channel_Is_Dropped()
    {
        // 审查问题 3：设备 A 的通道不得向设备 B 的会话注入输出/记录
        var (deviceA, agentA) = await ConnectAgentAsync();
        var (deviceB, agentB) = await ConnectAgentAsync();
        using var browserB = await ConnectBrowserAsync(deviceB);
        var sessionB = (await ReceiveBrowserMessageAsync(browserB)).GetProperty("sessionId").GetString()!;

        var injected = Convert.ToBase64String(Encoding.UTF8.GetBytes("injected"));
        await agentA.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermOutput, 1,
            JsonSerializer.SerializeToElement(new { sessionId = sessionB, data = injected })));

        // 合法输出（来自 B 自己的通道）正常到达：证明通道活着，且注入被丢弃
        var legit = Convert.ToBase64String(Encoding.UTF8.GetBytes("legit"));
        await agentB.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermOutput, 2,
            JsonSerializer.SerializeToElement(new { sessionId = sessionB, data = legit })));

        var received = await ReceiveBrowserMessageAsync(browserB);
        Assert.Equal("output", received.GetProperty("type").GetString());
        Assert.Equal("legit", received.GetProperty("data").GetString());

        var recorded = Store().QueryEntries(sessionB)
            .Where(e => e.Direction == TerminalEntryDirections.Output)
            .Select(e => e.Data);
        Assert.DoesNotContain("injected", string.Join("", recorded));
    }

    [Fact]
    public async Task Oversized_Browser_Frame_Does_Not_Kill_Session()
    {
        // 审查问题 7：超过单帧上限的输入（如一次性粘贴大文本）应被丢弃并告警，不得终止会话
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        await ReceiveBrowserMessageAsync(browser); // opened

        var bigInput = new string('a', 300 * 1024); // 一帧 ~300KB，超出 256KB 单帧上限
        await SendBrowserMessageAsync(browser, new { type = "input", data = bigInput });
        await SendBrowserMessageAsync(browser, new { type = "input", data = "alive" });

        var input = await agent.ReceiveUntilAsync(AgentMessageTypes.TermInput);
        Assert.Equal("alive", Encoding.UTF8.GetString(Convert.FromBase64String(input.Payload.GetProperty("data").GetString()!)));

        // 后续输入仍可中继：会话未死
        await SendBrowserMessageAsync(browser, new { type = "input", data = "still" });
        var next = await agent.ReceiveUntilAsync(AgentMessageTypes.TermInput);
        Assert.Equal("still", Encoding.UTF8.GetString(Convert.FromBase64String(next.Payload.GetProperty("data").GetString()!)));
    }

    [Fact]
    public async Task Browser_Resize_Is_Relayed_As_term_resize()
    {
        // 审查问题 5：浏览器窗口尺寸变更 → term.resize 调整 PTY winsize
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        await ReceiveBrowserMessageAsync(browser); // opened

        await SendBrowserMessageAsync(browser, new { type = "resize", cols = 150, rows = 48 });

        var resize = await agent.ReceiveUntilAsync(AgentMessageTypes.TermResize);
        Assert.Equal(150, resize.Payload.GetProperty("cols").GetInt32());
        Assert.Equal(48, resize.Payload.GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task Agent_Exit_Notifies_Browser_And_Closes_Session()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        var sessionId = (await ReceiveBrowserMessageAsync(browser)).GetProperty("sessionId").GetString()!;

        await agent.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermClosed, 2,
            JsonSerializer.SerializeToElement(new { sessionId })));

        var closed = await ReceiveBrowserMessageAsync(browser);
        Assert.Equal("closed", closed.GetProperty("type").GetString());
        var drain = await ReceiveCloseAsync(browser);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, drain.CloseStatus);

        var session = Assert.Single(Store().QuerySessions(deviceId, null, null));
        Assert.Equal(TerminalCloseReasons.AgentExit, session.CloseReason);
    }

    [Fact]
    public async Task Agent_Disconnect_Closes_Session_With_ConnectionLost()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        var sessionId = (await ReceiveBrowserMessageAsync(browser)).GetProperty("sessionId").GetString()!;

        agent.Abort();

        var closed = await ReceiveBrowserMessageAsync(browser);
        Assert.Equal("closed", closed.GetProperty("type").GetString());

        var session = Assert.Single(Store().QuerySessions(deviceId, null, null));
        Assert.Equal(TerminalCloseReasons.ConnectionLost, session.CloseReason);
    }

    [Fact]
    public async Task Agent_Open_Failure_Notifies_Browser_With_Error_And_Closes_Session()
    {
        var (deviceId, agent) = await ConnectAgentAsync();
        using var browser = await ConnectBrowserAsync(deviceId);
        var sessionId = (await ReceiveBrowserMessageAsync(browser)).GetProperty("sessionId").GetString()!;

        await agent.SendEnvelopeAsync(AgentEnvelope.Create(AgentMessageTypes.TermError, 3,
            JsonSerializer.SerializeToElement(new { sessionId, message = "打开 PTY 失败" })));

        var error = await ReceiveBrowserMessageAsync(browser);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("打开 PTY 失败", error.GetProperty("message").GetString());

        var session = Assert.Single(Store().QuerySessions(deviceId, null, null));
        Assert.Equal(TerminalCloseReasons.Error, session.CloseReason);
    }

    [Fact]
    public async Task Offline_Device_Is_Rejected()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);

        var exception = await Record.ExceptionAsync(() => ConnectBrowserAsync(created.Id));

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task Storage_Failure_Does_Not_Kill_Terminal_Session()
    {
        var factory = new Factory();
        try
        {
            factory.TestServices = services =>
            {
                services.AddSingleton<TimeProvider>(factory.Clock);
                services.AddSingleton<ITerminalStore, ThrowingTerminalStore>();
            };
            var (deviceId, agent) = await ConnectAgentAsync(factory);
            using var browser = await ConnectBrowserAsync(deviceId, factory);
            var sessionId = (await ReceiveBrowserMessageAsync(browser)).GetProperty("sessionId").GetString()!;

            await SendBrowserMessageAsync(browser, new { type = "input", data = "echo broken\n" });
            var input = await agent.ReceiveUntilAsync(AgentMessageTypes.TermInput);

            Assert.Equal(sessionId, input.Payload.GetProperty("sessionId").GetString());
        }
        finally
        {
            factory.Dispose();
        }
    }

    private ITerminalStore Store() => _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ITerminalStore>();

    private async Task<HttpClient> AuthenticatedClientAsync(Factory? factory = null) =>
        (await LoginAsync(factory)).Client;

    private async Task<string> LoginCookieAsync(Factory? factory = null)
    {
        var (_, cookie) = await LoginAsync(factory);
        return cookie;
    }

    private async Task<(HttpClient Client, string Cookie)> LoginAsync(Factory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        return (client, setCookie.Split(';')[0]);
    }

    private static async Task<(long Id, string AgentToken)> CreateDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/devices", new { name = "终端设备", tags = new[] { "机房A" } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    /// <summary>创建设备并把假 agent 接入 /agent/ws，返回设备 ID 与假 agent 句柄。</summary>
    private async Task<(long DeviceId, FakeAgent Agent)> ConnectAgentAsync(Factory? factory = null)
    {
        var client = await AuthenticatedClientAsync(factory);
        var (deviceId, token) = await CreateDeviceAsync(client);
        var agent = await FakeAgent.ConnectAsync(factory ?? _factory, token);
        return (deviceId, agent);
    }

    private async Task<WebSocket> ConnectBrowserAsync(long deviceId, Factory? factory = null)
    {
        var cookie = await LoginCookieAsync(factory);
        Assert.StartsWith("device_panel_session=", cookie);

        var wsClient = (factory ?? _factory).Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
        var uri = new Uri((factory ?? _factory).Server.BaseAddress, $"/api/devices/{deviceId}/terminal");
        return await wsClient.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveBrowserMessageAsync(WebSocket browser)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var endOfMessage = false;
        var received = 0;
        while (!endOfMessage)
        {
            var result = await browser.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cts.Token);
            received += result.Count;
            endOfMessage = result.EndOfMessage;
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"预期收到浏览器消息，实际连接被关闭：{result.CloseStatus}");
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(buffer, 0, received));
    }

    private static async Task<(WebSocketCloseStatus? CloseStatus, string? Reason)> ReceiveCloseAsync(WebSocket browser)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await browser.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.CloseStatus, result.CloseStatusDescription);
            }
        }
    }

    private static async Task SendBrowserMessageAsync(WebSocket browser, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await browser.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private sealed class ThrowingTerminalStore : ITerminalStore
    {
        public void OpenSession(string sessionId, long deviceId, string operatorName, DateTimeOffset openedAtUtc) => throw new IOException("存储故障");
        public void Append(string sessionId, string direction, string data, DateTimeOffset recordedAtUtc) => throw new IOException("存储故障");
        public void CloseSession(string sessionId, DateTimeOffset closedAtUtc, string closeReason) => throw new IOException("存储故障");
        public IReadOnlyList<TerminalSession> QuerySessions(long? deviceId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc) => throw new IOException("存储故障");
        public TerminalSession? GetSession(string sessionId) => throw new IOException("存储故障");
        public IReadOnlyList<TerminalEntry> QueryEntries(string sessionId) => throw new IOException("存储故障");
    }

    /// <summary>测试内嵌的假 agent：走真实 /agent/ws 通道，term.open 自动回 term.opened，可回发信封。</summary>
    private sealed class FakeAgent : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly List<AgentEnvelope> _received = new();
        private readonly object _lock = new();
        private int _cursor;
        private Task _pump = Task.CompletedTask;

        private FakeAgent(WebSocket socket) => _socket = socket;

        public static async Task<FakeAgent> ConnectAsync(Factory factory, string token)
        {
            var wsClient = factory.Server.CreateWebSocketClient();
            var uri = new Uri(factory.Server.BaseAddress, "/agent/ws");
            var socket = await wsClient.ConnectAsync(uri, CancellationToken.None);
            await SendAsync(socket, new AgentEnvelope
            {
                Type = AgentMessageTypes.Auth,
                Seq = 1,
                Payload = JsonSerializer.SerializeToElement(new { token }),
            });
            var authOk = await ReceiveEnvelopeAsync(socket);
            Assert.Equal("auth.ok", authOk.Type);
            var agent = new FakeAgent(socket);
            agent._pump = agent.PumpAsync();
            return agent;
        }

        private async Task PumpAsync()
        {
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var envelope = await ReceiveEnvelopeAsync(_socket);
                    lock (_lock)
                    {
                        _received.Add(envelope);
                    }

                    // 模拟真实 agent：收到 term.open 后回 term.opened（seq 沿用请求）
                    if (envelope.Type == AgentMessageTypes.TermOpen &&
                        envelope.Payload.ValueKind == JsonValueKind.Object &&
                        envelope.Payload.TryGetProperty("sessionId", out var sessionId))
                    {
                        await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.TermOpened, envelope.Seq,
                            JsonSerializer.SerializeToElement(new { sessionId = sessionId.GetString() })));
                    }
                }
            }
            catch (Exception)
            {
                // 测试收尾（Dispose/Abort）导致的断开，无需处理
            }
        }

        public async Task<AgentEnvelope> ReceiveUntilAsync(string type)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    for (var i = _cursor; i < _received.Count; i++)
                    {
                        if (_received[i].Type == type)
                        {
                            _cursor = i + 1;
                            return _received[i];
                        }
                    }
                }

                await Task.Delay(50, CancellationToken.None);
            }

            throw new TimeoutException($"等待 {type} 超时");
        }

        public async Task SendEnvelopeAsync(AgentEnvelope envelope)
        {
            await SendAsync(_socket, envelope);
        }

        public void Abort() => _socket.Abort();

        public void Dispose()
        {
            _socket.Dispose();
            try
            {
                _pump.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // 泵的收尾异常与断言无关
            }
        }

        private static async Task SendAsync(WebSocket socket, AgentEnvelope envelope)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }

        private static async Task<AgentEnvelope> ReceiveEnvelopeAsync(WebSocket socket)
        {
            var buffer = new byte[16 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var endOfMessage = false;
            var received = 0;
            while (!endOfMessage)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cts.Token);
                received += result.Count;
                endOfMessage = result.EndOfMessage;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException($"agent 预期收到消息，实际连接被关闭：{result.CloseStatus}");
                }
            }

            return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(buffer, 0, received).ToArray(), ProtocolJsonContext.Default.AgentEnvelope)!;
        }
    }
}
