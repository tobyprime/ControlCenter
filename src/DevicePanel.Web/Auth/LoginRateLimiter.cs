using System.Collections.Concurrent;

namespace DevicePanel.Web.Auth;

/// <summary>
/// 登录失败限速：按用户名统计连续失败次数，达到阈值后锁定一个时间窗口。
/// 进程内存态（单服务部署）；成功登录重置计数；锁定过期后从零重新计数。
/// </summary>
public interface ILoginRateLimiter
{
    bool IsLocked(string username, out TimeSpan remaining);

    void RecordFailure(string username);

    void Reset(string username);
}

public sealed class LoginRateLimiter : ILoginRateLimiter
{
    private sealed record FailureState(int Count, DateTimeOffset LockedUntilUtc);

    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, FailureState> _states = new(StringComparer.Ordinal);

    public LoginRateLimiter(AuthOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool IsLocked(string username, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_states.TryGetValue(username, out var state))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (state.LockedUntilUtc > now)
        {
            remaining = state.LockedUntilUtc - now;
            return true;
        }

        return false;
    }

    public void RecordFailure(string username)
    {
        var now = _timeProvider.GetUtcNow();
        _states.AddOrUpdate(
            username,
            _ => new FailureState(1, DateTimeOffset.MinValue),
            (_, state) =>
            {
                // 上一次锁定已过期：从零重新计数
                if (state.LockedUntilUtc > DateTimeOffset.MinValue && state.LockedUntilUtc <= now)
                {
                    return new FailureState(1, DateTimeOffset.MinValue);
                }

                var count = state.LockedUntilUtc > now ? state.Count : state.Count + 1;
                var lockedUntil = count >= _options.MaxFailedAttempts
                    ? now.AddSeconds(_options.LockoutSeconds)
                    : state.LockedUntilUtc;
                return new FailureState(count, lockedUntil);
            });
    }

    public void Reset(string username)
    {
        _states.TryRemove(username, out _);
    }
}
