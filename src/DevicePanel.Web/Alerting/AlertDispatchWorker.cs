using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

/// <summary>单次分发的结果：队列是否有待发消息、本次是否发送成功。</summary>
public sealed record DispatchOutcome(bool HadPending, bool Success);

/// <summary>
/// 待发队列分发 worker（核心契约：napcat 不可用时告警不丢）。
/// FIFO 取队头发送：成功即出队并立刻处理下一条（恢复后快速清空队列）；
/// 失败则记账留队、按 RetrySeconds 退避重试，永不放弃（无丢失）。
/// </summary>
public sealed class AlertDispatchWorker : BackgroundService
{
    private readonly IAlertOutboxStore _outbox;
    private readonly IReadOnlyList<INotifier> _notifiers;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AlertDispatchWorker> _logger;

    public AlertDispatchWorker(
        IAlertOutboxStore outbox,
        IEnumerable<INotifier> notifiers,
        AlertOptions options,
        TimeProvider clock,
        ILogger<AlertDispatchWorker> logger)
    {
        _outbox = outbox;
        _notifiers = notifiers.ToList();
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DispatchOutcome outcome;
            try
            {
                outcome = await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 存储层瞬时故障（如库文件忙）：记账在下游，此处按重试节奏退避后继续
                _logger.LogWarning(ex, "待发队列分发异常，稍后重试");
                outcome = new DispatchOutcome(HadPending: true, Success: false);
            }

            var delay = !outcome.HadPending ? TimeSpan.FromSeconds(_options.PollSeconds)
                : outcome.Success ? TimeSpan.Zero
                : TimeSpan.FromSeconds(_options.RetrySeconds);
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>处理一个队头消息（暴露为公开方法便于测试与手动驱动）。</summary>
    public async Task<DispatchOutcome> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var entry = _outbox.PeekOldest();
        if (entry is null)
        {
            return new DispatchOutcome(HadPending: false, Success: false);
        }

        var notifier = _notifiers.FirstOrDefault(n => n.ChannelName == entry.Channel);
        if (notifier is null)
        {
            _outbox.RecordFailure(entry.Id, $"渠道 {entry.Channel} 未注册", _clock.GetUtcNow());
            return new DispatchOutcome(HadPending: true, Success: false);
        }

        try
        {
            await notifier.NotifyAsync(entry.Message, cancellationToken).ConfigureAwait(false);
            _outbox.MarkSent(entry.Id);
            if (entry.Attempts > 0)
            {
                _logger.LogInformation("告警 #{Id} 经 {Attempts} 次尝试后补发成功（{Channel}）", entry.Id, entry.Attempts + 1, entry.Channel);
            }

            return new DispatchOutcome(HadPending: true, Success: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 渠道不可用：留队记账，按退避节奏重试，直至补发成功（无丢失契约）
            _outbox.RecordFailure(entry.Id, ex.Message, _clock.GetUtcNow());
            _logger.LogWarning("告警 #{Id} 发送失败（第 {Attempt} 次），已留在待发队列：{Error}", entry.Id, entry.Attempts + 1, ex.Message);
            return new DispatchOutcome(HadPending: true, Success: false);
        }
    }
}
