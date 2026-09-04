using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Infrastructure;

public interface IPanelSettingsStore
{
    string? Get(string key);

    void Set(string key, string? value);
}

/// <summary>面板 KV 设置通用读写（panel_settings）：工作流标记等非业务键的存取。</summary>
public sealed class PanelSettingsStore : IPanelSettingsStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public PanelSettingsStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? Get(string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM panel_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string is { Length: > 0 } value ? value : null;
    }

    public void Set(string key, string? value)
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
