using System.Security.Cryptography;
using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Devices;

public sealed record DeviceInfo(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc)
{
    /// <summary>在线 = 最近心跳距当前时间不超过连续 2 个心跳周期（AgentOptions.OfflineAfter）。</summary>
    public bool IsOnline(TimeProvider clock, AgentOptions options) =>
        LastSeenAtUtc is { } lastSeen && clock.GetUtcNow() - lastSeen <= options.OfflineAfter;
}

public sealed record DeviceCreated(DeviceInfo Device, string AgentToken);

/// <summary>
/// 设备台账：CRUD 与 agent token 签发/重置。
/// token 明文只在创建/重置时返回一次，库中仅存 SHA-256(token)；重置即覆盖唯一 hash，旧 token 立即失效。
/// </summary>
public interface IDeviceRegistry
{
    DeviceCreated Create(string name, IReadOnlyList<string> tags);

    DeviceInfo? Update(long id, string name, IReadOnlyList<string> tags);

    bool Delete(long id);

    string? ResetToken(long id);

    DeviceInfo? Get(long id);

    IReadOnlyList<DeviceInfo> List();

    long? FindDeviceIdByToken(string token);

    void Touch(long deviceId, DateTimeOffset seenAtUtc);
}

public sealed class DeviceRegistry : IDeviceRegistry
{
    public const string TokenTypePrefix = "dpk_";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public DeviceRegistry(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public DeviceCreated Create(string name, IReadOnlyList<string> tags)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var token = GenerateToken();
        using var connection = _connectionFactory.CreateOpenConnection();
        long id;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO devices(name, tags_json, agent_token_hash, created_at_utc, updated_at_utc)
                VALUES ($name, $tags, $tokenHash, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$tags", SerializeTags(tags));
            command.Parameters.AddWithValue("$tokenHash", HashToken(token));
            command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
            command.ExecuteNonQuery();
        }

        using var selectId = connection.CreateCommand();
        selectId.CommandText = "SELECT last_insert_rowid()";
        id = (long)(selectId.ExecuteScalar() ?? 0L);

        var device = new DeviceInfo(id, name, tags.ToArray(), nowUtc, nowUtc, null);
        return new DeviceCreated(device, token);
    }

    public DeviceInfo? Update(long id, string name, IReadOnlyList<string> tags)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET name = $name, tags_json = $tags, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$tags", SerializeTags(tags));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
        {
            return null;
        }

        return Get(id);
    }

    public bool Delete(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM devices WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public string? ResetToken(long id)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var token = GenerateToken();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE devices SET agent_token_hash = $tokenHash, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 0 ? null : token;
    }

    public DeviceInfo? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, tags_json, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM devices WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapDevice(reader) : null;
    }

    public IReadOnlyList<DeviceInfo> List()
    {
        var devices = new List<DeviceInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, tags_json, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM devices ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            devices.Add(MapDevice(reader));
        }

        return devices;
    }

    public long? FindDeviceIdByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM devices WHERE agent_token_hash = $tokenHash";
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        return command.ExecuteScalar() as long?;
    }

    public void Touch(long deviceId, DateTimeOffset seenAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE devices SET last_seen_at_utc = $seenAt WHERE id = $id";
        command.Parameters.AddWithValue("$seenAt", FormatUtc(seenAtUtc));
        command.Parameters.AddWithValue("$id", deviceId);
        command.ExecuteNonQuery();
    }

    private static DeviceInfo MapDevice(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var name = reader.GetString(1);
        var tags = ParseTags(reader.GetString(2));
        var createdAt = DateTimeOffset.Parse(reader.GetString(3));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(4));
        var lastSeenColumn = reader.IsDBNull(5) ? null : reader.GetString(5);
        var lastSeen = lastSeenColumn is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastSeenColumn);
        return new DeviceInfo(id, name, tags, createdAt, updatedAt, lastSeen);
    }

    private static string SerializeTags(IReadOnlyList<string> tags) => JsonSerializer.Serialize(tags);

    private static IReadOnlyList<string> ParseTags(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GenerateToken() => TokenTypePrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
