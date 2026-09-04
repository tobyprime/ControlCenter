using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Metrics;

/// <summary>指标值类型（TOB-360 约束 A：核心只做数据类型抽象，不解释业务含义）。</summary>
public enum MetricValueType
{
    Number,
    Enum,
    String,
    Bool,
}

/// <summary>指标值类型与库内文本的互转（库层校验 CHECK 约束）。</summary>
public static class MetricValueTypeText
{
    public static string Format(MetricValueType type) => type switch
    {
        MetricValueType.Number => "number",
        MetricValueType.Enum => "enum",
        MetricValueType.String => "string",
        MetricValueType.Bool => "bool",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static MetricValueType Parse(string text) => text switch
    {
        "number" => MetricValueType.Number,
        "enum" => MetricValueType.Enum,
        "string" => MetricValueType.String,
        "bool" => MetricValueType.Bool,
        _ => throw new ArgumentException($"未知指标类型：{text}", nameof(text)),
    };
}

/// <summary>注册的指标键：类型 + 展示元数据（unit / 展示名）。新增一种指标 = 注册一条，不改核心逻辑。</summary>
public sealed record MetricKeyInfo(string Key, MetricValueType ValueType, string? Unit, string DisplayName);

public interface IMetricKeyRegistry
{
    MetricKeyInfo? Get(string key);

    bool IsRegistered(string key);

    IReadOnlyList<MetricKeyInfo> List();

    /// <summary>注册（或更新元数据，传入值即最终值）。类型注册后不可变更，防止序列数据类型错位。</summary>
    MetricKeyInfo Register(string key, MetricValueType valueType, string? unit, string? displayName);
}

/// <summary>指标键注册表存储（metric_keys）。</summary>
public sealed class MetricKeyRegistry : IMetricKeyRegistry
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public MetricKeyRegistry(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MetricKeyInfo? Get(string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_type, unit, display_name FROM metric_keys WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return ReadOne(command);
    }

    public bool IsRegistered(string key) => Get(key) is not null;

    public IReadOnlyList<MetricKeyInfo> List()
    {
        var keys = new List<MetricKeyInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_type, unit, display_name FROM metric_keys ORDER BY key";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(Map(reader));
        }

        return keys;
    }

    public MetricKeyInfo Register(string key, MetricValueType valueType, string? unit, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("指标键不能为空", nameof(key));
        }

        var existing = Get(key);
        if (existing is { } same && same.ValueType != valueType)
        {
            throw new InvalidOperationException($"指标 {key} 已注册为 {MetricValueTypeText.Format(same.ValueType)}，不能变更为 {MetricValueTypeText.Format(valueType)}");
        }

        var finalDisplay = string.IsNullOrWhiteSpace(displayName) ? key : displayName!;
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metric_keys(key, value_type, unit, display_name, created_at_utc)
            VALUES ($key, $valueType, $unit, $displayName, $createdAt)
            ON CONFLICT(key) DO UPDATE SET
                unit = excluded.unit, display_name = excluded.display_name
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$valueType", MetricValueTypeText.Format(valueType));
        command.Parameters.AddWithValue("$unit", (object?)unit ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", finalDisplay);
        command.Parameters.AddWithValue("$createdAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();

        return new MetricKeyInfo(key, valueType, unit, finalDisplay);
    }

    private static MetricKeyInfo? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static MetricKeyInfo Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        MetricValueTypeText.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3));
}
