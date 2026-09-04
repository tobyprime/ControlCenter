using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Metrics;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Probing;

/// <summary>探针 JSON 提取映射：一条 JSONPath → 一个 metric key（值类型决定入库形态与可用告警规则类型）。</summary>
public sealed record ProbeMetricMapping(
    string MetricKey,
    string JsonPath,
    MetricValueType ValueType,
    string DisplayName,
    string Unit);

/// <summary>服务目标探针配置：一目标一配置；status/latency_ms 为内置指标，mappings 为可调的 JSONPath 提取项。</summary>
public sealed record ProbeConfig(
    long TargetId,
    string Url,
    int IntervalSeconds,
    IReadOnlyList<ProbeMetricMapping> Mappings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IProbeConfigStore
{
    ProbeConfig? Get(long targetId);

    IReadOnlyList<ProbeConfig> List();

    ProbeConfig Save(long targetId, string url, int intervalSeconds, IReadOnlyList<ProbeMetricMapping> mappings);

    bool Delete(long targetId);
}

/// <summary>探针配置持久化（probe_configs 表，随目标删除级联清理）。</summary>
public sealed class ProbeConfigStore : IProbeConfigStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public ProbeConfigStore(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public ProbeConfig? Get(long targetId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc
            FROM probe_configs WHERE target_id = $targetId
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapConfig(reader) : null;
    }

    public IReadOnlyList<ProbeConfig> List()
    {
        var configs = new List<ProbeConfig>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc
            FROM probe_configs ORDER BY target_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            configs.Add(MapConfig(reader));
        }

        return configs;
    }

    public ProbeConfig Save(long targetId, string url, int intervalSeconds, IReadOnlyList<ProbeMetricMapping> mappings)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO probe_configs(target_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc)
            VALUES ($targetId, $url, $intervalSeconds, $mappings, $createdAt, $updatedAt)
            ON CONFLICT(target_id) DO UPDATE SET
                url = $url, interval_seconds = $intervalSeconds, mappings_json = $mappings, updated_at_utc = $updatedAt
            """;
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$intervalSeconds", intervalSeconds);
        command.Parameters.AddWithValue("$mappings", SerializeMappings(mappings));
        command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.ExecuteNonQuery();
        return new ProbeConfig(targetId, url, intervalSeconds, mappings, nowUtc, nowUtc);
    }

    public bool Delete(long targetId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM probe_configs WHERE target_id = $targetId";
        command.Parameters.AddWithValue("$targetId", targetId);
        return command.ExecuteNonQuery() > 0;
    }

    private static ProbeConfig MapConfig(SqliteDataReader reader)
    {
        var mappings = JsonSerializer.Deserialize<List<StoredMapping>>(reader.GetString(3)) ?? [];
        return new ProbeConfig(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            mappings.Select(m => new ProbeMetricMapping(m.MetricKey, m.JsonPath, m.ValueType, m.DisplayName, m.Unit)).ToList(),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));
    }

    private static string SerializeMappings(IReadOnlyList<ProbeMetricMapping> mappings) =>
        JsonSerializer.Serialize(mappings.Select(m => new StoredMapping(m.MetricKey, m.JsonPath, m.ValueType, m.DisplayName, m.Unit)).ToList());

    private sealed record StoredMapping(string MetricKey, string JsonPath, MetricValueType ValueType, string DisplayName, string Unit);

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
