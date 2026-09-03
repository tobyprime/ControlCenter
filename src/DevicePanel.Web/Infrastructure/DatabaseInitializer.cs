namespace DevicePanel.Web.Infrastructure;

/// <summary>启动时初始化数据库：执行待应用的迁移（连接级 PRAGMA 由 SqliteConnectionFactory 统一设置）。</summary>
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
        DatabaseMigrator.Migrate(connection);
        _logger.LogInformation("数据库初始化完成：{DatabasePath}", connection.DataSource);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
