using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>一条告警规则实例：绑定 (target, metric) 与规则类型，参数用户可配、可关闭（TOB-360 约束 B）。</summary>
public sealed record AlertRule(
    long Id,
    long TargetId,
    string? Metric,
    string RuleType,
    string ParamsJson,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IAlertRuleStore
{
    AlertRule Create(long targetId, string? metric, string ruleType, string paramsJson, bool enabled);

    AlertRule? Get(long id);

    AlertRule? Update(long id, string? metric, string ruleType, string paramsJson, bool enabled);

    bool SetEnabled(long id, bool enabled);

    bool Delete(long id);

    IReadOnlyList<AlertRule> List(long? targetId = null, string? ruleType = null, bool? enabled = null);

    IReadOnlyList<AlertRule> ListForTargetMetric(long targetId, string metric);

    AlertRule? Find(long targetId, string? metric, string ruleType);
}

/// <summary>告警规则存储（alert_rules）：随目标级联删除。</summary>
public sealed class AlertRuleStore : IAlertRuleStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AlertRuleStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AlertRule Create(long targetId, string? metric, string ruleType, string paramsJson, bool enabled)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_rules(target_id, metric, rule_type, params_json, enabled, created_at_utc, updated_at_utc)
            VALUES ($targetId, $metric, $ruleType, $paramsJson, $enabled, $createdAt, $updatedAt)
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metric", (object?)metric ?? DBNull.Value);
        command.Parameters.AddWithValue("$ruleType", ruleType);
        command.Parameters.AddWithValue("$paramsJson", paramsJson);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.ExecuteNonQuery();

        using var selectId = connection.CreateCommand();
        selectId.CommandText = "SELECT last_insert_rowid()";
        var id = (long)(selectId.ExecuteScalar() ?? 0L);
        return new AlertRule(id, targetId, metric, ruleType, paramsJson, enabled, nowUtc, nowUtc);
    }

    public AlertRule? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return ReadOne(command);
    }

    public AlertRule? Update(long id, string? metric, string ruleType, string paramsJson, bool enabled)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_rules
            SET metric = $metric, rule_type = $ruleType, params_json = $paramsJson, enabled = $enabled, updated_at_utc = $updatedAt
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$metric", (object?)metric ?? DBNull.Value);
        command.Parameters.AddWithValue("$ruleType", ruleType);
        command.Parameters.AddWithValue("$paramsJson", paramsJson);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_rules SET enabled = $enabled, updated_at_utc = $updatedAt WHERE id = $id
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<AlertRule> List(long? targetId = null, string? ruleType = null, bool? enabled = null)
    {
        var conditions = new List<string>();
        if (targetId is not null)
        {
            conditions.Add("target_id = $targetId");
        }

        if (ruleType is not null)
        {
            conditions.Add("rule_type = $ruleType");
        }

        if (enabled is not null)
        {
            conditions.Add("enabled = $enabled");
        }

        var rules = new List<AlertRule>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty) + " ORDER BY id";
        if (targetId is not null)
        {
            command.Parameters.AddWithValue("$targetId", targetId.Value);
        }

        if (ruleType is not null)
        {
            command.Parameters.AddWithValue("$ruleType", ruleType);
        }

        if (enabled is not null)
        {
            command.Parameters.AddWithValue("$enabled", enabled.Value ? 1 : 0);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(Map(reader));
        }

        return rules;
    }

    public IReadOnlyList<AlertRule> ListForTargetMetric(long targetId, string metric)
    {
        var rules = new List<AlertRule>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE target_id = $targetId AND metric = $metric AND enabled = 1 ORDER BY id";
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metric", metric);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add(Map(reader));
        }

        return rules;
    }

    public AlertRule? Find(long targetId, string? metric, string ruleType)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE target_id = $targetId AND metric IS $metric AND rule_type = $ruleType ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metric", (object?)metric ?? DBNull.Value);
        command.Parameters.AddWithValue("$ruleType", ruleType);
        return ReadOne(command);
    }

    private const string SelectSql =
        "SELECT id, target_id, metric, rule_type, params_json, enabled, created_at_utc, updated_at_utc FROM alert_rules";

    private static AlertRule? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static AlertRule Map(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5) != 0,
        DateTimeOffset.Parse(reader.GetString(6)),
        DateTimeOffset.Parse(reader.GetString(7)));

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
