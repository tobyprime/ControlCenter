namespace DevicePanel.Web.Interactions;

/// <summary>Web shell 终端（TOB-339 交付：/api/devices/{id}/terminal + frontend TerminalView）注册为首个交互模式。</summary>
/// <remarks>仅做模式登记，协议与功能保持不变；终端中继、留痕与 term.* 通道均不感知本抽象。</remarks>
public sealed class ShellInteractionMode : IInteractionMode
{
    public const string ModeKey = "shell";

    public string Key => ModeKey;

    public string DisplayName => "Shell 终端";

    public string? Description => "经面板与 agent 回连通道直达目标 shell，目标无需开放入站端口。";
}
