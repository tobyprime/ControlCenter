using System.Linq;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>指标上报链路集成测试：agent 经 WS 通道上报 metrics.report → 入库 → 查询 API 可见。</summary>
public class MetricsIngestTests : IDisposable
{
    public class Factory : TestAppFactory
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
        using var socket = await ConnectAsync(_factory);
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
    public async Task Sustained_Threshold_Violation_Via_Ingest_Chain_Enqueues_Alert()
    {
        // TOB-341 挂接点：指标入库链路同步喂阈值越限评估，持续越限超过 60s 触发待发告警
        var (token, deviceId) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 95, mem: 40, disk: 55, netRx: 2048, netTx: 4096)));

        // WS 入站处理与测试线程并发：先等首点确认入库（消除时钟推进竞态），再推进时钟发第二个点
        var metricsStore = _factory.Services.GetRequiredService<IMetricsStore>();
        var deviceRowId = deviceId;
        for (var i = 0; i < 50; i++)
        {
            var points = metricsStore.QueryRaw(deviceRowId, _factory.Clock.GetUtcNow().AddMinutes(-5), _factory.Clock.GetUtcNow().AddMinutes(5));
            if (points.Count > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, Payload(cpu: 96, mem: 40, disk: 55, netRx: 2048, netTx: 4096)));

        // 轮询等待评估链路完成（上限 5s）
        var outbox = _factory.Services.GetRequiredService<DevicePanel.Web.Alerting.IAlertOutboxStore>();
        DevicePanel.Web.Alerting.AlertOutboxEntry? entry = null;
        for (var i = 0; i < 50 && entry is null; i++)
        {
            entry = outbox.PeekOldest();
            if (entry is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(entry);
        Assert.Contains("指标越限告警", entry!.Message.Title);
        Assert.Contains("96", entry.Message.Content);

        // 恢复（回落到阈值下）后，新一轮事件才重新告警；持续越限期间不重发
        Assert.Single(outbox.List());
    }

    [Fact]
    public async Task Malformed_Metrics_Payload_Is_Ignored_Without_Killing_Connection()
    {
        var (token, deviceId) = await CreateDeviceWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
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

    [Fact]
    public async Task Store_Failure_Drops_Point_But_Keeps_Session_Alive()
    {
        // 回归（TOB-338 审查问题 2）：落库失败必须"丢点保连"——Insert 抛异常只丢该点，
        // 不允许穿过 DispatchAsync 结束 WS 会话（否则一次存储抖动即设备离线 + agent 重连）。
        using var factory = new ThrowingStoreFactory();
        var client0 = factory.CreateClient();
        var login = await client0.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        var created = await client0.PostAsJsonAsync("/api/devices", new { name = "故障注入设备", tags = new[] { "机房F" } });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("agentToken").GetString()!;
        var deviceId = payload.GetProperty("id").GetInt64();

        using var socket = await ConnectAsync(factory);
        await SendAuthAsync(socket, token);

        // 连续两条上报均落库失败：不终止会话
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 12.5, mem: 40, disk: 55, netRx: 20480, netTx: 4096)));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, Payload(cpu: 13.5, mem: 41, disk: 56, netRx: 21480, netTx: 4196)));

        // 心跳链路仍活着：推进 2 个心跳周期（60s）后设备仍在线（会话若被结束，last_seen 停在 auth 时刻，此时已离线）
        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 4 });
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var client = await AuthenticatedClientAsync(factory);
        var list = await (await client.GetAsync("/api/devices")).Content.ReadFromJsonAsync<JsonElement>();
        var device = list.EnumerateArray().Single(d => d.GetProperty("id").GetInt64() == deviceId);
        Assert.True(device.GetProperty("online").GetBoolean(), "落库失败不应终止 WS 会话：心跳链路必须存活");
    }

    private sealed class ThrowingStoreFactory : Factory
    {
        public ThrowingStoreFactory()
        {
            TestServices = services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IMetricsStore>(new ThrowingOnInsertStore());
            };
        }
    }

    /// <summary>仅在 Insert 时抛 SQLite 异常的存储（模拟 busy_timeout 耗尽/磁盘故障）。</summary>
    private sealed class ThrowingOnInsertStore : IMetricsStore
    {
        public void Insert(long deviceId, DateTimeOffset collectedAtUtc, MetricsPoint point) =>
            throw new Microsoft.Data.Sqlite.SqliteException("模拟存储故障", 11);

        public IReadOnlyList<MetricsPoint> QueryRaw(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public IReadOnlyList<MetricsBucket> QueryHourly(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public IReadOnlyList<MetricsBucket> QueryDaily(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc) => new(0, 0, 0);
    }

    private async Task<(string Token, long DeviceId)> CreateDeviceWithTokenAsync()
    {
        var client = await AuthenticatedClientAsync(_factory);
        var response = await client.PostAsJsonAsync("/api/devices", new { name = "指标设备", tags = new[] { "机房M" } });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("agentToken").GetString()!, payload.GetProperty("id").GetInt64());
    }

    private async Task<HttpClient> AuthenticatedClientAsync(TestAppFactory factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<JsonElement> GetSeriesAsync(long deviceId)
    {
        var client = await AuthenticatedClientAsync(_factory);
        var from = Uri.EscapeDataString("2026-09-03T11:00:00Z");
        var to = Uri.EscapeDataString("2026-09-03T13:00:00Z");
        var response = await client.GetAsync($"/api/metrics/{deviceId}/series?from={from}&to={to}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement[]> ListDevicesAsync()
    {
        var client = await AuthenticatedClientAsync(_factory);
        var response = await client.GetAsync("/api/devices");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }

    private Task<WebSocket> ConnectAsync(TestAppFactory factory)
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, "/agent/ws");
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
