using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Targets;

/// <summary>目标类型：设备（挂 agent）与服务（面板侧探针）共用同一目标模型（TOB-360 模块 0）。</summary>
public static class TargetTypes
{
    public const string Device = "device";
    public const string Service = "service";

    public static bool IsValid(string type) => type is Device or Service;
}

/// <summary>目标：面板监测与告警的统一对象。device 目标的名称运行时联查 devices（改名自动跟随）。</summary>
public sealed record TargetInfo(
    long Id,
    string Type,
    string Name,
    long? DeviceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsDevice => Type == TargetTypes.Device;

    public bool IsService => Type == TargetTypes.Service;
}

public interface ITargetStore
{
    /// <summary>为设备建立（或幂等复用）device 目标：设备创建与迁移共用此入口。</summary>
    TargetInfo ProvisionForDevice(long deviceId, string name);

    TargetInfo? Get(long id);

    TargetInfo? GetByDeviceId(long deviceId);

    IReadOnlyList<TargetInfo> List(string? type = null);
}

/// <summary>目标存储（targets）：device 目标与 devices 行一一对应（唯一索引 + 级联删除）。</summary>
public sealed class TargetStore : ITargetStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public TargetStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TargetInfo ProvisionForDevice(long deviceId, string name)
    {
        var existing = GetByDeviceId(deviceId);
        if (existing is not null)
        {
            return existing;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO targets(type, name, device_id, created_at_utc, updated_at_utc)
            VALUES ('device', $name, $deviceId, $createdAt, $updatedAt)
            ON CONFLICT(device_id) WHERE device_id IS NOT NULL DO NOTHING
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$createdAt", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$updatedAt", FormatUtc(nowUtc));
        command.ExecuteNonQuery();

        return GetByDeviceId(deviceId)
            ?? throw new InvalidOperationException($"设备 {deviceId} 的目标创建失败");
    }

    public TargetInfo? Get(long id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE t.id = $id";
        command.Parameters.AddWithValue("$id", id);
        return ReadOne(command);
    }

    public TargetInfo? GetByDeviceId(long deviceId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE t.device_id = $deviceId";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        return ReadOne(command);
    }

    public IReadOnlyList<TargetInfo> List(string? type = null)
    {
        var targets = new List<TargetInfo>();
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        if (type is null)
        {
            command.CommandText = SelectSql + " ORDER BY t.id";
        }
        else
        {
            command.CommandText = SelectSql + " WHERE t.type = $type ORDER BY t.id";
            command.Parameters.AddWithValue("$type", type);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            targets.Add(Map(reader));
        }

        return targets;
    }

    // device 目标展示名联查 devices.name：设备改名后目标名自动跟随，无需同步
    private const string SelectSql = """
        SELECT t.id, t.type, COALESCE(d.name, t.name), t.device_id, t.created_at_utc, t.updated_at_utc
        FROM targets t LEFT JOIN devices d ON d.id = t.device_id
        """;

    private static TargetInfo? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static TargetInfo Map(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        DateTimeOffset.Parse(reader.GetString(4)),
        DateTimeOffset.Parse(reader.GetString(5)));

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O");
}
