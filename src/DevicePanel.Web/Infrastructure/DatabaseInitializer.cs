using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Infrastructure;

/// <summary>启动时初始化数据库：开启 WAL、外键约束并执行待应用的迁移。</summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(SqliteConnectionFactory connectionFactory, ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        SetPragmas(connection);
        DatabaseMigrator.Migrate(connection);
        _logger.LogInformation("数据库初始化完成：{DatabasePath}", connection.DataSource);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void SetPragmas(SqliteConnection connection)
    {
        ExecuteScalar(connection, "PRAGMA journal_mode = WAL;");
        ExecuteScalar(connection, "PRAGMA foreign_keys = ON;");
        ExecuteScalar(connection, "PRAGMA busy_timeout = 5000;");
    }

    private static object? ExecuteScalar(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        return command.ExecuteScalar();
    }
}
