namespace DevicePanel.Web.Alerting;

/// <summary>
/// napcat 配置的注入来源（appsettings: DevicePanel:Alert:Napcat，容器部署经 Secret 注入环境变量）。
/// 仅作为首次启动的种子默认值写入面板 KV 设置；此后以面板 UI 保存的配置为准。
/// </summary>
public sealed class NapcatSeedOptions
{
    public const string SectionName = "DevicePanel:Alert:Napcat";

    /// <summary>OneBot v11 HTTP 地址，如 http://napcat:3000。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>OneBot HTTP token。</summary>
    public string? Token { get; set; }

    /// <summary>通知目标类型：private / group。</summary>
    public string? TargetType { get; set; }

    /// <summary>通知目标：私聊 QQ 号或群号。</summary>
    public string? TargetId { get; set; }
}

/// <summary>
/// napcat 配置种子：仅当面板 KV 设置对应项为空时写入配置默认值（Secret 注入），
/// 已有配置（UI 保存）永不被覆盖；未配置时无操作。
/// </summary>
public sealed class AlertSettingsSeeder : IHostedService
{
    private readonly IAlertSettingsStore _settingsStore;
    private readonly NapcatSeedOptions _options;
    private readonly ILogger<AlertSettingsSeeder> _logger;

    public AlertSettingsSeeder(
        IAlertSettingsStore settingsStore,
        NapcatSeedOptions options,
        ILogger<AlertSettingsSeeder> logger)
    {
        _settingsStore = settingsStore;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var current = _settingsStore.Get();
        _settingsStore.SeedIfEmpty(new AlertDeliverySettings(
            _options.BaseUrl,
            _options.Token,
            _options.TargetType,
            _options.TargetId));

        var seeded = _settingsStore.Get();
        if (seeded != current)
        {
            _logger.LogInformation(
                "napcat 配置已从环境注入空缺项：地址 {BaseUrlConfigured}，token {TokenConfigured}，目标 {TargetConfigured}",
                !string.IsNullOrEmpty(_options.BaseUrl),
                !string.IsNullOrEmpty(_options.Token),
                !string.IsNullOrEmpty(_options.TargetType) && !string.IsNullOrEmpty(_options.TargetId));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
