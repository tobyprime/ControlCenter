using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class AgentWsTests : IDisposable
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

    // 每个测试独立 Factory：设备数据互不干扰
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Connect_With_Valid_Token_Authenticates_And_List_Shows_Online()
    {
        var (token, _) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync();

        var authOk = await SendAuthAsync(socket, token);

        Assert.Equal("auth.ok", authOk.Type);
        Assert.True(authOk.Payload.GetProperty("deviceId").GetInt64() > 0);
        Assert.Equal("验收设备", authOk.Payload.GetProperty("name").GetString());

        var device = await GetSingleDeviceAsync();
        Assert.True(device.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task Connect_With_Unknown_Token_Is_Rejected_And_Device_State_Unaffected()
    {
        var (_, deviceId) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync();

        var reply = await SendAuthAsync(socket, "dpk_totally-wrong-token");

        Assert.Equal("auth.error", reply.Type);
        var close = await DrainCloseAsync(socket);
        Assert.Equal((int)WebSocketCloseCodes.AuthFailed, (int)close.CloseStatus!.Value);

        var device = await GetSingleDeviceAsync();
        Assert.Equal(deviceId, device.GetProperty("id").GetInt64());
        Assert.False(device.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task First_Message_Before_Auth_Is_Rejected()
    {
        using var socket = await ConnectAsync();

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 1 });
        var reply = await ReceiveEnvelopeAsync(socket);

        Assert.Equal("auth.error", reply.Type);
        var close = await DrainCloseAsync(socket);
        Assert.Equal((int)WebSocketCloseCodes.AuthFailed, (int)close.CloseStatus!.Value);
    }

    [Fact]
    public async Task Heartbeat_Keeps_Device_Online_And_Two_Missed_Periods_Mark_Offline()
    {
        var (token, _) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, token);

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 2 });
        Assert.True((await GetSingleDeviceAsync()).GetProperty("online").GetBoolean());

        // 心跳 1 个周期未到（30s）：仍在线
        _factory.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True((await GetSingleDeviceAsync()).GetProperty("online").GetBoolean());

        // 连续 2 个周期（60s）无心跳：离线（验收：停 agent 后 60-90s 内变离线）
        _factory.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False((await GetSingleDeviceAsync()).GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task ResetToken_Disconnects_Existing_Session_And_Rejects_Old_Token()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, created.AgentToken);

        var reset = await client.PostAsJsonAsync($"/api/collectors/{created.Id}/token", new { });
        reset.EnsureSuccessStatusCode();
        var newToken = (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentToken").GetString()!;

        // 旧 token 的在线连接被立即断开
        var close = await DrainCloseAsync(socket);
        Assert.Equal((int)WebSocketCloseCodes.TokenReset, (int)close.CloseStatus!.Value);

        // 旧 token 重连被拒
        using var oldSocket = await ConnectAsync();
        await SendAuthAsync(oldSocket, created.AgentToken);
        Assert.Equal((int)WebSocketCloseCodes.AuthFailed, (int)(await DrainCloseAsync(oldSocket)).CloseStatus!.Value);

        // 新 token 正常接入
        using var newSocket = await ConnectAsync();
        var authOk = await SendAuthAsync(newSocket, newToken);
        Assert.Equal("auth.ok", authOk.Type);
    }

    [Fact]
    public async Task Delete_Device_Disconnects_Agent_And_Removes_From_List()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, created.AgentToken);

        var delete = await client.DeleteAsync($"/api/collectors/{created.Id}");
        delete.EnsureSuccessStatusCode();

        var close = await DrainCloseAsync(socket);
        Assert.Equal((int)WebSocketCloseCodes.DeviceDeleted, (int)close.CloseStatus!.Value);

        var list = await ListAsync();
        Assert.DoesNotContain(list, d => d.GetProperty("id").GetInt64() == created.Id);
    }

    [Fact]
    public async Task HeartbeatMonitor_Closes_Stale_Connections()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, created.AgentToken);

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        using var scope = _factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<HeartbeatMonitor>();
        await monitor.ScanOnceAsync(CancellationToken.None);

        var close = await DrainCloseAsync(socket);
        Assert.Equal((int)WebSocketCloseCodes.HeartbeatTimeout, (int)close.CloseStatus!.Value);
    }

    private async Task<(string Token, long DeviceId)> CreateDeviceWithTokenAsync()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client);
        return (created.AgentToken, created.Id);
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
        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "验收设备", tags = new[] { "机房A" } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    private Task<WebSocket> ConnectAsync()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, "/agent/ws");
        return wsClient.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task<AgentEnvelope> SendAuthAsync(WebSocket socket, string token)
    {
        await SendAsync(socket, new AgentEnvelope
        {
            Type = AgentMessageTypes.Auth,
            Seq = 1,
            Payload = JsonSerializer.SerializeToElement(new { token }),
        });
        return await ReceiveEnvelopeAsync(socket);
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
                throw new WebSocketException($"预期收到消息，实际连接被关闭：{result.CloseStatus}（{result.CloseStatusDescription}）");
            }
        }

        return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(buffer, 0, received).ToArray(), ProtocolJsonContext.Default.AgentEnvelope)!;
    }

    private static async Task<(WebSocketCloseStatus? CloseStatus, string? Reason)> DrainCloseAsync(
        WebSocket socket,
        int maxIntermediateMessages = 5)
    {
        var buffer = new byte[16 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var i = 0; i < maxIntermediateMessages; i++)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (result.CloseStatus, result.CloseStatusDescription);
            }
        }

        throw new InvalidOperationException("连接在预期时间内未被服务端关闭。");
    }

    private async Task<JsonElement> GetSingleDeviceAsync()
    {
        var list = await ListAsync();
        return Assert.Single(list);
    }

    private async Task<JsonElement[]> ListAsync()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/collectors");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }
}
