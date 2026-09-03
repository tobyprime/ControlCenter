using DevicePanel.Web.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

public class DeviceRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    private DeviceRegistry CreateRegistry() => new(_db.Factory, _clock);

    [Fact]
    public void Create_Returns_Token_And_Stores_Device_With_Tags()
    {
        var registry = CreateRegistry();

        var created = registry.Create("边缘网关", new[] { "机房A", "网关", "内网" });

        Assert.True(created.Device.Id > 0);
        Assert.Equal("边缘网关", created.Device.Name);
        Assert.Equal(new[] { "机房A", "网关", "内网" }, created.Device.Tags);
        Assert.StartsWith(DeviceRegistry.TokenTypePrefix, created.AgentToken);
        Assert.Null(created.Device.LastSeenAtUtc);
    }

    [Fact]
    public void Create_Stores_Token_Hash_Not_Plaintext()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_token_hash FROM devices WHERE id = $id";
        command.Parameters.AddWithValue("$id", created.Device.Id);
        var storedHash = Convert.ToString(command.ExecuteScalar());

        Assert.NotNull(storedHash);
        Assert.DoesNotContain(created.AgentToken, storedHash);
        Assert.Equal(64, storedHash!.Length);
    }

    [Fact]
    public void Create_Tokens_Are_Unique_Per_Device()
    {
        var registry = CreateRegistry();
        var first = registry.Create("设备1", Array.Empty<string>());
        var second = registry.Create("设备2", Array.Empty<string>());

        Assert.NotEqual(first.AgentToken, second.AgentToken);
    }

    [Fact]
    public void FindDeviceIdByToken_Resolves_Issued_Token()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        var deviceId = registry.FindDeviceIdByToken(created.AgentToken);

        Assert.Equal(created.Device.Id, deviceId);
    }

    [Fact]
    public void FindDeviceIdByToken_Rejects_Unknown_Or_Empty_Token()
    {
        var registry = CreateRegistry();
        registry.Create("设备", Array.Empty<string>());

        Assert.Null(registry.FindDeviceIdByToken("dpk_unknown"));
        Assert.Null(registry.FindDeviceIdByToken(""));
        Assert.Null(registry.FindDeviceIdByToken("garbage"));
    }

    [Fact]
    public void Update_Changes_Name_And_Tags()
    {
        var registry = CreateRegistry();
        var created = registry.Create("旧名", new[] { "旧标签" });

        var updated = registry.Update(created.Device.Id, "新名", new[] { "位置B", "测试机" });

        Assert.NotNull(updated);
        Assert.Equal("新名", updated!.Name);
        Assert.Equal(new[] { "位置B", "测试机" }, updated.Tags);
    }

    [Fact]
    public void Update_Missing_Device_Returns_Null()
    {
        var registry = CreateRegistry();

        Assert.Null(registry.Update(999, "任意", Array.Empty<string>()));
    }

    [Fact]
    public void Delete_Removes_Device_And_Token()
    {
        var registry = CreateRegistry();
        var created = registry.Create("待删除", Array.Empty<string>());

        Assert.True(registry.Delete(created.Device.Id));
        Assert.Null(registry.Get(created.Device.Id));
        Assert.Null(registry.FindDeviceIdByToken(created.AgentToken));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void Delete_Missing_Device_Returns_False()
    {
        var registry = CreateRegistry();

        Assert.False(registry.Delete(12345));
    }

    [Fact]
    public void ResetToken_Old_Token_Invalidates_Immediately_And_New_Works()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        var newToken = registry.ResetToken(created.Device.Id);

        Assert.NotNull(newToken);
        Assert.StartsWith(DeviceRegistry.TokenTypePrefix, newToken);
        Assert.Null(registry.FindDeviceIdByToken(created.AgentToken));
        Assert.Equal(created.Device.Id, registry.FindDeviceIdByToken(newToken!));
    }

    [Fact]
    public void ResetToken_Missing_Device_Returns_Null()
    {
        var registry = CreateRegistry();

        Assert.Null(registry.ResetToken(424242));
    }

    [Fact]
    public void Touch_Records_LastSeen_Utc()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        registry.Touch(created.Device.Id, _clock.GetUtcNow());
        var device = registry.Get(created.Device.Id);

        Assert.NotNull(device);
        Assert.Equal(_clock.GetUtcNow(), device!.LastSeenAtUtc);
    }

    [Fact]
    public void IsOnline_True_Within_Two_Heartbeat_Periods_And_False_After()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());
        var options = new AgentOptions { HeartbeatIntervalSeconds = 30 };

        registry.Touch(created.Device.Id, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(registry.Get(created.Device.Id)!.IsOnline(_clock, options));

        _clock.Advance(TimeSpan.FromSeconds(32)); // 超过连续 2 个周期（60s）未心跳
        Assert.False(registry.Get(created.Device.Id)!.IsOnline(_clock, options));
    }

    [Fact]
    public void Device_Never_Seen_Is_Offline()
    {
        var registry = CreateRegistry();
        var created = registry.Create("设备", Array.Empty<string>());

        Assert.False(registry.Get(created.Device.Id)!.IsOnline(_clock, new AgentOptions()));
    }

    public void Dispose() => _db.Dispose();
}
