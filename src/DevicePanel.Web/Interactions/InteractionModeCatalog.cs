using DevicePanel.Web.Targets;

namespace DevicePanel.Web.Interactions;

/// <summary>目标声明的交互模式目录（约束 C）：核心按目标声明渲染入口，不绑定单一形态。</summary>
public interface IInteractionModeCatalog
{
    /// <summary>目标声明可用的交互模式 key 列表；目标不存在或未声明时返回空列表。</summary>
    IReadOnlyList<string> GetDeclaredModeKeys(long targetId);
}

/// <summary>按目标类型驱动的声明实现：device 目标（agent 回连）声明 shell。</summary>
/// <remarks>TOB-361 Target 统一后由目标类型驱动：device 目标声明 shell，service 目标无 agent 通道、不声明任何交互模式（集成审查 round 1 问题 1）。</remarks>
public sealed class DeviceInteractionModeCatalog(ITargetRegistry targets) : IInteractionModeCatalog
{
    public IReadOnlyList<string> GetDeclaredModeKeys(long targetId)
    {
        return targets.Get(targetId)?.Type == TargetTypes.Device ? [ShellInteractionMode.ModeKey] : [];
    }
}
