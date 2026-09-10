using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>事件类别：触发 / 恢复（与通知标题一一对应）。</summary>
public static class AlertEventKinds
{
    public const string Trigger = "trigger";
    public const string Recover = "recover";
}

/// <summary>
/// 投递状态（TOB-403 审查口径修正）：只有 delivered 是终态。
/// pending＝尚未投递成功；failed＝最近一次投递失败（非终态，outbox 按无丢失契约持续补发，
/// 成功后由分发 worker 翻转为 delivered）；界面文案须与该补发语义一致，不得让 failed 读作「已丢失/不再重试」。
/// </summary>
public static class AlertDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

/// <summary>触发时的样本快照（恢复事件无样本）。</summary>
public sealed record AlertEventSample(DateTimeOffset TimeUtc, double? ValueNum, string? ValueText);

/// <summary>事件写入草稿：规则与目标信息由调用方快照（写入后不随源变更）。</summary>
public sealed record AlertEventDraft(
    long? RuleId,
    string RuleType,
    long? TargetId,
    string TargetName,
    string MetricKey,
    string MetricDisplayName,
    string Kind,
    string Title,
    string Content,
    AlertEventSample? Sample);

/// <summary>告警事件历史中的一行：回看触发/恢复时间、规则、当时值与 QQ 投递结果。</summary>
public sealed record AlertEvent(
    long Id,
    DateTimeOffset CreatedAtUtc,
    long? RuleId,
    string RuleType,
    long? TargetId,
    string TargetName,
    string MetricKey,
    string MetricDisplayName,
    string Kind,
    string Title,
    string Content,
    AlertEventSample? Sample,
    string DeliveryStatus,
    DateTimeOffset? DeliveredAtUtc,
    string? DeliveryError);

/// <summary>
/// 告警事件历史（alert_events，只读查询面）：挂在既有告警评估/分发链路上记账，
/// 不参与判定与分发语义；Append 返回事件 id，经待发队列关联列回写投递结果。
/// </summary>
public interface IAlertEventStore
{
    /// <summary>追加一条事件并返回其 id（供分发链路关联回写投递结果）。</summary>
    long Append(AlertEventDraft draft, DateTimeOffset nowUtc);

    void MarkDelivered(long eventId, DateTimeOffset nowUtc);

    void RecordDeliveryFailure(long eventId, string error, DateTimeOffset nowUtc);

    /// <summary>按目标与时间窗口筛选，时间倒序，至多 limit 条。</summary>
    IReadOnlyList<AlertEvent> List(long? targetId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit);
}

public sealed class AlertEventStore : IAlertEventStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public AlertEventStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public long Append(AlertEventDraft draft, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_events(rule_id, rule_type, target_id, target_name, metric_key, metric_display,
                                     kind, title, content, sample_json, delivery_status, created_at_utc)
            VALUES ($ruleId, $ruleType, $targetId, $targetName, $metricKey, $metricDisplay,
                    $kind, $title, $content, $sample, $deliveryStatus, $createdAt)
            """;
        command.Parameters.AddWithValue("$ruleId", (object?)draft.RuleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ruleType", draft.RuleType);
        command.Parameters.AddWithValue("$targetId", (object?)draft.TargetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetName", draft.TargetName);
        command.Parameters.AddWithValue("$metricKey", draft.MetricKey);
        command.Parameters.AddWithValue("$metricDisplay", draft.MetricDisplayName);
        command.Parameters.AddWithValue("$kind", draft.Kind);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$content", draft.Content);
        command.Parameters.AddWithValue("$sample", (object?)SerializeSample(draft.Sample) ?? DBNull.Value);
        command.Parameters.AddWithValue("$deliveryStatus", AlertDeliveryStatuses.Pending);
        command.Parameters.AddWithValue("$createdAt", nowUtc.ToString("O"));
        command.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT last_insert_rowid()";
        return (long)read.ExecuteScalar()!;
    }

    public void MarkDelivered(long eventId, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_events
            SET delivery_status = $status, delivered_at_utc = $deliveredAt, delivery_error = NULL
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$status", AlertDeliveryStatuses.Delivered);
        command.Parameters.AddWithValue("$deliveredAt", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    public void RecordDeliveryFailure(long eventId, string error, DateTimeOffset nowUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_events
            SET delivery_status = $status, delivery_error = $error
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$status", AlertDeliveryStatuses.Failed);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AlertEvent> List(long? targetId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit)
    {
        var conditions = new List<string>();
        if (targetId is { })
        {
            conditions.Add("target_id = $targetId");
        }

        if (fromUtc is { })
        {
            conditions.Add("created_at_utc >= $fromUtc");
        }

        if (toUtc is { })
        {
            conditions.Add("created_at_utc <= $toUtc");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, created_at_utc, rule_id, rule_type, target_id, target_name, metric_key, metric_display,
                   kind, title, content, sample_json, delivery_status, delivered_at_utc, delivery_error
            FROM alert_events {where} ORDER BY created_at_utc DESC, id DESC LIMIT $limit
            """;
        if (targetId is { })
        {
            command.Parameters.AddWithValue("$targetId", targetId.Value);
        }

        if (fromUtc is { })
        {
            command.Parameters.AddWithValue("$fromUtc", fromUtc.Value.ToString("O"));
        }

        if (toUtc is { })
        {
            command.Parameters.AddWithValue("$toUtc", toUtc.Value.ToString("O"));
        }

        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<AlertEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new AlertEvent(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                ReadSample(reader.IsDBNull(11) ? null : reader.GetString(11)),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return events;
    }

    private static string? SerializeSample(AlertEventSample? sample) =>
        sample is null ? null : JsonSerializer.Serialize(sample);

    private static AlertEventSample? ReadSample(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<AlertEventSample>(json);
}
