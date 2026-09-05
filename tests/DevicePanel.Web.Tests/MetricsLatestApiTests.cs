using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 按需查询 API（三期模块3）：GET /api/collectors/{id}/metrics/latest。
/// push 采集器在线 → metrics.latest.request 下行即时采样（只读不落库）；离线 409；超时 504；agent 错误 502。
/// pull 采集器 → 面板侧最新样本直读（探测即最新）；未探测/离线 409；未知采集器 404。
/// </summary>
public class MetricsLatestApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            Settings["DevicePanel:Metrics:RequestTimeoutSeconds"] = TimeoutSeconds.ToString();
        }

        public const int TimeoutSeconds = 2;
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Latest_Online_Push_Collector_Queries_Agent_And_Returns_Samples()
    {
        var (collectorId, agent) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{collectorId}/metrics/latest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var samples = payload.GetProperty("samples");
        Assert.True(samples.GetArrayLength() >= 6);

        double? cpu = null, netRx = null, temp = null;
        string? tempSensor = null;
        foreach (var sample in samples.EnumerateArray())
        {
            switch (sample.GetProperty("key").GetString())
            {
                case "cpu":
                    cpu = sample.GetProperty("valueNum").GetDouble();
                    break;
                case "net_rx":
                    netRx = sample.GetProperty("valueNum").GetDouble();
                    break;
                case "temp":
                    temp = sample.GetProperty("valueNum").GetDouble();
                    break;
                case "temp_sensor":
                    tempSensor = sample.GetProperty("valueText").GetString();
                    break;
            }
        }

        Assert.Equal(12.5, cpu);
        Assert.Equal(1024, netRx);
        Assert.Equal(46.5, temp);
        Assert.Equal("coretemp", tempSensor);
        Assert.True(samples.EnumerateArray().All(s => s.TryGetProperty("timeUtc", out _)), "样本应携带时间戳");

        // agent 收到的请求沿信封协议：type 正确、seq 为面板分配
        var request = await agent.ReceiveUntilAsync(AgentMessageTypes.MetricsLatestRequest);
        Assert.True(request.Seq > 0);

        // 按需查询只读不落库：不影响面板侧存储
        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        Assert.Null(store.GetLatest(collectorId, "cpu"));
    }

    [Fact]
    public async Task Latest_Offline_Collector_Returns_409()
    {
        var client = await AuthenticatedClientAsync();
        var collectorId = await CreateCollectorAsync(client);

        var response = await client.GetAsync($"/api/collectors/{collectorId}/metrics/latest");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("离线", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Latest_No_Agent_Response_Times_Out_As_504()
    {
        var (collectorId, _) = await ConnectAgentAsync(AgentMode.Silent);

        var response = await GetAsync($"/api/collectors/{collectorId}/metrics/latest");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Latest_Agent_Error_Becomes_502_With_Message()
    {
        var (collectorId, _) = await ConnectAgentAsync(AgentMode.Error);

        var response = await GetAsync($"/api/collectors/{collectorId}/metrics/latest");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("采样失败", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Latest_Unknown_Collector_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/collectors/424242/metrics/latest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Latest_Pull_Collector_Serves_Store_Latest()
    {
        var client = await AuthenticatedClientAsync();
        var collectorId = await CreatePullCollectorAsync(client);
        var now = DateTimeOffset.UtcNow;
        var metrics = _factory.Services.GetRequiredService<IMetricsStore>();
        metrics.Insert(collectorId, MetricKeys.Status, new MetricSample(now, 1, "true"));
        metrics.Insert(collectorId, "mc.players", new MetricSample(now, 7, null));
        _factory.Services.GetRequiredService<DevicePanel.Web.Collectors.ICollectorRegistry>().Touch(collectorId, now);

        var response = await client.GetAsync($"/api/collectors/{collectorId}/metrics/latest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var samples = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("samples");
        var players = samples.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "mc.players");
        Assert.Equal(7, players.GetProperty("valueNum").GetDouble());
    }

    [Fact]
    public async Task Latest_Pull_Collector_Before_First_Probe_Returns_409()
    {
        var client = await AuthenticatedClientAsync();
        var collectorId = await CreatePullCollectorAsync(client);

        var response = await client.GetAsync($"/api/collectors/{collectorId}/metrics/latest");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("离线", payload.GetProperty("error").GetString());
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        var client = await AuthenticatedClientAsync();
        return await client.GetAsync(path);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<long> CreateCollectorAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "指标设备", tags = new[] { "机房A" } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static async Task<long> CreatePullCollectorAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", new
        {
            name = "MC 服务",
            tags = Array.Empty<string>(),
            pull = new
            {
                url = "https://mc.zenoxs.cn/tiles/settings.json",
                intervalSeconds = 60,
                mappings = new[] { new { metricKey = "mc.players", jsonPath = "$.players.length()", valueType = "number", displayName = "在线玩家数", unit = "人" } },
            },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private enum AgentMode
    {
        Respond,
        Error,
        Silent,
    }

    /// <summary>建 push 采集器并让假 agent 走真实 /agent/ws 通道接入，返回 (采集器Id, 假 agent)。</summary>
    private async Task<(long CollectorId, FakeAgent Agent)> ConnectAgentAsync(AgentMode mode = AgentMode.Respond)
    {
        var client = await AuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/collectors", new { name = "指标设备", tags = new[] { "机房A" } });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        var collectorId = payload.GetProperty("id").GetInt64();
        var agent = await FakeAgent.ConnectAsync(_factory, payload.GetProperty("agentToken").GetString()!, mode);
        return (collectorId, agent);
    }

    /// <summary>测试内嵌假 agent：走真实 /agent/ws 通道，按模式自动应答 metrics.latest 请求（seq 沿用请求）。</summary>
    private sealed class FakeAgent : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly List<AgentEnvelope> _received = new();
        private readonly object _lock = new();
        private int _cursor;
        private Task _pump = Task.CompletedTask;

        private FakeAgent(WebSocket socket) => _socket = socket;

        public static async Task<FakeAgent> ConnectAsync(Factory factory, string token, AgentMode mode)
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
            agent._pump = agent.PumpAsync(mode);
            return agent;
        }

        private async Task PumpAsync(AgentMode mode)
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

                    if (mode == AgentMode.Silent)
                    {
                        continue;
                    }

                    if (envelope.Type == AgentMessageTypes.MetricsLatestRequest)
                    {
                        if (mode == AgentMode.Error)
                        {
                            await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.MetricsError, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { message = "指标采样失败：/proc/stat 不可读" })));
                            continue;
                        }

                        await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.MetricsLatestResponse, envelope.Seq,
                            JsonSerializer.SerializeToElement(new
                            {
                                cpu = 12.5,
                                mem = 40.25,
                                disk = 55.5,
                                netRx = 1024,
                                netTx = 2048,
                                extra = new { temp = 46.5, temp_sensor = "coretemp" },
                            })));
                    }
                }
            }
            catch (Exception)
            {
                // 测试收尾导致的断开，无需处理
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
