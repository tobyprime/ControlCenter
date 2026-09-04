using DevicePanel.Web.Infrastructure;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

public class DatabaseMigrationTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public DatabaseMigrationTests(TestAppFactory factory)
    {
        _factory = factory;
        // WebApplicationFactory 惰性启动：先创建客户端确保宿主（含 DatabaseInitializer）已运行
        _factory.CreateClient();
    }

    [Fact]
    public void Journal_Mode_Is_Wal_After_Startup()
    {
        var factory = new SqliteConnectionFactory(new DatabaseOptions
        {
            DataDir = _factory.DataDir,
        });
        using var connection = factory.CreateOpenConnection();

        var journalMode = ExecuteScalar(connection, "PRAGMA journal_mode;");

        Assert.Equal("wal", Convert.ToString(journalMode)?.ToLowerInvariant());
    }

    [Fact]
    public void Migrations_Applied_And_Tables_Exist()
    {
        var factory = new SqliteConnectionFactory(new DatabaseOptions
        {
            DataDir = _factory.DataDir,
        });
        using var connection = factory.CreateOpenConnection();

        var appliedMigrations = ExecuteScalar(connection, "SELECT COUNT(*) FROM schema_migrations;");
        Assert.True(Convert.ToInt64(appliedMigrations) >= 1, "至少应记录一条已应用的迁移");

        foreach (var table in new[] { "users", "sessions" })
        {
            var exists = ExecuteScalar(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;",
                ("$name", table));
            Assert.Equal(1L, Convert.ToInt64(exists));
        }
    }

    [Fact]
    public void Dashboard_Layout_Table_Exists_After_Startup()
    {
        var factory = new SqliteConnectionFactory(new DatabaseOptions
        {
            DataDir = _factory.DataDir,
        });
        using var connection = factory.CreateOpenConnection();

        var exists = ExecuteScalar(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'dashboard_layouts';");

        Assert.Equal(1L, Convert.ToInt64(exists));
    }

    [Fact]
    public void Migration_006_Applies_Cleanly_On_Phase1_Upgraded_Database()
    {
        // 模拟一期升级库：真实应用 001-005 建表（TOB-361 的 007_targets 等迁移依赖一期真实表结构），
        // 重启迁移补执行 006 起的未应用迁移且无错误
        var dataDir = Path.Combine(Path.GetTempPath(), "device-panel-upgrade-tests", Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new DatabaseOptions { DataDir = dataDir });
        try
        {
            using (var connection = factory.CreateOpenConnection())
            {
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    CREATE TABLE schema_migrations (
                        version        TEXT PRIMARY KEY,
                        applied_at_utc TEXT NOT NULL
                    );
                    """;
                seed.ExecuteNonQuery();
            }

            using (var connection = factory.CreateOpenConnection())
            {
                ApplyEmbeddedMigrationsUpTo(connection, "005_alerting");
            }

            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.Migrate(connection);

                var tableExists = ExecuteScalar(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'dashboard_layouts';");
                Assert.Equal(1L, Convert.ToInt64(tableExists));

                // 一期 devices 表升级为 targets，且设备数据保留（TOB-361 迁移链完整）
                var targetsExists = ExecuteScalar(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'targets';");
                Assert.Equal(1L, Convert.ToInt64(targetsExists));

                var applied = ExecuteScalar(
                    connection,
                    "SELECT COUNT(*) FROM schema_migrations WHERE version = '006_dashboard_layout';");
                Assert.Equal(1L, Convert.ToInt64(applied));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(dataDir))
                {
                    Directory.Delete(dataDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Wal_Sidecar_File_Exists_While_Connection_Open()
    {
        var options = new DatabaseOptions { DataDir = _factory.DataDir };
        var factory = new SqliteConnectionFactory(options);
        using (var connection = factory.CreateOpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ('test-probe', '2026-01-01T00:00:00.000Z');";
            command.ExecuteNonQuery();

            // 断言必须在连接关闭前：最后一个连接正常关闭时 SQLite 会 checkpoint 并删除 -wal 文件
            Assert.True(File.Exists(options.DatabasePath + "-wal"), "WAL 模式下运行时应能观察到 -wal 文件");
        }
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    /// <summary>应用指定版本及之前的嵌入式迁移（构造真实一期库结构，供升级路径测试使用）。</summary>
    private static void ApplyEmbeddedMigrationsUpTo(Microsoft.Data.Sqlite.SqliteConnection connection, string upTo)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("DevicePanel.Web.Infrastructure.Migrations", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var version = n.Substring("DevicePanel.Web.Infrastructure.Migrations".Length + 1)[..^4];
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Version: version, Sql: reader.ReadToEnd());
            })
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .Where(m => string.Compare(m.Version, upTo, StringComparison.Ordinal) <= 0);

        foreach (var (version, sql) in resources)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();

            using var record = connection.CreateCommand();
            record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, $appliedAt)";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O"));
            record.ExecuteNonQuery();
        }
    }
}
