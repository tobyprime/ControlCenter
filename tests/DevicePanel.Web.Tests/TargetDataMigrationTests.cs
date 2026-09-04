using System.Reflection;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 存量数据升级迁移测试（验收 1/2）：构造一期（迁移 001-005）数据库并灌入台账/历史曲线/阈值数据，
/// 应用 006-008 后验证：设备自动成为 device 目标、历史明细与聚合无损平移、全局默认阈值与按设备覆盖
/// 迁移为可编辑规则实例（cpu/mem/disk 阈值 90 或自定义值 + 在线状态规则）。
/// </summary>
public class TargetDataMigrationTests
{
    private const string ResourceNamespace = "DevicePanel.Web.Infrastructure.Migrations";

    [Fact]
    public void Legacy_Database_Upgrades_With_Targets_History_And_Rules_Intact()
    {
        using var database = new TempSqliteDatabase(applyMigrations: false);
        using var connection = database.CreateOpenConnection();

        // —— 构造一期库（001-005）——
        ApplyMigrationsUpTo(connection, "006_dashboard_layout");
        InsertLegacyData(connection);

        // —— 升级（006 起）——
        ApplyMigrations(connection);
        Assert.Equal(10L, Count(connection, "SELECT COUNT(*) FROM schema_migrations"));

        // 设备 → device 目标（type 自动补齐，token 保留）
        Assert.Equal(("旧设备A", "device"), QueryTuple(connection, "SELECT name, type FROM targets WHERE id = 1"));
        Assert.Equal(("旧设备B", "device"), QueryTuple(connection, "SELECT name, type FROM targets WHERE id = 2"));

        // 历史明细：每个采样点 × 5 指标平移为窄表行，数值不变
        Assert.Equal(4L, Count(connection, "SELECT COUNT(*) FROM metric_samples WHERE metric_key = 'cpu' AND target_id = 1"));
        Assert.Equal(10.0, Scalar(connection, "SELECT value_num FROM metric_samples WHERE target_id = 1 AND metric_key = 'cpu' AND time_utc = '2026-08-01T00:00:00.0000000+00:00'"));
        Assert.Equal(1000.0, Scalar(connection, "SELECT value_num FROM metric_samples WHERE target_id = 1 AND metric_key = 'net_rx' AND time_utc = '2026-08-01T00:00:00.0000000+00:00'"));

        // 聚合无损：sum/count/max 逐桶平移（cpu 10/20/30/40 → count 4、sum 100、max 40，均值口径不变）
        Assert.Equal((4L, 100.0, 40.0), QueryTriple(connection, """
            SELECT sample_count, value_sum, value_max FROM metric_samples_hourly
            WHERE target_id = 1 AND metric_key = 'cpu' AND bucket_start_utc = '2026-08-01T00:00:00Z'
            """));

        // 阈值 → 规则：全局行 → 全局规则；覆盖行 → 目标级规则；缺失全局行补种内置默认 90
        Assert.Equal(
            """{"threshold":75.0}""",
            Scalar(connection, "SELECT parameters_json FROM alert_rules WHERE target_id IS NULL AND metric_key = 'cpu' AND rule_type = 'threshold_above'"));
        Assert.Equal(
            """{"threshold":50.0}""",
            Scalar(connection, "SELECT parameters_json FROM alert_rules WHERE target_id = 1 AND metric_key = 'cpu' AND rule_type = 'threshold_above'"));
        Assert.Equal(90.0, Convert.ToDouble(Scalar(connection, "SELECT json_extract(parameters_json, '$.threshold') FROM alert_rules WHERE target_id IS NULL AND metric_key = 'mem' AND rule_type = 'threshold_above'")));
        Assert.Equal(90.0, Convert.ToDouble(Scalar(connection, "SELECT json_extract(parameters_json, '$.threshold') FROM alert_rules WHERE target_id IS NULL AND metric_key = 'disk' AND rule_type = 'threshold_above'")));

        // 在线状态规则播种（可编辑可关闭）
        Assert.Equal("state_mismatch", Scalar(connection, "SELECT rule_type FROM alert_rules WHERE metric_key = 'online'"));
        Assert.Equal("true", Scalar(connection, "SELECT json_extract(parameters_json, '$.expected') FROM alert_rules WHERE metric_key = 'online'"));
        Assert.Equal(0L, Count(connection, "SELECT sustain_seconds FROM alert_rules WHERE metric_key = 'online'"));

        // 旧表清理、瞬态状态重置
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'alert_thresholds'"));
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM alert_state"));
    }

