using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevicePanel.Web.Alerting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>告警 API 测试：napcat 设置（token 不回传）、告警规则 CRUD 与校验、内置规则类型目录、待发队列可见。</summary>
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

    [Theory]
    [InlineData("ftp://a:1", "t", "private", "10001")]
    [InlineData("http://a:1", "t", "channel", "10001")]
    [InlineData("http://a:1", "t", "private", "abc")]
    [InlineData("http://a:1", "t", "private", "")]
    public async Task Settings_Put_Rejects_Invalid_Input(string baseUrl, string token, string targetType, string targetId)
    {
        var client = await AuthenticatedAsync();
        var put = await client.PutAsJsonAsync("/api/alerts/settings", new { baseUrl, token, targetType, targetId });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Migration_Seeded_Global_Rules_Are_Listed()
    {
        // 一期全局默认阈值（cpu/mem/disk = 90）+ 在线状态规则由迁移播种，升级后可直接查看与编辑（验收 2）
        var client = await AuthenticatedAsync();

        var response = await client.GetAsync("/api/alert-rules");
        response.EnsureSuccessStatusCode();
        var rules = await response.Content.ReadFromJsonAsync<JsonElement>();

        var byKey = rules.EnumerateArray().ToDictionary(r => (r.GetProperty("metricKey").GetString(), r.GetProperty("ruleType").GetString()));
        Assert.Equal(4, byKey.Count);
        Assert.Equal(90, byKey[("cpu", "threshold_above")].GetProperty("parameters").GetProperty("threshold").GetDouble());
        Assert.Equal(90, byKey[("mem", "threshold_above")].GetProperty("parameters").GetProperty("threshold").GetDouble());
        Assert.Equal(90, byKey[("disk", "threshold_above")].GetProperty("parameters").GetProperty("threshold").GetDouble());
        Assert.Equal("true", byKey[("online", "state_mismatch")].GetProperty("parameters").GetProperty("expected").GetString());
        Assert.All(rules.EnumerateArray(), r => Assert.True(r.GetProperty("enabled").GetBoolean()));
        Assert.All(rules.EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("targetId").ValueKind));
    }

    [Fact]
    public async Task Rule_Types_Endpoint_Lists_Four_Builtin_Types()
    {
        var client = await AuthenticatedAsync();

        var response = await client.GetAsync("/api/alert-rules/types");
        response.EnsureSuccessStatusCode();
        var types = await response.Content.ReadFromJsonAsync<JsonElement>();

        var ids = types.EnumerateArray().Select(t => t.GetProperty("typeId").GetString()).ToHashSet();
        Assert.Equal(
            new HashSet<string?> { "threshold_above", "threshold_below", "no_data", "state_mismatch" },
            ids);
        Assert.All(types.EnumerateArray(), t => Assert.True(t.GetProperty("displayName").GetString()!.Length > 0));
    }

    [Fact]
    public async Task Rule_Create_List_Update_Delete_RoundTrip()
    {
        var client = await AuthenticatedAsync();
        var targetId = await CreateTargetAsync(client, "规则目标");

        var create = await client.PostAsJsonAsync("/api/alert-rules", new
        {
            targetId,
            metricKey = "cpu",
            ruleType = "threshold_above",
            parameters = new { threshold = 85 },
            sustainSeconds = 45,
            repeatMinutes = 10,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(targetId, created.GetProperty("targetId").GetInt64());
        Assert.Equal("规则目标", created.GetProperty("targetName").GetString());
        Assert.Equal("CPU 使用率", created.GetProperty("metricDisplayName").GetString());
        Assert.Equal(85, created.GetProperty("parameters").GetProperty("threshold").GetDouble());
        Assert.Equal(45, created.GetProperty("sustainSeconds").GetInt64());
        Assert.True(created.GetProperty("enabled").GetBoolean());

        // 同 (target, metric, type) 重复拒绝
        var duplicate = await client.PostAsJsonAsync("/api/alert-rules", new
        {
            targetId,
            metricKey = "cpu",
            ruleType = "threshold_above",
            parameters = new { threshold = 10 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // 修改参数 + 关闭规则（验收 3：可修改、关闭后不再触发）
        var id = created.GetProperty("id").GetInt64();
        var update = await client.PutAsJsonAsync($"/api/alert-rules/{id}", new
        {
            parameters = new { threshold = 70 },
            sustainSeconds = 30,
            repeatMinutes = 0,
            enabled = false,
        });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(70, updated.GetProperty("parameters").GetProperty("threshold").GetDouble());
        Assert.False(updated.GetProperty("enabled").GetBoolean());

        var delete = await client.DeleteAsync($"/api/alert-rules/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var missing = await client.DeleteAsync($"/api/alert-rules/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Rule_Create_Global_Rule_With_Null_Target()
    {
        var client = await AuthenticatedAsync();

        var create = await client.PostAsJsonAsync("/api/alert-rules", new
        {
            targetId = (long?)null,
            metricKey = "mem",
            ruleType = "threshold_below",
            parameters = new { threshold = 5 },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("targetId").ValueKind is JsonValueKind.Null);
        Assert.Equal("（全局）", created.GetProperty("targetName").GetString());
    }

    [Fact]
    public async Task Rule_Create_Filter_By_Target_And_Metric()
    {
        var client = await AuthenticatedAsync();
        var targetA = await CreateTargetAsync(client, "目标A");
        await CreateTargetAsync(client, "目标B");

        await client.PostAsJsonAsync("/api/alert-rules", new { targetId = targetA, metricKey = "cpu", ruleType = "threshold_above", parameters = new { threshold = 50 } });
        await client.PostAsJsonAsync("/api/alert-rules", new { targetId = targetA, metricKey = "mem", ruleType = "threshold_above", parameters = new { threshold = 50 } });

        var byTarget = await (await client.GetAsync($"/api/alert-rules?targetId={targetA}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, byTarget.GetArrayLength());

        var byMetric = await (await client.GetAsync($"/api/alert-rules?targetId={targetA}&metricKey=mem")).Content.ReadFromJsonAsync<JsonElement>();
        var rule = Assert.Single(byMetric.EnumerateArray());
        Assert.Equal("mem", rule.GetProperty("metricKey").GetString());
    }

    [Theory]
    [InlineData("not.registered", "threshold_above", 50.0)]     // 指标未注册
    [InlineData(null, "threshold_above", 50.0)]                 // 缺指标
    [InlineData("cpu", "expr_script", 50.0)]                    // 未知规则类型
    [InlineData("cpu", "threshold_above", null)]                // 缺参数
    [InlineData("online", "threshold_above", 50.0)]             // bool 指标不适用阈值上
    [InlineData("cpu", "state_mismatch", null)]                 // number 指标不适用状态不符
    public async Task Rule_Create_Rejects_Invalid_Combinations(string? metricKey, string ruleType, double? threshold)
    {
        var client = await AuthenticatedAsync();
        var targetId = await CreateTargetAsync(client, "校验目标");

        var body = new Dictionary<string, object?>
        {
            ["targetId"] = targetId,
            ["metricKey"] = metricKey,
            ["ruleType"] = ruleType,
            ["parameters"] = threshold.HasValue ? new { threshold } : null,
        };
        var response = await client.PostAsJsonAsync("/api/alert-rules", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rule_Create_No_Data_With_Minutes_Parameters()
    {
        var client = await AuthenticatedAsync();
        var targetId = await CreateTargetAsync(client, "无数据目标");

        var bad = await client.PostAsJsonAsync("/api/alert-rules", new
        {
            targetId,
            metricKey = "cpu",
            ruleType = "no_data",
            parameters = new { minutes = 0 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await client.PostAsJsonAsync("/api/alert-rules", new
        {
            targetId,
            metricKey = "cpu",
            ruleType = "no_data",
            parameters = new { minutes = 10 },
        });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task Queue_Endpoint_Lists_Pending_Alerts_Visible_While_Napcat_Down()
    {
        var client = await AuthenticatedAsync();
        var outbox = _factory.Services.GetRequiredService<IAlertOutboxStore>();
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("目标状态告警", "目标「q-1」设备在线状态 当前状态为 false"), DateTimeOffset.UtcNow);
        outbox.Enqueue(NapcatNotifier.ChannelNameValue, new AlertMessage("指标越限告警", "目标「q-2」CPU 使用率 当前 98.0%"), DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/alerts/queue");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, payload.GetProperty("count").GetInt64());
        var items = payload.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("目标状态告警", items[0].GetProperty("title").GetString());
        Assert.Equal(NapcatNotifier.ChannelNameValue, items[0].GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Active_Count_Endpoint_Counts_Fired_Unresolved_Events()
    {
        var client = await AuthenticatedAsync();
        var states = _factory.Services.GetRequiredService<IAlertStateStore>();

        var empty = await client.GetAsync("/api/alerts/active-count");
        empty.EnsureSuccessStatusCode();
        Assert.Equal(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt64());

        // 三个在册事件：仅"已触发且未恢复"（LastAlertedUtc 有值）计入活跃；防抖等待中不算
        states.Set("rule:1", """{"FirstSeenUtc":"2026-09-05T00:00:00Z","LastAlertedUtc":null}""", DateTimeOffset.UtcNow);
        states.Set("rule:2", """{"FirstSeenUtc":"2026-09-05T00:00:00Z","LastAlertedUtc":"2026-09-05T00:01:00Z"}""", DateTimeOffset.UtcNow);
        states.Set("rule:3", """{"FirstSeenUtc":"2026-09-05T00:00:00Z","LastAlertedUtc":"2026-09-05T00:01:00Z"}""", DateTimeOffset.UtcNow);

        var active = await client.GetAsync("/api/alerts/active-count");
        active.EnsureSuccessStatusCode();
        Assert.Equal(2, (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt64());

        states.Delete("rule:2"); // 恢复即删状态行，计数随之下降
        var after = await client.GetAsync("/api/alerts/active-count");
        after.EnsureSuccessStatusCode();
        Assert.Equal(1, (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt64());
    }

    private async Task<long> CreateTargetAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/targets", new { name, tags = new[] { "告警" } });
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
