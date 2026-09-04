using System.Collections.Concurrent;
using DevicePanel.Protocol;

namespace DevicePanel.Web.Targets;

/// <summary>注册表中的一条在线连接：设备 ID + 通道 + 最近活跃时间（任一入站消息都会刷新）。</summary>
public sealed class AgentConnectionEntry
{
    internal AgentConnectionEntry(long deviceId, IDeviceChannel channel, DateTimeOffset lastSeenUtc)
    {
        DeviceId = deviceId;
        Channel = channel;
        LastSeenUtc = lastSeenUtc;
    }

    public long DeviceId { get; }

    public IDeviceChannel Channel { get; }

    public DateTimeOffset LastSeenUtc { get; internal set; }
}

/// <summary>
/// 在线连接登记表：设备 ID → 当前通道。
/// - 同一设备重复接入：新连接顶替，旧连接以 DuplicateSession 关闭；
/// - 删除设备/token 重置：TryDisconnect 立即断开对应在线连接；
/// - 心跳超时由 HeartbeatMonitor 依据 LastSeenUtc 清理。
/// </summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<long, AgentConnectionEntry> _connections = new();
    private readonly TimeProvider _timeProvider;

    public AgentConnectionRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAdd(long deviceId, IDeviceChannel channel)
    {
        var entry = new AgentConnectionEntry(deviceId, channel, _timeProvider.GetUtcNow());
        while (true)
        {
            if (_connections.TryAdd(deviceId, entry))
            {
                return true;
            }

            // 顶替旧连接（设备重启、网络切换等导致的重复接入）
            if (!_connections.TryGetValue(deviceId, out var existing))
            {
                continue;
            }

            if (existing.Channel == channel)
            {
                return true;
            }

            if (_connections.TryUpdate(deviceId, entry, existing))
            {
                _ = existing.Channel.CloseAsync(
                    (int)WebSocketCloseCodes.DuplicateSession,
                    "该设备已有新连接接入",
                    CancellationToken.None);
                return true;
            }
        }
    }

    public bool IsConnected(long deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>查询设备当前在线通道；不在线返回 null。</summary>
    public IDeviceChannel? GetChannel(long deviceId) =>
        _connections.TryGetValue(deviceId, out var entry) ? entry.Channel : null;

    /// <summary>
    /// 连接移除事件（断连/被顶替/心跳超时/删设备/token 重置）。
    /// 终端会话等通道上的派生资源订阅它做清理；触发时机=登记表移除该通道。
    /// </summary>
    public event Action<long, IDeviceChannel>? ConnectionClosed;

    private void OnConnectionClosed(long deviceId, IDeviceChannel channel)
    {
        try
        {
            ConnectionClosed?.Invoke(deviceId, channel);
        }
        catch
        {
            // 订阅方异常不影响注册表自身的清理路径
        }
    }

    /// <summary>
    /// 认证后注册连接，并复核设备仍存在：认证（token 校验）与注册之间设备可能被删除，
    /// 此时连接立即按 DeviceDeleted 关闭并移除，避免形成永不清理的 ghost 连接。
    /// </summary>
    public bool TryRegister(long deviceId, IDeviceChannel channel, Func<bool> deviceExists)
    {
        TryAdd(deviceId, channel);
        if (deviceExists())
        {
            return true;
        }

        TryDisconnect(deviceId, WebSocketCloseCodes.DeviceDeleted, "设备已删除");
        return false;
    }

    public void Touch(long deviceId, DateTimeOffset seenAtUtc)
    {
        if (_connections.TryGetValue(deviceId, out var entry))
        {
            entry.LastSeenUtc = seenAtUtc;
        }
    }

    /// <summary>移除指定连接：仅当当前登记的仍是该通道时生效（避免误清新连接）。</summary>
    public void Remove(long deviceId, IDeviceChannel channel)
    {
        if (_connections.TryGetValue(deviceId, out var entry) && entry.Channel == channel)
        {
            _connections.TryRemove(new KeyValuePair<long, AgentConnectionEntry>(deviceId, entry));
            OnConnectionClosed(deviceId, channel);
        }
    }

    public bool TryDisconnect(long deviceId, WebSocketCloseCodes closeCode, string reason)
    {
        if (!_connections.TryRemove(deviceId, out var entry))
        {
            return false;
        }

        OnConnectionClosed(deviceId, entry.Channel);
        _ = entry.Channel.CloseAsync((int)closeCode, reason, CancellationToken.None);
        return true;
    }

    public IReadOnlyCollection<AgentConnectionEntry> Snapshot() => _connections.Values.ToArray();
}
