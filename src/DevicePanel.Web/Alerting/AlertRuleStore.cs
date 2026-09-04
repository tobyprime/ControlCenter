using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>一条告警规则实例：TargetId 为 NULL 表示全局规则（作用于所有上报该指标的目标），非空表示目标级规则。</summary>
public sealed record AlertRule(
    long Id,
    long? TargetId,
    string MetricKey,
    string RuleType,
    bool Enabled,
    string ParametersJson,
    int SustainSeconds,
    int RepeatMinutes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 告警规则实例存储（alert_rules）。同一 (target, metric, rule_type) 唯一：目标级与全局可并存，
/// 评估时目标级优先（与一期"按设备覆盖 ?? 全局默认"优先级一致）。
/// </summary>
public interface IAlertRuleStore
{
    /// <summary>新建规则；(target, metric, rule_type) 重复抛 InvalidOperationException。</summary>
    AlertRule Create(long? targetId, string metricKey, string ruleType, string parametersJson, int sustainSeconds, int repeatMinutes, bool enabled);

    AlertRule? Get(long id);

    /// <summary>规则列表（可按目标/指标过滤；targetId 传 -1 表示仅全局规则）。</summary>
    IReadOnlyList<AlertRule> List(long? targetId = null, string? metricKey = null);

    /// <summary>更新参数/防抖/重发间隔/启停（目标、指标、类型不可变）。返回 null 表示规则不存在。</summary>
    AlertRule? Update(long id, string parametersJson, int sustainSeconds, int repeatMinutes, bool enabled);

    bool Delete(long id);

    /// <summary>按 (target, metric, rule_type) 精确查找（target 为 null 匹配全局规则）。</summary>
    AlertRule? Find(long? targetId, string metricKey, string ruleType);

    /// <summary>对该 (target, metric) 生效的规则：目标级 + 全局（仅 enabled）。</summary>
    IReadOnlyList<AlertRule> ListApplicable(long targetId, string metricKey);

    /// <summary>某类型的全部启用规则（后台扫描用）。</summary>
    IReadOnlyList<AlertRule> ListEnabledByType(string ruleType);

    long CountByMetricKey(string metricKey);
}

public sealed class AlertRuleStore : IAlertRuleStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AlertRuleStore(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public AlertRule Create(long? targetId, string metricKey, string ruleType, string parametersJson, int sustainSeconds, int repeatMinutes, bool enabled)
    {
        // 全局规则（target_id 为 NULL）不受 UNIQUE 约束去重（SQLite 视 NULL 互异），在这里统一查重
        if (Find(targetId, metricKey, ruleType) is not null)
        {
            throw new InvalidOperationException("同一目标的同一指标只能有一条同类型规则");
        }

        var nowUtc = _timeProvider.GetUtcNow();
        try
        {
            using var connection = _connectionFactory.CreateOpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alert_rules(target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc)
                VALUES ($targetId, $metricKey, $ruleType, $enabled, $parametersJson, $sustainSeconds, $repeatMinutes, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$targetId", (object?)targetId ?? DBNull.Value);
            command.Parameters.AddWithValue("$metricKey", metricKey);
            command.Parameters.AddWithValue("$ruleType", ruleType);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$parametersJson", parametersJson);
            command.Parameters.AddWithValue("$sustainSeconds", sustainSeconds);
            command.Parameters.AddWithValue("$repeatMinutes", repeatMinutes);
            command.Parameters.AddWithValue("$createdAt", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", nowUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("同一目标的同一指标只能有一条同类型规则");
        }

        // 自增 id 由数据库分配；Create 紧随 List 便于返回完整实体
        return List(targetId, metricKey).First(r => r.RuleType == ruleType);
    }

    /// <summary>UNIQUE 约束冲突（SQLITE_CONSTRAINT / SQLITE_CONSTRAINT_UNIQUE，兼容主码与扩展码）。</summary>
    private static bool IsUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode is 19 or 2067;

    public AlertRule? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AlertRule> List(long? targetId = null, string? metricKey = null)
    {
        var conditions = new List<string>();
        if (targetId.HasValue)
        {
            conditions.Add(targetId.Value == GlobalOnly ? "target_id IS NULL" : "target_id = $targetId");
        }

        if (!string.IsNullOrEmpty(metricKey))
        {
            conditions.Add("metric_key = $metricKey");
        }

        var where = conditions.Count > 0 ? $" WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        var rules = new List<AlertRule>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + where + " ORDER BY target_id IS NOT NULL, metric_key, rule_type, id";
        if (targetId is { } tid && tid != GlobalOnly)
        {
            command.Parameters.AddWithValue("$targetId", tid);
        }

        if (!string.IsNullOrEmpty(metricKey))
        {
            command.Parameters.AddWithValue("$metricKey", metricKey);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(Map(reader));
        }

        return rules;
    }

    public AlertRule? Update(long id, string parametersJson, int sustainSeconds, int repeatMinutes, bool enabled)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_rules
            SET parameters_json = $parametersJson, sustain_seconds = $sustainSeconds,
                repeat_minutes = $repeatMinutes, enabled = $enabled, updated_at_utc = $updatedAt
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$parametersJson", parametersJson);
        command.Parameters.AddWithValue("$sustainSeconds", sustainSeconds);
        command.Parameters.AddWithValue("$repeatMinutes", repeatMinutes);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public bool Delete(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public AlertRule? Find(long? targetId, string metricKey, string ruleType)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE metric_key = $metricKey AND rule_type = $ruleType AND target_id IS $targetId";
        command.Parameters.AddWithValue("$metricKey", metricKey);
        command.Parameters.AddWithValue("$ruleType", ruleType);
        command.Parameters.AddWithValue("$targetId", (object?)targetId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AlertRule> ListApplicable(long targetId, string metricKey)
    {
        var rules = new List<AlertRule>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE enabled = 1 AND metric_key = $metricKey AND (target_id = $targetId OR target_id IS NULL) ORDER BY id";
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metricKey", metricKey);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(Map(reader));
        }

        return rules;
    }

    public IReadOnlyList<AlertRule> ListEnabledByType(string ruleType)
    {
        var rules = new List<AlertRule>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE enabled = 1 AND rule_type = $ruleType ORDER BY id";
        command.Parameters.AddWithValue("$ruleType", ruleType);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(Map(reader));
        }

        return rules;
    }

    public long CountByMetricKey(string metricKey)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alert_rules WHERE metric_key = $metricKey";
        command.Parameters.AddWithValue("$metricKey", metricKey);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private const long GlobalOnly = -1;

    private const string SelectSql = """
        SELECT id, target_id, metric_key, rule_type, enabled, parameters_json, sustain_seconds, repeat_minutes, created_at_utc, updated_at_utc
        FROM alert_rules
        """;

    private static AlertRule Map(SqliteDataReader reader)
    {
        return new AlertRule(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.GetString(5),
            (int)reader.GetInt64(6),
            (int)reader.GetInt64(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9)));
    }
}
