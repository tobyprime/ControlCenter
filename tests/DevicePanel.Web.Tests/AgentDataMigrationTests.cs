using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 存量数据升级迁移测试（TOB-375 模块2 验收 3/6）：构造 0.2.0（迁移 001-012）数据库灌入 device/service 目标，
/// 应用 013 后验证：每个 device 型 target 自动生成对应 agent（名字沿用、token hash 原样平移、last_seen 随 agent、
/// targets.agent_id 关联建立），service 型 target 不生成 agent；全程无手工步骤。
/// </summary>
public class AgentDataMigrationTests
{
    private const string ResourceNamespace = "DevicePanel.Web.Infrastructure.Migrations";

    [Fact]
    public void Device_Targets_Gain_Agents_With_Token_Hash_Carried_Over()
    {
        using var database = new TempSqliteDatabase(applyMigrations: false);
        using var connection = database.CreateOpenConnection();

        // —— 构造 0.2.0 库（001-012）——
        ApplyMigrationsUpTo(connection, "012_probe_metric_keys");
        Exec(connection, """
            INSERT INTO targets(id, type, name, tags_json, agent_token_hash, created_at_utc, updated_at_utc, last_seen_at_utc) VALUES
            (1, 'device', '在线设备', '["机房A"]', 'hash-device-a', '2026-08-01T00:00:00.0000000+00:00', '2026-08-01T00:00:00.0000000+00:00', '2026-08-30T12:00:00.0000000+00:00'),
            (2, 'device', '离线设备', '[]', 'hash-device-b', '2026-08-01T00:00:00.0000000+00:00', '2026-08-01T00:00:00.0000000+00:00', NULL),
            (3, 'service', 'HTTP 探针', '[]', 'hash-service-x', '2026-08-02T00:00:00.0000000+00:00', '2026-08-02T00:00:00.0000000+00:00', NULL)
            """);

        // —— 升级（013 起）——
        DatabaseMigrator.Migrate(connection);
        Assert.Equal(13L, Count(connection, "SELECT COUNT(*) FROM schema_migrations"));

        // device 目标 → agent：名字沿用、token hash 原样平移、last_seen 随 agent、关联建立
        Assert.Equal((1L, "在线设备", "hash-device-a", "2026-08-30T12:00:00.0000000+00:00"),
            QueryAgent(connection, "SELECT id, name, token_hash, last_seen_at_utc FROM agents ORDER BY id LIMIT 1 OFFSET 0"));
        Assert.Equal((2L, "离线设备", "hash-device-b", DBNull.Value),
            QueryAgent(connection, "SELECT id, name, token_hash, last_seen_at_utc FROM agents ORDER BY id LIMIT 1 OFFSET 1"));
        Assert.Equal(2L, Count(connection, "SELECT COUNT(*) FROM agents"));

        // targets.agent_id 关联指向对应 agent；service 目标不生成 agent、关联为空
        Assert.Equal(1L, Scalar(connection, "SELECT agent_id FROM targets WHERE id = 1"));
        Assert.Equal(2L, Scalar(connection, "SELECT agent_id FROM targets WHERE id = 2"));
        Assert.Equal(DBNull.Value, Scalar(connection, "SELECT agent_id FROM targets WHERE id = 3"));

        // service 目标本体不受影响
        Assert.Equal(("service", "hash-service-x"), QueryTuple(connection, "SELECT type, agent_token_hash FROM targets WHERE id = 3"));
    }

    [Fact]
    public void Fresh_Database_Applies_All_Migrations_Without_Manual_Steps()
    {
        using var database = new TempSqliteDatabase(applyMigrations: true);
        using var connection = database.CreateOpenConnection();

        Assert.Equal(13L, Count(connection, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(0L, Count(connection, "SELECT COUNT(*) FROM agents"));
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

    private static (long, string, string, object) QueryAgent(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetValue(3));
    }

    private static (string, string) QueryTuple(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetString(1));
    }
}
