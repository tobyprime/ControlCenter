using DevicePanel.Web.Auth;
using DevicePanel.Web.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class SessionServiceTests
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    public SessionServiceTests()
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO users(username, password_hash, created_at_utc) VALUES ('admin', 'pbkdf2-sha256:100000:x:y', '2026-09-03T12:00:00.000Z')";
        command.ExecuteNonQuery();
    }

    private SessionService CreateService(int sessionHours = 24)
    {
        return new SessionService(
            _db.Factory,
            new AuthOptions { SessionHours = sessionHours },
            _clock);
    }

    [Fact]
    public void Created_Token_Validates_And_Returns_Username()
    {
        var service = CreateService();
        var token = service.Create("admin");

        var session = service.Validate(token);

        Assert.NotNull(session);
        Assert.Equal("admin", session.Username);
    }

    [Fact]
    public void Unknown_Token_Is_Invalid()
    {
        var service = CreateService();
        service.Create("admin");

        Assert.Null(service.Validate("dps_unknown-token"));
        Assert.Null(service.Validate(""));
    }

    [Fact]
    public void Invalidate_Makes_Session_Immediately_Invalid()
    {
        var service = CreateService();
        var token = service.Create("admin");

        service.Invalidate(token);

        Assert.Null(service.Validate(token));
    }

    [Fact]
    public void Expired_Session_Is_Rejected()
    {
        var service = CreateService(sessionHours: 24);
        var token = service.Create("admin");

        _clock.Advance(TimeSpan.FromHours(25));

        Assert.Null(service.Validate(token));
    }

    [Fact]
    public void Session_Is_Still_Valid_Within_Expiry_Window()
    {
        var service = CreateService(sessionHours: 24);
        var token = service.Create("admin");

        _clock.Advance(TimeSpan.FromHours(23));

        Assert.NotNull(service.Validate(token));
    }

    [Fact]
    public void Database_Stores_Token_Hash_Not_Plaintext_Token()
    {
        var service = CreateService();
        var token = service.Create("admin");

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM sessions";
        var storedHash = Convert.ToString(command.ExecuteScalar());

        Assert.NotNull(storedHash);
        Assert.DoesNotContain(token, storedHash);
        Assert.Equal(64, storedHash!.Length);
    }

    [Fact]
    public void Session_Timestamps_Are_Stored_As_Utc()
    {
        var service = CreateService(sessionHours: 24);
        var token = service.Create("admin");

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT created_at_utc, expires_at_utc FROM sessions";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var createdAt = DateTimeOffset.Parse(reader.GetString(0));
        var expiresAt = DateTimeOffset.Parse(reader.GetString(1));

        Assert.Equal(TimeSpan.Zero, createdAt.Offset);
        Assert.Equal(TimeSpan.Zero, expiresAt.Offset);
        Assert.Equal(_clock.GetUtcNow(), createdAt);
        Assert.Equal(_clock.GetUtcNow().AddHours(24), expiresAt);
    }

    public void Dispose() => _db.Dispose();
}
