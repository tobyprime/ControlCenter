using DevicePanel.Protocol;

namespace DevicePanel.Web.Targets;

/// <summary>
/// 心跳监测：周期扫描在线连接，对连续 OfflineAfter（默认 2 个心跳周期）无任何入站消息的连接
/// 以 HeartbeatTimeout 关闭，腾出资源；设备在线/离线展示依据 last_seen 判定，与本清理解耦。
/// </summary>
public sealed class HeartbeatMonitor : BackgroundService
{
    private readonly AgentConnectionRegistry _connections;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatMonitor> _logger;

    public HeartbeatMonitor(
        AgentConnectionRegistry connections,
        AgentOptions options,
        TimeProvider timeProvider,
        ILogger<HeartbeatMonitor> logger)
    {
        _connections = connections;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "心跳监测扫描异常，继续下一轮");
            }
        }
    }

    /// <summary>执行一轮超时扫描（暴露为公开方法便于测试）。</summary>
    public Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        foreach (var entry in _connections.Snapshot())
        {
            if (cancellationToken.IsCancellationRequested || nowUtc - entry.LastSeenUtc <= _options.OfflineAfter)
            {
                continue;
            }

            _logger.LogInformation("设备 {DeviceId} 连续 {Seconds}s 未心跳，断开连接", entry.DeviceId, _options.OfflineAfter.TotalSeconds);
            _connections.TryDisconnect(entry.DeviceId, WebSocketCloseCodes.HeartbeatTimeout, "心跳超时（连续 2 个周期未上报）");
        }

        return Task.CompletedTask;
    }
}
