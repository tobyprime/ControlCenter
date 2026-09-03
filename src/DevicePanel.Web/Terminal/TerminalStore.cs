using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Terminal;

/// <summary>留痕条目方向：命令输入 / shell 输出。</summary>
public static class TerminalEntryDirections
{
    public const string Input = "input";
    public const string Output = "output";
}

/// <summary>会话关闭原因常量。</summary>
public static class TerminalCloseReasons
{
    /// <summary>操作者关闭终端（浏览器侧主动断开）。</summary>
    public const string Operator = "operator";

    /// <summary>目标设备上的 shell 进程退出。</summary>
    public const string AgentExit = "agent-exit";

    /// <summary>agent 通道断开（设备离线/心跳超时/被顶替等）。</summary>
    public const string ConnectionLost = "connection-lost";

    /// <summary>终端打开失败或会话异常结束。</summary>
    public const string Error = "error";
}

/// <summary>一条终端会话的元数据：何时、在哪台设备、由谁打开、何时因何关闭。</summary>
public sealed record TerminalSession(
    string Id,
    long DeviceId,
    string Operator,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? CloseReason);

/// <summary>会话内的一条留痕（命令输入或输出片段）。</summary>
public sealed record TerminalEntry(long Id, string SessionId, string Direction, string Data, DateTimeOffset RecordedAtUtc);

/// <summary>
/// 终端留痕存储：会话元数据 + 命令/输出留档，可回答「何时在哪台设备执行过什么」。
/// 会话路径上的调用方（中继/处理器）须自行兜异常——存储故障不杀终端会话（沿用 TOB-338 契约）。
/// </summary>
public interface ITerminalStore
{
    void OpenSession(string sessionId, long deviceId, string operatorName, DateTimeOffset openedAtUtc);

    void Append(string sessionId, string direction, string data, DateTimeOffset recordedAtUtc);

    void CloseSession(string sessionId, DateTimeOffset closedAtUtc, string closeReason);

    IReadOnlyList<TerminalSession> QuerySessions(long? deviceId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc);

    TerminalSession? GetSession(string sessionId);

    IReadOnlyList<TerminalEntry> QueryEntries(string sessionId);
}

/// <summary>终端留痕 SQLite 实现。</summary>
public sealed class TerminalStore : ITerminalStore
{
    /// <summary>单条留痕的最大字符数：防止单个超长输出撑爆库；超出部分截断保留（会话连续性不受影响）。</summary>
    public const int MaxEntryChars = 64 * 1024;

    private readonly SqliteConnectionFactory _connectionFactory;

    public TerminalStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void OpenSession(string sessionId, long deviceId, string operatorName, DateTimeOffset openedAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terminal_sessions(id, device_id, operator, opened_at_utc)
            VALUES ($id, $deviceId, $operator, $openedAt)
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$operator", operatorName ?? string.Empty);
        command.Parameters.AddWithValue("$openedAt", FormatUtc(openedAtUtc));
        command.ExecuteNonQuery();
    }

    public void Append(string sessionId, string direction, string data, DateTimeOffset recordedAtUtc)
    {
        if (data.Length > MaxEntryChars)
        {
            data = data[..MaxEntryChars];
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terminal_entries(session_id, direction, data, recorded_at_utc)
            VALUES ($sessionId, $direction, $data, $recordedAt)
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$direction", direction);
        command.Parameters.AddWithValue("$data", data);
        command.Parameters.AddWithValue("$recordedAt", FormatUtc(recordedAtUtc));
        command.ExecuteNonQuery();
    }

    public void CloseSession(string sessionId, DateTimeOffset closedAtUtc, string closeReason)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE terminal_sessions
            SET closed_at_utc = $closedAt, close_reason = $reason
            WHERE id = $id AND closed_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$closedAt", FormatUtc(closedAtUtc));
        command.Parameters.AddWithValue("$reason", closeReason);
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TerminalSession> QuerySessions(long? deviceId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        var conditions = new List<string>();
        if (deviceId is { } deviceFilter)
        {
            conditions.Add("device_id = $deviceId");
        }

        if (fromUtc is { } from)
        {
            conditions.Add("opened_at_utc >= $from");
        }

        if (toUtc is { } to)
        {
            conditions.Add("opened_at_utc <= $to");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, device_id, operator, opened_at_utc, closed_at_utc, close_reason
            FROM terminal_sessions {where}
            ORDER BY opened_at_utc DESC, id DESC
            """;
        if (deviceId is { } id)
        {
            command.Parameters.AddWithValue("$deviceId", id);
        }

        if (fromUtc is { } fromValue)
        {
            command.Parameters.AddWithValue("$from", FormatUtc(fromValue));
        }

        if (toUtc is { } toValue)
        {
            command.Parameters.AddWithValue("$to", FormatUtc(toValue));
        }

        var sessions = new List<TerminalSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var closedAtColumn = reader.IsDBNull(4) ? null : reader.GetString(4);
            var reasonColumn = reader.IsDBNull(5) ? null : reader.GetString(5);
            sessions.Add(new TerminalSession(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                closedAtColumn is null ? null : DateTimeOffset.Parse(closedAtColumn),
                reasonColumn));
        }

        return sessions;
    }

    public TerminalSession? GetSession(string sessionId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, device_id, operator, opened_at_utc, closed_at_utc, close_reason
            FROM terminal_sessions WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var closedAtColumn = reader.IsDBNull(4) ? null : reader.GetString(4);
        var reasonColumn = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new TerminalSession(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            closedAtColumn is null ? null : DateTimeOffset.Parse(closedAtColumn),
            reasonColumn);
    }

    public IReadOnlyList<TerminalEntry> QueryEntries(string sessionId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, direction, data, recorded_at_utc
            FROM terminal_entries
            WHERE session_id = $sessionId
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var entries = new List<TerminalEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new TerminalEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return entries;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
