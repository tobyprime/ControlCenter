using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 控制 API 集成测试（三期模块4）：假 agent 走真实 /agent/ws 通道接入（对象形态能力上报带控制器声明），
/// 浏览器侧 POST /api/collectors/{id}/controllers/{key}/invoke 与控制留痕/类型清单查询。
/// 验证下行请求负载、seq 关联回包、四类内置控制、三态回执（成功/失败/超时）、离线 409、
/// 未知控制器 404、参数非法 400（不留痕）、全量留痕与按控制器/时间筛选。
/// </summary>
public class ControlApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            Settings["DevicePanel:Control:RequestTimeoutSeconds"] = TimeoutSeconds.ToString();
        }

        public const int TimeoutSeconds = 2;
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Invoke_Button_Returns_Success_Receipt_And_Records_Log()
    {
        var (deviceId, agent) = await ConnectAgentAsync();

        var response = await InvokeAsync(deviceId, "restart", """{"value":"restart"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("success", payload.GetProperty("status").GetString());
        Assert.Contains("重启", payload.GetProperty("message").GetString());

        // agent 收到的请求沿信封协议：type 正确、seq 为面板分配、负载为声明 + 参数
        var request = await agent.ReceiveUntilAsync(AgentMessageTypes.ControlInvokeRequest);
        Assert.True(request.Seq > 0);
        Assert.Equal("restart", request.Payload.GetProperty("key").GetString());
        Assert.Equal("button", request.Payload.GetProperty("type").GetString());
        Assert.Equal("restart", request.Payload.GetProperty("params").GetProperty("value").GetString());

        // 全量留痕：操作者/控制器/参数/结论齐全
        var logs = await QueryLogsAsync(deviceId);
        var entry = Assert.Single(logs);
        Assert.Equal("restart", entry.GetProperty("controllerKey").GetString());
        Assert.Equal("button", entry.GetProperty("controllerType").GetString());
        Assert.Equal("重启服务", entry.GetProperty("controllerLabel").GetString());
        Assert.Equal("admin", entry.GetProperty("operator").GetString());
        Assert.Equal("success", entry.GetProperty("status").GetString());
        Assert.Equal("restart", entry.GetProperty("parameters").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Invoke_Toggle_Input_Slider_All_Succeed()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        Assert.Equal(HttpStatusCode.OK, (await InvokeAsync(deviceId, "power", """{"state":true}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InvokeAsync(deviceId, "remark", """{"text":"机房巡检"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InvokeAsync(deviceId, "fan", """{"value":60}""")).StatusCode);

        var logs = await QueryLogsAsync(deviceId);
        Assert.Equal(3, logs.Count);
        Assert.Equal(["fan", "remark", "power"],
            logs.Select(l => l.GetProperty("controllerKey").GetString()!).ToArray()); // 倒序：最新在前
    }

    [Fact]
    public async Task Unknown_Controller_Returns_404_Without_Log()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        var response = await InvokeAsync(deviceId, "ghost", """{"value":"x"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await QueryLogsAsync(deviceId)); // 未发生下发：不留痕
    }

    [Fact]
    public async Task Invalid_Params_Returns_400_Without_Log()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        // 滑块声明 0-100/步长 10：60.5 未对齐步长
        var response = await InvokeAsync(deviceId, "fan", """{"value":60.5}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("步长", payload.GetProperty("error").GetString());
        Assert.Empty(await QueryLogsAsync(deviceId));
    }

    [Fact]
    public async Task Offline_Device_Returns_409_And_Records_Failure()
    {
        // 先接入上报控制器声明，再断开（面板保留声明、通道下线）→ 下发走离线快速失败路径
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);
        var agent = await FakeAgent.ConnectAsync(_factory, created.AgentToken, AgentMode.Respond, AgentCommand.Accept);
        agent.Dispose();
        await Task.Delay(200, CancellationToken.None); // 等面板侧连接清理

        var response = await InvokeAsync(created.Id, "restart", """{"value":"restart"}""", client);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("离线", payload.GetProperty("error").GetString());

        var logs = await QueryLogsAsync(created.Id);
        var entry = Assert.Single(logs);
        Assert.Equal("failure", entry.GetProperty("status").GetString());
        Assert.Contains("离线", entry.GetProperty("resultMessage").GetString());
    }

    [Fact]
    public async Task Agent_Error_Becomes_502_And_Records_Failure()
    {
        var (deviceId, _) = await ConnectAgentAsync(AgentMode.Error, AgentCommand.Reject);

        var response = await InvokeAsync(deviceId, "restart", """{"value":"restart"}""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failure", payload.GetProperty("status").GetString());
        Assert.Contains("拒绝", payload.GetProperty("error").GetString());

        var entry = Assert.Single(await QueryLogsAsync(deviceId));
        Assert.Equal("failure", entry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task No_Agent_Response_Times_Out_As_504_And_Records_Timeout()
    {
        var (deviceId, _) = await ConnectAgentAsync(AgentMode.Silent);

        var response = await InvokeAsync(deviceId, "restart", """{"value":"restart"}""");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var entry = Assert.Single(await QueryLogsAsync(deviceId));
        Assert.Equal("timeout", entry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_Device_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await InvokeAsync(424242, "restart", """{"value":"restart"}""", client);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Control_Logs_Filter_By_Controller_Key_And_Time()
    {
        var (deviceId, _) = await ConnectAgentAsync();
        await InvokeAsync(deviceId, "restart", """{"value":"restart"}""");
        await InvokeAsync(deviceId, "power", """{"state":true}""");

        // 按控制器筛选
        var filtered = await QueryLogsAsync(deviceId, controllerKey: "power");
        var entry = Assert.Single(filtered);
        Assert.Equal("power", entry.GetProperty("controllerKey").GetString());

        // 时间窗筛选：上界早于全部留痕 → 空；下界早于全部留痕 → 全部
        Assert.Empty(await QueryLogsAsync(deviceId, to: DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(2, (await QueryLogsAsync(deviceId, from: DateTime.UtcNow.AddMinutes(-1))).Count);

        // limit 截断（倒序：最新的在前）
        Assert.Single(await QueryLogsAsync(deviceId, limit: 1));
    }

    [Fact]
    public async Task Control_Types_Endpoint_Lists_Registry()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/controls/types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var types = payload.GetProperty("types");
        Assert.Equal(4, types.GetArrayLength());
        Assert.Equal(["button", "input", "slider", "toggle"],
            types.EnumerateArray().Select(t => t.GetProperty("key").GetString()!).ToArray());
    }

    [Fact]
    public async Task Controllers_Endpoint_Returns_Declared_Controllers()
    {
        var (deviceId, _) = await ConnectAgentAsync();

        var response = await GetAsync($"/api/collectors/{deviceId}/controllers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var controllers = payload.GetProperty("controllers");
        Assert.Equal(4, controllers.GetArrayLength());
        var fan = controllers.EnumerateArray().First(c => c.GetProperty("key").GetString() == "fan");
        Assert.Equal("slider", fan.GetProperty("type").GetString());
        Assert.Equal("风扇调速", fan.GetProperty("label").GetString());
        Assert.Equal(100, fan.GetProperty("paramsSchema").GetProperty("max").GetInt32());
    }

    private async Task<HttpResponseMessage> InvokeAsync(long deviceId, string key, string paramsJson, HttpClient? client = null)
    {
        client ??= await AuthenticatedClientAsync();
        return await client.PostAsJsonAsync($"/api/collectors/{deviceId}/controllers/{key}/invoke",
            new { @params = JsonSerializer.Deserialize<JsonElement>(paramsJson) });
    }

    private async Task<List<JsonElement>> QueryLogsAsync(long deviceId, string? controllerKey = null,
        DateTime? from = null, DateTime? to = null, int? limit = null)
    {
        var client = await AuthenticatedClientAsync();
        var query = new List<string> { $"collectorId={deviceId}" };
        if (controllerKey is not null)
        {
            query.Add($"controllerKey={Uri.EscapeDataString(controllerKey)}");
        }

        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        }

        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        }

        if (limit is not null)
        {
            query.Add($"limit={limit}");
        }

        var response = await client.GetAsync($"/api/controls/logs?{string.Join("&", query)}");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("logs").EnumerateArray().ToList();
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
        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "控制设备", tags = new[] { "机房A" } });
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

    /// <summary>假 agent 对 ctrl.invoke.request 的处置（回执语义）。</summary>
    private enum AgentCommand
    {
        /// <summary>回 ctrl.invoke.response（回执含按钮 label）。</summary>
        Accept,

        /// <summary>回 ctrl.error（执行失败）。</summary>
        Reject,
    }

    private async Task<(long DeviceId, FakeAgent Agent)> ConnectAgentAsync(
        AgentMode mode = AgentMode.Respond, AgentCommand command = AgentCommand.Accept)
    {
        var client = await AuthenticatedClientAsync();
        var (deviceId, token) = await CreateDeviceAsync(client);
        var agent = await FakeAgent.ConnectAsync(_factory, token, mode, command);
        return (deviceId, agent);
    }

    /// <summary>
    /// 测试内嵌假 agent：走真实 /agent/ws 通道；认证后以对象形态上报能力与控制器声明
    /// （button/toggle/input/slider 各一），按模式应答 ctrl.invoke.request（seq 沿用请求）。
    /// </summary>
    private sealed class FakeAgent : IDisposable
    {
        /// <summary>对象形态能力上报的控制器声明（与面板注册表内置四类一一对应）。</summary>
        public const string CapabilitiesReport = """
            {
              "capabilities": ["metrics", "controllers"],
              "controllers": [
                { "key": "restart", "type": "button", "label": "重启服务", "tags": ["运维"],
                  "paramsSchema": { "items": [ { "label": "重启", "value": "restart" } ] } },
                { "key": "power", "type": "toggle", "label": "电源开关", "tags": [] },
                { "key": "remark", "type": "input", "label": "备注" },
                { "key": "fan", "type": "slider", "label": "风扇调速",
                  "paramsSchema": { "min": 0, "max": 100, "step": 10 } }
              ]
            }
            """;

        private readonly WebSocket _socket;
        private readonly List<AgentEnvelope> _received = new();
        private readonly object _lock = new();
        private int _cursor;
        private Task _pump = Task.CompletedTask;

        private FakeAgent(WebSocket socket) => _socket = socket;

        public static async Task<FakeAgent> ConnectAsync(Factory factory, string token, AgentMode mode, AgentCommand command)
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

            // 对象形态能力上报：控制器声明随后持久化（三期模块4）
            await SendAsync(socket, new AgentEnvelope
            {
                Type = AgentMessageTypes.AgentCapabilities,
                Seq = 2,
                Payload = JsonSerializer.Deserialize<JsonElement>(CapabilitiesReport),
            });

            var agent = new FakeAgent(socket);
            agent._pump = agent.PumpAsync(mode, command);
            return agent;
        }

        private async Task PumpAsync(AgentMode mode, AgentCommand command)
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

                    if (mode == AgentMode.Silent || envelope.Type != AgentMessageTypes.ControlInvokeRequest)
                    {
                        continue;
                    }

                    if (command == AgentCommand.Reject)
                    {
                        await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.ControlError, envelope.Seq,
                            JsonSerializer.SerializeToElement(new { message = "设备拒绝执行控制请求" })));
                        continue;
                    }

                    await SendAsync(_socket, AgentEnvelope.Create(AgentMessageTypes.ControlInvokeResponse, envelope.Seq,
                        JsonSerializer.SerializeToElement(new { message = "已执行按钮「重启」" })));
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
