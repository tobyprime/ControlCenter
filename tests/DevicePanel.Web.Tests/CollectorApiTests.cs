using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

public class CollectorApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    // 每个测试独立 Factory：采集器数据互不干扰
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Collectors_Require_Login()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/collectors");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Push_Collector_Returns_Token_Once_And_List_Shows_Offline()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/collectors", new { name = "办公区打印机主机", tags = new[] { "办公区", "打印" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(created.GetProperty("id").GetInt64() > 0);
        Assert.Equal("push", created.GetProperty("mode").GetString());

        var list = await ListAsync(client);
        var collector = Assert.Single(list);
        Assert.Equal("办公区打印机主机", collector.GetProperty("name").GetString());
        Assert.Equal("push", collector.GetProperty("mode").GetString());

        // device 语义由内置标签承载（服务端追加，创建时用户未传）
        var tags = collector.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("type:device", tags);
        Assert.Equal(JsonValueKind.Array, collector.GetProperty("tags").ValueKind);
        Assert.False(collector.GetProperty("online").GetBoolean());
        Assert.Null(collector.GetProperty("lastSeenAtUtc").GetString());

        // agent token 只在创建/重置响应中出现，列表不回显
        Assert.False(collector.TryGetProperty("agentToken", out _));
    }

    [Fact]
    public async Task Create_Pull_Collector_Returns_No_Token_And_Service_Tag()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/collectors", new
        {
            name = "MC 服务",
            tags = new[] { "zenoxs" },
            pull = new { url = "https://mc.zenoxs.cn/status", intervalSeconds = 60, mappings = new[] { new { metricKey = "queue_depth", jsonPath = "$.queue", valueType = "number", displayName = "队列深度", unit = "" } } },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pull", created.GetProperty("mode").GetString());
        // pull 采集器不签发 agent：token 字段为空串（不回显可用凭据）
        Assert.True(string.IsNullOrWhiteSpace(created.GetProperty("agentToken").GetString()));

        var tags = created.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("type:service", tags);
        Assert.DoesNotContain("type:device", tags);
    }

    [Fact]
    public async Task Create_With_Blank_Name_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "   ", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_With_User_Type_Tag_Strips_But_Builtin_Remains()
    {
        var client = await AuthenticatedClientAsync();

        // 用户自报 type:device 属内置命名空间：被剥离，服务端按模式重挂
        var create = await client.PostAsJsonAsync("/api/collectors", new { name = "伪装目标", tags = new[] { "type:service", "现场" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var tags = created.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.DoesNotContain("type:service", tags);
        Assert.Contains("type:device", tags);
        Assert.Contains("现场", tags);
    }

    [Fact]
    public async Task Create_Accepts_Many_Free_Text_Tags()
    {
        var client = await AuthenticatedClientAsync();
        var tags = Enumerable.Range(1, 30).Select(i => $"标签{i}").ToArray();

        // 自定义标签为自由文本（PRD 技术默认值）：不再限 20 个
        var response = await client.PostAsJsonAsync("/api/collectors", new { name = "多标签", tags });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("tags").GetArrayLength() >= 30);
    }

    [Fact]
    public async Task Update_Changes_Name_And_Tags_Keeps_Builtin()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreatePushCollectorAsync(client, "旧名", new[] { "旧" });

        var update = await client.PutAsJsonAsync($"/api/collectors/{created.Id}", new { name = "新名", tags = new[] { "位置A", "用途B" } });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        var tags = updated.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal("新名", updated.GetProperty("name").GetString());
        Assert.Contains("type:device", tags);
        Assert.Equal(3, tags.Count);
    }

    [Fact]
    public async Task Update_Missing_Collector_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/collectors/987654", new { name = "任意", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Collector_From_List()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreatePushCollectorAsync(client, "待删除采集器", Array.Empty<string>());

        var delete = await client.DeleteAsync($"/api/collectors/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Empty(await ListAsync(client));

        var again = await client.DeleteAsync($"/api/collectors/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ResetToken_Returns_New_Token_Different_From_Old()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreatePushCollectorAsync(client, "采集器", Array.Empty<string>());

        var reset = await client.PostAsJsonAsync($"/api/collectors/{created.Id}/token", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var payload = await reset.Content.ReadFromJsonAsync<JsonElement>();
        var newToken = payload.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newToken));
        Assert.NotEqual(created.AgentToken, newToken);
    }

    [Fact]
    public async Task ResetToken_On_Pull_Collector_Returns_400()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreatePullCollectorAsync(client);

        var response = await client.PostAsJsonAsync($"/api/collectors/{created}/token", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetToken_Missing_Collector_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/collectors/555555/token", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<(long Id, string AgentToken)> CreatePushCollectorAsync(HttpClient client, string name, string[] tags)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", new { name, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    private async Task<long> CreatePullCollectorAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/collectors", new
        {
            name = "MC 服务",
            tags = Array.Empty<string>(),
            pull = new { url = "https://mc.zenoxs.cn/status", intervalSeconds = 60, mappings = Array.Empty<object>() },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/collectors");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }
}
