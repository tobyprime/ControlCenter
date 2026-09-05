using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Targets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// Agent 管理端点集成测试（TOB-375 模块2 验收 1/2/5）：
/// 创建 token 只显示一次、重置后旧 token 立即失效（4001/4003 语义保持）、
/// 标签增删改与按标签筛选、删除断开在线连接、关联目标的 agent 不可从 agent 侧删除。
/// </summary>
public class AgentApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_Returns_Token_Once_And_List_Hides_Token()
    {
        var client = await AuthenticatedClientAsync();

        var created = await CreateAgentAsync(client, "边缘 agent", new[] { "机房A" });
        Assert.True(created.GetProperty("id").GetInt64() > 0);
        Assert.StartsWith(AgentToken.Prefix, created.GetProperty("agentToken").GetString());
        Assert.Equal(new[] { "机房A" }, created.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("capabilities").ValueKind); // 未声明能力

        var listed = await ListAgentsAsync(client);
        var entry = Assert.Single(listed);
        Assert.False(entry.TryGetProperty("agentToken", out _)); // token 只在创建/重置响应出现
        Assert.False(entry.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("targetId").ValueKind);
    }

    [Fact]
    public async Task Create_Without_Name_Is_Rejected()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/agents", new { name = "  ", labels = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Labels_Can_Be_Replaced_And_List_Filtered_By_Label()
    {
        var client = await AuthenticatedClientAsync();
        await CreateAgentAsync(client, "甲", new[] { "机房A" });
        var agent = await CreateAgentAsync(client, "乙", new[] { "机房B" });

        var update = await client.PutAsJsonAsync($"/api/agents/{agent.GetProperty("id").GetInt64()}/labels",
            new { labels = new[] { "网关", "测试机" } });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "网关", "测试机" }, updated.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ToArray());

        Assert.Equal(new[] { "甲" }, (await ListAgentsAsync(client, "机房A")).Select(a => a.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { "乙" }, (await ListAgentsAsync(client, "测试机")).Select(a => a.GetProperty("name").GetString()).ToArray());
        Assert.Equal(2, (await ListAgentsAsync(client)).Length);
    }

    [Fact]
    public async Task Labels_Of_Unknown_Agent_Return_404()
    {
        var client = await AuthenticatedClientAsync();

        var update = await client.PutAsJsonAsync("/api/agents/999/labels", new { labels = new[] { "x" } });

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task ResetToken_Disconnects_Online_Agent_And_Rotates_Token()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateAgentAsync(client, "在线 agent", Array.Empty<string>());
        var agentId = created.GetProperty("id").GetInt64();
        var oldToken = created.GetProperty("agentToken").GetString()!;
        using var socket = await ConnectAgentAsync(oldToken);
        Assert.Equal("auth.ok", (await ReceiveEnvelopeAsync(socket)).Type);

        var reset = await client.PostAsync($"/api/agents/{agentId}/token", content: null);
        reset.EnsureSuccessStatusCode();
        var newToken = (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentToken").GetString()!;

        // 旧 token 的在线连接被立即断开（4003 语义保持）
        Assert.Equal((int)WebSocketCloseCodes.TokenReset, (int)(await DrainCloseAsync(socket)).CloseStatus!.Value);

        // 旧 token 重连被拒（4001）；auth.error 作为中间消息被 DrainClose 跳过
        using var oldSocket = await ConnectAgentAsync(oldToken);
        Assert.Equal((int)WebSocketCloseCodes.AuthFailed, (int)(await DrainCloseAsync(oldSocket)).CloseStatus!.Value);

        // 新 token 正常接入
        using var newSocket = await ConnectAgentAsync(newToken);
        Assert.Equal("auth.ok", (await ReceiveEnvelopeAsync(newSocket)).Type);
    }

    [Fact]
    public async Task Delete_Unlinked_Agent_Disconnects_And_Removes()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateAgentAsync(client, "待删除", Array.Empty<string>());
        var agentId = created.GetProperty("id").GetInt64();
        using var socket = await ConnectAgentAsync(created.GetProperty("agentToken").GetString()!);
        Assert.Equal("auth.ok", (await ReceiveEnvelopeAsync(socket)).Type);

        var delete = await client.DeleteAsync($"/api/agents/{agentId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal((int)WebSocketCloseCodes.DeviceDeleted, (int)(await DrainCloseAsync(socket)).CloseStatus!.Value);
        Assert.Empty(await ListAgentsAsync(client));

        using var socketAfter = await ConnectAgentAsync(created.GetProperty("agentToken").GetString()!);
        Assert.Equal((int)WebSocketCloseCodes.AuthFailed, (int)(await DrainCloseAsync(socketAfter)).CloseStatus!.Value);
    }

    [Fact]
    public async Task Delete_Linked_Agent_Is_Refused_To_Keep_Target_Intact()
    {
        var client = await AuthenticatedClientAsync();
        await CreateDeviceTargetAsync(client, "有关联的设备");

        var listed = await ListAgentsAsync(client);
        var linked = Assert.Single(listed);
        Assert.NotEqual(JsonValueKind.Null, linked.GetProperty("targetId").ValueKind);

        var delete = await client.DeleteAsync($"/api/agents/{linked.GetProperty("id").GetInt64()}");

        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Single(await ListAgentsAsync(client));
    }

    /// <summary>注入 targets 落库失败（审查问题3）：device 目标创建失败路径不允许遗留带有效 token 的孤儿 agent。</summary>
    public sealed class ExplodingCreateFactory : TestAppFactory
    {
        public ExplodingCreateFactory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            // 生产注册为 singleton（被 singleton worker 消费），测试替换必须保持同生命周期
            TestServices = services => services.AddSingleton<ITargetRegistry>(sp => new ExplodingCreateTargetRegistry(
                new TargetRegistry(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>())));
        }

        private sealed class ExplodingCreateTargetRegistry : ITargetRegistry
        {
            private readonly TargetRegistry _inner;

            public ExplodingCreateTargetRegistry(TargetRegistry inner) => _inner = inner;

            public TargetInfo Create(string type, string name, IReadOnlyList<string> tags, long? agentId = null) =>
                throw new InvalidOperationException("注入故障：targets 落库失败");

            public TargetInfo? Update(long id, string name, IReadOnlyList<string> tags) => _inner.Update(id, name, tags);

            public bool Delete(long id) => _inner.Delete(id);

            public TargetInfo? Get(long id) => _inner.Get(id);

            public IReadOnlyList<TargetInfo> List() => _inner.List();

            public void Touch(long targetId, DateTimeOffset seenAtUtc) => _inner.Touch(targetId, seenAtUtc);
        }
    }

    [Fact]
    public async Task Device_Target_Create_Failure_Leaves_No_Orphan_Agent()
    {
        using var factory = new ExplodingCreateFactory();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();

        try
        {
            await client.PostAsJsonAsync("/api/targets", new { type = "device", name = "故障设备", tags = Array.Empty<string>() });
        }
        catch (Exception)
        {
            // 注入故障以 500/异常冒出均可：断言与失败形态无关，只看 agents 台账无孤儿
        }

        Assert.Empty(await ListAgentsAsync(client));
    }

    [Fact]
    public async Task Created_Device_Target_Linked_Agent_Appears_In_Agent_List()
    {
        var client = await AuthenticatedClientAsync();
        var target = await CreateDeviceTargetAsync(client, "迁移前设备");

        var linked = Assert.Single(await ListAgentsAsync(client));
        Assert.Equal("迁移前设备", linked.GetProperty("name").GetString());
        Assert.Equal(target.GetProperty("id").GetInt64(), linked.GetProperty("targetId").GetInt64());
    }

    [Fact]
    public async Task Unlinked_Agent_Connects_With_Agents_Page_Token_Keeps_Online_And_Undeclared_Capabilities()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateAgentAsync(client, "台账直建 agent", Array.Empty<string>());
        var token = created.GetProperty("agentToken").GetString()!;
        using var socket = await ConnectAgentAsync(token);

        var authOk = await ReceiveEnvelopeAsync(socket);
        Assert.Equal("auth.ok", authOk.Type);
        // 无关联 target：连接键为负 agent id（双写期约定，不与任何 target 混淆）
        Assert.True(authOk.Payload.GetProperty("deviceId").GetInt64() < 0);

        await SendAsync(socket, new AgentEnvelope { Type = AgentMessageTypes.Heartbeat, Seq = 2 });
        // 服务端处理信封是异步的：轮询至状态可见（上限 5s）
        var listed = await PollAgentUntilAsync(client, a => a.GetProperty("online").GetBoolean());
        Assert.True(listed.GetProperty("online").GetBoolean());
        // 未上报能力声明的 agent（含旧版）照常接入（向后兼容）
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("capabilities").ValueKind);
    }

    [Fact]
    public async Task Agent_Capabilities_Are_Persisted_And_Visible_In_List()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateAgentAsync(client, "能力声明 agent", Array.Empty<string>());
        using var socket = await ConnectAgentAsync(created.GetProperty("agentToken").GetString()!);
        Assert.Equal("auth.ok", (await ReceiveEnvelopeAsync(socket)).Type);

        await SendAsync(socket, new AgentEnvelope
        {
            Type = AgentMessageTypes.AgentCapabilities,
            Seq = 2,
            Payload = JsonSerializer.SerializeToElement(new[] { "metrics", "terminal" }),
        });

        var listed = await PollAgentUntilAsync(client,
            a => a.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array && caps.GetArrayLength() > 0);
        Assert.Equal(new[] { "metrics", "terminal" },
            listed.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    /// <summary>轮询 agent 列表直到条件满足（WS 信封服务端异步处理，立即读会与处理竞态）。</summary>
    private static async Task<JsonElement> PollAgentUntilAsync(HttpClient client, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var agent = Assert.Single(await ListAgentsAsync(client));
            if (condition(agent))
            {
                return agent;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("agent 状态未在预期时间内可见");
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> CreateAgentAsync(HttpClient client, string name, IEnumerable<string> labels)
    {
        var response = await client.PostAsJsonAsync("/api/agents", new { name, labels });
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateDeviceTargetAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/targets", new { name, tags = Array.Empty<string>() });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> ListAgentsAsync(HttpClient client, string? label = null)
    {
        var response = await client.GetAsync(label is null ? "/api/agents" : $"/api/agents?label={Uri.EscapeDataString(label)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
    }

    private Task<WebSocket> ConnectAgentAsync(string token)
    {
        // 仅发送 auth 不消费回复：auth.ok/auth.error 由调用方自行接收，避免双重读取
        return ConnectWsAsync(socket => SendAuthAsync(socket, token));
    }

    private static async Task SendAsync(WebSocket socket, AgentEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.AgentEnvelope);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task<WebSocket> ConnectWsAsync(Func<WebSocket, Task> prepare)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, "/agent/ws");
        var socket = await wsClient.ConnectAsync(uri, CancellationToken.None);
        await prepare(socket);
        return socket;
    }

    private static async Task SendAuthAsync(WebSocket socket, string token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AgentEnvelope
        {
            Type = AgentMessageTypes.Auth,
            Seq = 1,
            Payload = JsonSerializer.SerializeToElement(new { token }),
        }, ProtocolJsonContext.Default.AgentEnvelope);
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
}
