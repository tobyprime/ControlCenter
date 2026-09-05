using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// pull 采集器配置 API（三期模块3）：创建时带 pull 配置即 pull 采集器，映射的 metric key 经注册管道自动注册（约束 A）。
/// </summary>
public class PullCollectorApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            // 单元测试内探针不真发 HTTP：替换为快速失败的桩客户端（不依赖外网）
            TestServices = services => services.AddSingleton<IProbeHttpClient>(new FailFastProbeClient());
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_Pull_Collector_Registers_Metric_Keys_And_Saves_Config()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/collectors", PullRequest(
            "https://mc.zenoxs.cn/tiles/settings.json", 60, [PlayersMapping("number")]));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pull", created.GetProperty("mode").GetString());
        var id = created.GetProperty("id").GetInt64();

        // 约束 A：新指标 = metric key 注册，管道（查询/曲线/告警规则）零改动可用
        var keys = await client.GetAsync("/api/metrics/keys");
        keys.EnsureSuccessStatusCode();
        var keyList = await keys.Content.ReadFromJsonAsync<JsonElement>();
        var registered = keyList.EnumerateArray().FirstOrDefault(k => k.GetProperty("key").GetString() == "mc.players");
        Assert.Equal("在线玩家数", registered.GetProperty("displayName").GetString());
        Assert.Equal("人", registered.GetProperty("unit").GetString());
        Assert.Equal("number", registered.GetProperty("valueType").GetString());

        var pull = await client.GetAsync($"/api/collectors/{id}/pull");
        pull.EnsureSuccessStatusCode();
        var config = await pull.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://mc.zenoxs.cn/tiles/settings.json", config.GetProperty("url").GetString());
        Assert.Equal(60, config.GetProperty("intervalSeconds").GetInt32());
        var mapping = Assert.Single(config.GetProperty("mappings").EnumerateArray());
        Assert.Equal("mc.players", mapping.GetProperty("metricKey").GetString());
        Assert.Equal("$.players.length()", mapping.GetProperty("jsonPath").GetString());
    }

    [Fact]
    public async Task Create_Push_Collector_Ignores_Probe_And_Keeps_Token_Flow()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/collectors", new
        {
            name = "边缘网关",
            tags = new[] { "机房A" },
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("push", created.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("agentToken").GetString()));

        // push 采集器没有 pull 配置
        var id = created.GetProperty("id").GetInt64();
        var none = await client.GetAsync($"/api/collectors/{id}/pull");
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
    }

    [Theory]
    [InlineData("ftp://mc.zenoxs.cn/settings.json")]
    [InlineData("mc.zenoxs.cn/tiles/settings.json")]
    [InlineData("")]
    public async Task Create_Pull_Collector_With_Invalid_Url_Returns_400(string url)
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest(url, 60, [PlayersMapping("number")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(3601)]
    public async Task Create_Pull_Collector_With_Out_Of_Range_Interval_Returns_400(int intervalSeconds)
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", intervalSeconds, [PlayersMapping("number")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_Pull_Collector_With_Malformed_JsonPath_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new { metricKey = "mc.players", jsonPath = "players.length()", valueType = "number", displayName = "", unit = "" },
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_Pull_Collector_With_Unsupported_Value_Type_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            new { metricKey = "mc.flag", jsonPath = "$.flag", valueType = "bool", displayName = "", unit = "" },
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_Pull_Collector_With_Conflicting_Registered_Key_Type_Returns_400()
    {
        var client = await AuthenticatedClientAsync();
        await client.PostAsJsonAsync("/api/metrics/keys", new { key = "mc.players", valueType = "string", displayName = "玩家", unit = "" });

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", 60, [PlayersMapping("number")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_Pull_Collector_With_Duplicate_Mapping_Keys_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", 60,
        [
            PlayersMapping("number"),
            new { metricKey = "mc.players", jsonPath = "$.maxPlayers", valueType = "number", displayName = "", unit = "" },
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_Pull_Updates_Config_And_Registers_New_Key()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreatePullCollectorAsync(client);

        var update = await client.PutAsJsonAsync($"/api/collectors/{id}/pull", new
        {
            url = "https://map.zenoxs.cn/tiles/settings.json",
            intervalSeconds = 30,
            mappings = new[]
            {
                new { metricKey = "mc.capacity", jsonPath = "$.maxPlayers", valueType = "number", displayName = "最大玩家", unit = "人" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var pull = await (await client.GetAsync($"/api/collectors/{id}/pull")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://map.zenoxs.cn/tiles/settings.json", pull.GetProperty("url").GetString());
        Assert.Equal(30, pull.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal("mc.capacity", Assert.Single(pull.GetProperty("mappings").EnumerateArray()).GetProperty("metricKey").GetString());

        var keys = await (await client.GetAsync("/api/metrics/keys")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(keys.EnumerateArray(), k => k.GetProperty("key").GetString() == "mc.capacity");
    }

    [Fact]
    public async Task Get_Pull_Missing_Collector_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var missing = await client.GetAsync("/api/collectors/987654/pull");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Put_Pull_On_Push_Collector_Returns_400()
    {
        var client = await AuthenticatedClientAsync();
        var create = await client.PostAsJsonAsync("/api/collectors", new { name = "设备", tags = Array.Empty<string>() });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var response = await client.PutAsJsonAsync($"/api/collectors/{id}/pull", new { url = "https://a.example.com", mappings = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_Pull_On_Missing_Collector_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/collectors/987654/pull", new { url = "https://a.example.com", mappings = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Pull_Collector_Removes_Config()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreatePullCollectorAsync(client);

        var delete = await client.DeleteAsync($"/api/collectors/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var pull = await client.GetAsync($"/api/collectors/{id}/pull");
        Assert.Equal(HttpStatusCode.NotFound, pull.StatusCode);
    }

    [Fact]
    public async Task Pull_Collector_Online_Mirrors_Status_Metric()
    {
        var client = await AuthenticatedClientAsync();
        var id = await CreatePullCollectorAsync(client);
        var metrics = _factory.Services.GetRequiredService<IMetricsStore>();

        // 未探测：无 status 样本 → online=false（前端结合 lastSeenAtUtc 显示"未探测"）
        var before = await (await client.GetAsync("/api/collectors")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(before[0].GetProperty("online").GetBoolean());
        Assert.Null(before[0].GetProperty("lastSeenAtUtc").GetString());

        var now = DateTimeOffset.UtcNow;
        metrics.Insert(id, MetricKeys.Status, new MetricSample(now, 1, "true"));
        _factory.Services.GetRequiredService<ICollectorRegistry>().Touch(id, now);
        var up = await (await client.GetAsync("/api/collectors")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(up[0].GetProperty("online").GetBoolean());

        metrics.Insert(id, MetricKeys.Status, new MetricSample(now.AddMinutes(1), 0, "false"));
        var down = await (await client.GetAsync("/api/collectors")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(down[0].GetProperty("online").GetBoolean());
    }

    private static object PlayersMapping(string valueType) =>
        new { metricKey = "mc.players", jsonPath = "$.players.length()", valueType, displayName = "在线玩家数", unit = "人" };

    private static object PullRequest(string url, int intervalSeconds, object[] mappings) =>
        new { name = "MC 服务", tags = Array.Empty<string>(), pull = new { url, intervalSeconds, mappings } };

    private static async Task<long> CreatePullCollectorAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", PullRequest("https://mc.zenoxs.cn/tiles/settings.json", 60, [PlayersMapping("number")]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private sealed class FailFastProbeClient : IProbeHttpClient
    {
        public Task<ProbeFetchResult> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeFetchResult(false, null, null));
    }
}
