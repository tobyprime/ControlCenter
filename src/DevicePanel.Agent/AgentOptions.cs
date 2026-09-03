namespace DevicePanel.Agent;

/// <summary>agent 运行参数：命令行优先，其次环境变量（PANEL_URL / PANEL_TOKEN / PANEL_INTERVAL_SECONDS）。</summary>
public sealed class AgentOptions
{
    /// <summary>面板 agent 接入端点，如 wss://panel.example.com/agent/ws。</summary>
    public string Url { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>心跳周期（秒），需与面板侧 DevicePanel:Agent:HeartbeatIntervalSeconds 一致。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public static AgentOptions Parse(IReadOnlyList<string> args, Func<string, string?> env)
    {
        var options = new AgentOptions
        {
            Url = env("PANEL_URL") ?? string.Empty,
            Token = env("PANEL_TOKEN") ?? string.Empty,
        };
        if (int.TryParse(env("PANEL_INTERVAL_SECONDS"), out var interval) && interval > 0)
        {
            options.HeartbeatIntervalSeconds = interval;
        }

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Count:
                    options.Url = args[++i];
                    break;
                case "--token" when i + 1 < args.Count:
                    options.Token = args[++i];
                    break;
                case "--interval" when i + 1 < args.Count && int.TryParse(args[++i], out var value) && value > 0:
                    options.HeartbeatIntervalSeconds = value;
                    break;
            }
        }

        return options;
    }

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            error = "缺少面板接入地址：请通过 --url 或环境变量 PANEL_URL 指定（如 wss://panel.example.com/agent/ws）";
            return false;
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            error = $"接入地址无效：{Url}（须为 ws:// 或 wss:// 完整地址）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            error = "缺少设备 token：请通过 --token 或环境变量 PANEL_TOKEN 指定（面板设备详情中获取）";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
