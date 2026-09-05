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
/// AgentRunner 控制通道集成测试（三期模块4）：真实 WebSocket 链路上验证——
/// 控制器声明随 agent.capabilities 以对象形态上报（本机 command 不外发）、
/// 无声明回退旧版字符串数组、ctrl.invoke.request 经内置执行器按请求 seq 回执、
/// 慢控制后台化不破坏心跳/指标节拍（验收 5，TOB-338 回归契约）。
/// </summary>
public class AgentRunnerControllersTests
{
    [Fact]
    public async Task Declarations_Are_Reported_In_Object_Form_Without_Private_Command()
    {
        using var server = new RecordingWsServer();
        await server.StartAsync();

        var specs = new ControllerSpec[]
        {
            new("restart", ControlTypeNames.Button, "重启服务", ["运维"],
                Json("""{"items":[{"label":"重启","value":"restart"}]}"""), Command: "systemctl restart app"),
            new("fan", ControlTypeNames.Slider, "风扇调速", [], Json("""{"min":0,"max":100,"step":10}"""), null),
        };
        var runner = CreateRunner(server, downlink => new ControllersChannel((IControllersDownlink)downlink, specs));
        var (runTask, cts) = Start(runner);

        var report = await WaitForEnvelopeAsync(server, AgentMessageTypes.AgentCapabilities);

        Assert.Equal(JsonValueKind.Object, report.Payload.ValueKind);
        Assert.Contains("controllers", report.Payload.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString()));
        var controllers = report.Payload.GetProperty("controllers");
        Assert.Equal(2, controllers.GetArrayLength());
        Assert.Equal("restart", controllers[0].GetProperty("key").GetString());
        Assert.Equal("button", controllers[0].GetProperty("type").GetString());
        Assert.Equal("重启服务", controllers[0].GetProperty("label").GetString());
        Assert.Equal("运维", controllers[0].GetProperty("tags")[0].GetString());
        Assert.Equal(10, controllers[1].GetProperty("paramsSchema").GetProperty("step").GetInt32());
        Assert.DoesNotContain("command", report.Payload.GetProperty("controllers")[0].GetRawText()); // 本机私有动作不外发

