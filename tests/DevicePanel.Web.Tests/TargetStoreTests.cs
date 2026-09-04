using DevicePanel.Web.Devices;
using DevicePanel.Web.Targets;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>目标统一数据模型（TOB-360 模块 0）：现有设备自动迁移为 device 目标，服务目标共用同一张表。</summary>
public class TargetStoreTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly DeviceRegistry _devices;
    private readonly TargetStore _targets;

    public TargetStoreTests()
    {
        _devices = new DeviceRegistry(_db.Factory, TimeProvider.System);
        _targets = new TargetStore(_db.Factory, TimeProvider.System);
    }

    [Fact]
    public void ProvisionForDevice_Creates_Exactly_One_Device_Target()
    {
        var device = _devices.Create("srv-1", []).Device;

        var first = _targets.ProvisionForDevice(device.Id, device.Name);
        var second = _targets.ProvisionForDevice(device.Id, device.Name);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TargetTypes.Device, first.Type);
        Assert.Equal(device.Id, first.DeviceId);
        Assert.Single(_targets.List());
    }

    [Fact]
    public void Device_Target_Name_Follows_Live_Device_Name()
    {
        var device = _devices.Create("old-name", []).Device;
        var target = _targets.ProvisionForDevice(device.Id, device.Name);

        _devices.Update(device.Id, "renamed-device", []);

        Assert.Equal("renamed-device", _targets.Get(target.Id)?.Name);
    }

    [Fact]
    public void GetByDeviceId_Resolves_Target_For_Device()
    {
        var device = _devices.Create("srv-2", []).Device;
        _targets.ProvisionForDevice(device.Id, device.Name);

        var target = _targets.GetByDeviceId(device.Id);

        Assert.NotNull(target);
        Assert.Equal(TargetTypes.Device, target!.Type);
        Assert.Null(_targets.GetByDeviceId(9999));
    }

    [Fact]
    public void List_Can_Filter_By_Type()
    {
        var device = _devices.Create("srv-3", []).Device;
        _targets.ProvisionForDevice(device.Id, device.Name);

        Assert.Single(_targets.List(TargetTypes.Device));
        Assert.Empty(_targets.List(TargetTypes.Service));
        Assert.Single(_targets.List());
    }

    [Fact]
    public void Delete_Device_Cascades_Target()
    {
        var device = _devices.Create("srv-4", []).Device;
        var target = _targets.ProvisionForDevice(device.Id, device.Name);

        _devices.Delete(device.Id);

        Assert.Null(_targets.Get(target.Id));
    }

    public void Dispose() => _db.Dispose();
}
