using System.Linq;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 指标上报链路集成测试：agent 经 WS 通道上报 metrics.report → 注册表过滤 → 入库 → 查询 API 可见；
/// 规则评估挂接入库链路；未注册指标拒收；附加指标（extra）走同一管道（约束 A 验证）。
/// </summary>
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
        var (token, targetId) = await CreateTargetWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 12.5, mem: 40, disk: 55, netRx: 20480, netTx: 4096)));

        var series = await GetSeriesAsync(targetId, "cpu,mem,disk,net_rx,net_tx");
        Assert.Equal("raw", series.GetProperty("granularity").GetString());
        var byKey = series.GetProperty("series").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!);
        Assert.Equal(12.5, byKey["cpu"].GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);
        Assert.Equal(40, byKey["mem"].GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);
        Assert.Equal(55, byKey["disk"].GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);
        Assert.Equal(20480, byKey["net_rx"].GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);
        Assert.Equal(4096, byKey["net_tx"].GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);
    }

    [Fact]
    public async Task Extra_Metrics_Flow_Through_Typed_Pipeline_Once_Registered()
    {
        // 约束 A 验收 5：新增一种指标 = 注册 metric key + 类型，核心管道零改动
        var client = await AuthenticatedClientAsync(_factory);
        var register = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "test.metric", valueType = "number", displayName = "测试指标", unit = "个" });
        register.EnsureSuccessStatusCode();

        var (token, targetId) = await CreateTargetWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2,
            PayloadWithExtra(cpu: 10, mem: 20, disk: 30, netRx: 100, netTx: 200, """{"test.metric":42.5}""")));

        var series = await GetSeriesAsync(targetId, "test.metric");
        var testSeries = series.GetProperty("series").EnumerateArray().Single();
        Assert.Equal(42.5, testSeries.GetProperty("points")[0].GetProperty("v").GetDouble(), precision: 6);

        // 未注册 key（extra 内）：拒收入库，注册后即可见（注册表即门禁）
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3,
            PayloadWithExtra(cpu: 10, mem: 20, disk: 30, netRx: 100, netTx: 200, """{"future.metric":"v1"}""")));
        var before = await (await client.GetAsync($"/api/metrics/{targetId}/overview")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(before.EnumerateArray(), i => i.GetProperty("key").GetString() == "future.metric");

        var reg = await client.PostAsJsonAsync("/api/metrics/keys", new { key = "future.metric", valueType = "string", displayName = "未来指标", unit = "" });
        reg.EnsureSuccessStatusCode();
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 4,
            PayloadWithExtra(cpu: 10, mem: 20, disk: 30, netRx: 100, netTx: 200, """{"future.metric":"v1"}""")));

        // WS 入站处理与测试线程并发：轮询等待注册后的 key 可见（上限 5s）
        JsonElement item = default;
        for (var i = 0; i < 50 && item.ValueKind is not JsonValueKind.Object; i++)
        {
            var current = await (await client.GetAsync($"/api/metrics/{targetId}/overview")).Content.ReadFromJsonAsync<JsonElement>();
            item = current.EnumerateArray().FirstOrDefault(i2 => i2.GetProperty("key").GetString() == "future.metric");
            if (item.ValueKind is not JsonValueKind.Object)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal("v1", item.GetProperty("latestValueText").GetString());
    }

    [Fact]
    public async Task Sustained_Threshold_Violation_Via_Ingest_Chain_Enqueues_Alert()
    {
        // 挂接点回归：指标入库链路同步喂规则引擎，持续越限超过 60s 触发待发告警（全局默认规则 90）
        var (token, targetId) = await CreateTargetWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 95, mem: 40, disk: 55, netRx: 2048, netTx: 4096)));

        // WS 入站处理与测试线程并发：先等首点确认入库（消除时钟推进竞态），再推进时钟发第二个点
        var metricsStore = _factory.Services.GetRequiredService<IMetricsStore>();
        for (var i = 0; i < 50; i++)
        {
            var points = metricsStore.QueryRaw(targetId, MetricKeys.Cpu, _factory.Clock.GetUtcNow().AddMinutes(-5), _factory.Clock.GetUtcNow().AddMinutes(5));
            if (points.Count > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, Payload(cpu: 96, mem: 40, disk: 55, netRx: 2048, netTx: 4096)));

        // 轮询等待评估链路完成（上限 5s）
        var outbox = _factory.Services.GetRequiredService<IAlertOutboxStore>();
        AlertOutboxEntry? entry = null;
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
        var (token, targetId) = await CreateTargetWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        // 缺字段 / 非数值 / 布尔值 / 非标量 extra：均应忽略，不影响连接
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, JsonDocument.Parse("""{"cpu":10}""").RootElement.Clone()));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, JsonDocument.Parse("""{"cpu":"abc","mem":1,"disk":1,"netRx":1,"netTx":1}""").RootElement.Clone()));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 4, JsonDocument.Parse("""{"cpu":true,"mem":2,"disk":3,"netRx":4,"netTx":5}""").RootElement.Clone()));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 5, JsonDocument.Parse("""{"cpu":1,"mem":2,"disk":3,"netRx":4,"netTx":5,"extra":{"nested":{"a":1}}}""").RootElement.Clone()));

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 6 });

        var series = await GetSeriesAsync(targetId, "cpu");
        Assert.Empty(series.GetProperty("series")[0].GetProperty("points").EnumerateArray());

        // 连接仍可用：心跳被处理后目标保持在线
        var target = (await ListTargetsAsync()).Single(t => t.GetProperty("id").GetInt64() == targetId);
        Assert.True(target.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_Writes_Online_True_Sample()
    {
        var (token, targetId) = await CreateTargetWithTokenAsync();
        using var socket = await ConnectAsync(_factory);
        await SendAuthAsync(socket, token);

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 2 });
        await WaitForSampleAsync(targetId, MetricKeys.Online);

        var overview = await GetOverviewAsync(targetId);
        var online = overview.EnumerateArray().Single(i => i.GetProperty("key").GetString() == MetricKeys.Online);
        Assert.Equal("true", online.GetProperty("latestValueText").GetString());
        Assert.Equal("bool", online.GetProperty("valueType").GetString());
    }

    [Fact]
    public async Task Store_Failure_Drops_Point_But_Keeps_Session_Alive()
    {
        // 回归：落库失败必须"丢点保连"——Insert 抛异常只丢该点，
        // 不允许穿过 DispatchAsync 结束 WS 会话（否则一次存储抖动即目标离线 + agent 重连）。
        using var factory = new ThrowingStoreFactory();
        var client0 = factory.CreateClient();
        var login = await client0.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        var created = await client0.PostAsJsonAsync("/api/targets", new { name = "故障注入设备", tags = new[] { "机房F" } });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("agentToken").GetString()!;
        var targetId = payload.GetProperty("id").GetInt64();

        using var socket = await ConnectAsync(factory);
        await SendAuthAsync(socket, token);

        // 连续两条上报均落库失败：不终止会话
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 2, Payload(cpu: 12.5, mem: 40, disk: 55, netRx: 20480, netTx: 4096)));
        await SendAsync(socket, AgentEnvelope.Create(AgentMessageTypes.MetricsReport, 3, Payload(cpu: 13.5, mem: 41, disk: 56, netRx: 21480, netTx: 4196)));

        // 心跳链路仍活着：穿插推进时钟并持续心跳，落库失败不应结束 WS 会话（最后一条心跳时刻距判定时刻远小于离线窗口）
        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 4 });
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 5 });
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 6 });
        var client = await AuthenticatedClientAsync(factory);
        var list = await (await client.GetAsync("/api/targets")).Content.ReadFromJsonAsync<JsonElement>();
        var target = list.EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == targetId);
        Assert.True(target.GetProperty("online").GetBoolean(), "落库失败不应终止 WS 会话：心跳链路必须存活");
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
        public void Insert(long targetId, string metricKey, MetricSample sample) =>
            throw new Microsoft.Data.Sqlite.SqliteException("模拟存储故障", 11);

        public IReadOnlyList<MetricSample> QueryRaw(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public IReadOnlyList<MetricBucket> QueryHourly(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public IReadOnlyList<MetricBucket> QueryDaily(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc) => [];

        public MetricSample? GetLatest(long targetId, string metricKey) => null;

        public IReadOnlyList<string> ListReportedKeys(long targetId) => [];

        public IReadOnlyList<long> ListTargetsReporting(string metricKey) => [];

        public bool HasAnySample(string metricKey) => false;

        public MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc) => new(0, 0, 0);
    }

    private async Task<(string Token, long TargetId)> CreateTargetWithTokenAsync()
    {
        var client = await AuthenticatedClientAsync(_factory);
        var response = await client.PostAsJsonAsync("/api/targets", new { name = "指标设备", tags = new[] { "机房M" } });
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

    private async Task<JsonElement> GetSeriesAsync(long targetId, string keys)
    {
        var client = await AuthenticatedClientAsync(_factory);
        var from = Uri.EscapeDataString("2026-09-03T11:00:00Z");
        var to = Uri.EscapeDataString("2026-09-03T13:00:00Z");
        var response = await client.GetAsync($"/api/metrics/{targetId}/series?keys={Uri.EscapeDataString(keys)}&from={from}&to={to}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetOverviewAsync(long targetId)
    {
        var client = await AuthenticatedClientAsync(_factory);
        var response = await client.GetAsync($"/api/metrics/{targetId}/overview");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task WaitForSampleAsync(long targetId, string metricKey)
    {
        var store = _factory.Services.GetRequiredService<IMetricsStore>();
        for (var i = 0; i < 50; i++)
        {
            if (store.GetLatest(targetId, metricKey) is not null)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private async Task<JsonElement[]> ListTargetsAsync()
    {
        var client = await AuthenticatedClientAsync(_factory);
        var response = await client.GetAsync("/api/targets");
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

    private static JsonElement PayloadWithExtra(double cpu, double mem, double disk, double netRx, double netTx, string extraJson) =>
        JsonDocument.Parse($$"""{"cpu":{{cpu}},"mem":{{mem}},"disk":{{disk}},"netRx":{{netRx}},"netTx":{{netTx}},"extra":{{extraJson}}}""").RootElement.Clone();
}
