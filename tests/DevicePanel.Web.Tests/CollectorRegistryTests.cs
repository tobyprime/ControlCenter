using DevicePanel.Web.Agents;
using DevicePanel.Web.Collectors;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class CollectorRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    private CollectorRegistry CreateRegistry() => new(_db.Factory, _clock);

    private AgentRegistry CreateAgentRegistry() => new(_db.Factory, _clock);

    [Fact]
    public void Create_Stores_Collector_With_Tags()
    {
        var registry = CreateRegistry();

        var created = registry.Create("边缘网关", new[] { "机房A", "网关", "内网" });

        Assert.True(created.Id > 0);
        Assert.Equal("边缘网关", created.Name);
        Assert.Equal(new[] { "机房A", "网关", "内网" }, created.Tags);
        Assert.Null(created.AgentId);
        Assert.Null(created.LastSeenAtUtc);
    }

    [Fact]
    public void Create_With_Agent_Mirrors_Agent_Token_Hash()
    {
        var agents = CreateAgentRegistry();
        var registry = CreateRegistry();
        var agentCreated = agents.Create("设备", Array.Empty<string>());

        // 三期模块2：token 明文由 agent 台账签发，采集器侧仅镜像 hash（不参与认证）
        var created = registry.Create("设备", Array.Empty<string>(), agentCreated.Agent.Id);

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT token_hash FROM agents WHERE id = $agentId) =
                   (SELECT agent_token_hash FROM collectors WHERE id = $collectorId)
            """;
        command.Parameters.AddWithValue("$agentId", agentCreated.Agent.Id);
        command.Parameters.AddWithValue("$collectorId", created.Id);
        Assert.Equal(1L, (long)(command.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void Update_Changes_Name_And_Tags()
    {
        var registry = CreateRegistry();
        var created = registry.Create("旧名", new[] { "旧标签" });

        var updated = registry.Update(created.Id, "新名", new[] { "位置B", "测试机" });

        Assert.NotNull(updated);
        Assert.Equal("新名", updated!.Name);
        Assert.Equal(new[] { "位置B", "测试机" }, updated.Tags);
    }

    [Fact]
    public void Delete_Removes_Collector()
    {
        var registry = CreateRegistry();
        var created = registry.Create("待删除", Array.Empty<string>());

        Assert.True(registry.Delete(created.Id));
        Assert.Null(registry.Get(created.Id));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void Touch_Records_LastSeen_Utc()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        registry.Touch(created.Id, _clock.GetUtcNow());
        var collector = registry.Get(created.Id);

        Assert.NotNull(collector);
        Assert.Equal(_clock.GetUtcNow(), collector!.LastSeenAtUtc);
    }

    [Fact]
    public void IsOnline_True_Within_Two_Heartbeat_Periods_And_False_After()
    {
        var registry = CreateRegistry();
        var agents = CreateAgentRegistry();
        var agentCreated = agents.Create("设备", Array.Empty<string>());
        var created = registry.Create("设备", Array.Empty<string>(), agentCreated.Agent.Id);
        var options = new AgentOptions { HeartbeatIntervalSeconds = 30 };

        registry.Touch(created.Id, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(registry.Get(created.Id)!.IsOnline(_clock, options));

        _clock.Advance(TimeSpan.FromSeconds(32)); // 超过连续 2 个周期（60s）未心跳
        Assert.False(registry.Get(created.Id)!.IsOnline(_clock, options));
    }

    public void Dispose() => _db.Dispose();
}
