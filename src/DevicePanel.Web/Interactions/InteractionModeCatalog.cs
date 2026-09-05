using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Interactions;

/// <summary>采集器声明的交互模式目录（约束 C）：核心按采集器声明渲染入口，不绑定单一形态。</summary>
public interface IInteractionModeCatalog
{
    /// <summary>采集器声明可用的交互模式 key 列表；采集器不存在或未声明时返回空列表。</summary>
    IReadOnlyList<string> GetDeclaredModeKeys(long collectorId);
}

/// <summary>按采集器模式驱动的声明实现（三期模块3 泛化）：push 采集器（agent 回连）声明 shell；pull 采集器无 agent 通道、不声明任何交互模式。</summary>
public sealed class DeviceInteractionModeCatalog(ICollectorRegistry collectors) : IInteractionModeCatalog
{
    public IReadOnlyList<string> GetDeclaredModeKeys(long collectorId)
    {
        return collectors.Get(collectorId)?.AgentId is not null ? [ShellInteractionMode.ModeKey] : [];
    }
}
