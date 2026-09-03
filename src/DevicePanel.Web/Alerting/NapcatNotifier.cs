using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// QQ 渠道（首个 INotifier 实现）：对接本机 napcat 的 OneBot v11 HTTP API。
/// 每次发送都读取最新配置（面板保存即生效）；未配置/HTTP 失败均抛异常，交给分发 worker 记账重试。
/// </summary>
public sealed class NapcatNotifier : INotifier
{
    public const string ChannelNameValue = "qq";

    public const string TargetPrivate = "private";
    public const string TargetGroup = "group";

    private readonly IAlertSettingsStore _settings;
    private readonly HttpClient _http;

    public NapcatNotifier(IAlertSettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    public string ChannelName => ChannelNameValue;

    public async Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var settings = _settings.Get();
        if (string.IsNullOrWhiteSpace(settings.NapcatBaseUrl))
        {
            throw new InvalidOperationException("napcat 尚未配置：缺少 OneBot HTTP 地址");
        }

        if (settings.NapcatTargetType is not (TargetPrivate or TargetGroup)
            || !long.TryParse(settings.NapcatTargetId, out var targetId))
        {
            throw new InvalidOperationException("napcat 尚未配置：缺少有效的通知目标（私聊 QQ 或群号）");
        }

        var path = settings.NapcatTargetType == TargetGroup ? "send_group_msg" : "send_private_msg";
        var text = $"{message.Title}\n{message.Content}";
        var payload = settings.NapcatTargetType == TargetGroup
            ? SerializeBody(new { group_id = targetId, message = new object[] { OneBotText(text) } })
            : SerializeBody(new { user_id = targetId, message = new object[] { OneBotText(text) } });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.NapcatBaseUrl.TrimEnd('/')}/{path}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(settings.NapcatToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.NapcatToken}");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"napcat 返回 HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>OneBot 消息体保留 UTF-8 原文（默认编码器会把中文转义成 \uXXXX，不利于 napcat 侧日志排查）。</summary>
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string SerializeBody<T>(T payload) where T : class => JsonSerializer.Serialize(payload, BodyJsonOptions);

    private static object OneBotText(string text) => new { type = "text", data = new { text } };
}
