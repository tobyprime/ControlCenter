using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 存量数据升级迁移测试（TOB-376 模块3 验收 1/5/6）：构造模块2 落地后的库（迁移 001-013）灌入
/// device/service 目标与探针配置、指标样本，应用 014 后验证：targets 更名 collectors 且行数据无损、
/// type 硬分类下沉为内置标签（type:device / type:service，可编辑可筛选）、type 列移除、
/// probe_configs 更名 collector_pull_configs、全部外键（指标明细/聚合/告警规则/终端留痕）随迁有效。
/// </summary>
public class CollectorDataMigrationTests
{
    private const string ResourceNamespace = "DevicePanel.Web.Infrastructure.Migrations";

    [Fact]
    public void Targets_Upgrade_To_Collectors_With_Type_As_Builtin_Tag()
    {
        using var database = new TempSqliteDatabase(applyMigrations: false);
        using var connection = database.CreateOpenConnection();

        // —— 构造模块2 后的库（001-013）——
        ApplyMigrationsUpTo(connection, "013_agents");
        Exec(connection, """
            INSERT INTO agents(id, name, labels_json, token_hash, created_at_utc, updated_at_utc) VALUES
            (1, '在线设备', '[]', 'hash-device-a', '2026-08-01T00:00:00.0000000+00:00', '2026-08-01T00:00:00.0000000+00:00')
            """);
        Exec(connection, """
            INSERT INTO targets(id, type, name, tags_json, agent_token_hash, agent_id, created_at_utc, updated_at_utc, last_seen_at_utc) VALUES
            (1, 'device', '在线设备', '["机房A"]', 'hash-device-a', 1, '2026-08-01T00:00:00.0000000+00:00', '2026-08-01T00:00:00.0000000+00:00', '2026-08-30T12:00:00.0000000+00:00'),
            (2, 'service', 'HTTP 探针', '[]', 'hash-service-x', NULL, '2026-08-02T00:00:00.0000000+00:00', '2026-08-02T00:00:00.0000000+00:00', NULL)
            """);
        Exec(connection, """
            INSERT INTO probe_configs(target_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc) VALUES
            (2, 'https://mc.zenoxs.cn/status', 60, '[]', '2026-08-02T00:00:00.0000000+00:00', '2026-08-02T00:00:00.0000000+00:00')
            """);
        Exec(connection, """
            INSERT INTO metric_samples(target_id, metric_key, value_num, value_text, time_utc) VALUES
            (2, 'status', 0, 'true', '2026-08-30T12:00:00.0000000+00:00')
            """);
        Exec(connection, """
            INSERT INTO alert_rules(target_id, metric_key, rule_type, parameters_json, enabled, created_at_utc, updated_at_utc) VALUES
            (2, 'status', 'state_mismatch', '{"expected":"true"}', 1, '2026-08-02T00:00:00.0000000+00:00', '2026-08-02T00:00:00.0000000+00:00')
            """);

        // —— 升级（014 起）——
        DatabaseMigrator.Migrate(connection);
        Assert.Equal(14L, Count(connection, "SELECT COUNT(*) FROM schema_migrations"));

        // 台账更名 collectors，行数据无损；type 列移除
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'targets'"));
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM pragma_table_info('collectors') WHERE name = 'type'"));
        Assert.Equal(("在线设备", "2026-08-30T12:00:00.0000000+00:00"),
            QueryTuple(connection, "SELECT name, last_seen_at_utc FROM collectors WHERE id = 1"));
        Assert.Equal(1L, Scalar(connection, "SELECT agent_id FROM collectors WHERE id = 1"));

        // type 硬分类 → 内置标签：device/service 语义经标签保留；既有自定义标签不受影响
        Assert.Contains("type:device", ParseTags(Scalar(connection, "SELECT tags_json FROM collectors WHERE id = 1")));
        Assert.Equal(new[] { "机房A", "type:device" }, ParseTags(Scalar(connection, "SELECT tags_json FROM collectors WHERE id = 1")));
        Assert.Equal(new[] { "type:service" }, ParseTags(Scalar(connection, "SELECT tags_json FROM collectors WHERE id = 2")));

        // 探针配置更名 collector_pull_configs，配置与外键随迁无损
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'probe_configs'"));
        Assert.Equal(("https://mc.zenoxs.cn/status", "60"),
            QueryTuple(connection, "SELECT url, CAST(interval_seconds AS TEXT) FROM collector_pull_configs WHERE collector_id = 2"));

        // 指标样本与聚合（历史曲线）无损：外键自动改写后行仍指向同一采集器
        Assert.Equal(1L, Count(connection, "SELECT COUNT(*) FROM metric_samples WHERE target_id = 2 AND metric_key = 'status' AND value_text = 'true'"));
        // 告警规则随迁
        Assert.Equal(1L, Count(connection, "SELECT COUNT(*) FROM alert_rules WHERE target_id = 2 AND rule_type = 'state_mismatch'"));
        // 外键完整性：新写入仍受级联约束（删采集器 → 探针配置级联清理）
        Exec(connection, "PRAGMA foreign_keys = ON");
        Assert.Equal(1L, Count(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('collector_pull_configs') WHERE [table] = 'collectors'"));
        Exec(connection, "DELETE FROM collectors WHERE id = 2");
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM collector_pull_configs WHERE collector_id = 2"));
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM metric_samples WHERE target_id = 2"));
    }

    [Fact]
    public void Fresh_Database_Applies_All_Migrations_Without_Manual_Steps()
    {
        using var database = new TempSqliteDatabase(applyMigrations: true);
        using var connection = database.CreateOpenConnection();

        Assert.Equal(14L, Count(connection, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM collectors"));
    }

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

    private static string[] ParseTags(object tagsJson) =>
        System.Text.Json.JsonSerializer.Deserialize<string[]>((string)tagsJson) ?? [];
}
