using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>布局 API 集成测试：GET 默认布局、PUT 保存往返、config 透传、非法载荷 4xx。</summary>
public class DashboardApiTests : IDisposable
{
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Layout_Get_Without_Configuration_Returns_NonEmpty_Default()
    {
        var client = await AuthenticatedAsync();

        var response = await client.GetAsync("/api/dashboard/layout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await ReadJsonAsync(response);
        var cards = payload.GetProperty("cards");
        Assert.Equal(JsonValueKind.Array, cards.ValueKind);
        Assert.True(cards.GetArrayLength() > 0, "未配置时 GET 必须返回非空默认布局");

        var ids = cards.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Contains("overview-total-devices", ids);
        Assert.Contains("overview-online-devices", ids);
        Assert.Contains("overview-active-alerts", ids);
        Assert.All(cards.EnumerateArray(), card =>
        {
            Assert.Equal(JsonValueKind.Object, card.GetProperty("config").ValueKind);
            Assert.True(card.GetProperty("visible").GetBoolean());
            Assert.True(card.GetProperty("sort").GetInt32() >= 0);
        });
    }

    [Fact]
    public async Task Layout_Put_Then_Get_Returns_Saved_Layout_With_Config_Passthrough()
    {
        var client = await AuthenticatedAsync();
        const string put = """
            {
                "cards": [
                    { "id": "card-metric", "type": "metric-value", "sort": 1, "visible": false,
                      "config": { "targetId": 7, "key": "cpu", "windowHours": 6, "tags": ["cpu", "内存"], "note": null } },
                    { "id": "card-total", "type": "overview-total-devices", "sort": 0, "visible": true, "config": {} }
                ]
            }
            """;

        var putResponse = await PutLayoutAsync(client, put);

        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var getResponse = await client.GetAsync("/api/dashboard/layout");
        getResponse.EnsureSuccessStatusCode();
        var payload = await ReadJsonAsync(getResponse);

        // GET 按 sort 升序返回
        var cards = payload.GetProperty("cards");
        Assert.Equal(2, cards.GetArrayLength());
        Assert.Equal("card-total", cards[0].GetProperty("id").GetString());
        Assert.Equal("card-metric", cards[1].GetProperty("id").GetString());

        // config 原样往返：结构化字段与额外透传键的值均不被篡改（未知键语义仍归前端解释）
        var config = cards[1].GetProperty("config");
        Assert.Equal(7, config.GetProperty("targetId").GetInt64());
        Assert.Equal("cpu", config.GetProperty("key").GetString());
        Assert.Equal(6, config.GetProperty("windowHours").GetDouble());
        Assert.Equal("内存", config.GetProperty("tags")[1].GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("note").ValueKind);
        Assert.False(cards[1].GetProperty("visible").GetBoolean());
    }

    [Fact]
    public async Task Layout_Put_Replaces_Previously_Saved_Layout()
    {
        var client = await AuthenticatedAsync();
        await PutLayoutAsync(client, """{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": true, "config": {} } ] }""");

        await PutLayoutAsync(client, """
            { "cards": [
                { "id": "b", "type": "overview-online-devices", "sort": 0, "visible": true, "config": {} },
                { "id": "c", "type": "overview-active-alerts", "sort": 1, "visible": false, "config": {} }
            ] }
            """);

        var payload = await ReadJsonAsync(await client.GetAsync("/api/dashboard/layout"));
        var cards = payload.GetProperty("cards");
        Assert.Equal(2, cards.GetArrayLength());
        Assert.Equal("b", cards[0].GetProperty("id").GetString());
        Assert.Equal("c", cards[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Layout_Put_Missing_Config_Normalizes_To_Empty_Object()
    {
        var client = await AuthenticatedAsync();

        var putResponse = await PutLayoutAsync(
            client,
            """{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": true, "config": null } ] }""");
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var payload = await ReadJsonAsync(await client.GetAsync("/api/dashboard/layout"));
        Assert.Equal(JsonValueKind.Object, payload.GetProperty("cards")[0].GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task Layout_Put_Omitted_Config_Normalizes_To_Empty_Object()
    {
        var client = await AuthenticatedAsync();

        var putResponse = await PutLayoutAsync(
            client,
            """{ "cards": [ { "id": "a", "type": "overview-online-devices", "sort": 0, "visible": true } ] }""");
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var payload = await ReadJsonAsync(await client.GetAsync("/api/dashboard/layout"));
        var config = payload.GetProperty("cards")[0].GetProperty("config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);
        Assert.Equal("{}", config.GetRawText());
    }

    [Fact]
    public async Task Layout_Put_Accepts_Metric_Card_With_Structured_Config()
    {
        var client = await AuthenticatedAsync();

        // 指标卡 config 必须携带来源结构（targetId + key）；windowHours 可选，缺省不强造
        var putResponse = await PutLayoutAsync(
            client,
            """{ "cards": [ { "id": "m", "type": "metric-status", "sort": 0, "visible": true, "config": { "targetId": 3, "key": "online" } } ] }""");
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var payload = await ReadJsonAsync(await client.GetAsync("/api/dashboard/layout"));
        var config = payload.GetProperty("cards")[0].GetProperty("config");
        Assert.Equal(3, config.GetProperty("targetId").GetInt64());
        Assert.Equal("online", config.GetProperty("key").GetString());
        Assert.False(config.TryGetProperty("windowHours", out _));
    }

    [Fact]
    public async Task Layout_Put_Id_Type_Length_At_Boundary()
    {
        var client = await AuthenticatedAsync();
        var idAtLimit = new string('i', 128);
        var typeOverLimit = new string('t', 129);
        var idOverLimit = new string('i', 129);

        // 恰好 128 字符：允许
        var atLimit = await PutLayoutAsync(
            client,
            $$"""{ "cards": [ { "id": "{{idAtLimit}}", "type": "overview-total-devices", "sort": 0, "visible": true, "config": {} } ] }""");
        Assert.Equal(HttpStatusCode.NoContent, atLimit.StatusCode);
        var payload = await ReadJsonAsync(await client.GetAsync("/api/dashboard/layout"));
        Assert.Equal(idAtLimit, payload.GetProperty("cards")[0].GetProperty("id").GetString());

        // type 超 128 字符：400
        var typeTooLong = await PutLayoutAsync(
            client,
            $$"""{ "cards": [ { "id": "a", "type": "{{typeOverLimit}}", "sort": 0, "visible": true, "config": {} } ] }""");
        Assert.Equal(HttpStatusCode.BadRequest, typeTooLong.StatusCode);

        // id 超 128 字符：400
        var idTooLong = await PutLayoutAsync(
            client,
            $$"""{ "cards": [ { "id": "{{idOverLimit}}", "type": "overview-online-devices", "sort": 0, "visible": true, "config": {} } ] }""");
        Assert.Equal(HttpStatusCode.BadRequest, idTooLong.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "cards": {} }""")]
    [InlineData("""{ "cards": [] }""")]
    [InlineData("""{ "cards": [ { "type": "overview-total-devices", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "", "type": "overview-total-devices", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": 1, "type": "overview-total-devices", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": -1, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 1.5, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": "0", "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0 } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": "yes" } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": true, "config": "x" } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": true, "config": [1] } ] }""")]
    [InlineData("""{ "cards": [ { "id": "a", "type": "overview-total-devices", "sort": 0, "visible": true }, { "id": "a", "type": "overview-online-devices", "sort": 1, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ 1 ] }""")]
    // 未知卡片类型（目录外）拒绝：防脏类型入库后前端静默丢卡
    [InlineData("""{ "cards": [ { "id": "a", "type": "no-such-card", "sort": 0, "visible": true } ] }""")]
    // 指标卡 config 必须携带来源结构：缺 config / 缺 key / 空 key / 非法 targetId / 非法 windowHours
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": 1 } } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": 1, "key": "  " } } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": 0, "key": "cpu" } } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": "1", "key": "cpu" } } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": 1, "key": "cpu", "windowHours": 0 } } ] }""")]
    [InlineData("""{ "cards": [ { "id": "m", "type": "metric-value", "sort": 0, "visible": true, "config": { "targetId": 1, "key": "cpu", "windowHours": "24" } } ] }""")]
    public async Task Layout_Put_Rejects_Invalid_Payload_With_BadRequest(string body)
    {
        var client = await AuthenticatedAsync();

        var response = await PutLayoutAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(payload.TryGetProperty("error", out _), "400 响应应包含 error 字段");
    }

    [Fact]
    public async Task Layout_Put_Malformed_Json_Returns_BadRequest_NotServerError()
    {
        var client = await AuthenticatedAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/dashboard/layout")
        {
            Content = new StringContent("{ not-valid-json", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Layout_Requires_Authentication()
    {
        var client = _factory.CreateClient();

        var get = await client.GetAsync("/api/dashboard/layout");
        var put = await PutLayoutAsync(client, """{ "cards": [] }""");

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutLayoutAsync(HttpClient client, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/dashboard/layout")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement;
    }

    private async Task<HttpClient> AuthenticatedAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }
}