        await Stop(cts, runTask);
    }

    [Fact]
    public async Task No_Declarations_Falls_Back_To_Legacy_String_Array()
    {
        using var server = new RecordingWsServer();
        await server.StartAsync();

        // 通道存在但零声明：能力上报保持旧版字符串数组形态（面板向后兼容）
        var runner = CreateRunner(server, downlink => new ControllersChannel((IControllersDownlink)downlink, []));
        var (runTask, cts) = Start(runner);

        var report = await WaitForEnvelopeAsync(server, AgentMessageTypes.AgentCapabilities);

        Assert.Equal(JsonValueKind.Array, report.Payload.ValueKind);
        Assert.Contains("metrics", report.Payload.EnumerateArray().Select(c => c.GetString()));
        Assert.DoesNotContain("controllers", report.Payload.EnumerateArray().Select(c => c.GetString()));

        await Stop(cts, runTask);
    }

    [Fact]
    public async Task Control_Invoke_Runs_Builtin_Executor_And_Responds_With_Echoed_Seq()
    {
        using var server = new RecordingWsServer();
        await server.StartAsync();

        var specs = new ControllerSpec[]
        {
            new("fan", ControlTypeNames.Slider, "风扇调速", [], Json("""{"min":0,"max":100,"step":10}"""), null),
        };
        var runner = CreateRunner(server, downlink => new ControllersChannel((IControllersDownlink)downlink, specs));
        var (runTask, cts) = Start(runner);

        await WaitFirstHeartbeatAsync(server);
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.ControlInvokeRequest, 42,
            JsonSerializer.SerializeToElement(new
            {
                key = "fan",
                type = ControlTypeNames.Slider,
                @params = JsonSerializer.Deserialize<JsonElement>("""{"value":60}"""),
            })));

        var reply = await WaitForEnvelopeAsync(server, AgentMessageTypes.ControlInvokeResponse);
        Assert.Equal(42, reply.Seq);
        Assert.Equal("已设置到 60", reply.Payload.GetProperty("message").GetString());

        await Stop(cts, runTask);
    }

    [Fact]
    public async Task Slow_Control_Does_Not_Disrupt_Heartbeat_Cadence()
    {
        // 验收 5：控制下发（本机脚本耗时 2s）期间与结束后，心跳/指标节拍不间断、连接不重连
        using var server = new RecordingWsServer();
        await server.StartAsync();

        var specs = new ControllerSpec[]
        {
            new("restart", ControlTypeNames.Button, "重启服务", [], Json("{}"), Command: "sleep 2 && echo 已重启"),
        };
        var runner = CreateRunner(server, downlink => new ControllersChannel((IControllersDownlink)downlink, specs));
        var (runTask, cts) = Start(runner);

        await WaitFirstHeartbeatAsync(server);
        await server.SendDownstreamAsync(AgentEnvelope.Create(AgentMessageTypes.ControlInvokeRequest, 9,
            JsonSerializer.SerializeToElement(new
            {
                key = "restart",
                type = ControlTypeNames.Button,
                @params = JsonSerializer.Deserialize<JsonElement>("""{"value":"restart"}"""),
            })));

        var reply = await WaitForEnvelopeAsync(server, AgentMessageTypes.ControlInvokeResponse);
        Assert.Equal(9, reply.Seq);
        Assert.Contains("已重启", reply.Payload.GetProperty("message").GetString());

        // 慢命令执行期间节拍未被阻塞（2s 命令 @ 1s 周期 → 已有 ≥2 拍），回执后再等下一拍确认连接依旧健康
        await WaitForHeartbeatCountAsync(server, 3, deadlineSeconds: 8);

        Assert.Equal(1, server.AuthCount);
        Assert.True(server.ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.MetricsReport) >= 3,
            "指标上报节拍中断");
        await Stop(cts, runTask);
    }

    // ---------- helpers ----------

    private static AgentRunner CreateRunner(RecordingWsServer server, Func<IAgentDownlink, IControllersChannel> controllers) =>
        new(new AgentOptions
            {
                Url = $"ws://127.0.0.1:{server.Port}/agent/ws",
                Token = "dpk_test",
                HeartbeatIntervalSeconds = 1,
            }, new StringWriter(), new FixedCollector(),
            controllersChannelFactory: controllers);

    private static (Task RunTask, CancellationTokenSource Cts) Start(AgentRunner runner)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var runTask = Task.Run(() => runner.RunAsync(cts.Token));
        return (runTask, cts);
    }

    private static async Task Stop(CancellationTokenSource cts, Task runTask)
    {
        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static async Task WaitFirstHeartbeatAsync(RecordingWsServer server)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.Heartbeat) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "等待首个心跳超时");
            await Task.Delay(50, CancellationToken.None);
        }
    }

    private static async Task WaitForHeartbeatCountAsync(RecordingWsServer server, int count, int deadlineSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds);
        while (server.ReceivedSnapshot().Count(e => e.Type == AgentMessageTypes.Heartbeat) < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"等待第 {count} 次心跳超时");
            await Task.Delay(50, CancellationToken.None);
        }
    }

    private static async Task<AgentEnvelope> WaitForEnvelopeAsync(RecordingWsServer server, string type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var match = server.ReceivedSnapshot().FirstOrDefault(e => e.Type == type);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException($"等待 {type} 超时");
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FixedCollector : IMetricsCollector
    {
        public MetricsSample Sample() => new(11, 22, 33, 4400, 5500);
    }

    /// <summary>内建 Kestrel WS 服务：接受任意 token，记录全部入站信封，支持服务端主动下行。</summary>
    private sealed class RecordingWsServer : IDisposable
    {
        private readonly ConcurrentQueue<AgentEnvelope> _received = new();
        private readonly ConcurrentQueue<WebSocket> _connections = new();
        private WebApplication? _app;

        public int AuthCount => _authCount;
        private int _authCount;

        public int Port { get; private set; }

        public List<AgentEnvelope> ReceivedSnapshot() => _received.ToList();

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
                            Interlocked.Increment(ref _authCount);
                            var reply = AgentEnvelope.Create(AgentMessageTypes.AuthOk, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { deviceId = 7, name = "集成测试" }));
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
