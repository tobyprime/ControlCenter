using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Collectors;

/// <summary>
/// 内置标签（三期模块3）：device / service 语义不再硬分类，经标签保留——与自定义标签同渠道（可编辑、可筛选）。
/// 内置标签由服务端在创建/更新时维护（每台采集器恰有一个 type:* 标签），客户端传入的同名标签被忽略。
/// </summary>
public static class CollectorBuiltinTags
{
    public const string Device = "type:device";
    public const string Service = "type:service";

    public static bool Contains(IReadOnlyList<string> tags, string builtin) => tags.Contains(builtin);

    /// <summary>去除用户传入的全部 type:* 标签（内置标签语义服务端所有），保留自定义标签。</summary>
    public static List<string> Strip(IReadOnlyList<string> tags) => tags.Where(t => !t.StartsWith("type:", StringComparison.Ordinal)).ToList();
}

public sealed record CollectorInfo(
    long Id,
    string Name,
    IReadOnlyList<string> Tags,
    long? AgentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc)
{
    /// <summary>在线 = 最近心跳距当前时间不超过连续 2 个心跳周期（AgentOptions.OfflineAfter）；仅 push 采集器有心跳语义。</summary>
    public bool IsOnline(TimeProvider clock, AgentOptions options) =>
        LastSeenAtUtc is { } lastSeen && clock.GetUtcNow() - lastSeen <= options.OfflineAfter;

    /// <summary>采集器模式由关联推导而非存储：关联 agent = push（agent 回连上报）；有 pull 配置 = pull（面板侧轮询）。</summary>
    public bool IsPush => AgentId is not null;
}

/// <summary>
/// 采集器台账（targets → collectors 泛化，三期模块3）：统一 push/pull 两类采集器的 CRUD，无 type 列。
/// push 采集器创建时携带关联 agentId（token hash 镜像其值）；pull 采集器 agentId 为空（占位 hash）。
/// token 认证只查 agents 表，本表不再参与。
/// </summary>
public interface ICollectorRegistry
{
    /// <summary>创建采集器。tags 由调用方组装（含内置 type:* 标签）；push 型传入关联 agentId。</summary>
    CollectorInfo Create(string name, IReadOnlyList<string> tags, long? agentId = null);

    CollectorInfo? Update(long id, string name, IReadOnlyList<string> tags);

    bool Delete(long id);

    CollectorInfo? Get(long id);

    IReadOnlyList<CollectorInfo> List();

    void Touch(long collectorId, DateTimeOffset seenAtUtc);
}

public sealed class CollectorRegistry : ICollectorRegistry
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public CollectorRegistry(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public CollectorInfo Create(string name, IReadOnlyList<string> tags, long? agentId = null)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        long id;
        using (var command = connection.CreateCommand())
        {
            // push 型采集器：token hash 镜像关联 agent 的值（子查询）并落 agent_id 关联列；pull 型无 agent，占位 hash 满足历史 NOT NULL UNIQUE 约束
            command.CommandText = """
                INSERT INTO collectors(name, tags_json, agent_token_hash, agent_id, created_at_utc, updated_at_utc)
                VALUES ($name, $tags,
                        COALESCE((SELECT token_hash FROM agents WHERE id = $agentId), $placeholderHash),
                        $agentId, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$tags", SerializeTags(tags));
            command.Parameters.AddWithValue("$agentId", agentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$placeholderHash", AgentToken.Hash(AgentToken.Generate()));
            command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
            command.ExecuteNonQuery();
        }

        using var selectId = connection.CreateCommand();
        selectId.CommandText = "SELECT last_insert_rowid()";
        id = (long)(selectId.ExecuteScalar() ?? 0L);

        return new CollectorInfo(id, name, tags.ToArray(), agentId, nowUtc, nowUtc, null);
    }

    public CollectorInfo? Update(long id, string name, IReadOnlyList<string> tags)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE collectors SET name = $name, tags_json = $tags, updated_at_utc = $updatedAt
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
        command.CommandText = "DELETE FROM collectors WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public CollectorInfo? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, tags_json, agent_id, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM collectors WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapCollector(reader) : null;
    }

    public IReadOnlyList<CollectorInfo> List()
    {
        var collectors = new List<CollectorInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, tags_json, agent_id, created_at_utc, updated_at_utc, last_seen_at_utc
            FROM collectors ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            collectors.Add(MapCollector(reader));
        }

        return collectors;
    }

    public void Touch(long collectorId, DateTimeOffset seenAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE collectors SET last_seen_at_utc = $seenAt WHERE id = $id";
        command.Parameters.AddWithValue("$seenAt", FormatUtc(seenAtUtc));
        command.Parameters.AddWithValue("$id", collectorId);
        command.ExecuteNonQuery();
    }

    private static CollectorInfo MapCollector(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var name = reader.GetString(1);
        var tags = ParseTags(reader.GetString(2));
        var agentId = reader.IsDBNull(3) ? null : (long?)reader.GetInt64(3);
        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(5));
        var lastSeenColumn = reader.IsDBNull(6) ? null : reader.GetString(6);
        var lastSeen = lastSeenColumn is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastSeenColumn);
        return new CollectorInfo(id, name, tags, agentId, createdAt, updatedAt, lastSeen);
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

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
