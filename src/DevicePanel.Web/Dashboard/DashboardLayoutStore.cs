using System.Text.Json;
using DevicePanel.Web.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevicePanel.Web.Dashboard;

/// <summary>布局中的单个卡片条目；config 为业务透传字段，后端只存不解释语义。</summary>
public sealed record DashboardCard(string Id, string Type, int Sort, bool Visible, JsonElement Config);

/// <summary>整份主页布局：单用户单套，整体替换保存。</summary>
public sealed record DashboardLayout(IReadOnlyList<DashboardCard> Cards);

public interface IDashboardLayoutStore
{
    /// <summary>读取已保存的布局；未配置或存储内容损坏时返回 null（由上层回退服务端默认布局）。</summary>
    DashboardLayout? GetLayout();

    /// <summary>整份替换保存布局。</summary>
    void SaveLayout(DashboardLayout layout);
}

/// <summary>dashboard_layouts 上的主页布局读写；整份布局以 JSON 文本存储，与设备/目标数据无外键关联。</summary>
public sealed class DashboardLayoutStore : IDashboardLayoutStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public DashboardLayoutStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DashboardLayout? GetLayout()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT layout_json FROM dashboard_layouts WHERE id = 1";
        var json = command.ExecuteScalar() as string;
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DashboardLayout>(json);
        }
        catch (JsonException)
        {
            // 存储内容损坏时按未配置处理，上层回退默认布局，读路径不因脏数据 500
            return null;
        }
    }

    public void SaveLayout(DashboardLayout layout)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dashboard_layouts(id, layout_json, updated_at_utc) VALUES (1, $layoutJson, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET layout_json = excluded.layout_json, updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$layoutJson", JsonSerializer.Serialize(layout));
        command.Parameters.AddWithValue("$updatedAt", _timeProvider.GetUtcNow().ToString("O"));
        command.ExecuteNonQuery();
    }
}
