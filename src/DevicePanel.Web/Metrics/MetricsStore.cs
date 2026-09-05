using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Metrics;

/// <summary>明细数据点：一次采样的单指标值。number 存 ValueNum；enum/string 存 ValueText；bool 两者都存（ValueNum 0/1）。</summary>
public sealed record MetricSample(DateTimeOffset TimeUtc, double? ValueNum, string? ValueText);

/// <summary>聚合桶（仅 number 指标）：Avg 为桶内样本平均值，Max 为桶内峰值。</summary>
public sealed record MetricBucket(DateTimeOffset TimeUtc, long SampleCount, double Avg, double? Max);

public sealed record MetricsCleanupResult(long DetailDeleted, long HourlyDeleted, long DailyDeleted);

/// <summary>
/// 指标存储（约束 A：语义中立 KV 模型）：按 (target, metric_key) 存任意已注册类型的指标序列。
/// 明细（30s 采样）写入即增量更新小时/天级聚合桶（number 指标，sum/count 求平均、max 取峰值），
/// 查询时按粒度取明细或聚合，两侧口径一致（聚合平均值 = 桶内明细样本均值）。
/// </summary>
public interface IMetricsStore
{
    void Insert(long targetId, string metricKey, MetricSample sample);

    IReadOnlyList<MetricSample> QueryRaw(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    IReadOnlyList<MetricBucket> QueryHourly(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    IReadOnlyList<MetricBucket> QueryDaily(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    /// <summary>该 (target, metric) 的最新样本；从未上报返回 null。</summary>
    MetricSample? GetLatest(long targetId, string metricKey);

    /// <summary>目标已上报过的全部指标 key。</summary>
    IReadOnlyList<string> ListReportedKeys(long targetId);

    /// <summary>上报过指定指标的全部目标 id（全局无数据规则的扫描展开用）。</summary>
    IReadOnlyList<long> ListTargetsReporting(string metricKey);

    /// <summary>该指标是否有任何样本（注册表删除保护用）。</summary>
    bool HasAnySample(string metricKey);

    MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc);
}

public sealed class MetricsStore : IMetricsStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public MetricsStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void Insert(long targetId, string metricKey, MetricSample sample)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var detail = connection.CreateCommand())
        {
            detail.Transaction = transaction;
            detail.CommandText = """
                INSERT INTO metric_samples(target_id, metric_key, time_utc, value_num, value_text)
                VALUES ($targetId, $metricKey, $timeUtc, $valueNum, $valueText)
                """;
            AddSampleParameters(detail, targetId, metricKey, FormatUtc(sample.TimeUtc), sample);
            detail.ExecuteNonQuery();
        }

        if (sample.ValueNum is { } number)
        {
            UpsertAggregate(connection, transaction, "metric_samples_hourly", targetId, metricKey, FormatBucket(TruncateToHour(sample.TimeUtc)), number);
            UpsertAggregate(connection, transaction, "metric_samples_daily", targetId, metricKey, FormatBucket(TruncateToDay(sample.TimeUtc)), number);
        }

