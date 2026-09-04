using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>外键约束与忙等是 per-connection 设置：连接池出的每一条新连接都必须带上。</summary>
public class ConnectionPragmasTests
{
    [Fact]
    public void Foreign_Keys_Enabled_On_Every_New_Connection()
    {
        using var db = new TempSqliteDatabase();
        using var first = db.CreateOpenConnection();
        using var second = db.CreateOpenConnection();

        Assert.Equal(1L, QueryScalar(first, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, QueryScalar(second, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public void Busy_Timeout_Applied_On_Every_New_Connection()
    {
        using var db = new TempSqliteDatabase();
        using var first = db.CreateOpenConnection();
        using var second = db.CreateOpenConnection();

        Assert.Equal(5000L, QueryScalar(first, "PRAGMA busy_timeout;"));
        Assert.Equal(5000L, QueryScalar(second, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void Journal_Mode_Is_Wal_On_Every_New_Connection()
    {
        using var db = new TempSqliteDatabase();
        using var first = db.CreateOpenConnection();
        using var second = db.CreateOpenConnection();

        Assert.Equal("wal", QueryText(first, "PRAGMA journal_mode;"));
        Assert.Equal("wal", QueryText(second, "PRAGMA journal_mode;"));
    }

    private static long QueryScalar(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string QueryText(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
