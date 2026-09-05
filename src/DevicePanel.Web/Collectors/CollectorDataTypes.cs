namespace DevicePanel.Web.Collectors;

/// <summary>
/// 采集器数据类型抽象（三期模块3 验收8）：新增一种采集器数据类型 = 注册一个 ICollectorDataType 实现
/// （配套查询端点随类型自带），清单 API 经 DI 自动纳入，核心管道（信封/入库/告警/曲线）零改动。
/// </summary>
public interface ICollectorDataType
{
    string Key { get; }

    string DisplayName { get; }
}

/// <summary>内置数据类型：指标（push 周期上报 / pull 轮询映射，入库与聚合经指标管道）。</summary>
public sealed class MetricsDataType : ICollectorDataType
{
    public string Key => "metrics";

    public string DisplayName => "指标";
}

/// <summary>内置数据类型：日志（按需只读拉取，面板不落库）。</summary>
public sealed class LogsDataType : ICollectorDataType
{
    public string Key => "logs";

    public string DisplayName => "日志";
}

/// <summary>数据类型清单：收集 DI 中全部 ICollectorDataType 注册（新增类型零改动出现在清单）。</summary>
public sealed class CollectorDataTypeCatalog
{
    private readonly IReadOnlyList<ICollectorDataType> _types;

    public CollectorDataTypeCatalog(IEnumerable<ICollectorDataType> types) => _types = [.. types];

    public IReadOnlyList<(string Key, string DisplayName)> List() =>
        _types.Select(t => (t.Key, t.DisplayName)).ToList();
}
