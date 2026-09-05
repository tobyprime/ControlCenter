using System.Text.Json;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 控制器声明持久化（三期模块4）：SetCapabilities 随能力上报整体覆盖 controllers_json，
/// 旧版字符串数组重报清空控制器（后报者胜），库内损坏 JSON 降级为空清单。
/// </summary>
public class AgentControllersRegistryTests : IDisposable
{
    private readonly TempSqliteDatabase _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AgentRegistry CreateRegistry() => new(_db.Factory, _clock);

    private static ControllerDeclaration Declaration(string key, string type, string label) =>
        new(key, type, label, ["机房"], JsonSerializer.SerializeToElement(new { min = 0, max = 100 }));

    [Fact]
    public void SetCapabilities_Persists_And_Returns_Declarations()
    {
        var registry = CreateRegistry();
        var created = registry.Create("边缘 agent", []);
        var controllers = new[]
        {
            Declaration("fan", "slider", "风扇调速"),
            new ControllerDeclaration("power", "toggle", "电源", [], JsonSerializer.SerializeToElement(new { })),
        };

        Assert.True(registry.SetCapabilities(created.Agent.Id, ["metrics", "controllers"], controllers));

        var agent = registry.Get(created.Agent.Id);
        Assert.NotNull(agent);
        Assert.Equal(2, agent.Controllers!.Count);
        Assert.Equal("fan", agent.Controllers[0].Key);
        Assert.Equal("风扇调速", agent.Controllers[0].Label);
        Assert.Equal(100, agent.Controllers[0].ParamsSchema.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Legacy_Capability_Report_Clears_Previously_Declared_Controllers()
    {
        var registry = CreateRegistry();
        var created = registry.Create("边缘 agent", []);
        registry.SetCapabilities(created.Agent.Id, ["metrics", "controllers"], [Declaration("fan", "slider", "风扇")]);

        // agent 降级/旧版重报字符串数组：控制器声明整体清空（后报者胜）
        registry.SetCapabilities(created.Agent.Id, ["metrics"]);

        var agent = registry.Get(created.Agent.Id);
        Assert.NotNull(agent);
        Assert.Empty(agent.Controllers!);
    }

    [Fact]
    public void Corrupt_Controllers_Json_Degrades_To_Empty_List()
    {
        var registry = CreateRegistry();
        var created = registry.Create("边缘 agent", []);

        using (var connection = _db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE agents SET controllers_json = '{broken' WHERE id = $id";
            command.Parameters.AddWithValue("$id", created.Agent.Id);
            command.ExecuteNonQuery();
        }

        var agent = registry.Get(created.Agent.Id);
        Assert.NotNull(agent);
        Assert.Empty(agent.Controllers!);
    }

    [Fact]
    public void New_Agent_Reports_No_Declarations_Until_First_Report()
    {
        var registry = CreateRegistry();
        var created = registry.Create("边缘 agent", []);

        var agent = registry.Get(created.Agent.Id);
        Assert.NotNull(agent);
        Assert.Null(agent.Capabilities);
        Assert.Empty(agent.Controllers!); // 未上报 = 空清单
    }
}
