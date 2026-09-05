using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 日志 API 集成测试：假 agent 走真实 /agent/ws 通道接入，浏览器侧 GET
/// /api/collectors/{id}/logs/services 与 /logs/tail（会话 Cookie 认证）。
/// 验证请求下行、seq 关联回包、离线 409、未知设备 404、agent 错误 502、超时 504。
/// </summary>
public class LogsApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            Settings["DevicePanel:Logs:RequestTimeoutSeconds"] = TimeoutSeconds.ToString();
        }

        public const int TimeoutSeconds = 2;
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Services_Returns_Agent_Response()
    {
        var (deviceId, agent) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/services");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var services = payload.GetProperty("services");
        Assert.Equal(2, services.GetArrayLength());
        Assert.Equal("nginx.service", services[0].GetProperty("name").GetString());
        Assert.Equal("systemd", services[0].GetProperty("kind").GetString());

        // agent 收到的请求沿信封协议：type 正确、seq 为面板分配
        var request = await agent.ReceiveUntilAsync(AgentMessageTypes.LogsServicesRequest);
        Assert.True(request.Seq > 0);
    }

    [Fact]
    public async Task Tail_Returns_Lines_And_Request_Matches_Query()
    {
        var (deviceId, agent) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=nginx.service&kind=systemd&lines=37");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = await agent.ReceiveUntilAsync(AgentMessageTypes.LogsTailRequest);
        Assert.Equal("nginx.service", request.Payload.GetProperty("service").GetString());
        Assert.Equal("systemd", request.Payload.GetProperty("kind").GetString());
        Assert.Equal(37, request.Payload.GetProperty("lines").GetInt32());

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lines = payload.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("2026-02-02T02:40:00.000Z", lines[0].GetProperty("ts").GetString());
        Assert.Equal("error", lines[0].GetProperty("level").GetString());
        Assert.Equal("connect() failed", lines[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Lines_Is_Clamped_And_Defaults_Applied()
    {
        var (deviceId, agent) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=nginx.service&kind=systemd&lines=99999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = await agent.ReceiveUntilAsync(AgentMessageTypes.LogsTailRequest);
        Assert.Equal(1000, request.Payload.GetProperty("lines").GetInt32());

        var defaultResponse = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=nginx.service&kind=systemd");
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
        var defaultRequest = await agent.ReceiveUntilAsync(AgentMessageTypes.LogsTailRequest);
        Assert.Equal(200, defaultRequest.Payload.GetProperty("lines").GetInt32());
    }

    [Fact]
    public async Task Unknown_Device_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/collectors/424242/logs/services");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Offline_Device_Returns_409()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);

        var response = await client.GetAsync($"/api/collectors/{created.Id}/logs/services");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("离线", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Agent_Error_Becomes_502_With_Message()
    {
        var (deviceId, agent) = await ConnectAgentAsync(AgentMode.Error);

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=ghost.service&kind=systemd&lines=10");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("No entries", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task No_Agent_Response_Times_Out_As_504()
    {
        // 假 agent 收到请求后不回复（黑洞模式）
        var (deviceId, _) = await ConnectAgentAsync(AgentMode.Silent);

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/services");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Service_Name_Returns_400()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=bad%3Bname&kind=systemd&lines=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("服务名", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_Kind_Returns_400()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?service=a.service&kind=files&lines=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_Service_Returns_400()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/logs/tail?kind=systemd&lines=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private static async Task<(long Id, string AgentToken)> CreateDeviceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "日志设备", tags = new[] { "机房A" } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    private enum AgentMode
    {
        Respond,
        Error,
        Silent,
    }

    private async Task<(long DeviceId, FakeAgent Agent)> ConnectAgentAsync(AgentMode mode = AgentMode.Respond)
    {
        var client = await AuthenticatedClientAsync();
        var (deviceId, token) = await CreateDeviceAsync(client);
        var agent = await FakeAgent.ConnectAsync(_factory, token, mode);
        return (deviceId, agent);
    }

    /// <summary>测试内嵌假 agent：走真实 /agent/ws 通道，按模式自动应答 logs.* 请求（seq 沿用请求）。</summary>
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

                    if (envelope.Type == AgentMessageTypes.LogsServicesRequest)
                    {
                        await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.LogsServicesResponse, envelope.Seq,
                            JsonSerializer.SerializeToElement(new
                            {
                                services = new object[]
                                {
                                    new { name = "nginx.service", kind = "systemd", description = "web" },
                                    new { name = "web", kind = "docker", description = "nginx:1.27" },
                                },
                            })));
                    }
                    else if (envelope.Type == AgentMessageTypes.LogsTailRequest)
                    {
                        if (mode == AgentMode.Error)
                        {
                            await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.LogsError, envelope.Seq,
                                JsonSerializer.SerializeToElement(new { message = "journalctl 失败（exit=1）：No entries" })));
                            continue;
                        }

                        await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.LogsTailResponse, envelope.Seq,
                            JsonSerializer.SerializeToElement(new
                            {
                                lines = new object[]
                                {
                                    new { ts = "2026-02-02T02:40:00.000Z", level = "error", message = "connect() failed" },
                                    new { ts = "2026-02-02T02:40:01.000Z", level = "info", message = "ready" },
                                },
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
