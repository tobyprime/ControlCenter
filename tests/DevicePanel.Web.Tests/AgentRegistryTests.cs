using DevicePanel.Web.Agents;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Collectors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// Agent 台账单元测试（TOB-375 模块2）：一 agent 一 token（明文只显示一次、库中仅存 SHA-256）、
/// 自由文本标签（不限量）、能力声明持久化、与 target 的双写期关联。
/// </summary>
public class AgentRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

    private AgentRegistry CreateRegistry() => new(_db.Factory, _clock);

    private CollectorRegistry CreateCollectorRegistry() => new(_db.Factory, _clock);

    [Fact]
    public void Create_Returns_Token_Once_And_Stores_Agent_Without_Plaintext()
    {
        var registry = CreateRegistry();

        var created = registry.Create("边缘 agent", new[] { "机房A" });

        Assert.True(created.Agent.Id > 0);
        Assert.Equal("边缘 agent", created.Agent.Name);
        Assert.Equal(new[] { "机房A" }, created.Agent.Labels);
        Assert.StartsWith(AgentToken.Prefix, created.Token);
        Assert.Null(created.Agent.Capabilities); // 未声明（旧版 agent 兼容语义）
        Assert.Null(created.Agent.LastSeenAtUtc);

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM agents WHERE id = $id";
        command.Parameters.AddWithValue("$id", created.Agent.Id);
        var storedHash = Convert.ToString(command.ExecuteScalar());
        Assert.Equal(64, storedHash!.Length);
        Assert.DoesNotContain(created.Token, storedHash);
    }

    [Fact]
    public void FindAgentIdByToken_Resolves_Issued_Token_And_Rejects_Unknown()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        Assert.Equal(created.Agent.Id, registry.FindAgentIdByToken(created.Token));
        Assert.Null(registry.FindAgentIdByToken("dpk_unknown"));
        Assert.Null(registry.FindAgentIdByToken(""));
    }

    [Fact]
    public void ResetToken_Old_Token_Invalidates_Immediately()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        var newToken = registry.ResetToken(created.Agent.Id);

        Assert.NotNull(newToken);
        Assert.StartsWith(AgentToken.Prefix, newToken);
        Assert.Null(registry.FindAgentIdByToken(created.Token));
        Assert.Equal(created.Agent.Id, registry.FindAgentIdByToken(newToken!));
    }

    [Fact]
    public void ResetToken_Mirrors_Hash_To_Linked_Target()
    {
        var agents = CreateRegistry();
        var targets = CreateCollectorRegistry();
        var created = agents.Create("设备", Array.Empty<string>());
        var target = targets.Create("设备", Array.Empty<string>(), created.Agent.Id);

        var newToken = agents.ResetToken(created.Agent.Id);

        Assert.NotNull(newToken);
        Assert.Equal(created.Agent.Id, agents.FindCollectorIdByAgentId(created.Agent.Id));
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT token_hash FROM agents WHERE id = $agentId) =
                   (SELECT agent_token_hash FROM collectors WHERE id = $targetId)
            """;
        command.Parameters.AddWithValue("$agentId", created.Agent.Id);
        command.Parameters.AddWithValue("$targetId", target.Id);
        Assert.Equal(1L, (long)(command.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void UpdateLabels_Replaces_FreeText_Labels_Without_Limits()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        // 自由文本、不限量：超过 target tags 上限（20 个 × 50 字符）的标签也合法
        var labels = Enumerable.Range(1, 30).Select(i => $"标签{i}-" + new string('字', 80)).ToArray();
        var updated = registry.UpdateLabels(created.Agent.Id, labels);

        Assert.NotNull(updated);
        Assert.Equal(labels, updated!.Labels);

        var reloaded = registry.Get(created.Agent.Id);
        Assert.Equal(labels, reloaded!.Labels);
    }

    [Fact]
    public void UpdateLabels_Trims_Drops_Empty_And_Deduplicates()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        var updated = registry.UpdateLabels(created.Agent.Id, new[] { " 机房A ", "", "机房A", "  " });

        Assert.Equal(new[] { "机房A" }, updated!.Labels);
    }

    [Fact]
    public void List_Filters_By_Label()
    {
        var registry = CreateRegistry();
        registry.Create("甲", new[] { "机房A", "网关" });
        registry.Create("乙", new[] { "机房B" });
        var special = registry.Create("丙", Array.Empty<string>());
        registry.UpdateLabels(special.Agent.Id, new[] { "带\"引号\"的标签" });

        var inMachineA = registry.List("机房A").Select(a => a.Name).ToList();
        Assert.Equal(new[] { "甲" }, inMachineA);

        var quoted = registry.List("带\"引号\"的标签").Select(a => a.Name).ToList();
        Assert.Equal(new[] { "丙" }, quoted);

        Assert.Equal(3, registry.List().Count);
    }

    [Fact]
    public void SetCapabilities_Persists_Declaration_Without_Bumping_Admin_Fields()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());
        var before = registry.Get(created.Agent.Id)!;

        Assert.True(registry.SetCapabilities(created.Agent.Id, new[] { "metrics", "terminal", "logs", "metrics" }));

        var after = registry.Get(created.Agent.Id)!;
        Assert.Equal(new[] { "metrics", "terminal", "logs" }, after.Capabilities);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc); // 能力声明非管理编辑，不 bump
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
    }

    [Fact]
    public void SetCapabilities_Returns_False_For_Unknown_Agent()
    {
        var registry = CreateRegistry();

        Assert.False(registry.SetCapabilities(999, new[] { "metrics" }));
    }

    [Fact]
    public void Touch_Records_LastSeen_And_IsOnline_Follows_Two_Period_Rule()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());
        var options = new AgentOptions();

        registry.Touch(created.Agent.Id, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(registry.Get(created.Agent.Id)!.IsOnline(_clock, options));

        _clock.Advance(TimeSpan.FromSeconds(32)); // 超过连续 2 个心跳周期
        Assert.False(registry.Get(created.Agent.Id)!.IsOnline(_clock, options));
    }

    [Fact]
    public void Delete_Removes_Unlinked_Agent_And_Token()
    {
        var registry = CreateRegistry();
        var created = registry.Create("待删除", Array.Empty<string>());

        Assert.True(registry.Delete(created.Agent.Id));
        Assert.Null(registry.Get(created.Agent.Id));
        Assert.Null(registry.FindAgentIdByToken(created.Token));
    }

    [Fact]
    public void Linkage_FindTarget_By_Agent_And_Vice_Versa()
    {
        var agents = CreateRegistry();
        var targets = CreateCollectorRegistry();
        var created = agents.Create("设备", Array.Empty<string>());
        var target = targets.Create("设备", Array.Empty<string>(), created.Agent.Id);

        Assert.Equal(target.Id, agents.FindCollectorIdByAgentId(created.Agent.Id));
        Assert.Equal(created.Agent.Id, agents.FindAgentIdByCollectorId(target.Id));
        Assert.Null(agents.FindCollectorIdByAgentId(999));
        Assert.Null(agents.FindAgentIdByCollectorId(999));
    }

    [Fact]
    public void ResetToken_Keeps_Agent_Hash_When_Mirror_Write_Fails()
    {
        // 审查问题2：agent hash 与镜像 hash 两条写库必须同生共死——镜像写失败（触发器注入故障）时
        // agent 侧不允许已推进到新 hash（否则库中留下「新 agent hash + 旧镜像 hash」的不一致态）
        var agents = CreateRegistry();
        var targets = CreateCollectorRegistry();
        var created = agents.Create("设备", Array.Empty<string>());
        targets.Create("设备", Array.Empty<string>(), created.Agent.Id);
        var oldHash = StoredAgentHash(created.Agent.Id);

        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_mirror_update BEFORE UPDATE OF agent_token_hash ON collectors
                BEGIN SELECT RAISE(ABORT, '注入故障：镜像写入失败'); END
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => agents.ResetToken(created.Agent.Id));
        Assert.Equal(oldHash, StoredAgentHash(created.Agent.Id));
    }

    private string StoredAgentHash(long agentId)
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM agents WHERE id = $id";
        command.Parameters.AddWithValue("$id", agentId);
        return Convert.ToString(command.ExecuteScalar())!;
    }

    public void Dispose() => _db.Dispose();
}
