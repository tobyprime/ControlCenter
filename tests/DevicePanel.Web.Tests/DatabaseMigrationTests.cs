using DevicePanel.Web.Infrastructure;
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
        // 模拟一期升级库：真实应用 001-005（007 的 targets 回填依赖 devices 表，伪造版本记录不再构成合法升级态），
        // 重启迁移只补执行 006 及之后版本且无错误
        var dataDir = Path.Combine(Path.GetTempPath(), "device-panel-upgrade-tests", Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new DatabaseOptions { DataDir = dataDir });
        try
        {
            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.MigrateUpTo(connection, "005_alerting");
            }

            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.Migrate(connection);

                var tableExists = ExecuteScalar(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'dashboard_layouts';");
                Assert.Equal(1L, Convert.ToInt64(tableExists));

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
}
