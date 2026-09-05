using System.Text.Json;
using DevicePanel.Web.Agents;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 控制器声明解析（三期模块4）：能力上报对象形态里的 controllers 清单——
/// 缺 key/type、未知 type、key 重复的条目丢弃（保序保首条），label 缺省回退 key，paramsSchema 缺省 {}；
/// 库内 JSON 损坏降级为空清单（agent 重报即自愈）；序列化为 camelCase 存储。
/// </summary>
public class ControllerDeclarationTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static bool TypeKnown(string type) => type is "button" or "toggle" or "input" or "slider";

    [Fact]
    public void Normalize_Keeps_Valid_Entries_With_Label_Fallback_And_Default_Schema()
    {
        var payload = Json("""
            {
              "capabilities": ["metrics", "controllers"],
              "controllers": [
                { "key": "restart", "type": "button", "label": "重启服务", "tags": ["运维"],
                  "paramsSchema": { "items": [ { "label": "重启", "value": "restart" } ] } },
                { "key": "brightness", "type": "slider" }
              ]
            }
            """);
        var controllers = payload.GetProperty("controllers");

        var normalized = ControllerDeclarationList.Normalize(controllers, TypeKnown);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("restart", normalized[0].Key);
        Assert.Equal("button", normalized[0].Type);
        Assert.Equal("重启服务", normalized[0].Label);
        Assert.Equal(["运维"], normalized[0].Tags);
        Assert.Equal(JsonValueKind.Object, normalized[0].ParamsSchema.ValueKind);
        Assert.Equal("brightness", normalized[1].Key);
        Assert.Equal("slider", normalized[1].Type);
        Assert.Equal("brightness", normalized[1].Label); // label 缺省回退 key
        Assert.Empty(normalized[1].Tags);
        Assert.Equal(JsonValueKind.Object, normalized[1].ParamsSchema.ValueKind);
        Assert.Equal("{}", normalized[1].ParamsSchema.GetRawText()); // paramsSchema 缺省 {}
    }

    [Fact]
    public void Normalize_Drops_Invalid_Unknown_And_Duplicate_Entries()
    {
        var controllers = Json("""
            [
              { "type": "button", "label": "缺 key" },
              { "key": "ghost", "type": "teleport" },
              { "key": "a", "type": "toggle", "label": "首条" },
              { "key": "a", "type": "toggle", "label": "重复" },
              { "key": "b", "type": "toggle" }
            ]
            """);

        var normalized = ControllerDeclarationList.Normalize(controllers, TypeKnown);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("首条", normalized[0].Label); // key 重复保留第一条
        Assert.Equal("b", normalized[1].Key);
    }

    [Fact]
    public void Normalize_Without_Type_Knowledge_Keeps_Any_Non_Empty_Type()
    {
        // 面板侧解析库内持久化副本时不带注册表（类型合法性由上报时刻校验）
        var controllers = Json("""[{ "key": "k", "type": "anything" }]""");
        var normalized = ControllerDeclarationList.Normalize(controllers);
        Assert.Single(normalized);
        Assert.Equal("anything", normalized[0].Type);
    }

    [Fact]
    public void Parse_Corrupt_Json_Returns_Empty_List()
    {
        Assert.Empty(ControllerDeclarationList.Parse("not json"));
        Assert.Empty(ControllerDeclarationList.Parse("[{"));
        Assert.Empty(ControllerDeclarationList.Parse(null));
        Assert.Empty(ControllerDeclarationList.Parse("{}")); // 形态不对也降级为空
    }

    [Fact]
    public void Serialize_And_Parse_Round_Trips_Through_CamelCase()
    {
        var declarations = new[]
        {
            new ControllerDeclaration("fan", "slider", "风扇", ["机房"],
                Json("""{"min":0,"max":100,"step":10}""")),
        };

        var json = ControllerDeclarationList.Serialize(declarations);
        Assert.Contains("\"paramsSchema\"", json); // camelCase 存储（面板/前端契约）

        var parsed = ControllerDeclarationList.Parse(json);
        Assert.Single(parsed);
        Assert.Equal("fan", parsed[0].Key);
        Assert.Equal(100, parsed[0].ParamsSchema.GetProperty("max").GetInt32());
    }
}
