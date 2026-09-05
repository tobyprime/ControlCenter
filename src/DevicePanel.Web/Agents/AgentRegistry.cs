using System.Security.Cryptography;
using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Targets;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Agents;

/// <summary>Agent 实体（三期模块2）：连接身份与能力声明的唯一宿主。TargetId 非空表示与 target 处于双写期关联。</summary>
public sealed record AgentInfo(
    long Id,
    string Name,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string>? Capabilities,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    long? TargetId = null)
{
    /// <summary>在线 = 最近心跳距当前时间不超过连续 2 个心跳周期（AgentOptions.OfflineAfter），与 target 在线口径一致。</summary>
    public bool IsOnline(TimeProvider clock, AgentOptions options) =>
        LastSeenAtUtc is { } lastSeen && clock.GetUtcNow() - lastSeen <= options.OfflineAfter;
}

public sealed record AgentCreated(AgentInfo Agent, string Token);

/// <summary>
/// Agent 台账：一 agent 一 token（明文只在创建/重置时返回一次，库中仅存 SHA-256）；标签为自由文本不限量；
/// 能力声明由 agent 连接后上报持久化（未声明 = null，旧版 agent 兼容）。
/// token 认证只走本表；target 侧 agent_token_hash 退化为镜像列（双写期保持一致，不参与认证）。
/// </summary>
public interface IAgentRegistry
{
    AgentCreated Create(string name, IReadOnlyList<string> labels);

    long? FindAgentIdByToken(string token);

    /// <summary>重置 token：旧 token 立即失效；关联 target 的镜像 hash 同步平移。</summary>
    string? ResetToken(long agentId);

    AgentInfo? UpdateLabels(long agentId, IReadOnlyList<string> labels);

    AgentInfo? Get(long agentId);

    IReadOnlyList<AgentInfo> List(string? label = null);

    /// <summary>持久化能力声明（去重保序）；不视为管理编辑，不刷新 updated_at。返回 false 表示 agent 不存在。</summary>
    bool SetCapabilities(long agentId, IReadOnlyList<string> capabilities);

    void Touch(long agentId, DateTimeOffset seenAtUtc);

    bool Delete(long agentId);

    long? FindTargetIdByAgentId(long agentId);