    private static void InsertLegacyData(SqliteConnection connection)
    {
        Exec(connection, """
            INSERT INTO devices(id, name, tags_json, agent_token_hash, created_at_utc, updated_at_utc, last_seen_at_utc) VALUES
            (1, '旧设备A', '["机房A"]', 'hash-a', '2026-07-01T00:00:00.0000000+00:00', '2026-07-01T00:00:00.0000000+00:00', '2026-08-01T00:30:00.0000000+00:00'),
            (2, '旧设备B', '[]', 'hash-b', '2026-07-01T00:00:00.0000000+00:00', '2026-07-01T00:00:00.0000000+00:00', NULL)
            """);
        Exec(connection, "INSERT INTO alert_thresholds(device_id, metric, threshold, updated_at_utc) VALUES (0, 'cpu', 75, '2026-07-01T00:00:00.0000000+00:00')");
        Exec(connection, "INSERT INTO alert_thresholds(device_id, metric, threshold, updated_at_utc) VALUES (1, 'cpu', 50, '2026-07-01T00:00:00.0000000+00:00')");

        // 目标 1：8/1 00:10/00:20/00:30/00:40 四个采样点（同小时桶），目标 2：一个采样点
        for (var i = 0; i < 4; i++)
        {
            var at = $"2026-08-01T00:{10 * i:D2}:00.0000000+00:00";
            Exec(connection, $"""
                INSERT INTO metric_samples(device_id, collected_at_utc, cpu_percent, mem_percent, disk_percent, net_rx_bps, net_tx_bps)
                VALUES (1, '{at}', {10 + 10.0 * i}, {20 + 10.0 * i}, {30 + 10.0 * i}, {1000.0 * (i + 1)}, {2000.0 * (i + 1)})
                """);
        }

        Exec(connection, """
            INSERT INTO metric_samples(device_id, collected_at_utc, cpu_percent, mem_percent, disk_percent, net_rx_bps, net_tx_bps)
            VALUES (2, '2026-08-01T00:00:00.0000000+00:00', 5, 6, 7, 8, 9)
            """);

        // 小时桶（cpu 10/20/30/40 → sum 100 max 40；net_rx sum 10000）
        Exec(connection, """
            INSERT INTO metric_samples_hourly(device_id, bucket_start_utc, sample_count, cpu_sum, cpu_max, mem_sum, mem_max, disk_sum, disk_max, net_rx_sum, net_rx_max, net_tx_sum, net_tx_max)
            VALUES (1, '2026-08-01T00:00:00Z', 4, 100, 40, 200, 50, 300, 60, 10000, 4000, 20000, 8000)
            """);
        Exec(connection, """
            INSERT INTO metric_samples_hourly(device_id, bucket_start_utc, sample_count, cpu_sum, cpu_max, mem_sum, mem_max, disk_sum, disk_max, net_rx_sum, net_rx_max, net_tx_sum, net_tx_max)
            VALUES (2, '2026-08-01T00:00:00Z', 1, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9)
            """);
        Exec(connection, """
            INSERT INTO metric_samples_daily(device_id, bucket_start_utc, sample_count, cpu_sum, cpu_max, mem_sum, mem_max, disk_sum, disk_max, net_rx_sum, net_rx_max, net_tx_sum, net_tx_max)
            VALUES (1, '2026-08-01T00:00:00Z', 4, 100, 40, 200, 50, 300, 60, 10000, 4000, 20000, 8000)
            """);
        Exec(connection, "INSERT INTO alert_state(rule_key, state_json, updated_at_utc) VALUES ('threshold:1:cpu', '{\"FirstSeenUtc\":\"2026-08-01T00:00:00+00:00\"}', '2026-08-01T00:00:00.0000000+00:00')");
    }

    private static void ApplyMigrations(SqliteConnection connection) => DatabaseMigrator.Migrate(connection);

    /// <summary>只应用指定版本之前的迁移（含 upTo），模拟一期存量库。</summary>
    private static void ApplyMigrationsUpTo(SqliteConnection connection, string upTo)
    {
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version        TEXT PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            )
            """);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT version FROM schema_migrations";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        var assembly = typeof(DatabaseMigrator).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourceNamespace, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var version = n.Substring(ResourceNamespace.Length + 1)[..^4];
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Version: version, Sql: reader.ReadToEnd());
            })
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .ToList();

        foreach (var (version, sql) in resources.Where(m => string.Compare(m.Version, upTo, StringComparison.Ordinal) <= 0))
        {
            if (applied.Contains(version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                script.CommandText = sql;
                script.ExecuteNonQuery();
            }

            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAt)";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O"));
            record.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string sql) => (long)Scalar(connection, sql);

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private static (string, string) QueryTuple(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetString(1));
    }

    private static (long, double, double) QueryTriple(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
    }
}
