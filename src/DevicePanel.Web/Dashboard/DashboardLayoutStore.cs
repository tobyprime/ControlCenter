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

public sealed class DashboardLayoutStore : IDashboardLayoutStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public DashboardLayoutStore(SqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DashboardLayout? GetLayout() => null;

    public void SaveLayout(DashboardLayout layout)
    {
    }
}
