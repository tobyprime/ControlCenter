using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Collectors;
using Xunit;

namespace DevicePanel.Web.Tests;

public class AgentEnvelopeTests
{
    [Fact]
    public void Envelope_Serializes_To_Type_Seq_Payload()
    {
        var envelope = new AgentEnvelope
        {
            Type = AgentMessageTypes.Heartbeat,
            Seq = 7,
            Payload = JsonSerializer.Deserialize<JsonElement>(""""{"uptimeSec":42}""""),
        };

        var json = JsonSerializer.Serialize(envelope, ProtocolJsonContext.Default.AgentEnvelope);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("heartbeat", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(42, doc.RootElement.GetProperty("payload").GetProperty("uptimeSec").GetInt64());
    }

    [Fact]
    public void Envelope_RoundTrips_Unknown_Payload_Without_Loss()
    {
        // 新增消息类型只改 type 不改信封：payload 必须对未知结构透明透传
        var original = new AgentEnvelope
        {
            Type = "future.capability",
            Seq = 1,
            Payload = JsonSerializer.Deserialize<JsonElement>(""""{"nested":{"list":[1,2,3],"ok":true}}""""),
        };

        var json = JsonSerializer.Serialize(original, ProtocolJsonContext.Default.AgentEnvelope);
        var parsed = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AgentEnvelope);

        Assert.NotNull(parsed);
        Assert.Equal(original.Type, parsed!.Type);
        Assert.Equal(original.Seq, parsed.Seq);
        Assert.Equal(JsonValueKind.Object, parsed.Payload.ValueKind);
        Assert.True(parsed.Payload.GetProperty("nested").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Envelope_Missing_Payload_Deserializes_As_Empty()
    {
        var parsed = JsonSerializer.Deserialize("""{"type":"heartbeat","seq":3}""", ProtocolJsonContext.Default.AgentEnvelope);

        Assert.NotNull(parsed);
        Assert.Equal("heartbeat", parsed!.Type);
        Assert.Equal(3, parsed.Seq);
        Assert.Equal(JsonValueKind.Null, parsed.Payload.ValueKind);
    }
}

public class AgentMessageDispatcherTests
{
    private sealed class CapturingHandler : IAgentMessageHandler
    {
        public string MessageType { get; }
        public AgentChannelContext? LastContext { get; private set; }

        public CapturingHandler(string messageType) => MessageType = messageType;

        public Task HandleAsync(AgentChannelContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatch_Routes_Message_To_Registered_Handler()
    {
        var heartbeat = new CapturingHandler(AgentMessageTypes.Heartbeat);
        var dispatcher = new AgentMessageDispatcher(new IAgentMessageHandler[] { heartbeat });
        var envelope = new AgentEnvelope
        {
            Type = AgentMessageTypes.Heartbeat,
            Seq = 2,
            Payload = JsonSerializer.Deserialize<JsonElement>(""""{"uptimeSec":9}""""),
        };

        await dispatcher.DispatchAsync(new AgentChannelContext(new FakeDeviceChannel(), envelope), CancellationToken.None);

        Assert.Equal(9, heartbeat.LastContext!.Payload.GetProperty("uptimeSec").GetInt64());
        Assert.Equal(2, heartbeat.LastContext.Seq);
    }

    [Fact]
    public async Task Dispatch_Ignores_Unknown_Message_Type()
    {
        var dispatcher = new AgentMessageDispatcher(Array.Empty<IAgentMessageHandler>());
        var envelope = new AgentEnvelope { Type = "brand.new.type", Seq = 1 };

        await dispatcher.DispatchAsync(new AgentChannelContext(new FakeDeviceChannel(), envelope), CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_Leaves_Payload_Untouched_For_Extension_Types()
    {
        // 预留扩展点：指标/终端/日志类型未来只需注册 handler，不改通道代码
        var metricsHandler = new CapturingHandler(AgentMessageTypes.MetricsPrefix + "report");
        var dispatcher = new AgentMessageDispatcher(new IAgentMessageHandler[] { metricsHandler });
        var envelope = new AgentEnvelope
        {
            Type = AgentMessageTypes.MetricsPrefix + "report",
            Seq = 5,
            Payload = JsonSerializer.Deserialize<JsonElement>(""""{"cpu":12.5}""""),
        };

        await dispatcher.DispatchAsync(new AgentChannelContext(new FakeDeviceChannel(), envelope), CancellationToken.None);

        Assert.True(metricsHandler.LastContext!.Payload.TryGetProperty("cpu", out _));
    }

    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        public long DeviceId => 1;

        public long AgentId => 0;
        public bool IsOpen => true;
        public Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class AgentConnectionRegistryTests
{
    private sealed class FakeDeviceChannel : IDeviceChannel
    {
        public long DeviceId { get; set; } = 1;

        public long AgentId { get; set; }
        public bool IsOpen => true;
        public List<int> CloseCalls { get; } = new();

        public Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken)
        {
            CloseCalls.Add(closeStatus);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TryAdd_Tracks_Connection_Per_Device()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeDeviceChannel();

        Assert.True(registry.TryAdd(1, channel));
        Assert.True(registry.IsConnected(1));
        Assert.False(registry.IsConnected(2));
    }

    [Fact]
    public void TryAdd_Replaces_Existing_Connection_And_Closes_Old_One()
    {
        var registry = new AgentConnectionRegistry();
        var oldChannel = new FakeDeviceChannel();
        var newChannel = new FakeDeviceChannel();
        registry.TryAdd(1, oldChannel);

        Assert.True(registry.TryAdd(1, newChannel));

        Assert.Equal(new[] { (int)WebSocketCloseCodes.DuplicateSession }, oldChannel.CloseCalls);
        Assert.True(registry.IsConnected(1));
    }

    [Fact]
    public void Remove_Only_Removes_Matching_Connection()
    {
        var registry = new AgentConnectionRegistry();
        var first = new FakeDeviceChannel();
        registry.TryAdd(1, first);
        registry.TryAdd(1, new FakeDeviceChannel());

        registry.Remove(1, first);

        Assert.True(registry.IsConnected(1)); // 新连接不受旧连接清理影响
    }

    [Fact]
    public void TryDisconnect_Closes_And_Removes_Connection()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeDeviceChannel();
        registry.TryAdd(1, channel);

        Assert.True(registry.TryDisconnect(1, WebSocketCloseCodes.DeviceDeleted, "设备已删除"));
        Assert.False(registry.IsConnected(1));
        Assert.Equal(new[] { (int)WebSocketCloseCodes.DeviceDeleted }, channel.CloseCalls);
        Assert.False(registry.TryDisconnect(1, WebSocketCloseCodes.DeviceDeleted, "设备已删除"));
    }

    [Fact]
    public void TryRegister_Discards_Connection_When_Device_Deleted_During_Auth_Window()
    {
        // 删除窗口竞态：auth 校验 token 时设备还在，注册进 registry 前设备被删
        // ——连接必须立即按 4002 关闭并移除，否则成为永不清理的 ghost 连接
        var registry = new AgentConnectionRegistry();
        var channel = new FakeDeviceChannel();

        var registered = registry.TryRegister(1, channel, deviceExists: () => false);

        Assert.False(registered);
        Assert.False(registry.IsConnected(1));
        Assert.Equal(new[] { (int)WebSocketCloseCodes.DeviceDeleted }, channel.CloseCalls);
    }

    [Fact]
    public void TryRegister_Keeps_Connection_When_Device_Still_Exists()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeDeviceChannel();

        var registered = registry.TryRegister(1, channel, deviceExists: () => true);

        Assert.True(registered);
        Assert.True(registry.IsConnected(1));
        Assert.Empty(channel.CloseCalls);
    }
}
