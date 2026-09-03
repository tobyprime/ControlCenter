using DevicePanel.Web.Devices;
using Microsoft.Extensions.Logging;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 离线告警规则：复用 TOB-337 的离线判定（AgentOptions.OfflineAfter = 连续 2 个心跳周期），
/// 只在"在线 → 离线"状态转换时发一次告警；状态持久化（重启不重复告警），恢复在线即关闭，
/// 再次离线按新事件重新告警。从未上报过心跳的设备不告警。
/// </summary>
public sealed class OfflineAlertScanner : BackgroundService
{
    private readonly IDeviceRegistry _devices;
    private readonly AlertDispatcher _dispatcher;
    private readonly IAlertStateStore _states;
    private readonly AgentOptions _agentOptions;
    private readonly AlertOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OfflineAlertScanner> _logger;

    public OfflineAlertScanner(
        IDeviceRegistry devices,
        AlertDispatcher dispatcher,
        IAlertStateStore states,
        AgentOptions agentOptions,
        AlertOptions options,
        TimeProvider clock,
        ILogger<OfflineAlertScanner> logger)
    {
        _devices = devices;
        _dispatcher = dispatcher;
        _states = states;
        _agentOptions = agentOptions;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ScanInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "离线告警扫描异常，继续下一轮");
            }
        }
    }

    /// <summary>执行一轮离线扫描（暴露为公开方法便于测试）。</summary>
    public Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        foreach (var device in _devices.List())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var ruleKey = $"offline:{device.Id}";
            var isOnline = device.IsOnline(_clock, _agentOptions);
            var alerted = _states.Get(ruleKey) is not null;

            if (isOnline)
            {
                if (alerted)
                {
                    // 恢复在线：关闭事件（恢复本身不发消息，下次离线是新事件）
                    _states.Delete(ruleKey);
                }

                continue;
            }

            if (alerted || device.LastSeenAtUtc is null)
            {
                // 已告警过（重启后也不重复）；或设备从未接入（无 last_seen，没有"掉线"可言）
                continue;
            }

            _dispatcher.Enqueue(
                new AlertMessage(
                    "设备离线告警",
                    $"设备「{device.Name}」已离线（超过 {_agentOptions.OfflineAfter.TotalSeconds:F0} 秒未上报心跳）"),
                nowUtc);
            _states.Set(ruleKey, SerializeState(nowUtc), nowUtc);
            _logger.LogInformation("设备 {DeviceId} 判定离线，已入队离线告警", device.Id);
        }

        return Task.CompletedTask;
    }

    private static string SerializeState(DateTimeOffset alertedAtUtc) =>
        System.Text.Json.JsonSerializer.Serialize(new { AlertedAtUtc = alertedAtUtc });
}
