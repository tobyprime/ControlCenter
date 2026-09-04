using DevicePanel.Web.Devices;

namespace DevicePanel.Web.Interactions;

/// <summary>目标声明的交互模式目录（约束 C）：核心按目标声明渲染入口，不绑定单一形态。</summary>
public interface IInteractionModeCatalog
{
    /// <summary>目标声明可用的交互模式 key 列表；目标不存在或未声明时返回空列表。</summary>
    IReadOnlyList<string> GetDeclaredModeKeys(long targetId);
}

/// <summary>设备目标声明实现：现有设备（agent 回连目标）均声明 shell。</summary>
/// <remarks>TOB-361 Target 统一后由目标类型驱动：device 目标声明 shell，service 目标未声明即不显示终端入口。</remarks>
public sealed class DeviceInteractionModeCatalog(IDeviceRegistry devices) : IInteractionModeCatalog
{
    public IReadOnlyList<string> GetDeclaredModeKeys(long targetId)
    {
        return devices.Get(targetId) is null ? [] : [ShellInteractionMode.ModeKey];
    }
}
