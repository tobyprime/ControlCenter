using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 主页控制卡布局校验（三期模块4）：control-card 的 config 须为 { controllers: [{ collectorId, key }] } 非空数组，
/// 非法载荷 400 拒绝入库（脏数据入库等于卡片消失，与指标卡来源校验同语义）。
/// </summary>
public class DashboardControlCardApiTests : IDisposable
{
    private readonly TestAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        _factory.Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static StringContent Layout(string config, string type = "control-card") =>
        new($$"""
            { "cards": [ { "id": "c1", "type": "{{type}}", "sort": 0, "visible": true, "config": {{config}} } ] }
            """, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Control_Card_With_Valid_Composition_Is_Persisted()
    {
        var client = await AuthenticatedClientAsync();

        var put = await client.PutAsync("/api/dashboard/layout", Layout(
            """{ "controllers": [ { "collectorId": 3, "key": "restart" }, { "collectorId": 5, "key": "fan" } ] }"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/api/dashboard/layout");
        get.EnsureSuccessStatusCode();
        var payload = await get.Content.ReadFromJsonAsync<JsonElement>();
        var card = payload.GetProperty("cards").EnumerateArray().Single();
        Assert.Equal("control-card", card.GetProperty("type").GetString());
        var controllers = card.GetProperty("config").GetProperty("controllers");
        Assert.Equal(2, controllers.GetArrayLength());
        Assert.Equal("fan", controllers[1].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Control_Card_Requires_Non_Empty_Controllers()
    {
        var client = await AuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout", Layout("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout", Layout("""{ "controllers": [] }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout", Layout("""{ "controllers": "restart" }"""))).StatusCode);
    }

    [Fact]
    public async Task Control_Card_Rejects_Invalid_Composition_Entries()
    {
        var client = await AuthenticatedClientAsync();

        // collectorId 非正整数
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout",
                Layout("""{ "controllers": [ { "collectorId": 0, "key": "restart" } ] }"""))).StatusCode);
        // key 缺失
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout",
                Layout("""{ "controllers": [ { "collectorId": 3 } ] }"""))).StatusCode);
    }

    [Fact]
    public async Task Metric_Types_Keep_Their_Own_Rules()
    {
        var client = await AuthenticatedClientAsync();

        // 回归：控制卡加入后指标卡校验不受影响（缺 targetId 来源仍拒绝）
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout",
                Layout("""{ "key": "cpu" }""", type: "metric-value"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsync("/api/dashboard/layout",
                Layout("""{ "controllers": [ { "collectorId": 1, "key": "k" } ] }""", type: "metric-value"))).StatusCode);
    }
}
