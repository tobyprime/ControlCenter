using DevicePanel.Agent;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// 终端通道测试共享假件：供 TerminalChannelTests 之外的测试（如 TOB-403 F8 启动噪声过滤的通道级验证）
/// 复用同款假下行链路与假 PTY 会话语义。
/// </summary>
internal sealed class SharedFakeDownlink : IAgentDownlink
{
    public bool IsOpen { get; set; } = true;

    public List<(string Type, string SessionId, object? Content)> Sent { get; } = new();

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

/// <summary>假 PTY 会话：记录写入、可注入输出、可触发 EOF/关闭。</summary>
internal sealed class SharedFakePtySession : IPtySession
{
    private readonly SemaphoreSlim _available = new(0);
    private readonly Queue<byte> _output = new();
    private readonly object _lock = new();

    public bool Killed { get; private set; }

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

    public void Write(byte[] data)
    {
    }

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

    public void SetWindowSize(int cols, int rows)
    {
    }

    public void Kill()
    {
        Killed = true;
        _available.Release();
    }
}

internal sealed class SharedFakePtySessionFactory : IPtySessionFactory
{
    public List<SharedFakePtySession> Sessions { get; } = new();

    public SharedFakePtySession? LastSession => Sessions.Count > 0 ? Sessions[^1] : null;

    public IPtySession Create(int cols, int rows)
    {
        var session = new SharedFakePtySession();
        Sessions.Add(session);
        return session;
    }
}

/// <summary>组装被测通道的共享入口。</summary>
internal static class TerminalChannelTestFakes
{
    public static (TerminalChannel Channel, SharedFakeDownlink Downlink, SharedFakePtySessionFactory Factory) CreateChannel()
    {
        var downlink = new SharedFakeDownlink();
        var factory = new SharedFakePtySessionFactory();
        return (new TerminalChannel(downlink, factory), downlink, factory);
    }
}
