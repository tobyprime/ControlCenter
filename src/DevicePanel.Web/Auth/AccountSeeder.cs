using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Auth;

/// <summary>
/// 账号初始化：仅当 users 表为空时创建初始单用户。
/// 密码优先取配置 DevicePanel:Auth:InitialPassword；未配置则生成随机密码并以 WARN 级别打印一次。
/// </summary>
public sealed class AccountSeeder : IHostedService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthOptions _options;
    private readonly ILogger<AccountSeeder> _logger;

    public AccountSeeder(
        SqliteConnectionFactory connectionFactory,
        IPasswordHasher passwordHasher,
        AuthOptions options,
        ILogger<AccountSeeder> logger)
    {
        _connectionFactory = connectionFactory;
        _passwordHasher = passwordHasher;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        if (HasAnyUser(connection))
        {
            _logger.LogInformation("已存在用户账号，跳过初始化");
            return Task.CompletedTask;
        }

        var username = string.IsNullOrWhiteSpace(_options.InitialUsername) ? "admin" : _options.InitialUsername.Trim();
        var generatedPassword = false;
        var password = _options.InitialPassword;
        if (string.IsNullOrEmpty(password))
        {
            password = PasswordGenerator.Generate();
            generatedPassword = true;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO users(username, password_hash, created_at_utc) VALUES ($username, $hash, $createdAt)";
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", _passwordHasher.Hash(password));
        command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        if (generatedPassword)
        {
            _logger.LogWarning(
                "初始账号已创建：用户名 {Username}，随机密码 {Password}（仅本次打印，请尽快登录使用）",
                username,
                password);
        }
        else
        {
            _logger.LogInformation("初始账号已创建：用户名 {Username}（密码来自配置）", username);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool HasAnyUser(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
