namespace DevicePanel.Web.Interactions;

/// <summary>交互模式注册表（约束 C）：收集 DI 中全部 IInteractionMode 实现，核心按目标声明从表中解析模式。</summary>
public sealed class InteractionModeRegistry
{
    private readonly Dictionary<string, IInteractionMode> _modesByKey;

    public InteractionModeRegistry(IEnumerable<IInteractionMode> modes)
    {
        _modesByKey = [];
        var ordered = new List<IInteractionMode>();
        foreach (var mode in modes)
        {
            if (!_modesByKey.TryAdd(mode.Key, mode))
            {
                throw new ArgumentException($"交互模式 key 重复：{mode.Key}", nameof(modes));
            }

            ordered.Add(mode);
        }

        Modes = ordered;
    }

    /// <summary>全部已注册模式（按注册顺序）。</summary>
    public IReadOnlyList<IInteractionMode> Modes { get; }

    /// <summary>按键查找模式；未注册返回 null。</summary>
    public IInteractionMode? Find(string key) => _modesByKey.TryGetValue(key, out var mode) ? mode : null;
}
