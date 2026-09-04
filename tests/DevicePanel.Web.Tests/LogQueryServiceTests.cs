using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Targets;
using DevicePanel.Web.Logs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// LogQueryService 单元测试：假设备通道验证请求-响应关联（seq 沿用请求 + 通道绑定）、
/// 设备离线/超时/agent 错误的异常映射、响应负载解析。响应经真实的 IAgentMessageHandler 投递。
/// </summary>
public class LogQueryServiceTests
{
    [Fact]
    public async Task Offline_Device_Throws_DeviceOffline()
    {
        var service = CreateService(new AgentConnectionRegistry());

        await Assert.ThrowsAsync<DeviceOfflineException>(() => service.ListServicesAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Services_Request_Sends_Envelope_And_Parses_Response()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.ListServicesAsync(1, CancellationToken.None);
        var request = Assert.Single(channel.Sent);
        Assert.Equal(AgentMessageTypes.LogsServicesRequest, request.Type);
        Respond(service, channel, request.Seq, AgentMessageTypes.LogsServicesResponse, new
        {
            services = new[]
            {
                new { name = "nginx.service", kind = "systemd", description = "web" },
                new { name = "web", kind = "docker", description = "nginx:1.27（Up 2 hours）" },
            },
        });
        var services = await query;

        Assert.Equal(2, services.Count);
        Assert.Equal(("nginx.service", "systemd", "web"), (services[0].Name, services[0].Kind, services[0].Description));
        Assert.Equal("docker", services[1].Kind);
    }

    [Fact]
    public async Task Tail_Request_Carries_Service_Kind_Lines_And_Parses_Lines()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.TailAsync(1, "nginx.service", "systemd", 50, CancellationToken.None);
        var request = Assert.Single(channel.Sent);
        Assert.Equal(AgentMessageTypes.LogsTailRequest, request.Type);
        Assert.Equal("nginx.service", request.Payload.GetProperty("service").GetString());
        Assert.Equal("systemd", request.Payload.GetProperty("kind").GetString());
        Assert.Equal(50, request.Payload.GetProperty("lines").GetInt32());
        Respond(service, channel, request.Seq, AgentMessageTypes.LogsTailResponse, new
        {
            lines = new[]
            {
                new { ts = "2026-02-02T02:40:00.000Z", level = "error", message = "connect() failed" },
            },
        });
        var lines = await query;

        var line = Assert.Single(lines);
        Assert.Equal(("2026-02-02T02:40:00.000Z", "error", "connect() failed"), (line.Ts, line.Level, line.Message));
    }

    [Fact]
    public async Task Response_From_Different_Channel_Does_Not_Resolve_Request()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        var staleChannel = new FakeChannel(1); // 同设备重连前的旧通道
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.ListServicesAsync(1, CancellationToken.None);
        var request = Assert.Single(channel.Sent);
        // 旧通道上迟到的响应不得完成新通道上的请求（通道绑定）
        Respond(service, staleChannel, request.Seq, AgentMessageTypes.LogsServicesResponse, new { services = Array.Empty<object>() });
        await Task.Delay(200, CancellationToken.None);

        Assert.False(query.IsCompleted);
    }

    [Fact]
    public async Task Agent_Error_Throws_AgentLogException_With_Message()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.TailAsync(1, "ghost.service", "systemd", 10, CancellationToken.None);
        var request = Assert.Single(channel.Sent);
        Respond(service, channel, request.Seq, AgentMessageTypes.LogsError, new { message = "journalctl 失败：No entries" });

        var exception = await Assert.ThrowsAsync<AgentLogException>(() => query);
        Assert.Contains("journalctl 失败", exception.Message);
    }

    [Fact]
    public async Task Mismatched_Seq_Response_Is_Ignored()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.TailAsync(1, "a.service", "systemd", 10, CancellationToken.None);
        Respond(service, channel, seq: 999, AgentMessageTypes.LogsError, new { message = "late error" });
        await Task.Delay(200, CancellationToken.None);

        Assert.False(query.IsCompleted);
    }

    [Fact]
    public async Task No_Response_In_Time_Throws_AgentTimeout()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry, requestTimeoutSeconds: 1);

        var exception = await Assert.ThrowsAsync<AgentTimeoutException>(
            () => service.ListServicesAsync(1, CancellationToken.None));
        Assert.Contains("超时", exception.Message);
    }

    [Fact]
    public async Task Malformed_Response_Payload_Throws_InvalidOperation()
    {
        var registry = new AgentConnectionRegistry();
        var channel = new FakeChannel(1);
        registry.TryAdd(1, channel);
        var service = CreateService(registry);

        var query = service.ListServicesAsync(1, CancellationToken.None);
        var request = Assert.Single(channel.Sent);
        Respond(service, channel, request.Seq, AgentMessageTypes.LogsServicesResponse, new { nope = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => query);
    }

    private static LogQueryService CreateService(AgentConnectionRegistry registry, int requestTimeoutSeconds = 30) =>
        new(registry, new LogsOptions { RequestTimeoutSeconds = requestTimeoutSeconds }, NullLogger<LogQueryService>.Instance);

    /// <summary>模拟 agent 回包：按 type 选择真实处理器，把信封投递给服务（与 /agent/ws 分发路径一致）。</summary>
    private static void Respond(LogQueryService service, IDeviceChannel channel, long seq, string type, object payload)
    {
        var envelope = AgentEnvelope.Create(type, seq, JsonSerializer.SerializeToElement(payload));
        IAgentMessageHandler handler = type switch
        {
            AgentMessageTypes.LogsServicesResponse => new LogsServicesResponseHandler(service),
            AgentMessageTypes.LogsTailResponse => new LogsTailResponseHandler(service),
            _ => new LogsErrorHandler(service),
        };
        handler.HandleAsync(new AgentChannelContext(channel, envelope), CancellationToken.None);
    }

    /// <summary>假设备通道：记录面板发出的信封。</summary>
    private sealed class FakeChannel : IDeviceChannel
    {
        public FakeChannel(long deviceId) => DeviceId = deviceId;

        public long DeviceId { get; }

        public bool IsOpen => true;

        public List<AgentEnvelope> Sent { get; } = new();

        public Task SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(envelope);
            }

            return Task.CompletedTask;
        }

        public Task CloseAsync(int closeStatus, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
