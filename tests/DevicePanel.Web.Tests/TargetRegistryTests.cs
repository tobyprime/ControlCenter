using DevicePanel.Web.Targets;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class TargetRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    private TargetRegistry CreateRegistry() => new(_db.Factory, _clock);

    [Fact]
    public void Create_Returns_Token_And_Stores_Target_With_Type_And_Tags()
    {
        var registry = CreateRegistry();

        var created = registry.Create(TargetTypes.Device, "边缘网关", new[] { "机房A", "网关", "内网" });

        Assert.True(created.Target.Id > 0);
        Assert.Equal(TargetTypes.Device, created.Target.Type);
        Assert.Equal("边缘网关", created.Target.Name);
        Assert.Equal(new[] { "机房A", "网关", "内网" }, created.Target.Tags);
        Assert.StartsWith(TargetRegistry.TokenTypePrefix, created.AgentToken);
        Assert.Null(created.Target.LastSeenAtUtc);
    }

    [Fact]
    public void Create_Supports_Service_Type_Targets()
    {
        var registry = CreateRegistry();

        var created = registry.Create(TargetTypes.Service, "MC 服务", new[] { "zenoxs" });

        Assert.Equal(TargetTypes.Service, created.Target.Type);
    }

    [Fact]
    public void Create_Stores_Token_Hash_Not_Plaintext()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_token_hash FROM targets WHERE id = $id";
        command.Parameters.AddWithValue("$id", created.Target.Id);
        var storedHash = Convert.ToString(command.ExecuteScalar());

        Assert.NotNull(storedHash);
        Assert.DoesNotContain(created.AgentToken, storedHash);
        Assert.Equal(64, storedHash!.Length);
    }

    [Fact]
    public void FindTargetIdByToken_Resolves_Issued_Token()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());

        var targetId = registry.FindTargetIdByToken(created.AgentToken);

        Assert.Equal(created.Target.Id, targetId);
    }

    [Fact]
    public void FindTargetIdByToken_Rejects_Unknown_Or_Empty_Token()
    {
        var registry = CreateRegistry();
        registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());

        Assert.Null(registry.FindTargetIdByToken("dpk_unknown"));
        Assert.Null(registry.FindTargetIdByToken(""));
        Assert.Null(registry.FindTargetIdByToken("garbage"));
    }

    [Fact]
    public void Update_Changes_Name_And_Tags()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "旧名", new[] { "旧标签" });

        var updated = registry.Update(created.Target.Id, "新名", new[] { "位置B", "测试机" });

        Assert.NotNull(updated);
        Assert.Equal("新名", updated!.Name);
        Assert.Equal(new[] { "位置B", "测试机" }, updated.Tags);
    }

    [Fact]
    public void Delete_Removes_Target_And_Token()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "待删除", Array.Empty<string>());

        Assert.True(registry.Delete(created.Target.Id));
        Assert.Null(registry.Get(created.Target.Id));
        Assert.Null(registry.FindTargetIdByToken(created.AgentToken));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void ResetToken_Old_Token_Invalidates_Immediately_And_New_Works()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());

        var newToken = registry.ResetToken(created.Target.Id);

        Assert.NotNull(newToken);
        Assert.StartsWith(TargetRegistry.TokenTypePrefix, newToken);
        Assert.Null(registry.FindTargetIdByToken(created.AgentToken));
        Assert.Equal(created.Target.Id, registry.FindTargetIdByToken(newToken!));
    }

    [Fact]
    public void Touch_Records_LastSeen_Utc()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());

        registry.Touch(created.Target.Id, _clock.GetUtcNow());
        var target = registry.Get(created.Target.Id);

        Assert.NotNull(target);
        Assert.Equal(_clock.GetUtcNow(), target!.LastSeenAtUtc);
    }

    [Fact]
    public void IsOnline_True_Within_Two_Heartbeat_Periods_And_False_After()
    {
        var registry = CreateRegistry();
        var created = registry.Create(TargetTypes.Device, "设备", Array.Empty<string>());
        var options = new AgentOptions { HeartbeatIntervalSeconds = 30 };

        registry.Touch(created.Target.Id, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(registry.Get(created.Target.Id)!.IsOnline(_clock, options));

        _clock.Advance(TimeSpan.FromSeconds(32)); // 超过连续 2 个周期（60s）未心跳
        Assert.False(registry.Get(created.Target.Id)!.IsOnline(_clock, options));
    }

    public void Dispose() => _db.Dispose();
}
