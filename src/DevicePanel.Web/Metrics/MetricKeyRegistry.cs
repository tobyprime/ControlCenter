using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Metrics;

public enum MetricValueType
{
    Number,
    Enum,
    String,
    Bool,
}

public static class MetricValueTypeExtensions
{
    public static string ToStorage(this MetricValueType type) => type switch
    {
        MetricValueType.Number => "number",
        MetricValueType.Enum => "enum",
        MetricValueType.String => "string",
        MetricValueType.Bool => "bool",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryFromStorage(string value, out MetricValueType type)
    {
        switch (value)
        {
            case "number": type = MetricValueType.Number; return true;
            case "enum": type = MetricValueType.Enum; return true;
            case "string": type = MetricValueType.String; return true;
            case "bool": type = MetricValueType.Bool; return true;
            default: type = default; return false;
        }
    }

    /// <summary>样本值统一转文本（状态不符规则比较与告警文案用）：bool 归一为 true/false，数值去掉多余小数位。</summary>
    public static string? FormatValue(this MetricSample sample, MetricValueType type) => type switch
    {
        MetricValueType.Number => sample.ValueNum is { } num ? num.ToString("0.###") : null,
        MetricValueType.Bool => sample.ValueNum is { } flag ? (flag != 0 ? "true" : "false") : sample.ValueText,
        _ => sample.ValueText,
    };
}

public sealed record MetricKeyInfo(
    string Key,
    MetricValueType ValueType,
    string DisplayName,
    string Unit,
    bool BuiltIn,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// MetricKey 注册表（约束 A：指标语义中立）——核心只承载带类型的指标序列与展示元数据，不内置业务含义；
/// 新增一种指标 = 注册 key + 值类型，上报、存储、告警、展示全链路零核心改动。
/// 内置 key（built_in）随迁移播种，不可删除、不可改类型。
/// </summary>
public interface IMetricKeyRegistry
{
    IReadOnlyList<MetricKeyInfo> List();

    MetricKeyInfo? Get(string key);

    /// <summary>注册新指标；key 重复抛 InvalidOperationException（调用方先查重）。</summary>
    MetricKeyInfo Register(string key, MetricValueType valueType, string displayName, string unit);

    /// <summary>更新展示元数据（显示名/单位）；值类型注册后不可变。</summary>
    MetricKeyInfo? UpdateDisplay(string key, string displayName, string unit);

    bool Delete(string key);
}

public sealed class MetricKeyRegistry : IMetricKeyRegistry
{
    public const int MaxKeyLength = 64;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public MetricKeyRegistry(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public static string? NormalizeKey(string? key)
    {
        var candidate = (key ?? string.Empty).Trim();
        if (candidate.Length is 0 or > MaxKeyLength)
        {
            return null;
        }

        foreach (var segment in candidate.Split('.'))
        {
            if (segment.Length == 0 || !char.IsAsciiLetterLower(segment[0]) || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            {
                return null;
            }
        }

        return candidate;
    }

    public IReadOnlyList<MetricKeyInfo> List()
    {
        var keys = new List<MetricKeyInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc FROM metric_keys ORDER BY built_in DESC, key";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(Map(reader));
        }

        return keys;
    }

    public MetricKeyInfo? Get(string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc FROM metric_keys WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public MetricKeyInfo Register(string key, MetricValueType valueType, string displayName, string unit)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        try
        {
            using var connection = _connectionFactory.CreateOpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO metric_keys(key, value_type, display_name, unit, built_in, created_at_utc, updated_at_utc)
                VALUES ($key, $valueType, $displayName, $unit, 0, $createdAt, $updatedAt)
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$valueType", valueType.ToStorage());
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$unit", unit);
            command.Parameters.AddWithValue("$createdAt", nowUtc.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", nowUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"指标 {key} 已注册");
        }

        return new MetricKeyInfo(key, valueType, displayName, unit, false, nowUtc, nowUtc);
    }

    /// <summary>UNIQUE 约束冲突（SQLITE_CONSTRAINT / SQLITE_CONSTRAINT_UNIQUE，兼容主码与扩展码）。</summary>
    private static bool IsUniqueViolation(SqliteException ex) =>
        ex.SqliteErrorCode is 19 or 2067;

    public MetricKeyInfo? UpdateDisplay(string key, string displayName, string unit)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE metric_keys SET display_name = $displayName, unit = $unit, updated_at_utc = $updatedAt
            WHERE key = $key
            """;
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$unit", unit);
        command.Parameters.AddWithValue("$updatedAt", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteNonQuery() == 0 ? null : Get(key);
    }

    public bool Delete(string key)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metric_keys WHERE key = $key AND built_in = 0";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteNonQuery() > 0;
    }

    private static MetricKeyInfo Map(SqliteDataReader reader)
    {
        if (!MetricValueTypeExtensions.TryFromStorage(reader.GetString(1), out var valueType))
        {
            throw new InvalidOperationException($"指标 {reader.GetString(0)} 的值类型不合法：{reader.GetString(1)}");
        }

        return new MetricKeyInfo(
            reader.GetString(0),
            valueType,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)));
    }
}
