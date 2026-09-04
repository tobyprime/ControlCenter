using System.Security.Cryptography;
using DevicePanel.Web.Infrastructure;

namespace DevicePanel.Web.Auth;

public sealed record SessionInfo(string Username, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 服务端会话：token 只出现在 Cookie，库里仅存 SHA-256(token)；
/// 登出即删行，会话立即失效；到期判定用注入的 TimeProvider（UTC）。
/// </summary>
public interface ISessionService
{
    string Create(string username);

    SessionInfo? Validate(string token);

    void Invalidate(string token);
}

public sealed class SessionService : ISessionService
{
    public const string TokenTypePrefix = "dps_";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;

    public SessionService(
        SqliteConnectionFactory connectionFactory,
        AuthOptions options,
        TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _timeProvider = timeProvider;
    }

    public string Create(string username)
    {
        var token = TokenTypePrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var nowUtc = _timeProvider.GetUtcNow();
        var expiresUtc = nowUtc.AddHours(_options.SessionHours);

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(token_hash, username, created_at_utc, expires_at_utc)
            VALUES ($tokenHash, $username, $createdAt, $expiresAt)
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$expiresAt", FormatUtc(expiresUtc));
        command.ExecuteNonQuery();

        return token;
    }

    public SessionInfo? Validate(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT username, expires_at_utc FROM sessions WHERE token_hash = $tokenHash";
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var username = reader.GetString(0);
        if (!DateTimeOffset.TryParse(reader.GetString(1), out var expiresUtc))
        {
            return null;
        }

        if (expiresUtc <= _timeProvider.GetUtcNow())
        {
            Invalidate(token);
            return null;
        }

        return new SessionInfo(username, expiresUtc);
    }

    public void Invalidate(string token)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE token_hash = $tokenHash";
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.ExecuteNonQuery();
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
