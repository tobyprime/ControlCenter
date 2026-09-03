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
        return connection;
    }
}
