using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Targets;
using Xunit;

namespace DevicePanel.Web.Tests;

public class TargetApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    // 每个测试独立 Factory：目标数据互不干扰
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Targets_Require_Login()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/targets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Returns_Token_Once_And_List_Shows_Device_Target_Offline()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/targets", new { name = "办公区打印机主机", tags = new[] { "办公区", "打印" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(created.GetProperty("id").GetInt64() > 0);
        Assert.Equal("device", created.GetProperty("type").GetString());

        var list = await ListAsync(client);
        var target = Assert.Single(list);
        Assert.Equal("办公区打印机主机", target.GetProperty("name").GetString());
        Assert.Equal("device", target.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, target.GetProperty("tags").ValueKind);
        Assert.False(target.GetProperty("online").GetBoolean());
        Assert.Null(target.GetProperty("lastSeenAtUtc").GetString());

        // agent token 只在创建/重置响应中出现，列表不回显
        Assert.False(target.TryGetProperty("agentToken", out _));
    }

    [Fact]
    public async Task Create_With_Blank_Name_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/targets", new { name = "   ", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_With_Too_Many_Tags_Returns_400()
    {
        var client = await AuthenticatedClientAsync();
        var tags = Enumerable.Range(1, 21).Select(i => $"标签{i}").ToArray();

        var response = await client.PostAsJsonAsync("/api/targets", new { name = "目标", tags });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_Changes_Name_And_Tags()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateTargetAsync(client, "旧名", new[] { "旧" });

        var update = await client.PutAsJsonAsync($"/api/targets/{created.Id}", new { name = "新名", tags = new[] { "位置A", "用途B" } });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var list = await ListAsync(client);
        var target = Assert.Single(list);
        Assert.Equal("新名", target.GetProperty("name").GetString());
        Assert.Equal(2, target.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Update_Missing_Target_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/targets/987654", new { name = "任意", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Target_From_List()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateTargetAsync(client, "待删除目标", Array.Empty<string>());

        var delete = await client.DeleteAsync($"/api/targets/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Empty(await ListAsync(client));

        var again = await client.DeleteAsync($"/api/targets/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ResetToken_Returns_New_Token_Different_From_Old()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateTargetAsync(client, "目标", Array.Empty<string>());

        var reset = await client.PostAsJsonAsync($"/api/targets/{created.Id}/token", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var payload = await reset.Content.ReadFromJsonAsync<JsonElement>();
        var newToken = payload.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newToken));
        Assert.NotEqual(created.AgentToken, newToken);
    }

    [Fact]
    public async Task ResetToken_Missing_Target_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/targets/555555/token", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<(long Id, string AgentToken)> CreateTargetAsync(HttpClient client, string name, string[] tags)
    {
        var response = await client.PostAsJsonAsync("/api/targets", new { name, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/targets");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }
}
