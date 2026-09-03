using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>指标上报链路集成测试：agent 经 WS 通道上报 metrics.report → 入库 → 查询 API 可见。</summary>
public class MetricsIngestTests : IDisposable
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
    public async Task Metrics_Report_From_Agent_Is_Stored_And_Visible_Via_Api()
    {
        var (token, deviceId) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, token);

        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 12.5, mem: 40, disk: 55, netRx: 20480, netTx: 4096)));

        var series = await GetSeriesAsync(deviceId);
        Assert.Equal("raw", series.GetProperty("granularity").GetString());
        var points = series.GetProperty("points");
        var point = Assert.Single(points.EnumerateArray());
        Assert.Equal(12.5, point.GetProperty("cpu").GetDouble(), precision: 6);
        Assert.Equal(40, point.GetProperty("mem").GetDouble(), precision: 6);
        Assert.Equal(55, point.GetProperty("disk").GetDouble(), precision: 6);
        Assert.Equal(20480, point.GetProperty("netRx").GetDouble(), precision: 6);
        Assert.Equal(4096, point.GetProperty("netTx").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Malformed_Metrics_Payload_Is_Ignored_Without_Killing_Connection()
    {
        var (token, deviceId) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync();
        await SendAuthAsync(socket, token);

        // 缺字段 / 非数值 / 布尔值：均应忽略，不影响连接
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, JsonDocument.Parse("""{"cpu":10}""").RootElement.Clone()));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, JsonDocument.Parse("""{"cpu":"abc","mem":1,"disk":1,"netRx":1,"netTx":1}""").RootElement.Clone()));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 4, JsonDocument.Parse("""{"cpu":true,"mem":2,"disk":3,"netRx":4,"netTx":5}""").RootElement.Clone()));

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 5 });

        var series = await GetSeriesAsync(deviceId);
        Assert.Empty(series.GetProperty("points").EnumerateArray());

        // 连接仍可用：心跳被处理后设备保持在线
        var device = (await ListDevicesAsync()).Single(d => d.GetProperty("id").GetInt64() == deviceId);
        Assert.True(device.GetProperty("online").GetBoolean());
    }

    private async Task<(string Token, long DeviceId)> CreateDeviceWithTokenAsync()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/devices", new { name = "指标设备", tags = new[] { "机房M" } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("agentToken").GetString()!, payload.GetProperty("id").GetInt64());
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<JsonElement> GetSeriesAsync(long deviceId)
    {
        var client = await AuthenticatedClientAsync();
        var from = Uri.EscapeDataString("2026-09-03T11:00:00Z");
        var to = Uri.EscapeDataString("2026-09-03T13:00:00Z");
        var response = await client.GetAsync($"/api/metrics/{deviceId}/series?from={from}&to={to}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement[]> ListDevicesAsync()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/devices");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }

    private Task<WebSocket> ConnectAsync()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, "/agent/ws");
        return wsClient.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task SendAuthAsync(WebSocket socket, string token)
    {
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.Auth, 1, JsonSerializer.SerializeToElement(new { token })));
        await ReceiveAsync(socket); // auth.ok
    }

    private static Task SendAsync(WebSocket socket, AgentEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<AgentEnvelope> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var received = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), CancellationToken.None);
            received += result.Count;
            if (result.EndOfMessage)
            {
                return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(buffer, 0, received).ToArray(), ProtocolJsonContext.Default.AgentEnvelope)!;
            }
        }
    }

    private static JsonElement Payload(double cpu, double mem, double disk, double netRx, double netTx) =>
        JsonDocument.Parse($$"""{"cpu":{{cpu}},"mem":{{mem}},"disk":{{disk}},"netRx":{{netRx}},"netTx":{{netTx}}}""").RootElement.Clone();
}
