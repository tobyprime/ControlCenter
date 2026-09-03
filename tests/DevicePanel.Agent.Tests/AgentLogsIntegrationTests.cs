using System.Collections.Concurrent;
using System.Net.WebSockets;
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
/// 日志通道端到端集成测试：AgentRunner 接入内建 Kestrel WS 服务（模拟面板），
/// 验证 logs.services.request / logs.tail.request 的请求-响应往返（seq 沿用请求），
/// 以及慢日志请求执行期间心跳/指标节拍不中断（TOB-338 回归契约在日志路径上的延伸）。
/// </summary>
public class AgentLogsIntegrationTests
{
    [Fact]
    public async Task Logs_Request_Roundtrips_Without_Stalling_Heartbeat()
    {
        using var server = new PanelStubServer();
        await server.StartAsync();

        var options = new AgentOptions
        {
            Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
            Token = "dpk_test",
            HeartbeatIntervalSeconds = 1,
        };
        // 假日志源：模拟 journalctl 慢执行（1.5s），执行期间消息循环必须照常发心跳
        var source = new SlowLogsSource(TimeSpan.FromSeconds(1.5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = new AgentRunner(options, new StringWriter(), new FixedCollector(),
            logsChannelFactory: downlink => new LogsChannel((ILogsDownlink)downlink, source));
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));

        await server.WaitHeartbeatAsync();

        // 服务清单往返
        var servicesSeq = 11L;
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.LogsServicesRequest, servicesSeq));
        var servicesReply = await server.WaitReceiveAsync(AgentMessageTypes.LogsServicesResponse);
        Assert.Equal(servicesSeq, servicesReply.Seq);
        Assert.Equal("nginx.service", servicesReply.Payload.GetProperty("services")[0].GetProperty("name").GetString());

        // 尾部拉取往返：seq 沿用请求
        var tailSeq = 12L;
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.LogsTailRequest, tailSeq,
            JsonSerializer.SerializeToElement(new { service = "nginx.service", kind = "systemd", lines = 50 })));
        var tailReply = await server.WaitReceiveAsync(AgentMessageTypes.LogsTailResponse);
        Assert.Equal(tailSeq, tailReply.Seq);
        Assert.Equal(("2026-02-02T02:40:00.000Z", "info", "ready"), (
            tailReply.Payload.GetProperty("lines")[0].GetProperty("ts").GetString(),
            tailReply.Payload.GetProperty("lines")[0].GetProperty("level").GetString(),
            tailReply.Payload.GetProperty("lines")[0].GetProperty("message").GetString()));

        // 慢请求不破坏节拍：期间心跳持续
        var envelopes = server.ReceivedSnapshot();
        Assert.True(envelopes.Count(e => e.Type == AgentMessageTypes.Heartbeat) >= 2,
            $"日志请求执行期间心跳中断：{Describe(envelopes)}");

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
    public async Task Logs_Source_Failure_Responds_Error_Without_Breaking_Loop()
    {
        using var server = new PanelStubServer();
        await server.StartAsync();

        var options = new AgentOptions
        {
            Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
            Token = "dpk_test",
            HeartbeatIntervalSeconds = 1,
        };
        var source = new SlowLogsSource(TimeSpan.Zero, throwOnTail: new InvalidOperationException("journalctl 失败"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runner = new AgentRunner(options, new StringWriter(), new FixedCollector(),
            logsChannelFactory: downlink => new LogsChannel((ILogsDownlink)downlink, source));
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));

        await server.WaitHeartbeatAsync();
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.LogsTailRequest, 21,
            JsonSerializer.SerializeToElement(new { service = "ghost.service", kind = "systemd", lines = 10 })));

        var error = await server.WaitReceiveAsync(AgentMessageTypes.LogsError);
        Assert.Equal(21, error.Seq);
        Assert.Contains("journalctl 失败", error.Payload.GetProperty("message").GetString());

        // 节拍仍在：错误后继续有心跳
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (server.ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.Heartbeat) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, $"错误后心跳中断：{Describe(server.ReceivedSnapshot())}");
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

    /// <summary>慢速假日志源：模拟外部命令耗时；可注入尾部拉取异常。</summary>
    private sealed class SlowLogsSource : ILogsSource
    {
        private readonly TimeSpan _delay;
        private readonly Exception? _throwOnTail;

        public SlowLogsSource(TimeSpan delay, Exception? throwOnTail = null)
        {
            _delay = delay;
            _throwOnTail = throwOnTail;
        }

        public IReadOnlyList<LogService> ListServices()
        {
            Thread.Sleep(_delay);
            return [new LogService("nginx.service", LogsSourceNames.KindSystemd, "web server")];
        }

        public IReadOnlyList<LogLine> ReadTail(string service, string kind, int lines)
        {
            Thread.Sleep(_delay);
            if (_throwOnTail is not null)
            {
                throw _throwOnTail;
            }

            return [new LogLine("2026-02-02T02:40:00.000Z", "info", "ready")];
        }
    }

    /// <summary>
    /// 面板桩：接受 agent 接入，记录全部入站信封，支持下发信封。
    /// </summary>
    private sealed class PanelStubServer : IDisposable
    {
        private readonly ConcurrentQueue<AgentEnvelope> _received = new();
        private readonly ConcurrentQueue<WebSocket> _connections = new();
        private WebApplication? _app;

        public int Port { get; private set; }

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
                            var reply = AgentEnvelope.Create(AgentMessageTypes.AuthOk, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { deviceId = 7, name = "日志集成测试" }));
                            await socket.SendAsync(
                                JsonSerializer.SerializeToUtf8Bytes(reply, ProtocolJsonContext.Default.AgentEnvelope),
                                WebSocketMessageType.Text, true, CancellationToken.None);
                        }

                        _received.Enqueue(envelope);
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
