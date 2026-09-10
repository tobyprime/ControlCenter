using System.Text;

namespace DevicePanel.Agent;

/// <summary>
/// 终端启动噪声过滤器（TOB-403 F8）：PTY 内 stderr 与 stdout 同流，无法按流区分，
/// 因此只做「会话开头已知良性警告行」的白名单过滤（如 bash 的两行启动警告）。
/// 语义：过滤器仅在会话起始窗口生效——缓存不完整行直到能判定；命中白名单的整行丢弃；
/// 遇到任何非白名单内容立即透传并永久失效，后续输出（含相同的警告文本）不再过滤。
/// </summary>
internal sealed class TerminalStartupNoiseFilter
{
    /// <summary>已知良性警告行的稳定前缀（bash 源码内建文案，不随 locale 变化）。</summary>
    private static readonly string[] WhitelistPrefixes =
    [
        "bash: cannot set terminal process group",
        "bash: no job control",
    ];

    /// <summary>缓存上限：超过即整段透传并失效，防止白名单前缀长期扣住输出。</summary>
    private const int MaxPendingBytes = 256;

    private readonly List<byte> _pending = new();
    private bool _active = true;

    public bool IsActive => _active;

    /// <summary>喂入一段 PTY 输出，返回可下发到面板的字节（可能为空）。</summary>
    public byte[] Feed(ReadOnlySpan<byte> data)
    {
        if (!_active)
        {
            return data.ToArray();
        }

        _pending.AddRange(data.ToArray());
        while (true)
        {
            var newline = _pending.IndexOf((byte)'\n');
            if (newline < 0)
            {
                break;
            }

            if (!IsWhitelistedLine(_pending.GetRange(0, newline + 1)))
            {
                return DeactivateAndDrain();
            }

            _pending.RemoveRange(0, newline + 1);
        }

        // 无完整行：仅当缓存内容仍是某条白名单行的前缀时才继续等待，否则立即透传
        if (_pending.Count > 0 && !IsWhitelistPrefix(_pending))
        {
            return DeactivateAndDrain();
        }

        if (_pending.Count > MaxPendingBytes)
        {
            return DeactivateAndDrain();
        }

        return [];
    }

    /// <summary>会话收尾（EOF/关闭）时补发缓存，保证白名单前缀场景不吞字。</summary>
    public byte[] FlushPending()
    {
        _active = false;
        if (_pending.Count == 0)
        {
            return [];
        }

        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    private byte[] DeactivateAndDrain()
    {
        _active = false;
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    private static bool IsWhitelistedLine(List<byte> line)
    {
        var text = Decode(line);
        return WhitelistPrefixes.Any(prefix => text.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsWhitelistPrefix(List<byte> pending)
    {
        var text = Decode(pending).TrimStart();
        return WhitelistPrefixes.Any(prefix => prefix.StartsWith(text, StringComparison.Ordinal));
    }

    private static string Decode(List<byte> bytes) =>
        Encoding.UTF8.GetString(bytes.ToArray());
}
