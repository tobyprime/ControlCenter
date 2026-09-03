using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>支持阈值告警的指标（网络速率为字节/秒，无合理默认阈值，一期不提供）。</summary>
public static class AlertMetrics
{
    public const string Cpu = "cpu";
    public const string Mem = "mem";
    public const string Disk = "disk";

    public static readonly IReadOnlyList<string> Known = [Cpu, Mem, Disk];

    public static bool IsKnown(string metric) => Known.Contains(metric);

    public static string DisplayName(string metric) => metric switch
    {
        Cpu => "CPU 使用率",
        Mem => "内存使用率",
        Disk => "磁盘使用率",
        _ => metric,
    };

    /// <summary>内置默认阈值（百分比，可被全局设置覆盖，再被按设备覆盖覆盖）。</summary>
    public static double DefaultFor(string metric) => 90;
}

/// <summary>一条阈值配置：DeviceId = 0 表示全局默认，&gt;0 表示按设备覆盖。</summary>
public sealed record AlertThresholdEntry(long DeviceId, string Metric, double ThresholdValue);

public interface IAlertThresholdStore
{
    /// <summary>全局阈值（未设置时返回内置默认）。</summary>
    double GetGlobal(string metric);

    /// <summary>设备生效阈值 = 按设备覆盖 ?? 全局 ?? 内置默认。</summary>
    double GetEffective(long deviceId, string metric);
    void SetGlobal(string metric, double value);

    void SetOverride(long deviceId, string metric, double value);

    bool DeleteOverride(long deviceId, string metric);

    /// <summary>全部按设备覆盖（不含全局行）。</summary>
    IReadOnlyList<AlertThresholdEntry> ListOverrides();
}

/// <summary>阈值配置存储（alert_thresholds，device_id = 0 为全局默认行）。</summary>
public sealed class AlertThresholdStore : IAlertThresholdStore
{
    private const long GlobalDeviceId = 0;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AlertThresholdStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public double GetGlobal(string metric) => Get(GlobalDeviceId, metric);

    public double GetEffective(long deviceId, string metric) =>
        deviceId == GlobalDeviceId ? GetGlobal(metric) : (Read(deviceId, metric) ?? GetGlobal(metric));

    public void SetGlobal(string metric, double value) => Upsert(GlobalDeviceId, metric, value);

    public void SetOverride(long deviceId, string metric, double value) => Upsert(deviceId, metric, value);

    public bool DeleteOverride(long deviceId, string metric)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_thresholds WHERE device_id = $deviceId AND metric = $metric";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$metric", metric);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<AlertThresholdEntry> ListOverrides()
    {
        var entries = new List<AlertThresholdEntry>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, metric, threshold FROM alert_thresholds WHERE device_id <> 0 ORDER BY device_id, metric";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new AlertThresholdEntry(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return entries;
    }

    private double Get(long deviceId, string metric) =>
        Read(deviceId, metric) ?? AlertMetrics.DefaultFor(metric);

    private double? Read(long deviceId, string metric)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT threshold FROM alert_thresholds WHERE device_id = $deviceId AND metric = $metric";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$metric", metric);
        return command.ExecuteScalar() as double?;
    }

    private void Upsert(long deviceId, string metric, double value)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_thresholds(device_id, metric, threshold, updated_at_utc)
            VALUES ($deviceId, $metric, $threshold, $updatedAt)
            ON CONFLICT(device_id, metric) DO UPDATE SET
                threshold = excluded.threshold, updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$metric", metric);
        command.Parameters.AddWithValue("$threshold", value);
        command.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }
}
