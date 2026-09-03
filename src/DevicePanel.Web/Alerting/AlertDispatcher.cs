using System.Text.Json;

namespace DevicePanel.Web.Alerting;

/// <summary>一条待分发的告警消息：标题 + 正文（渠道实现负责最终排版）。</summary>
public sealed record AlertMessage(string Title, string Content);

/// <summary>
/// 消息渠道抽象：每新增一种分发渠道 = 实现本接口 + 注册 DI，分发器按 ChannelName 写入待发队列，
/// 渠道实现自身不关心排队与补发（napcat 断线补发是 worker 的统一契约）。
/// </summary>
public interface INotifier
{
    /// <summary>渠道标识（如 qq），同时是待发队列行的 channel 字段。</summary>
    string ChannelName { get; }

    /// <summary>发送一条消息；失败抛异常，由分发 worker 记账并重试。</summary>
    Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// 告警入队口：规则侧（离线扫描/阈值越限）统一经此投递，一条消息对每个已注册渠道各写一行待发记录，
/// 保证"渠道故障只影响发送、不丢消息"。
/// </summary>
public sealed class AlertDispatcher
{
    private readonly IAlertOutboxStore _outbox;
    private readonly IReadOnlyList<INotifier> _notifiers;

    public AlertDispatcher(IAlertOutboxStore outbox, IEnumerable<INotifier> notifiers)
    {
        _outbox = outbox;
        _notifiers = notifiers.ToList();
    }

    public void Enqueue(AlertMessage message, DateTimeOffset nowUtc)
    {
        foreach (var notifier in _notifiers)
        {
            _outbox.Enqueue(notifier.ChannelName, message, nowUtc);
        }
    }

    internal static string Serialize(AlertMessage message) => JsonSerializer.Serialize(message);

    internal static AlertMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<AlertMessage>(json);
}
