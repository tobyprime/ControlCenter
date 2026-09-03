using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 告警规则状态存储（alert_state）：防刷屏与重启去重的持久化依据——
/// 离线已告警、越限事件的首见/最近告警时间都落库，面板重启不会重复告警同一事件。
/// </summary>
public interface IAlertStateStore
{
    string? Get(string ruleKey);

    void Set(string ruleKey, string stateJson, DateTimeOffset nowUtc);

    void Delete(string ruleKey);
}

public sealed class AlertStateStore : IAlertStateStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AlertStateStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? Get(string ruleKey)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM alert_state WHERE rule_key = $ruleKey";
        command.Parameters.AddWithValue("$ruleKey", ruleKey);
        return command.ExecuteScalar() as string;
    }

    public void Set(string ruleKey, string stateJson, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_state(rule_key, state_json, updated_at_utc) VALUES ($ruleKey, $stateJson, $updatedAt)
            ON CONFLICT(rule_key) DO UPDATE SET state_json = excluded.state_json, updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$ruleKey", ruleKey);
        command.Parameters.AddWithValue("$stateJson", stateJson);
        command.Parameters.AddWithValue("$updatedAt", nowUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete(string ruleKey)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_state WHERE rule_key = $ruleKey";
        command.Parameters.AddWithValue("$ruleKey", ruleKey);
        command.ExecuteNonQuery();
    }

    internal static T? Read<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json);
}