    long? FindAgentIdByTargetId(long targetId);
}

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AgentRegistry(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public AgentCreated Create(string name, IReadOnlyList<string> labels)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var token = GenerateToken();
        using var connection = _connectionFactory.CreateOpenConnection();
        long id;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO agents(name, labels_json, token_hash, capabilities_json, created_at_utc, updated_at_utc)
                VALUES ($name, $labels, $tokenHash, NULL, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(NormalizeLabels(labels)));
            command.Parameters.AddWithValue("$tokenHash", HashToken(token));
            command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
            command.ExecuteNonQuery();
        }

        using var selectId = connection.CreateCommand();
        selectId.CommandText = "SELECT last_insert_rowid()";
        id = (long)(selectId.ExecuteScalar() ?? 0L);

        var agent = new AgentInfo(id, name, NormalizeLabels(labels), null, nowUtc, nowUtc, null);
        return new AgentCreated(agent, token);
    }

    public long? FindAgentIdByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM agents WHERE token_hash = $tokenHash";
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        return command.ExecuteScalar() as long?;
    }

    public string? ResetToken(long agentId)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var token = GenerateToken();
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE agents SET token_hash = $tokenHash, updated_at_utc = $updatedAt
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$tokenHash", HashToken(token));
            command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
            command.Parameters.AddWithValue("$id", agentId);
            if (command.ExecuteNonQuery() == 0)
            {
                return null;
            }
        }

        // 双写镜像：target 侧 hash 同步平移（历史 NOT NULL UNIQUE 约束保留），认证链路不读该列
        using var mirror = connection.CreateCommand();
        mirror.CommandText = "UPDATE targets SET agent_token_hash = $tokenHash WHERE agent_id = $id";
        mirror.Parameters.AddWithValue("$tokenHash", HashToken(token));
        mirror.Parameters.AddWithValue("$id", agentId);
        mirror.ExecuteNonQuery();

        return token;
    }

    public AgentInfo? UpdateLabels(long agentId, IReadOnlyList<string> labels)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var normalized = NormalizeLabels(labels);
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents SET labels_json = $labels, updated_at_utc = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$labels", JsonSerializer.Serialize(normalized));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", agentId);
        return command.ExecuteNonQuery() == 0 ? null : Get(agentId);
    }

    public AgentInfo? Get(long agentId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM agents WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapAgent(reader) : null;
    }

    public IReadOnlyList<AgentInfo> List(string? label = null)
    {
        var agents = new List<AgentInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = label is null
            ? $"""
                SELECT {SelectColumns}
                FROM agents ORDER BY id
                """
            : $"""
                SELECT {SelectColumns}
                FROM agents
                WHERE EXISTS (SELECT 1 FROM json_each(agents.labels_json) WHERE json_each.value = $label)
                ORDER BY id
                """;
        if (label is not null)
        {
            command.Parameters.AddWithValue("$label", label);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            agents.Add(MapAgent(reader));
        }

        return agents;
    }

    public bool SetCapabilities(long agentId, IReadOnlyList<string> capabilities)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        // 能力声明由 agent 上报产生，不是管理编辑：不刷新 updated_at
        command.CommandText = "UPDATE agents SET capabilities_json = $capabilities WHERE id = $id";
        command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(NormalizeLabels(capabilities)));
        command.Parameters.AddWithValue("$id", agentId);
        return command.ExecuteNonQuery() > 0;
    }

    public void Touch(long agentId, DateTimeOffset seenAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agents SET last_seen_at_utc = $seenAt WHERE id = $id";
        command.Parameters.AddWithValue("$seenAt", FormatUtc(seenAtUtc));
        command.Parameters.AddWithValue("$id", agentId);
        command.ExecuteNonQuery();
    }

    public bool Delete(long agentId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agents WHERE id = $id";
        command.Parameters.AddWithValue("$id", agentId);
        return command.ExecuteNonQuery() > 0;
    }

    public long? FindTargetIdByAgentId(long agentId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM targets WHERE agent_id = $id";
        command.Parameters.AddWithValue("$id", agentId);
        return command.ExecuteScalar() as long?;
    }

    public long? FindAgentIdByTargetId(long targetId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_id FROM targets WHERE id = $id";
        command.Parameters.AddWithValue("$id", targetId);
        return command.ExecuteScalar() as long?;
    }

    /// <summary>关联目标的 id 以子查询带出（关联列在 targets 侧），无关联时为 NULL。</summary>
    private const string SelectColumns = """
        agents.id, agents.name, agents.labels_json, agents.capabilities_json, agents.created_at_utc, agents.updated_at_utc, agents.last_seen_at_utc,
        (SELECT t.id FROM targets t WHERE t.agent_id = agents.id) AS target_id
        """;

    private static AgentInfo MapAgent(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var name = reader.GetString(1);
        var labels = ParseJsonArray(reader.GetString(2));
        var capabilitiesColumn = reader.IsDBNull(3) ? null : reader.GetString(3);
        var capabilities = capabilitiesColumn is null ? null : (IReadOnlyList<string>?)ParseJsonArray(capabilitiesColumn);
        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(5));
        var lastSeenColumn = reader.IsDBNull(6) ? null : reader.GetString(6);
        var lastSeen = lastSeenColumn is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastSeenColumn);
        var targetId = reader.IsDBNull(7) ? null : (long?)reader.GetInt64(7);
        return new AgentInfo(id, name, labels, capabilities, createdAt, updatedAt, lastSeen, targetId);
    }

    private static List<string> ParseJsonArray(string json)
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

    /// <summary>自由文本标签：仅去除首尾空白、丢弃空串并去重，不限数量与长度。</summary>
    private static List<string> NormalizeLabels(IReadOnlyList<string> labels) =>
        labels.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();

    private static string GenerateToken() => TargetRegistry.TokenTypePrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