        transaction.Commit();
    }

    public IReadOnlyList<MetricSample> QueryRaw(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time_utc, value_num, value_text
            FROM metric_samples
            WHERE target_id = $targetId AND metric_key = $metricKey AND time_utc >= $from AND time_utc <= $to
            ORDER BY time_utc, id
            """;
        AddRangeParameters(command, targetId, metricKey, FormatUtc(fromUtc), FormatUtc(toUtc));
        return ReadSamples(command);
    }

    public IReadOnlyList<MetricBucket> QueryHourly(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => QueryAggregate("metric_samples_hourly", targetId, metricKey, TruncateToHour(fromUtc), TruncateToHour(toUtc));

    public IReadOnlyList<MetricBucket> QueryDaily(long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => QueryAggregate("metric_samples_daily", targetId, metricKey, TruncateToDay(fromUtc), TruncateToDay(toUtc));

    public MetricSample? GetLatest(long targetId, string metricKey)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time_utc, value_num, value_text
            FROM metric_samples
            WHERE target_id = $targetId AND metric_key = $metricKey
            ORDER BY time_utc DESC, id DESC
            LIMIT 1
            """;
        AddLatestParameters(command, targetId, metricKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSample(reader) : null;
    }

    public IReadOnlyList<string> ListReportedKeys(long targetId)
    {
        var keys = new List<string>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT metric_key FROM metric_samples WHERE target_id = $targetId ORDER BY metric_key";
        command.Parameters.AddWithValue("$targetId", targetId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public IReadOnlyList<long> ListTargetsReporting(string metricKey)
    {
        var targets = new List<long>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT target_id FROM metric_samples WHERE metric_key = $metricKey ORDER BY target_id";
        command.Parameters.AddWithValue("$metricKey", metricKey);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            targets.Add(reader.GetInt64(0));
        }

        return targets;
    }

    public bool HasAnySample(string metricKey)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM metric_samples WHERE metric_key = $metricKey)";
        command.Parameters.AddWithValue("$metricKey", metricKey);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    public MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc)
    {
        var cutoff = FormatUtc(cutoffUtc);
        using var connection = _connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        var result = new MetricsCleanupResult(
            DetailDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples WHERE time_utc < $cutoff", cutoff),
            HourlyDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples_hourly WHERE bucket_start_utc < $cutoff", cutoff),
            DailyDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples_daily WHERE bucket_start_utc < $cutoff", cutoff));
        transaction.Commit();
        return result;
    }

    private IReadOnlyList<MetricBucket> QueryAggregate(string table, long targetId, string metricKey, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT bucket_start_utc, sample_count, value_sum / sample_count, value_max
            FROM {table}
            WHERE target_id = $targetId AND metric_key = $metricKey AND bucket_start_utc >= $from AND bucket_start_utc <= $to
            ORDER BY bucket_start_utc
            """;
        AddRangeParameters(command, targetId, metricKey, FormatBucket(fromUtc), FormatBucket(toUtc));
        var buckets = new List<MetricBucket>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new MetricBucket(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3)));
        }

        return buckets;
    }

    private static void UpsertAggregate(
        SqliteConnection connection, SqliteTransaction transaction, string table, long targetId, string metricKey, string bucket, double value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}(target_id, metric_key, bucket_start_utc, sample_count, value_sum, value_max)
            VALUES ($targetId, $metricKey, $bucket, 1, $value, $value)
            ON CONFLICT(target_id, metric_key, bucket_start_utc) DO UPDATE SET
                sample_count = sample_count + 1,
                value_sum = value_sum + excluded.value_sum,
                value_max = MAX(value_max, excluded.value_max)
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metricKey", metricKey);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<MetricSample> ReadSamples(SqliteCommand command)
    {
        var samples = new List<MetricSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(ReadSample(reader));
        }

        return samples;
    }

    private static MetricSample ReadSample(SqliteDataReader reader)
    {
        var timeUtc = DateTimeOffset.Parse(reader.GetString(0));
        var valueNum = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
        var valueText = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new MetricSample(timeUtc, valueNum, valueText);
    }

    private static void AddSampleParameters(SqliteCommand command, long targetId, string metricKey, string timeUtc, MetricSample sample)
    {
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metricKey", metricKey);
        command.Parameters.AddWithValue("$timeUtc", timeUtc);
        command.Parameters.AddWithValue("$valueNum", (object?)sample.ValueNum ?? DBNull.Value);
        command.Parameters.AddWithValue("$valueText", (object?)sample.ValueText ?? DBNull.Value);
    }

    private static void AddRangeParameters(SqliteCommand command, long targetId, string metricKey, string? from, string? to)
    {
        AddLatestParameters(command, targetId, metricKey);
        command.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
    }

    private static void AddLatestParameters(SqliteCommand command, long targetId, string metricKey)
    {
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$metricKey", metricKey);
    }

    private static long ExecuteDelete(SqliteConnection connection, SqliteTransaction transaction, string sql, string cutoff)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    internal static DateTimeOffset TruncateToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);

    internal static DateTimeOffset TruncateToDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>聚合桶键固定为整点/整日的 ISO 文本，保证字典序即时间序、查询区间可直接做字符串比较。</summary>
    private static string FormatBucket(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
