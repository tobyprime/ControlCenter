using DevicePanel.Web.Targets;
using DevicePanel.Web.Interactions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>交互模式注册表（约束 C）：收集 DI 中全部 IInteractionMode、按键查找、重复 key 拒绝。</summary>
public class InteractionModeRegistryTests
{
    private sealed class FakeMode(string key) : IInteractionMode
    {
        public string Key => key;
        public string DisplayName => $"模式 {key}";
        public string? Description => null;
    }

    [Fact]
    public void Modes_Lists_All_Registered_In_Registration_Order()
    {
        var registry = new InteractionModeRegistry([new FakeMode("console"), new ShellInteractionMode(), new FakeMode("rcon")]);

        Assert.Equal(["console", ShellInteractionMode.ModeKey, "rcon"], [.. registry.Modes.Select(m => m.Key)]);
    }

    [Fact]
    public void Find_Known_Key_Returns_Registered_Mode()
    {
        var shell = new ShellInteractionMode();
        var registry = new InteractionModeRegistry([shell]);

        Assert.Same(shell, registry.Find(ShellInteractionMode.ModeKey));
    }

    [Fact]
    public void Find_Unknown_Key_Returns_Null()
    {
        var registry = new InteractionModeRegistry([new ShellInteractionMode()]);

        Assert.Null(registry.Find("no-such-mode"));
    }

    [Fact]
    public void Duplicate_Key_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new InteractionModeRegistry([new ShellInteractionMode(), new ShellInteractionMode()]));
    }
}

/// <summary>设备目标声明目录：现有设备（agent 回连目标）均声明 shell，目标不存在返回空声明。</summary>
public class DeviceInteractionModeCatalogTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Existing_Device_Declares_Shell_Mode()
    {
        var targets = new TargetRegistry(_db.Factory, _clock);
        var deviceId = targets.Create(TargetTypes.Device, "网关", []).Target.Id;
        var catalog = new DeviceInteractionModeCatalog(targets);

        Assert.Equal([ShellInteractionMode.ModeKey], catalog.GetDeclaredModeKeys(deviceId));
    }

    [Fact]
    public void Service_Target_Declares_No_Modes()
    {
        // 服务目标无 agent 回连通道：不声明任何交互模式（集成审查 round 1 问题 1）
        var targets = new TargetRegistry(_db.Factory, _clock);
        var serviceId = targets.Create(TargetTypes.Service, "探针服务", []).Target.Id;
        var catalog = new DeviceInteractionModeCatalog(targets);

        Assert.Empty(catalog.GetDeclaredModeKeys(serviceId));
    }

    [Fact]
    public void Unknown_Target_Returns_Empty_Declaration()
    {
        var catalog = new DeviceInteractionModeCatalog(new TargetRegistry(_db.Factory, _clock));

        Assert.Empty(catalog.GetDeclaredModeKeys(424242));
    }
}
