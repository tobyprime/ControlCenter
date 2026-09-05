using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Metrics;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Probing;

/// <summary>pull 采集器 JSON 提取映射：一条 JSONPath → 一个 metric key（值类型决定入库形态与可用告警规则类型）。</summary>
public sealed record PullMetricMapping(
    string MetricKey,
    string JsonPath,
    MetricValueType ValueType,
    string DisplayName,
    string Unit);

/// <summary>pull 采集器配置：一采集器一配置；status/latency_ms 为内置指标，mappings 为可调的 JSONPath 提取项。</summary>
public sealed record PullCollectorConfig(
    long CollectorId,
    string Url,
    int IntervalSeconds,
    IReadOnlyList<PullMetricMapping> Mappings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IPullCollectorConfigStore
{
    PullCollectorConfig? Get(long collectorId);

    IReadOnlyList<PullCollectorConfig> List();

    PullCollectorConfig Save(long collectorId, string url, int intervalSeconds, IReadOnlyList<PullMetricMapping> mappings);
}

/// <summary>pull 采集器配置持久化（collector_pull_configs 表，随采集器删除级联清理）。</summary>
public sealed class PullCollectorConfigStore : IPullCollectorConfigStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public PullCollectorConfigStore(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public PullCollectorConfig? Get(long collectorId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT collector_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc
            FROM collector_pull_configs WHERE collector_id = $collectorId
            """;
        command.Parameters.AddWithValue("$collectorId", collectorId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapConfig(reader) : null;
    }

    public IReadOnlyList<PullCollectorConfig> List()
    {
        var configs = new List<PullCollectorConfig>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT collector_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc
            FROM collector_pull_configs ORDER BY collector_id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            configs.Add(MapConfig(reader));
        }

        return configs;
    }

    public PullCollectorConfig Save(long collectorId, string url, int intervalSeconds, IReadOnlyList<PullMetricMapping> mappings)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collector_pull_configs(collector_id, url, interval_seconds, mappings_json, created_at_utc, updated_at_utc)
            VALUES ($collectorId, $url, $intervalSeconds, $mappings, $createdAt, $updatedAt)
            ON CONFLICT(collector_id) DO UPDATE SET
                url = $url, interval_seconds = $intervalSeconds, mappings_json = $mappings, updated_at_utc = $updatedAt
            """;
        command.Parameters.AddWithValue("$collectorId", collectorId);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$intervalSeconds", intervalSeconds);
        command.Parameters.AddWithValue("$mappings", SerializeMappings(mappings));
        command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.ExecuteNonQuery();
        return new PullCollectorConfig(collectorId, url, intervalSeconds, mappings, nowUtc, nowUtc);
    }

    private static PullCollectorConfig MapConfig(SqliteDataReader reader)
    {
        var mappings = JsonSerializer.Deserialize<List<StoredMapping>>(reader.GetString(3)) ?? [];
        return new PullCollectorConfig(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            mappings.Select(m => new PullMetricMapping(m.MetricKey, m.JsonPath, m.ValueType, m.DisplayName, m.Unit)).ToList(),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));
    }

    private static string SerializeMappings(IReadOnlyList<PullMetricMapping> mappings) =>
        JsonSerializer.Serialize(mappings.Select(m => new StoredMapping(m.MetricKey, m.JsonPath, m.ValueType, m.DisplayName, m.Unit)).ToList());

    private sealed record StoredMapping(string MetricKey, string JsonPath, MetricValueType ValueType, string DisplayName, string Unit);

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
