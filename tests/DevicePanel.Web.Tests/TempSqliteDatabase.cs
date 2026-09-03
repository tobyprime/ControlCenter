using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Tests;

/// <summary>临时 SQLite 数据库，已应用全部迁移，用于单元级持久化测试。</summary>
public sealed class TempSqliteDatabase : IDisposable
{
    public TempSqliteDatabase()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "device-panel-unit-tests", Guid.NewGuid().ToString("N"));
        Options = new DatabaseOptions { DataDir = DataDir };
        Factory = new SqliteConnectionFactory(Options);
        using var connection = Factory.CreateOpenConnection();
        DatabaseInitializer.SetPragmas(connection);
        DatabaseMigrator.Migrate(connection);
    }

    public string DataDir { get; }

    public DatabaseOptions Options { get; }

    public SqliteConnectionFactory Factory { get; }

    public SqliteConnection CreateOpenConnection() => Factory.CreateOpenConnection();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
