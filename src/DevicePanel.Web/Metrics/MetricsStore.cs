using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Metrics;

/// <summary>明细数据点：一次 30s 采样的指标快照。</summary>
public sealed record MetricsPoint(
    DateTimeOffset TimeUtc,
    double Cpu,
    double Mem,
    double Disk,
    double NetRx,
    double NetTx);

/// <summary>聚合桶：Avg 为桶内样本平均值，Max 为桶内峰值（面板展示均值，保留峰值供告警/排查）。</summary>
public sealed record MetricsBucket(
    DateTimeOffset TimeUtc,
    long SampleCount,
    double CpuAvg, double CpuMax,
    double MemAvg, double MemMax,
    double DiskAvg, double DiskMax,
    double NetRxAvg, double NetRxMax,
    double NetTxAvg, double NetTxMax);

public sealed record MetricsCleanupResult(long DetailDeleted, long HourlyDeleted, long DailyDeleted);

/// <summary>
/// 指标存储：明细（30s 采样）写入即增量更新小时/天级聚合桶（sum/count 求平均、max 取峰值），
/// 查询时按粒度取明细或聚合，两侧口径一致（聚合平均值 = 桶内明细样本均值）。
/// </summary>
public interface IMetricsStore
{
    void Insert(long deviceId, DateTimeOffset collectedAtUtc, MetricsPoint point);

    public IReadOnlyList<MetricsPoint> QueryRaw(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    IReadOnlyList<MetricsBucket> QueryHourly(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    IReadOnlyList<MetricsBucket> QueryDaily(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc);

    MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc);
}

public sealed class MetricsStore : IMetricsStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public MetricsStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void Insert(long deviceId, DateTimeOffset collectedAtUtc, MetricsPoint point)
    {
        var hourBucket = FormatBucket(TruncateToHour(collectedAtUtc));
        var dayBucket = FormatBucket(TruncateToDay(collectedAtUtc));
        using var connection = _connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var detail = connection.CreateCommand())
        {
            detail.Transaction = transaction;
            detail.CommandText = """
                INSERT INTO metric_samples(device_id, collected_at_utc, cpu_percent, mem_percent, disk_percent, net_rx_bps, net_tx_bps)
                VALUES ($deviceId, $collectedAt, $cpu, $mem, $disk, $netRx, $netTx)
                """;
            AddPointParameters(detail, deviceId, FormatUtc(collectedAtUtc), point);
            detail.ExecuteNonQuery();
        }

        UpsertAggregate(connection, transaction, "metric_samples_hourly", deviceId, hourBucket, point);
        UpsertAggregate(connection, transaction, "metric_samples_daily", deviceId, dayBucket, point);

