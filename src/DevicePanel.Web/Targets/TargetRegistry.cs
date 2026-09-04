using System.Security.Cryptography;
using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Targets;

/// <summary>目标类型：device = agent 回连接入的计算设备；service = 服务级监测目标（HTTP 探针等，后续模块接入）。</summary>
public static class TargetTypes
{
    public const string Device = "device";
    public const string Service = "service";

    public static bool IsValid(string type) => type is Device or Service;
}

public sealed record TargetInfo(
    long Id,
    string Type,
    string Name,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc)
{
    /// <summary>在线 = 最近心跳距当前时间不超过连续 2 个心跳周期（AgentOptions.OfflineAfter）；心跳语义仅 device 类目标有。</summary>
    public bool IsOnline(TimeProvider clock, AgentOptions options) =>
        LastSeenAtUtc is { } lastSeen && clock.GetUtcNow() - lastSeen <= options.OfflineAfter;
}

public sealed record TargetCreated(TargetInfo Target, string AgentToken);

/// <summary>
/// 目标台账（设备与服务统一实体，二期模块0）：CRUD 与 agent token 签发/重置。
/// token 明文只在创建/重置时返回一次，库中仅存 SHA-256(token)；重置即覆盖唯一 hash，旧 token 立即失效。
/// service 类目标不走 agent 通道，token 仅占位。
/// </summary>
public interface ITargetRegistry
{
    TargetCreated Create(string type, string name, IReadOnlyList<string> tags);

    TargetInfo? Update(long id, string name, IReadOnlyList<string> tags);

    bool Delete(long id);

    string? ResetToken(long id);

    TargetInfo? Get(long id);

    IReadOnlyList<TargetInfo> List();

    long? FindTargetIdByToken(string token);

    void Touch(long targetId, DateTimeOffset seenAtUtc);
}

public sealed class TargetRegistry : ITargetRegistry
{
    public const string TokenTypePrefix = "dpk_";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public TargetRegistry(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public TargetCreated Create(string type, string name, IReadOnlyList<string> tags)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var token = GenerateToken();
        using var connection = _connectionFactory.CreateOpenConnection();
        long id;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO targets(type, name, tags_json, agent_token_hash, created_at_utc, updated_at_utc)
                VALUES ($type, $name, $tags, $tokenHash, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$type", type);
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

        var target = new TargetInfo(id, type, name, tags.ToArray(), nowUtc, nowUtc, null);
        return new TargetCreated(target, token);
    }

    public TargetInfo? Update(long id, string name, IReadOnlyList<string> tags)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE targets SET name = $name, tags_json = $tags, updated_at_utc = $updatedAt
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
        command.CommandText = "DELETE FROM targets WHERE id = $id";
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
            UPDATE targets SET agent_token_hash = $tokenHash, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 0 ? null : token;
    }

    public TargetInfo? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, name, tags_json, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM targets WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapTarget(reader) : null;
    }

    public IReadOnlyList<TargetInfo> List()
    {
        var targets = new List<TargetInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, name, tags_json, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM targets ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            targets.Add(MapTarget(reader));
        }

        return targets;
    }

    public long? FindTargetIdByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM targets WHERE agent_token_hash = $tokenHash";
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        return command.ExecuteScalar() as long?;
    }

    public void Touch(long targetId, DateTimeOffset seenAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE targets SET last_seen_at_utc = $seenAt WHERE id = $id";
        command.Parameters.AddWithValue("$seenAt", FormatUtc(seenAtUtc));
        command.Parameters.AddWithValue("$id", targetId);
        command.ExecuteNonQuery();
    }

    private static TargetInfo MapTarget(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var type = reader.GetString(1);
        var name = reader.GetString(2);
        var tags = ParseTags(reader.GetString(3));
        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(5));
        var lastSeenColumn = reader.IsDBNull(6) ? null : reader.GetString(6);
        var lastSeen = lastSeenColumn is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastSeenColumn);
        return new TargetInfo(id, type, name, tags, createdAt, updatedAt, lastSeen);
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
