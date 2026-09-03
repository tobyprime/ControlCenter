using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>待发队列中的一行：按渠道排队，失败记账（attempts/last_error）后仍在队列等待补发。</summary>
public sealed record AlertOutboxEntry(
    long Id,
    DateTimeOffset CreatedAtUtc,
    string Channel,
    AlertMessage Message,
    int Attempts,
    string? LastError);

public interface IAlertOutboxStore
{
    void Enqueue(string channel, AlertMessage message, DateTimeOffset nowUtc);

    /// <summary>最老的一条待发消息（FIFO 队头）；队列为空返回 null。</summary>
    AlertOutboxEntry? PeekOldest();

    /// <summary>发送成功后移除。</summary>
    void MarkSent(long id);

    /// <summary>发送失败：attempts+1 并记录错误，行保留在队列（无丢失契约）。</summary>
    void RecordFailure(long id, string error, DateTimeOffset nowUtc);

    IReadOnlyList<AlertOutboxEntry> List();

    long Count();
}

/// <summary>
/// 本地待发队列（SQLite 持久化）：渠道不可用时告警落库，恢复后由 AlertDispatchWorker 按 FIFO 补发；
/// 队列随库持久化，面板重启不丢。
/// </summary>
public sealed class AlertOutboxStore : IAlertOutboxStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public AlertOutboxStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void Enqueue(string channel, AlertMessage message, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_outbox(created_at_utc, channel, payload_json)
            VALUES ($createdAt, $channel, $payload)
            """;
        command.Parameters.AddWithValue("$createdAt", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$payload", AlertDispatcher.Serialize(message));
        command.ExecuteNonQuery();
    }

    public AlertOutboxEntry? PeekOldest() => ReadOne("SELECT * FROM alert_outbox ORDER BY id LIMIT 1");

    public void MarkSent(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_outbox WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void RecordFailure(long id, string error, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_outbox
            SET attempts = attempts + 1, last_error = $error, last_attempt_utc = $attemptedAt
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$attemptedAt", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AlertOutboxEntry> List()
    {
        var entries = new List<AlertOutboxEntry>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM alert_outbox ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(MapEntry(reader));
        }

        return entries;
    }

    public long Count()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alert_outbox";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private AlertOutboxEntry? ReadOne(string sql)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapEntry(reader) : null;
    }

    private static AlertOutboxEntry MapEntry(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var createdAt = DateTimeOffset.Parse(reader.GetString(1));
        var channel = reader.GetString(2);
        var message = AlertDispatcher.Deserialize(reader.GetString(3)) ?? new AlertMessage("告警", "（消息负载不可解析）");
        var attempts = reader.GetInt32(4);
        var lastError = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new AlertOutboxEntry(id, createdAt, channel, message, attempts, lastError);
    }
}
