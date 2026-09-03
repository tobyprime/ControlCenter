using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevicePanel.Agent;
using DevicePanel.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// 真实 PTY 终端端到端集成测试：AgentRunner 接入内建 Kestrel WS 服务（模拟面板），
/// 验证 term.open → 真 shell → term.input/term.output 往返、term.close 收尾，
/// 以及终端会话期间心跳/指标节拍不中断（TOB-338 回归契约在终端路径上的延伸）。
/// </summary>
public class AgentTerminalIntegrationTests
{
    [Fact]
    public async Task Terminal_Session_Runs_Real_Shell_And_Closes_Cleanly()
    {
        using var server = new PanelStubServer();
        await server.StartAsync();

        var options = new AgentOptions
        {
            Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
            Token = "dpk_test",
            HeartbeatIntervalSeconds = 1,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = new AgentRunner(options, new StringWriter());
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));

        // 等待 agent 接入（首个心跳），随后下发 term.open
        await server.WaitHeartbeatAsync();

        var sessionId = "it-" + Guid.NewGuid().ToString("N");
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.TermOpen, 1,
            JsonSerializer.SerializeToElement(new { sessionId, cols = 120, rows = 30 })));
        var opened = await server.WaitReceiveAsync(AgentMessageTypes.TermOpened);

        Assert.Equal(sessionId, opened.Payload.GetProperty("sessionId").GetString());

        // 真实 shell：echo 往返
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.TermInput, 2,
            JsonSerializer.SerializeToElement(new
            {
                sessionId,
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes("echo tp339_$((6*7))_ok\n")),
            })));

        var output = await server.WaitOutputAsync(sessionId, text => text.Contains("tp339_42_ok"), TimeSpan.FromSeconds(10));
        Assert.Contains("tp339_42_ok", output);

        // 关闭：term.close → shell 退出 → term.closed
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.TermClose, 3,
            JsonSerializer.SerializeToElement(new { sessionId })));
        var closed = await server.WaitReceiveAsync(AgentMessageTypes.TermClosed);
        Assert.Equal(sessionId, closed.Payload.GetProperty("sessionId").GetString());

        // 终端会话不破坏节拍：期间心跳持续
        var envelopes = server.ReceivedSnapshot();
        Assert.True(envelopes.Count(e => e.Type == AgentMessageTypes.Heartbeat) >= 2,
            $"终端会话期间心跳中断：{Describe(envelopes)}");

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Terminal_Session_On_Unsupported_Platform_Reports_Error_Without_Breaking_Loop()
    {
        // 打开失败（工厂抛异常）→ term.error 回面板，连接与会话循环不受影响
        using var server = new PanelStubServer();
        await server.StartAsync();

        var options = new AgentOptions
        {
            Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
            Token = "dpk_test",
            HeartbeatIntervalSeconds = 1,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runner = new AgentRunner(options, new StringWriter(), new FixedCollector(),
            downlink => new TerminalChannel(downlink, new ThrowingPtyFactory()));
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));

        await server.WaitHeartbeatAsync();
        var sessionId = "err-" + Guid.NewGuid().ToString("N");
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.TermOpen, 1,
            JsonSerializer.SerializeToElement(new { sessionId, cols = 80, rows = 24 })));

        var error = await server.WaitReceiveAsync(AgentMessageTypes.TermError);
        Assert.Equal(sessionId, error.Payload.GetProperty("sessionId").GetString());

        // 节拍仍在：错误后继续有心跳（按截止时间轮询，不写死等待时长）
        var errorDeadline = DateTime.UtcNow.AddSeconds(4);
        var heartbeatCount = 0;
        while (heartbeatCount < 2)
        {
            heartbeatCount = server.ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.Heartbeat);
            if (heartbeatCount >= 2)
            {
                break;
            }

            Assert.True(DateTime.UtcNow < errorDeadline, $"错误后心跳中断：{Describe(server.ReceivedSnapshot())}");
            await Task.Delay(100, CancellationToken.None);
        }

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Describe(List<AgentEnvelope> envelopes) =>
        string.Join(",", envelopes.Select(e => e.Type));

    private sealed class FixedCollector : IMetricsCollector
    {
        public MetricsSample Sample() => new(1, 2, 3, 4, 5);
    }

    private sealed class ThrowingPtyFactory : IPtySessionFactory
    {
        public IPtySession Create(int cols, int rows) => throw new InvalidOperationException("无可用 PTY");
    }

    /// <summary>
    /// 面板桩：接受 agent 接入，记录全部入站信封，支持下发信封与按条件聚合终端输出。
    /// </summary>
    private sealed class PanelStubServer : IDisposable
    {
        private readonly ConcurrentQueue<AgentEnvelope> _received = new();
        private readonly ConcurrentQueue<WebSocket> _connections = new();
        private WebApplication? _app;
        private int _authCount;

        public int Port { get; private set; }

        public int AuthCount => _authCount;

        public List<AgentEnvelope> ReceivedSnapshot() => _received.ToList();

        public async Task WaitHeartbeatAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.Heartbeat) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "等待首个心跳超时");
                await Task.Delay(50, CancellationToken.None);
            }
        }

        public async Task<AgentEnvelope> WaitReceiveAsync(string type)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var match = _received.FirstOrDefault(e => e.Type == type);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(50, CancellationToken.None);
            }

            throw new TimeoutException($"等待 {type} 超时（已收到：{Describe(ReceivedSnapshot())}）");
        }

        /// <summary>聚合某会话的 term.output（含等待新帧），直到谓词满足。</summary>
        public async Task<string> WaitOutputAsync(string sessionId, Func<string, bool> predicate, TimeSpan timeout)
        {
            var accumulated = new List<string>();
            var consumed = 0;
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var snapshots = ReceivedSnapshot()
                    .Where(e => e.Type == AgentMessageTypes.TermOutput)
                    .ToList();
                for (var i = consumed; i < snapshots.Count; i++)
                {
                    consumed = i + 1;
                    var payload = snapshots[i].Payload;
                    if (payload.ValueKind == JsonValueKind.Object &&
                        payload.TryGetProperty("sessionId", out var sid) &&
                        sid.GetString() == sessionId &&
                        payload.TryGetProperty("data", out var data))
                    {
                        var text = Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString() ?? string.Empty));
                        accumulated.Add(text);
                        if (predicate(string.Concat(accumulated)))
                        {
                            return string.Concat(accumulated);
                        }
                    }
                }

                await Task.Delay(50, CancellationToken.None);
            }

            throw new TimeoutException($"等待终端输出超时（已累积：{string.Concat(accumulated)}）");
        }

        public async Task SendDownstreamAsync(AgentEnvelope envelope)
        {
            var socket = _connections.FirstOrDefault(s => s.State == WebSocketState.Open)
                ?? throw new InvalidOperationException("没有在线连接可下发");
            await socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var received = _received;
            var app = builder.Build();
            app.UseWebSockets();
            app.MapGet("/agent/ws", async (HttpContext http) =>
            {
                var socket = await http.WebSockets.AcceptWebSocketAsync();
                _connections.Enqueue(socket);
                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    var receivedBytes = 0;
                    try
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer, receivedBytes, buffer.Length - receivedBytes),
                                CancellationToken.None);
                            receivedBytes += result.Count;
                        }
                        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            return;
                        }

                        var envelope = JsonSerializer.Deserialize(
                            buffer.AsSpan(0, receivedBytes).ToArray(), ProtocolJsonContext.Default.AgentEnvelope);
                        if (envelope is null)
                        {
                            continue;
                        }

                        if (envelope.Type == AgentMessageTypes.Auth)
                        {
                            Interlocked.Increment(ref _authCount);
                            var reply = AgentEnvelope.Create(AgentMessageTypes.AuthOk, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { deviceId = 7, name = "终端集成测试" }));
                            await socket.SendAsync(
                                JsonSerializer.SerializeToUtf8Bytes(reply, ProtocolJsonContext.Default.AgentEnvelope),
                                WebSocketMessageType.Text, true, CancellationToken.None);
                        }

                        received.Enqueue(envelope);
                    }
                    catch (WebSocketException)
                    {
                        return;
                    }
                }
            });
            _app = app;
            await _app.StartAsync();
            Port = new Uri(_app.Urls.First()).Port;
        }

        public void Dispose() => _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
