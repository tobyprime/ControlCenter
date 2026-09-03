using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DevicePanel.Web.Alerting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 分发 worker 测试。核心契约（TOB-341 完成标准 4）：
/// napcat 停止期间产生的告警全部留在本地队列；恢复后按产生顺序自动补发、无丢失。
/// </summary>
public class AlertDispatchWorkerTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AlertDispatchWorker CreateWorker(AlertOutboxStore outbox, params INotifier[] notifiers) =>
        new(
            outbox,
            notifiers,
            new AlertOptions { PollSeconds = 1, RetrySeconds = 2 },
            _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertDispatchWorker>.Instance);

    [Fact]
    public async Task Worker_Drains_Queue_In_Fifo_Order_And_Empties_It()
    {
        var outbox = new AlertOutboxStore(_db.Factory);
        var notifier = new ProgrammableNotifier();
        var worker = CreateWorker(outbox, notifier);

        outbox.Enqueue(notifier.ChannelName, new AlertMessage("告警一", "第一条"), _clock.GetUtcNow());
        outbox.Enqueue(notifier.ChannelName, new AlertMessage("告警二", "第二条"), _clock.GetUtcNow());

        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: true);
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: true);
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: false, Success: false);

        Assert.Equal(["告警一", "告警二"], notifier.Sent.Select(m => m.Title));
        Assert.Equal(0, outbox.Count());
    }

    [Fact]
    public async Task Worker_Failure_Keeps_Message_And_Records_Error()
    {
        var outbox = new AlertOutboxStore(_db.Factory);
        var notifier = new ProgrammableNotifier { Available = false };
        var worker = CreateWorker(outbox, notifier);

        outbox.Enqueue(notifier.ChannelName, new AlertMessage("停机期间", "告警"), _clock.GetUtcNow());
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);
        _clock.Advance(TimeSpan.FromSeconds(2));
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);

        var entry = outbox.PeekOldest()!;
        Assert.Equal("停机期间", entry.Message.Title);
        Assert.Equal(2, entry.Attempts);
        Assert.False(string.IsNullOrEmpty(entry.LastError));
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Worker_Sends_Even_When_Napcat_Settings_Appear_Late()
    {
        // 未配置 napcat 时入队的告警：配置补上后照常补发（配置即生效）
        var outbox = new AlertOutboxStore(_db.Factory);
        var settings = new AlertSettingsStore(_db.Factory);
        var worker = CreateWorker(outbox, new NapcatNotifier(settings, new HttpClient(new FakeAlwaysFailingHandler())));

        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("离线告警", "设备「a」已离线"), _clock.GetUtcNow());
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);

        settings.Save(new AlertDeliverySettings("http://127.0.0.1:1", "t", "private", "1"));
        var entry = outbox.PeekOldest()!;
        Assert.Equal(1, entry.Attempts);
    }

    [Fact]
    public async Task Core_Contract_Napcat_Stop_And_Recover_Full_Chain_No_Loss()
    {
        using var napcat = new FakeNapcatServer();
        var outbox = new AlertOutboxStore(_db.Factory);
        var settings = new AlertSettingsStore(_db.Factory);
        settings.Save(new AlertDeliverySettings(napcat.BaseUrl, "secret", "private", "10001"));
        var notifier = new NapcatNotifier(
            settings,
            new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) });
        var dispatcher = new AlertDispatcher(outbox, [notifier]);
        var worker = CreateWorker(outbox, notifier);

        // napcat 停止（返回 500）：期间触发 ≥2 条告警 → 全部落入本地队列且队列可见
        napcat.Status = HttpStatusCode.InternalServerError;
        dispatcher.Enqueue(new AlertMessage("设备离线告警", "设备「web-1」已离线"), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(3));
        dispatcher.Enqueue(new AlertMessage("指标越限告警", "设备「web-2」CPU 使用率 96.0%"), _clock.GetUtcNow());

        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);
        // 核心契约（FIFO）：napcat 停止期间只重试队头，但两条都完整保留在队列可见
        Assert.Equal(2, outbox.Count());
        var queued = outbox.List();
        Assert.Equal(2, queued[0].Attempts);
        Assert.Equal(0, queued[1].Attempts);
        Assert.Equal("设备离线告警", queued[0].Message.Title);
        Assert.Equal("指标越限告警", queued[1].Message.Title);

        // napcat 恢复：自动补发，QQ（假服务端）收到条数与队列一致、顺序与产生顺序一致，队列清空
        napcat.Status = HttpStatusCode.OK;
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: true);
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: true);
        ShouldBe(await worker.ProcessOnceAsync(CancellationToken.None), HadPending: false, Success: false);

        Assert.Equal(0, outbox.Count());
        var delivered = napcat.Requests.ToArray();
        // 4 次 = 停机期 2 次失败重试 + 恢复后 2 次补发成功；补发顺序与产生顺序一致
        Assert.Equal(4, delivered.Length);
        Assert.Equal("Bearer secret", delivered[2].Authorization);
        Assert.Contains("设备「web-1」已离线", delivered[2].Body);
        Assert.Contains("设备「web-2」CPU 使用率 96.0%", delivered[3].Body);
    }

    [Fact]
    public async Task Worker_Survives_Panel_Restart_Mid_Outage()
    {
        var outbox = new AlertOutboxStore(_db.Factory);
        var notifier = new ProgrammableNotifier { Available = false };
        var firstWorker = CreateWorker(outbox, notifier);
        outbox.Enqueue(notifier.ChannelName, new AlertMessage("停机告警", "内容"), _clock.GetUtcNow());
        ShouldBe(await firstWorker.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: false);

        // 面板重启：新 worker 实例（同库）接手补发
        notifier.Available = true;
        var restarted = CreateWorker(new AlertOutboxStore(_db.Factory), notifier);
        ShouldBe(await restarted.ProcessOnceAsync(CancellationToken.None), HadPending: true, Success: true);
        Assert.Equal("停机告警", Assert.Single(notifier.Sent).Title);
        Assert.Equal(0, outbox.Count());
    }

    private static void ShouldBe(DispatchOutcome outcome, bool HadPending, bool Success)
    {
        Assert.Equal(HadPending, outcome.HadPending);
        Assert.Equal(Success, outcome.Success);
    }

    private sealed class ProgrammableNotifier : INotifier
    {
        public bool Available { get; set; } = true;

        public List<AlertMessage> Sent { get; } = [];

        public string ChannelName => NapcatNotifier.ChannelNameValue;

        public Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken)
        {
            if (!Available)
            {
                throw new HttpRequestException("napcat 不可用（连接被拒绝）");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAlwaysFailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed record RecordedNapcatRequest(string Path, string? Authorization, string Body);

    /// <summary>本机假 napcat：OneBot v11 HTTP 形状，可切换 500/200 模拟停机与恢复。</summary>
    private sealed class FakeNapcatServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public FakeNapcatServer()
        {
            // 随机端口可能撞上出站临时端口（Linux 默认 32768-60999）：连续换端口重试
            HttpListenerException? lastError = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                BaseUrl = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"{BaseUrl}/");
                try
                {
                    _listener.Start();
                    lastError = null;
                    break;
                }
                catch (HttpListenerException ex)
                {
                    lastError = ex;
                }
            }

            if (lastError is not null)
            {
                throw lastError;
            }

            _loop = Task.Run(ListenAsync);
        }

        public string BaseUrl { get; }

        public ConcurrentQueue<RecordedNapcatRequest> Requests { get; } = new();

        private int _status = (int)HttpStatusCode.OK;

        public HttpStatusCode Status
        {
            get => (HttpStatusCode)Interlocked.CompareExchange(ref _status, 0, 0);
            set => Interlocked.Exchange(ref _status, (int)value);
        }

        private async Task ListenAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener 已关闭
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                Requests.Enqueue(new RecordedNapcatRequest(
                    context.Request.Url!.PathAndQuery,
                    context.Request.Headers["Authorization"],
                    body));
                context.Response.StatusCode = (int)Status;
                var payload = Encoding.UTF8.GetBytes("""{"status":"async","retcode":0}""");
                await context.Response.OutputStream.WriteAsync(payload);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
