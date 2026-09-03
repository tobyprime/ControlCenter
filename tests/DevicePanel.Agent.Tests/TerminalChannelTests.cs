using System.Text;
using DevicePanel.Agent;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// TerminalChannel 单元测试：用假 PTY 会话工厂与假下行链路，验证 term.* 消息处理语义
/// （opened 确认、input 解码写入、output 流式回发、closed/错误收尾、close 杀会话、Shutdown 全清理）。
/// </summary>
public class TerminalChannelTests
{
    [Fact]
    public async Task Open_Creates_Pty_With_Size_And_Confirms_Opened()
    {
        var (channel, downlink, factory) = CreateChannel();

        await channel.HandleAsync(TermOpen("s1", cols: 120, rows: 40), CancellationToken.None);

        var created = Assert.Single(factory.Created);
        Assert.Equal((120, 40), created);
        Assert.Contains(("opened", "s1", (object?)null), downlink.Sent);
    }

    [Fact]
    public async Task Open_Failure_Sends_Term_Error()
    {
        var (channel, downlink, factory) = CreateChannel();
        factory.ThrowOnCreate = new InvalidOperationException("无可用 PTY");

        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);

        Assert.Empty(factory.Created);
        var error = Assert.Single(downlink.Sent);
        Assert.Equal(("error", "s1"), (error.Item1, error.Item2));
        Assert.Contains("无可用 PTY", (string)error.Item3!);
    }

    [Fact]
    public async Task Input_Is_Decoded_And_Written_To_Pty()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        var pty = factory.LastSession!;

        await channel.HandleAsync(TermInput("s1", "echo hi\n"), CancellationToken.None);

        Assert.Equal("echo hi\n", Encoding.UTF8.GetString(pty.Written.ToArray()));
    }

    [Fact]
    public async Task Pty_Output_Is_Streamed_As_Term_Output()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        var pty = factory.LastSession!;

        pty.EnqueueOutput("hello\n"u8.ToArray());
        var entry = await downlink.WaitForAsync("output", "s1");

        Assert.Equal("hello\n", Encoding.UTF8.GetString((byte[])entry!));
    }

    [Fact]
    public async Task Pty_Eof_Sends_Term_Closed_And_Removes_Session()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        var pty = factory.LastSession!;

        pty.CloseStream();
        await downlink.WaitForAsync("closed", "s1");

        // 会话已移除：迟到的 input 不再写入 PTY
        var writtenBefore = pty.Written.Count;
        await channel.HandleAsync(TermInput("s1", "late\n"), CancellationToken.None);
        Assert.Equal(writtenBefore, pty.Written.Count);
    }

    [Fact]
    public async Task Resize_Updates_Pty_Winsize()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        var pty = factory.LastSession!;

        await channel.HandleAsync(TermResize("s1", cols: 132, rows: 43), CancellationToken.None);

        Assert.Equal((132, 43), pty.Resized);
    }

    [Fact]
    public async Task Close_Kills_Pty()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        var pty = factory.LastSession!;

        await channel.HandleAsync(TermClose("s1"), CancellationToken.None);

        Assert.True(pty.Killed);
    }

    [Fact]
    public async Task Unknown_Session_Messages_Are_Ignored()
    {
        var (channel, downlink, factory) = CreateChannel();

        await channel.HandleAsync(TermInput("ghost", "x"), CancellationToken.None);
        await channel.HandleAsync(TermClose("ghost"), CancellationToken.None);

        Assert.Empty(downlink.Sent);
    }

    [Fact]
    public async Task Shutdown_Kills_All_Sessions()
    {
        var (channel, downlink, factory) = CreateChannel();
        await channel.HandleAsync(TermOpen("s1", 80, 24), CancellationToken.None);
        await channel.HandleAsync(TermOpen("s2", 80, 24), CancellationToken.None);

        await channel.ShutdownAsync();

        Assert.All(factory.Sessions, pty => Assert.True(pty.Killed));
    }

    private static (TerminalChannel Channel, FakeDownlink Downlink, FakePtySessionFactory Factory) CreateChannel()
    {
        var downlink = new FakeDownlink();
        var factory = new FakePtySessionFactory();
        var channel = new TerminalChannel(downlink, factory);
        return (channel, downlink, factory);
    }

    private static AgentEnvelope TermOpen(string sessionId, int cols, int rows) =>
        AgentEnvelope.Create(AgentMessageTypes.TermOpen, 1,
            System.Text.Json.JsonSerializer.SerializeToElement(new TermOpenPayload(sessionId, cols, rows),
                AgentJsonContext.Default.TermOpenPayload));

    private static AgentEnvelope TermInput(string sessionId, string text) =>
        AgentEnvelope.Create(AgentMessageTypes.TermInput, 2,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new TermInputPayload(sessionId, Convert.ToBase64String(Encoding.UTF8.GetBytes(text))),
                AgentJsonContext.Default.TermInputPayload));

    private static AgentEnvelope TermResize(string sessionId, int cols, int rows) =>
        AgentEnvelope.Create(AgentMessageTypes.TermResize, 4,
            System.Text.Json.JsonSerializer.SerializeToElement(new TermResizePayload(sessionId, cols, rows),
                AgentJsonContext.Default.TermResizePayload));

    private static AgentEnvelope TermClose(string sessionId) =>
        AgentEnvelope.Create(AgentMessageTypes.TermClose, 3,
            System.Text.Json.JsonSerializer.SerializeToElement(new TermClosedPayload(sessionId),
                AgentJsonContext.Default.TermClosedPayload));

    private sealed class FakeDownlink : IAgentDownlink
    {
        public bool IsOpen { get; set; } = true;

        public List<(string Type, string SessionId, object? Content)> Sent { get; } = new();

        /// <summary>轮询等待某条（type, sessionId）信封发出，返回其负载。</summary>
        public async Task<object?> WaitForAsync(string type, string sessionId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (Sent)
                {
                    var entry = Sent.FirstOrDefault(s => s.Type == type && s.SessionId == sessionId);
                    if (entry != default)
                    {
                        return entry.Content;
                    }
                }

                await Task.Delay(20, CancellationToken.None);
            }

            throw new TimeoutException($"等待 {type}（{sessionId}）发出超时");
        }

        public Task SendOpenedAsync(string sessionId, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(("opened", sessionId, null));
            }

            return Task.CompletedTask;
        }

        public Task SendOutputAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(("output", sessionId, data.ToArray()));
            }

            return Task.CompletedTask;
        }

        public Task SendClosedAsync(string sessionId, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(("closed", sessionId, null));
            }

            return Task.CompletedTask;
        }

        public Task SendErrorAsync(string sessionId, string message, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(("error", sessionId, message));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakePtySessionFactory : IPtySessionFactory
    {
        public Exception? ThrowOnCreate { get; set; }

        public List<(int Cols, int Rows)> Created { get; } = new();

        public List<FakePtySession> Sessions { get; } = new();

        public FakePtySession? LastSession => Sessions.Count > 0 ? Sessions[^1] : null;

        public IPtySession Create(int cols, int rows)
        {
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }

            Created.Add((cols, rows));
            var session = new FakePtySession();
            Sessions.Add(session);
            return session;
        }
    }

    /// <summary>假 PTY 会话：记录写入、可注入输出、可触发 EOF/关闭。</summary>
    private sealed class FakePtySession : IPtySession
    {
        private readonly SemaphoreSlim _available = new(0);
        private readonly Queue<byte> _output = new();
        private readonly object _lock = new();

        public List<byte> Written { get; } = new();
        public bool Killed { get; private set; }
        public (int Cols, int Rows)? Resized { get; private set; }

        public void EnqueueOutput(byte[] data)
        {
            lock (_lock)
            {
                foreach (var b in data)
                {
                    _output.Enqueue(b);
                }
            }

            _available.Release();
        }

        public void CloseStream()
        {
            _available.Release();
        }

        public void Kill()
        {
            Killed = true;
            // 与真实实现一致：终止即释放主端流（读端收到 EOF）
            CloseStream();
        }

        public void SetWindowSize(int cols, int rows) => Resized = (cols, rows);

        public int Read(byte[] buffer, int offset, int count)
        {
            _available.Wait();
            lock (_lock)
            {
                if (_output.Count == 0)
                {
                    return 0; // EOF
                }

                var n = Math.Min(count, _output.Count);
                for (var i = 0; i < n; i++)
                {
                    buffer[offset + i] = _output.Dequeue();
                }

                return n;
            }
        }

        public void Write(byte[] data)
        {
            Written.AddRange(data);
        }
    }
}
