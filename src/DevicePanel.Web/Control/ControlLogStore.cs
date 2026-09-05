using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Control;

/// <summary>一条控制留痕：何时、哪台采集器上的哪个控制器、谁、带了什么参数、结果如何。</summary>
public sealed record ControlLogEntry(
    long Id,
    long CollectorId,
    string ControllerKey,
    string ControllerType,
    string ControllerLabel,
    string Operator,
    JsonElement Parameters,
    string Status,
    string? ResultMessage,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 控制留痕存储（参照 TerminalStore 模式）：只追加，不参与下发主链路的成败判定——
/// 调用方（ControlInvokeService）兜异常，存储故障不阻断下发。
/// </summary>
public interface IControlLogStore
{
    void Append(long collectorId, string controllerKey, string controllerType, string controllerLabel,
        string operatorName, string parametersJson, string status, string? resultMessage, DateTimeOffset createdAtUtc);

    IReadOnlyList<ControlLogEntry> Query(long? collectorId, string? controllerKey,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit);
}

/// <summary>控制留痕 SQLite 实现。</summary>
public sealed class ControlLogStore : IControlLogStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ControlLogStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void Append(long collectorId, string controllerKey, string controllerType, string controllerLabel,
        string operatorName, string parametersJson, string status, string? resultMessage, DateTimeOffset createdAtUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO control_logs(collector_id, controller_key, controller_type, controller_label, operator, params_json, status, result_message, created_at_utc)
            VALUES ($collectorId, $controllerKey, $controllerType, $controllerLabel, $operator, $paramsJson, $status, $resultMessage, $createdAt)
            """;
        command.Parameters.AddWithValue("$collectorId", collectorId);
        command.Parameters.AddWithValue("$controllerKey", controllerKey);
        command.Parameters.AddWithValue("$controllerType", controllerType);
        command.Parameters.AddWithValue("$controllerLabel", controllerLabel);
        command.Parameters.AddWithValue("$operator", operatorName ?? string.Empty);
        command.Parameters.AddWithValue("$paramsJson", parametersJson);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$resultMessage", (object?)resultMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(createdAtUtc));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ControlLogEntry> Query(long? collectorId, string? controllerKey,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit)
    {
        var conditions = new List<string>();
        if (collectorId is { } collectorFilter)
        {
            conditions.Add("collector_id = $collectorId");
        }

        if (!string.IsNullOrEmpty(controllerKey))
        {
            conditions.Add("controller_key = $controllerKey");
        }

        if (fromUtc is { } from)
        {
            conditions.Add("created_at_utc >= $from");
        }

        if (toUtc is { } to)
        {
            conditions.Add("created_at_utc <= $to");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, collector_id, controller_key, controller_type, controller_label, operator, params_json, status, result_message, created_at_utc
            FROM control_logs {where}
            ORDER BY created_at_utc DESC, id DESC
            LIMIT $limit
            """;
        if (collectorId is { } id)
        {
            command.Parameters.AddWithValue("$collectorId", id);
        }

        if (!string.IsNullOrEmpty(controllerKey))
        {
            command.Parameters.AddWithValue("$controllerKey", controllerKey);
        }

        if (fromUtc is { } fromValue)
        {
            command.Parameters.AddWithValue("$from", FormatUtc(fromValue));
        }

        if (toUtc is { } toValue)
        {
            command.Parameters.AddWithValue("$to", FormatUtc(toValue));
        }

        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<ControlLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var messageColumn = reader.IsDBNull(8) ? null : reader.GetString(8);
            entries.Add(new ControlLogEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseJson(reader.GetString(6)),
                reader.GetString(7),
                messageColumn,
                DateTimeOffset.Parse(reader.GetString(9))));
        }

        return entries;
    }

    /// <summary>留痕 params 允许任意 JSON（对象/原始值），损坏内容降级为 null 而非打断整页查询。</summary>
    private static JsonElement ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