        transaction.Commit();
    }

    public IReadOnlyList<MetricsPoint> QueryRaw(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT collected_at_utc, cpu_percent, mem_percent, disk_percent, net_rx_bps, net_tx_bps
            FROM metric_samples
            WHERE device_id = $deviceId AND collected_at_utc >= $from AND collected_at_utc <= $to
            ORDER BY collected_at_utc
            """;
        AddRangeParameters(command, deviceId, FormatUtc(fromUtc), FormatUtc(toUtc));
        return ReadPoints(command);
    }

    public IReadOnlyList<MetricsBucket> QueryHourly(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => QueryAggregate("metric_samples_hourly", deviceId, TruncateToHour(fromUtc), TruncateToHour(toUtc));

    public IReadOnlyList<MetricsBucket> QueryDaily(long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => QueryAggregate("metric_samples_daily", deviceId, TruncateToDay(fromUtc), TruncateToDay(toUtc));

    public MetricsCleanupResult DeleteOlderThan(DateTimeOffset cutoffUtc)
    {
        var cutoff = FormatUtc(cutoffUtc);
        using var connection = _connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        var result = new MetricsCleanupResult(
            DetailDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples WHERE collected_at_utc < $cutoff", cutoff),
            HourlyDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples_hourly WHERE bucket_start_utc < $cutoff", cutoff),
            DailyDeleted: ExecuteDelete(connection, transaction, "DELETE FROM metric_samples_daily WHERE bucket_start_utc < $cutoff", cutoff));
        transaction.Commit();
        return result;
    }

    private IReadOnlyList<MetricsBucket> QueryAggregate(string table, long deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT bucket_start_utc, sample_count,
                   cpu_sum / sample_count, cpu_max,
                   mem_sum / sample_count, mem_max,
                   disk_sum / sample_count, disk_max,
                   net_rx_sum / sample_count, net_rx_max,
                   net_tx_sum / sample_count, net_tx_max
            FROM {table}
            WHERE device_id = $deviceId AND bucket_start_utc >= $from AND bucket_start_utc <= $to
            ORDER BY bucket_start_utc
            """;
        AddRangeParameters(command, deviceId, FormatBucket(fromUtc), FormatBucket(toUtc));
        var buckets = new List<MetricsBucket>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buckets.Add(new MetricsBucket(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetDouble(2), reader.GetDouble(3),
                reader.GetDouble(4), reader.GetDouble(5),
                reader.GetDouble(6), reader.GetDouble(7),
                reader.GetDouble(8), reader.GetDouble(9),
                reader.GetDouble(10), reader.GetDouble(11)));
        }

        return buckets;
    }

    private static void UpsertAggregate(
        SqliteConnection connection, SqliteTransaction transaction, string table, long deviceId, string bucket, MetricsPoint point)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}(device_id, bucket_start_utc, sample_count,
                                cpu_sum, cpu_max, mem_sum, mem_max, disk_sum, disk_max,
                                net_rx_sum, net_rx_max, net_tx_sum, net_tx_max)
            VALUES ($deviceId, $bucket, 1, $cpu, $cpu, $mem, $mem, $disk, $disk, $netRx, $netRx, $netTx, $netTx)
            ON CONFLICT(device_id, bucket_start_utc) DO UPDATE SET
                sample_count = sample_count + 1,
                cpu_sum    = cpu_sum    + excluded.cpu_sum,
                cpu_max    = MAX(cpu_max,    excluded.cpu_max),
                mem_sum    = mem_sum    + excluded.mem_sum,
                mem_max    = MAX(mem_max,    excluded.mem_max),
                disk_sum   = disk_sum   + excluded.disk_sum,
                disk_max   = MAX(disk_max,   excluded.disk_max),
                net_rx_sum = net_rx_sum + excluded.net_rx_sum,
                net_rx_max = MAX(net_rx_max, excluded.net_rx_max),
                net_tx_sum = net_tx_sum + excluded.net_tx_sum,
                net_tx_max = MAX(net_tx_max, excluded.net_tx_max)
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$cpu", point.Cpu);
        command.Parameters.AddWithValue("$mem", point.Mem);
        command.Parameters.AddWithValue("$disk", point.Disk);
        command.Parameters.AddWithValue("$netRx", point.NetRx);
        command.Parameters.AddWithValue("$netTx", point.NetTx);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<MetricsPoint> ReadPoints(SqliteCommand command)
    {
        var points = new List<MetricsPoint>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            points.Add(new MetricsPoint(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }

        return points;
    }

    private static void AddPointParameters(SqliteCommand command, long deviceId, string collectedAt, MetricsPoint point)
    {
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$collectedAt", collectedAt);
        command.Parameters.AddWithValue("$cpu", point.Cpu);
        command.Parameters.AddWithValue("$mem", point.Mem);
        command.Parameters.AddWithValue("$disk", point.Disk);
        command.Parameters.AddWithValue("$netRx", point.NetRx);
        command.Parameters.AddWithValue("$netTx", point.NetTx);
    }

    private static void AddRangeParameters(SqliteCommand command, long deviceId, string from, string to)
    {
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
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
