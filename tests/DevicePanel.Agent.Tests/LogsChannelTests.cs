using DevicePanel.Agent;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// LogsChannel 单元测试：用假日志源与假下行链路，验证 logs.* 消息处理语义
/// （services/tail 请求后台执行并按 seq 回包、失败回 logs.error、非法负载与越界参数兜底、异常不外抛）。
/// </summary>
public class LogsChannelTests
{
    [Fact]
    public async Task Services_Request_Runs_Source_And_Responds_With_Echoed_Seq()
    {
        var (channel, downlink, source) = CreateChannel();
        source.Services.AddRange(
        [
            new LogService("nginx.service", "systemd", "A high performance web server"),
            new LogService("web", "docker", "nginx:1.27 (Up 2 hours)"),
        ]);

        await channel.HandleAsync(Request(AgentMessageTypes.LogsServicesRequest, seq: 7), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsServicesResponse);

        Assert.Equal(7, reply.Seq);
        Assert.Single(source.ListServicesCalls);
        var services = reply.Payload.GetProperty("services");
        Assert.Equal(2, services.GetArrayLength());
        Assert.Equal("nginx.service", services[0].GetProperty("name").GetString());
        Assert.Equal("systemd", services[0].GetProperty("kind").GetString());
        Assert.Equal("A high performance web server", services[0].GetProperty("description").GetString());
        Assert.Equal("docker", services[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Tail_Request_Passes_Service_Kind_Lines_And_Responds_With_Lines()
    {
        var (channel, downlink, source) = CreateChannel();
        source.TailLines.AddRange(
        [
            new LogLine("2026-09-04T08:00:00.000000Z", "error", "connect() failed"),
            new LogLine("2026-09-04T08:00:01.000000Z", "info", "ready"),
        ]);

        await channel.HandleAsync(Request(AgentMessageTypes.LogsTailRequest, seq: 3,
            new LogsTailRequestPayload("nginx.service", "systemd", 50)), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsTailResponse);

        Assert.Equal(("nginx.service", "systemd", 50), Assert.Single(source.ReadTailCalls));
        Assert.Equal(3, reply.Seq);
        var lines = reply.Payload.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("2026-09-04T08:00:00.000000Z", lines[0].GetProperty("ts").GetString());
        Assert.Equal("error", lines[0].GetProperty("level").GetString());
        Assert.Equal("connect() failed", lines[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Source_Failure_Responds_Logs_Error_With_Message()
    {
        var (channel, downlink, source) = CreateChannel();
        source.TailException = new InvalidOperationException("journalctl 失败：No entries");

        await channel.HandleAsync(Request(AgentMessageTypes.LogsTailRequest, seq: 9,
            new LogsTailRequestPayload("ghost.service", "systemd", 100)), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsError);

        Assert.Equal(9, reply.Seq);
        Assert.Contains("journalctl 失败", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Invalid_Service_Name_Is_Rejected_Without_Running_Source()
    {
        var (channel, downlink, source) = CreateChannel();

        await channel.HandleAsync(Request(AgentMessageTypes.LogsTailRequest, seq: 2,
            new LogsTailRequestPayload("bad name; rm -rf /", "systemd", 100)), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsError);

        Assert.Empty(source.ReadTailCalls);
        Assert.Equal(2, reply.Seq);
        Assert.Contains("服务名", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Lines_Out_Of_Range_Is_Clamped()
    {
        var (channel, downlink, source) = CreateChannel();

        await channel.HandleAsync(Request(AgentMessageTypes.LogsTailRequest, seq: 1,
            new LogsTailRequestPayload("nginx.service", "systemd", 100000)), CancellationToken.None);
        await downlink.WaitForAsync(AgentMessageTypes.LogsTailResponse);

        Assert.Equal(1000, Assert.Single(source.ReadTailCalls).Lines);
    }

    [Fact]
    public async Task Malformed_Payload_Responds_Logs_Error_Without_Throwing()
    {
        var (channel, downlink, source) = CreateChannel();

        var exception = await Record.ExceptionAsync(() => channel.HandleAsync(
            AgentEnvelope.Create(AgentMessageTypes.LogsTailRequest, 4,
                System.Text.Json.JsonSerializer.SerializeToElement(new { service = 42 })), CancellationToken.None));

        Assert.Null(exception);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsError);
        Assert.Equal(4, reply.Seq);
    }

    [Fact]
    public async Task Source_Exception_Does_Not_Escape_HandleAsync()
    {
        var (channel, downlink, source) = CreateChannel();
        source.ListServicesException = new InvalidOperationException("命令执行超时");

        var exception = await Record.ExceptionAsync(() => channel.HandleAsync(
            Request(AgentMessageTypes.LogsServicesRequest, seq: 5), CancellationToken.None));

        Assert.Null(exception);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.LogsError);
        Assert.Equal(5, reply.Seq);
    }

    [Fact]
    public void Downlink_Closed_During_Execution_Does_Not_Throw()
    {
        var (channel, downlink, source) = CreateChannel();
        downlink.IsOpen = false;

        Assert.Equal(Task.CompletedTask, channel.HandleAsync(Request(AgentMessageTypes.LogsServicesRequest, seq: 1), CancellationToken.None));
    }

    private static (LogsChannel Channel, FakeLogsDownlink Downlink, FakeLogsSource Source) CreateChannel()
    {
        var downlink = new FakeLogsDownlink();
        var source = new FakeLogsSource();
        var channel = new LogsChannel(downlink, source);
        return (channel, downlink, source);
    }

    private static AgentEnvelope Request(string type, long seq, object? payload = null) =>
        AgentEnvelope.Create(type, seq,
            payload is null
                ? System.Text.Json.JsonSerializer.SerializeToElement(new { })
                : System.Text.Json.JsonSerializer.SerializeToElement(payload, AgentJsonContext.Default.LogsTailRequestPayload));

    private sealed class FakeLogsSource : ILogsSource
    {
        public List<LogService> Services { get; } = new();
        public List<LogLine> TailLines { get; } = new();
        public Exception? ListServicesException { get; set; }
        public Exception? TailException { get; set; }
        public List<(string Service, string Kind, int Lines)> ReadTailCalls { get; } = new();
        public List<object> ListServicesCalls { get; } = new();

        public IReadOnlyList<LogService> ListServices()
        {
            ListServicesCalls.Add(new object());
            if (ListServicesException is not null)
            {
                throw ListServicesException;
            }

            return Services;
        }

        public IReadOnlyList<LogLine> ReadTail(string service, string kind, int lines)
        {
            ReadTailCalls.Add((service, kind, lines));
            if (TailException is not null)
            {
                throw TailException;
            }

            return TailLines;
        }
    }

    /// <summary>假下行链路：记录 (type, seq) 与负载，支持按类型等待（请求在后台任务执行）。</summary>
    private sealed class FakeLogsDownlink : ILogsDownlink
    {
        public bool IsOpen { get; set; } = true;

        public List<(string Type, long Seq, System.Text.Json.JsonElement Payload)> Sent { get; } = new();

        public async Task<AgentEnvelope> WaitForAsync(string type)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (Sent)
                {
                    var entry = Sent.FirstOrDefault(s => s.Type == type);
                    if (entry.Type is not null)
                    {
                        return new AgentEnvelope { Type = entry.Type, Seq = entry.Seq, Payload = entry.Payload.Clone() };
                    }
                }

                await Task.Delay(20, CancellationToken.None);
            }

            throw new TimeoutException($"等待 {type} 发出超时");
        }

        private void Add(string type, long seq, System.Text.Json.JsonElement payload)
        {
            lock (Sent)
            {
                Sent.Add((type, seq, payload.Clone()));
            }
        }

        public Task SendServicesResponseAsync(long seq, IReadOnlyList<LogsServicePayload> services, CancellationToken cancellationToken)
        {
            Add(AgentMessageTypes.LogsServicesResponse, seq,
                System.Text.Json.JsonSerializer.SerializeToElement(new LogsServicesPayload(services), AgentJsonContext.Default.LogsServicesPayload));
            return Task.CompletedTask;
        }

        public Task SendTailResponseAsync(long seq, IReadOnlyList<LogsLinePayload> lines, CancellationToken cancellationToken)
        {
            Add(AgentMessageTypes.LogsTailResponse, seq,
                System.Text.Json.JsonSerializer.SerializeToElement(new LogsTailPayload(lines), AgentJsonContext.Default.LogsTailPayload));
            return Task.CompletedTask;
        }

        public Task SendLogsErrorAsync(long seq, string message, CancellationToken cancellationToken)
        {
            Add(AgentMessageTypes.LogsError, seq,
                System.Text.Json.JsonSerializer.SerializeToElement(new LogsErrorPayload(message), AgentJsonContext.Default.LogsErrorPayload));
            return Task.CompletedTask;
        }
    }
}
