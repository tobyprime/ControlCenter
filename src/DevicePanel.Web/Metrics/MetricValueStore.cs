using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Metrics;

/// <summary>通用指标数据点：number 指标取 NumValue，enum/string/bool 指标取 TextValue。</summary>
public sealed record MetricValue(DateTimeOffset TimeUtc, double? NumValue, string? TextValue);

/// <summary>聚合桶点：number 指标取桶内均值，文本指标取桶内最新值。</summary>
public sealed record MetricBucketPoint(DateTimeOffset TimeUtc, double? AvgNum, string? LastText, long SampleCount);

/// <summary>
/// 通用类型化指标序列存储（metric_values，TOB-360 约束 A）：任意注册指标统一落库，
/// 新增指标无需改表；类型校验以指标注册表为准。同一秒重复上报按覆盖处理（采样周期远大于 1s）。
/// </summary>
public interface IMetricValueStore
{
    /// <summary>写入前按注册表校验类型；未注册或类型不符抛异常（调用方负责丢点保连）。</summary>
    void Insert(long targetId, string key, DateTimeOffset collectedAtUtc, MetricValue value);

    IReadOnlyList<MetricValue> QueryRaw(long targetId, string key, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    /// <summary>按粒度聚合（hour/day）：number 求均值，文本取桶内最新值。</summary>
    IReadOnlyList<MetricBucketPoint> QueryBucketed(long targetId, string key, string granularity, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    MetricValue? TryGetLatest(long targetId, string key);

    long DeleteOlderThan(DateTimeOffset cutoffUtc);
}

public sealed class MetricValueStore : IMetricValueStore
{
    public const string GranularityHour = "hour";
    public const string GranularityDay = "day";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IMetricKeyRegistry _registry;

    public MetricValueStore(SqliteConnectionFactory connectionFactory, IMetricKeyRegistry registry)
    {
        _connectionFactory = connectionFactory;
        _registry = registry;
    }

    public void Insert(long targetId, string key, DateTimeOffset collectedAtUtc, MetricValue value)
    {
        var keyInfo = _registry.Get(key)
            ?? throw new InvalidOperationException($"指标 {key} 未注册，拒绝写入");
        double? numValue;
        string? textValue;
        switch (keyInfo.ValueType)
        {
            case MetricValueType.Number:
                numValue = value.NumValue ?? throw new InvalidOperationException($"指标 {key} 是 number 类型，必须提供数值");
                textValue = null;
                break;
            case MetricValueType.Bool:
                numValue = null;
                textValue = NormalizeBool(value.TextValue ?? throw new InvalidOperationException($"指标 {key} 是 bool 类型，必须提供文本值"));
                break;
            case MetricValueType.Enum:
            case MetricValueType.String:
                numValue = null;
                textValue = value.TextValue ?? throw new InvalidOperationException($"指标 {key} 是 {MetricValueTypeText.Format(keyInfo.ValueType)} 类型，必须提供文本值");
                break;
            default:
                throw new InvalidOperationException($"指标 {key} 类型未知：{keyInfo.ValueType}");
        }

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metric_values(target_id, key, collected_at_utc, num_value, text_value)
            VALUES ($targetId, $key, $collectedAt, $numValue, $textValue)
            ON CONFLICT(target_id, key, collected_at_utc) DO UPDATE SET
                num_value = excluded.num_value, text_value = excluded.text_value
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$collectedAt", FormatUtc(collectedAtUtc));
        command.Parameters.AddWithValue("$numValue", (object?)numValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$textValue", (object?)textValue ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MetricValue> QueryRaw(long targetId, string key, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var points = new List<MetricValue>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT collected_at_utc, num_value, text_value
            FROM metric_values
            WHERE target_id = $targetId AND key = $key
              AND collected_at_utc >= $from AND collected_at_utc <= $to
            ORDER BY collected_at_utc
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$from", FormatUtc(fromUtc));
        command.Parameters.AddWithValue("$to", FormatUtc(toUtc));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new MetricValue(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return points;
    }

    public IReadOnlyList<MetricBucketPoint> QueryBucketed(long targetId, string key, string granularity, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var bucket = granularity switch
        {
            GranularityHour => "substr(collected_at_utc, 1, 13) || ':00:00Z'",
            GranularityDay => "substr(collected_at_utc, 1, 10) || 'T00:00:00Z'",
            _ => throw new ArgumentException($"granularity 仅支持 {GranularityHour}/{GranularityDay}", nameof(granularity)),
        };

        var buckets = new List<MetricBucketPoint>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        // SQLite min/max 裸列语义：text_value 取自 MAX(collected_at_utc) 所在行（桶内最新文本值）
        command.CommandText = $"""
            SELECT {bucket} AS bucket, COUNT(*), AVG(num_value), text_value, MAX(collected_at_utc)
            FROM metric_values
            WHERE target_id = $targetId AND key = $key
              AND collected_at_utc >= $from AND collected_at_utc <= $to
            GROUP BY bucket ORDER BY bucket
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$from", FormatUtc(fromUtc));
        command.Parameters.AddWithValue("$to", FormatUtc(toUtc));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new MetricBucketPoint(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(1)));
        }

        return buckets;
    }

    public MetricValue? TryGetLatest(long targetId, string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT collected_at_utc, num_value, text_value
            FROM metric_values
            WHERE target_id = $targetId AND key = $key
            ORDER BY collected_at_utc DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MetricValue(
            DateTimeOffset.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public long DeleteOlderThan(DateTimeOffset cutoffUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metric_values WHERE collected_at_utc < $cutoff";
        command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoffUtc));
        return command.ExecuteNonQuery();
    }

    private static string NormalizeBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => "true",
        "false" or "0" or "no" or "off" => "false",
        _ => throw new InvalidOperationException($"bool 指标值无法解析：{value}"),
    };

    /// <summary>固定 UTC 文本（秒级精度）：字典序即时间序，支撑字符串比较与 substr 分桶。</summary>
    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
