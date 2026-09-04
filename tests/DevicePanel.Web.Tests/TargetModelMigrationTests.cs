using DevicePanel.Web.Devices;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>迁移 006（TOB-360 模块 0）：目标统一表 + 指标注册表 + 通用指标序列 + 告警规则，现有设备回填为 device 目标。</summary>
public class TargetModelMigrationTests : IDisposable
{
    private static readonly string RootDir = Path.Combine(Path.GetTempPath(), "device-panel-unit-tests");

    [Fact]
    public void New_Tables_Exist_After_Migration()
    {
        using var db = new TempSqliteDatabase();
        using var connection = db.CreateOpenConnection();
        foreach (var table in new[] { "targets", "metric_keys", "metric_values", "alert_rules" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }
    }

    [Fact]
    public void Existing_Devices_Are_Backfilled_As_Device_Targets()
    {
        // 模拟一期库：先升级到 005，建设备，再升级到 006
        var dbDir = Path.Combine(RootDir, Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new DatabaseOptions { DataDir = dbDir });
        try
        {
            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.MigrateUpTo(connection, "005_alerting");
            }

            var devices = new DeviceRegistry(factory, TimeProvider.System);
            var created = devices.Create("legacy-host", ["edge"]).Device;

            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.Migrate(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT type, device_id FROM targets WHERE device_id = $deviceId";
                command.Parameters.AddWithValue("$deviceId", created.Id);
                using var reader = command.ExecuteReader();

                Assert.True(reader.Read());
                Assert.Equal("device", reader.GetString(0));
                Assert.Equal(created.Id, reader.GetInt64(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Migration_Rerun_Does_Not_Duplicate_Backfilled_Targets()
    {
        var dbDir = Path.Combine(RootDir, Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new DatabaseOptions { DataDir = dbDir });
        try
        {
            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.MigrateUpTo(connection, "005_alerting");
            }

            var devices = new DeviceRegistry(factory, TimeProvider.System);
            var created = devices.Create("host-a", []).Device;
            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.Migrate(connection);
            }

            // 迁移幂等重跑：schema_migrations 已记录 006，重跑不重复回填
            using (var connection = factory.CreateOpenConnection())
            {
                DatabaseMigrator.Migrate(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM targets WHERE device_id = $deviceId";
                command.Parameters.AddWithValue("$deviceId", created.Id);
                Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, recursive: true); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
