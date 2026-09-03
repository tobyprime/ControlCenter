using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Infrastructure;

public sealed class SqliteConnectionFactory
{
    private readonly DatabaseOptions _options;

    public SqliteConnectionFactory(DatabaseOptions options)
    {
        _options = options;
    }

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    public SqliteConnection CreateOpenConnection()
    {
        Directory.CreateDirectory(_options.DataDir);
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        // per-connection 设置必须随每条物理连接：WAL（幂等，持久化到库文件头）、外键强制、忙等
        ExecutePragma(connection, "PRAGMA journal_mode = WAL;");
        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        ExecutePragma(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void ExecutePragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        command.ExecuteNonQuery();
    }
}
