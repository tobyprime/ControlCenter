using System.Net;
using System.Text;
using DevicePanel.Web.Alerting;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>napcat（OneBot v11 HTTP）渠道实现测试：请求形状、token、失败语义。</summary>
public class NapcatNotifierTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Notify_Private_Target_Posts_OneBot_V11_Shape_With_Bearer_Token()
    {
        var handler = new FakeNapcatHandler(HttpStatusCode.OK);
        var notifier = CreateNotifier(handler, new AlertDeliverySettings(
            "http://napcat.local:3000/", "qq-token", "private", "10001"));

        await notifier.NotifyAsync(new AlertMessage("设备离线告警", "设备「web-1」已离线"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://napcat.local:3000/send_private_msg", request.Url);
        Assert.Equal("Bearer qq-token", request.Authorization);
        Assert.Contains("\"user_id\":10001", request.Body);
        Assert.Contains("设备离线告警", request.Body);
        Assert.Contains("设备「web-1」已离线", request.Body);
    }

    [Fact]
    public async Task Notify_Group_Target_Posts_Group_Endpoint()
    {
        var handler = new FakeNapcatHandler(HttpStatusCode.OK);
        var notifier = CreateNotifier(handler, new AlertDeliverySettings(
            "http://napcat.local:3000", null, "group", "778899"));

        await notifier.NotifyAsync(new AlertMessage("指标越限告警", "CPU 95%"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://napcat.local:3000/send_group_msg", request.Url);
        Assert.Null(request.Authorization);
        Assert.Contains("\"group_id\":778899", request.Body);
    }

    [Fact]
    public async Task Notify_Throws_When_Napcat_Is_Not_Configured()
    {
        var handler = new FakeNapcatHandler(HttpStatusCode.OK);
        var notifier = CreateNotifier(handler, new AlertDeliverySettings(null, null, null, null));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.NotifyAsync(new AlertMessage("t", "c"), CancellationToken.None));
        Assert.Contains("未配置", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Notify_Throws_On_Non_Success_Status_Keeping_Message_In_Queue_Upstream(HttpStatusCode status)
    {
        var handler = new FakeNapcatHandler(status);
        var notifier = CreateNotifier(handler, new AlertDeliverySettings(
            "http://napcat.local:3000", "t", "private", "10001"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.NotifyAsync(new AlertMessage("t", "c"), CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    private NapcatNotifier CreateNotifier(FakeNapcatHandler handler, AlertDeliverySettings settings)
    {
        var store = new AlertSettingsStore(_db.Factory);
        store.Save(settings);
        return new NapcatNotifier(store, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });
    }
    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    private sealed class FakeNapcatHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var values) ? string.Join(',', values) : null, body));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"status":"ok"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
