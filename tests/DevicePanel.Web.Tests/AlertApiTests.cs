using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Alerting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>告警配置 API 测试：napcat 设置（token 不回传）、告警规则 CRUD 与校验、待发队列可见。</summary>
public class AlertApiTests : IDisposable
{
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Settings_Put_Then_Get_Hides_Token_But_Reports_Presence()
    {
        var client = await AuthenticatedAsync();

        var put = await client.PutAsJsonAsync("/api/alerts/settings", new
        {
            baseUrl = "http://127.0.0.1:3000",
            token = "napcat-secret",
            targetType = "private",
            targetId = "10001",
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/api/alerts/settings");
        get.EnsureSuccessStatusCode();
        var raw = await get.Content.ReadAsStringAsync();
        var payload = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("http://127.0.0.1:3000", payload.GetProperty("napcat").GetProperty("baseUrl").GetString());
        Assert.True(payload.GetProperty("napcat").GetProperty("tokenSet").GetBoolean());
        Assert.Equal("private", payload.GetProperty("napcat").GetProperty("targetType").GetString());
        Assert.Equal("10001", payload.GetProperty("napcat").GetProperty("targetId").GetString());
        Assert.DoesNotContain("napcat-secret", raw);
    }

    [Fact]
    public async Task Settings_Put_Without_Token_Keeps_Existing_And_Empty_Token_Clears()
    {
        var client = await AuthenticatedAsync();
        await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl = "http://a:1", token = "keep-me", targetType = "group", targetId = "42" });

        await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl = "http://b:2" });
        Assert.True(await TokenSetAsync());

        await client.PutAsJsonAsync("/api/alerts/settings", new { token = "" });
        Assert.False(await TokenSetAsync());
    }

    [Theory]
    [InlineData("ftp://a:1", "t", "private", "10001", "baseUrl")]
    [InlineData("http://a:1", "t", "channel", "10001", "targetType")]
    [InlineData("http://a:1", "t", "private", "abc", "targetId")]
    [InlineData("http://a:1", "t", "private", "", "targetId")]
    public async Task Settings_Put_Rejects_Invalid_Input(string baseUrl, string token, string targetType, string targetId, string _)
    {
        var client = await AuthenticatedAsync();
        var put = await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl, token, targetType, targetId });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Rules_New_Device_Gets_Default_Rules_Listed_With_Metadata()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "规则设备");
        var targetId = await GetTargetIdAsync(client, deviceId);

        var payload = await GetRulesAsync(client, targetId);
        var items = payload.GetProperty("items");
        Assert.Equal(4, items.GetArrayLength());

        var cpu = items.EnumerateArray().Single(i => i.GetProperty("metric").GetString() == "cpu");
        Assert.Equal("threshold_above", cpu.GetProperty("ruleType").GetString());
        Assert.Equal("规则设备", cpu.GetProperty("targetName").GetString());
        Assert.Equal("CPU 使用率", cpu.GetProperty("metricDisplayName").GetString());
        Assert.True(cpu.GetProperty("enabled").GetBoolean());
        Assert.Contains("\"threshold\":90", cpu.GetProperty("paramsJson").GetString());
        Assert.Equal(1, items.EnumerateArray().Count(i => i.GetProperty("ruleType").GetString() == "no_data"));
    }

    [Fact]
    public async Task Rules_Create_Update_Disable_Delete_Roundtrip()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "往返设备");
        var targetId = await GetTargetIdAsync(client, deviceId);

        var create = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId,
            metric = "net_rx",
            ruleType = "threshold_above",
            @params = new { threshold = 1024.0, sustainSeconds = 30, repeatMinutes = 5 },
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ruleId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // 编辑：参数与指标类型校验后的更新
        var update = await client.PutAsJsonAsync($"/api/alerts/rules/{ruleId}", new
        {
            @params = new { threshold = 2048.0, sustainSeconds = 0, repeatMinutes = 0 },
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var items = (await GetRulesAsync(client, targetId)).GetProperty("items");
        var updated = items.EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == ruleId);
        Assert.Contains("\"threshold\":2048", updated.GetProperty("paramsJson").GetString());

        // 关闭的规则保留但标记禁用（验收 3：关闭的规则不再触发）
        var disable = await client.PutAsJsonAsync($"/api/alerts/rules/{ruleId}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        items = (await GetRulesAsync(client, targetId)).GetProperty("items");
        Assert.False(items.EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == ruleId).GetProperty("enabled").GetBoolean());

        var delete = await client.DeleteAsync($"/api/alerts/rules/{ruleId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(4, (await GetRulesAsync(client, targetId)).GetProperty("items").GetArrayLength());

        var missing = await client.DeleteAsync($"/api/alerts/rules/{ruleId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Rules_Create_Rejects_Unknown_Type_Unregistered_Metric_And_Duplicates()
    {
        var client = await AuthenticatedAsync();
        var deviceId = await CreateDeviceAsync(client, "校验设备");
        var targetId = await GetTargetIdAsync(client, deviceId);

        var unknownType = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId, metric = "cpu", ruleType = "magic_v9", @params = new { }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);

        var unregisteredMetric = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId, metric = "not-registered", ruleType = "threshold_above", @params = new { threshold = 1 }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unregisteredMetric.StatusCode);

        var missingMetric = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId, metric = (string?)null, ruleType = "threshold_above", @params = new { threshold = 1 }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingMetric.StatusCode);

        var badParams = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId, metric = "cpu", ruleType = "threshold_above", @params = new { }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, badParams.StatusCode);

        // 同目标同指标同类型重复 → 409
        var duplicate = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId, metric = "cpu", ruleType = "threshold_above", @params = new { threshold = 80 }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var unknownTarget = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            targetId = 99999, metric = "cpu", ruleType = "threshold_above", @params = new { threshold = 80 }, enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownTarget.StatusCode);
    }

    [Fact]
    public async Task Rules_Types_Endpoint_Lists_Four_Built_In_Types()
    {
        var client = await AuthenticatedAsync();

        var response = await client.GetAsync("/api/alerts/rules/types");
        response.EnsureSuccessStatusCode();
        var types = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Equal(4, types.Count);
        Assert.Contains(types, t => t.GetProperty("type").GetString() == "threshold_above");
        Assert.Contains(types, t => t.GetProperty("type").GetString() == "threshold_below");
        Assert.Contains(types, t => t.GetProperty("type").GetString() == "no_data");
        Assert.Contains(types, t => t.GetProperty("type").GetString() == "status_mismatch");
        var noData = types.Single(t => t.GetProperty("type").GetString() == "no_data");
        Assert.True(noData.GetProperty("allowsNullMetric").GetBoolean());
        var threshold = types.Single(t => t.GetProperty("type").GetString() == "threshold_above");
        Assert.Contains(threshold.GetProperty("paramDescriptors").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "threshold");
    }

    [Fact]
    public async Task Queue_Endpoint_Lists_Pending_Alerts_Visible_While_Napcat_Down()
    {
        var client = await AuthenticatedAsync();
        var outbox = _factory.Services.GetRequiredService<IAlertOutboxStore>();
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("设备离线告警", "设备「q-1」已离线"), DateTimeOffset.UtcNow);
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("指标越限告警", "设备「q-2」CPU 使用率 98.0%"), DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/alerts/queue");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, payload.GetProperty("count").GetInt64());
        var items = payload.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("设备离线告警", items[0].GetProperty("title").GetString());
        Assert.Equal(NapcatNotifier.ChannelNameValue, items[0].GetProperty("channel").GetString());
        Assert.Equal("设备「q-2」CPU 使用率 98.0%", items[1].GetProperty("content").GetString());
    }

    private async Task<bool> TokenSetAsync()
    {
        var client = await AuthenticatedAsync();
        var payload = await (await client.GetAsync("/api/alerts/settings")).Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("napcat").GetProperty("tokenSet").GetBoolean();
    }

    private async Task<JsonElement> GetRulesAsync(HttpClient client, long? targetId = null)
    {
        var url = targetId is { } id ? $"/api/alerts/rules?targetId={id}" : "/api/alerts/rules";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<long> GetTargetIdAsync(HttpClient client, long deviceId)
    {
        var response = await client.GetAsync("/api/targets");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.EnumerateArray().Single(t => t.GetProperty("deviceId").GetInt64() == deviceId)
            .GetProperty("id").GetInt64();
    }

    private async Task<long> CreateDeviceAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/devices", new { name, tags = new[] { "告警" } });
        created.EnsureSuccessStatusCode();
        var payload = await created.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("id").GetInt64();
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
