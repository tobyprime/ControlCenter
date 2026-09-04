using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Alerting;

/// <summary>napcat 连接配置：OneBot v11 HTTP 地址 + token + 通知目标（私聊 user_id 或群 group_id，二选一）。</summary>
public sealed record AlertDeliverySettings(string? NapcatBaseUrl, string? NapcatToken, string? NapcatTargetType, string? NapcatTargetId);

public interface IAlertSettingsStore
{
    AlertDeliverySettings Get();

    void Save(AlertDeliverySettings settings);

    /// <summary>把配置来源的默认值种入空缺项：已有值（UI 保存）与未配置项均不触碰。</summary>
    void SeedIfEmpty(AlertDeliverySettings defaults);
}

/// <summary>面板 KV 设置存储（panel_settings）上的 napcat 配置读写；token 只入库，不回传 API。</summary>
public sealed class AlertSettingsStore : IAlertSettingsStore
{
    public const string KeyBaseUrl = "napcat.base_url";
    public const string KeyToken = "napcat.token";
    public const string KeyTargetType = "napcat.target_type";
    public const string KeyTargetId = "napcat.target_id";

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public AlertSettingsStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AlertDeliverySettings Get() => new(
        Read(KeyBaseUrl),
        Read(KeyToken),
        Read(KeyTargetType),
        Read(KeyTargetId));

    public void Save(AlertDeliverySettings settings)
    {
        Write(KeyBaseUrl, settings.NapcatBaseUrl);
        Write(KeyToken, settings.NapcatToken);
        Write(KeyTargetType, settings.NapcatTargetType);
        Write(KeyTargetId, settings.NapcatTargetId);
    }

    public void SeedIfEmpty(AlertDeliverySettings defaults)
    {
        WriteIfEmpty(KeyBaseUrl, defaults.NapcatBaseUrl);
        WriteIfEmpty(KeyToken, defaults.NapcatToken);
        WriteIfEmpty(KeyTargetType, defaults.NapcatTargetType);
        WriteIfEmpty(KeyTargetId, defaults.NapcatTargetId);
    }

    private void WriteIfEmpty(string key, string? value)
    {
        if (string.IsNullOrEmpty(value) || Read(key) is not null)
        {
            return;
        }

        Write(key, value);
    }

    private string? Read(string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM panel_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string is { Length: > 0 } value ? value : null;
    }

    private void Write(string key, string? value)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO panel_settings(key, value, updated_at_utc) VALUES ($key, $value, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value ?? string.Empty);
        command.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }
}
