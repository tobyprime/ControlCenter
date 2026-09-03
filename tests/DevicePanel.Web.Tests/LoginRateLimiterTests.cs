using DevicePanel.Web.Auth;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class LoginRateLimiterTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    private LoginRateLimiter CreateLimiter(int maxFailedAttempts = 5, int lockoutSeconds = 600)
    {
        return new LoginRateLimiter(
            new AuthOptions { MaxFailedAttempts = maxFailedAttempts, LockoutSeconds = lockoutSeconds },
            _clock);
    }

    [Fact]
    public void Not_Locked_Before_Threshold()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 5);

        for (var i = 0; i < 4; i++)
        {
            limiter.RecordFailure("admin");
        }

        Assert.False(limiter.IsLocked("admin", out _));
    }

    [Fact]
    public void Locked_After_Reaching_Threshold()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 5);

        for (var i = 0; i < 5; i++)
        {
            limiter.RecordFailure("admin");
        }

        Assert.True(limiter.IsLocked("admin", out var remaining));
        Assert.True(Math.Abs((remaining - TimeSpan.FromMinutes(10)).TotalSeconds) < 1, $"锁定剩余时间应约 10 分钟，实际 {remaining}");
    }

    [Fact]
    public void Lock_Is_Per_Username()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 3);

        for (var i = 0; i < 3; i++)
        {
            limiter.RecordFailure("admin");
        }

        Assert.True(limiter.IsLocked("admin", out _));
        Assert.False(limiter.IsLocked("someone-else", out _));
    }

    [Fact]
    public void Reset_Clears_Failures()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 3);

        limiter.RecordFailure("admin");
        limiter.RecordFailure("admin");
        limiter.Reset("admin");
        limiter.RecordFailure("admin");
        limiter.RecordFailure("admin");

        Assert.False(limiter.IsLocked("admin", out _));
    }

    [Fact]
    public void Lock_Expires_After_Window()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 2, lockoutSeconds: 10);

        limiter.RecordFailure("admin");
        limiter.RecordFailure("admin");
        Assert.True(limiter.IsLocked("admin", out _));

        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.False(limiter.IsLocked("admin", out _));
    }

    [Fact]
    public void Failure_After_Lock_Expiry_Starts_Fresh_Count()
    {
        var limiter = CreateLimiter(maxFailedAttempts: 2, lockoutSeconds: 10);

        limiter.RecordFailure("admin");
        limiter.RecordFailure("admin");
        _clock.Advance(TimeSpan.FromSeconds(11));
        limiter.RecordFailure("admin");

        Assert.False(limiter.IsLocked("admin", out _));
    }
}
