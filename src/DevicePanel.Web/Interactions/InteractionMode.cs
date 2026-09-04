namespace DevicePanel.Web.Interactions;

/// <summary>交互模式：目标可声明的一种交互入口形态（shell 终端、未来的 MC 控制台 / RCON 等）。</summary>
/// <remarks>约束 C：核心只认模式注册表与目标声明，不绑定任何单一形态；新实现注册 DI 即接入注册表。</remarks>
public interface IInteractionMode
{
    /// <summary>模式稳定标识（如 shell），目标声明与前端入口渲染均以它为准。</summary>
    string Key { get; }

    /// <summary>展示名（如「Shell 终端」）。</summary>
    string DisplayName { get; }

    /// <summary>可选说明文案。</summary>
    string? Description { get; }
}
