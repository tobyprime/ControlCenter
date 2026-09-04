using DevicePanel.Web.Alerting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// napcat 配置经环境变量/Secret 注入的种子测试（TOB-342 完成标准配套）：
/// 仅当面板 KV 设置为空时写入配置默认值，UI 保存的既有配置永不被覆盖。
/// </summary>
public class AlertSettingsSeederTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void SeedIfEmpty_On_Fresh_Database_Writes_All_Provided_Values()
    {
        var store = new AlertSettingsStore(_db.Factory);

        store.SeedIfEmpty(new AlertDeliverySettings("http://127.0.0.1:3000", "secret-token", "group", "123456"));

        Assert.Equal(
            new AlertDeliverySettings("http://127.0.0.1:3000", "secret-token", "group", "123456"),
            store.Get());
    }

    [Fact]
    public void SeedIfEmpty_Never_Overwrites_Existing_Values()
    {
        var store = new AlertSettingsStore(_db.Factory);
        store.Save(new AlertDeliverySettings("http://ui-configured:3000", "ui-token", "private", "10001"));

        store.SeedIfEmpty(new AlertDeliverySettings("http://from-secret:3000", "secret-token", "group", "123456"));

        Assert.Equal(
            new AlertDeliverySettings("http://ui-configured:3000", "ui-token", "private", "10001"),
            store.Get());
    }

    [Fact]
    public void SeedIfEmpty_Fills_Individual_Gaps_Only()
    {
        var store = new AlertSettingsStore(_db.Factory);
        store.Save(new AlertDeliverySettings("http://ui-configured:3000", null, null, null));

        store.SeedIfEmpty(new AlertDeliverySettings("http://ignored:3000", "secret-token", "group", "123456"));

        Assert.Equal(
            new AlertDeliverySettings("http://ui-configured:3000", "secret-token", "group", "123456"),
            store.Get());
    }

    [Fact]
    public void SeedIfEmpty_Without_Configured_Values_Is_Noop()
    {
        var store = new AlertSettingsStore(_db.Factory);

        store.SeedIfEmpty(new AlertDeliverySettings(null, null, null, null));

        Assert.Equal(new AlertDeliverySettings(null, null, null, null), store.Get());
    }

    [Fact]
    public async Task Seeder_StartAsync_Seeds_Settings_From_Options()
    {
        var store = new AlertSettingsStore(_db.Factory);
        var seeder = new AlertSettingsSeeder(
            store,
            new NapcatSeedOptions
            {
                BaseUrl = "http://napcat:3000",
                Token = "secret-token",
                TargetType = NapcatNotifier.TargetGroup,
                TargetId = "123456",
            },
            NullLogger<AlertSettingsSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(
            new AlertDeliverySettings("http://napcat:3000", "secret-token", "group", "123456"),
            store.Get());
    }

    [Fact]
    public async Task Seeder_StartAsync_Without_Configured_Values_Leaves_Store_Empty()
    {
        var store = new AlertSettingsStore(_db.Factory);
        var seeder = new AlertSettingsSeeder(
            store,
            new NapcatSeedOptions(),
            NullLogger<AlertSettingsSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(new AlertDeliverySettings(null, null, null, null), store.Get());
    }
}
