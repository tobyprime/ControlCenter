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
/// AgentRunner 真实 WebSocket 链路集成测试：接入内建 Kestrel WS 服务，
/// 验证心跳节拍不中断连接、metrics.report 按周期发出（回归：ReceiveAsync 超时取消会中止 ClientWebSocket）。
/// </summary>
public class AgentRunnerLoopTests
{
    [Fact]
    public async Task Loop_Sends_Heartbeat_And_Metrics_Every_Period_Without_Dropping_Connection()
    {
        using var server = new RecordingWsServer();
        await server.StartAsync();

        var collector = new FixedCollector(new MetricsSample(11, 22, 33, 4400, 5500));
        var options = new AgentOptions
        {
            Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
            Token = "dpk_test",
            HeartbeatIntervalSeconds = 1,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var runner = new AgentRunner(options, new StringWriter(), collector);

        // 断线会触发重连循环，用 token 之外的方式没有意义——直接跑 RunAsync，超时后取消
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));

        await Task.Delay(3500, CancellationToken.None);
        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }

        // 3.5s @ 1s 周期：至少 2 次心跳与 2 次指标上报，且连接未被中止
        var envelopes = server.ReceivedSnapshot();
        Assert.True(envelopes.Count(e => e.Type == AgentMessageTypes.Heartbeat) >= 2,
            $"心跳数不足：{Describe(envelopes)}");
        Assert.True(envelopes.Count(e => e.Type == AgentMessageTypes.MetricsReport) >= 2,
            $"指标上报数不足：{Describe(envelopes)}");

        var metrics = envelopes.Where(e => e.Type == AgentMessageTypes.MetricsReport).ToList();
        Assert.All(metrics, e =>
        {
            Assert.Equal(11, e.Payload.GetProperty("cpu").GetDouble());
            Assert.Equal(4400, e.Payload.GetProperty("netRx").GetDouble());
        });

        // 连接稳定：没有反复重连（auth 恰好 1 次）
        Assert.Equal(1, server.AuthCount);
    }

    private static string Describe(List<AgentEnvelope> envelopes) =>
        string.Join(",", envelopes.Select(e => e.Type));

    private sealed class FixedCollector : IMetricsCollector
    {
        private readonly MetricsSample _sample;

        public FixedCollector(MetricsSample sample) => _sample = sample;

        public MetricsSample Sample() => _sample;
    }

    /// <summary>内建 Kestrel WS 服务：接受任意 token，记录全部入站信封，保持连接打开。</summary>
    private sealed class RecordingWsServer : IDisposable
    {
        private readonly ConcurrentQueue<AgentEnvelope> _received = new();
        private WebApplication? _app;

        public int AuthCount => _authCount;
        private int _authCount;

        public int Port { get; private set; }

        public List<AgentEnvelope> ReceivedSnapshot() => _received.ToList();

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
                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    var received_bytes = 0;
                    try
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer, received_bytes, buffer.Length - received_bytes),
                                CancellationToken.None);
                            received_bytes += result.Count;
                        }
                        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            return;
                        }

                        var envelope = JsonSerializer.Deserialize(
                            buffer.AsSpan(0, received_bytes).ToArray(), ProtocolJsonContext.Default.AgentEnvelope);
                        if (envelope is null)
                        {
                            continue;
                        }

                        if (envelope.Type == AgentMessageTypes.Auth)
                        {
                            Interlocked.Increment(ref _authCount);
                            var reply = AgentEnvelope.Create(AgentMessageTypes.AuthOk, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { deviceId = 7, name = "集成测试" }));
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
