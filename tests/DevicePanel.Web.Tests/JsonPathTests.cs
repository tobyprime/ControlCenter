using System.Text.Json;
using DevicePanel.Web.Probing;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>探针 JSONPath 极简求值器：覆盖 mc.zenoxs.cn settings.json 场景所需的子集，不做完整 JSONPath 规范。</summary>
public class JsonPathTests
{
    // 对齐 mc.zenoxs.cn/tiles/settings.json 的 Pl3xMap 响应形态
    private const string MapSettingsJson = """
        {
          "format": "png",
          "maxPlayers": 200,
          "players": [
            { "name": "steve", "uuid": "u1", "world": "world", "position": { "x": 1, "z": 2 } },
            { "name": "alex", "uuid": "u2", "world": "world_nether", "position": { "x": 3, "z": 4 } },
            { "name": "creeper", "uuid": "u3", "world": "world", "position": { "x": 5, "z": 6 } }
          ],
          "spawn": { "x": 0, "z": 0 }
        }
        """;

    [Fact]
    public void Root_Number_Is_Extracted()
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        var value = JsonPath.Evaluate(document.RootElement, "$.maxPlayers");

        Assert.NotNull(value);
        Assert.Equal(200, value!.Value.GetInt32());
    }

    [Fact]
    public void Nested_Property_Is_Extracted()
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        var value = JsonPath.Evaluate(document.RootElement, "$.spawn.x");

        Assert.NotNull(value);
        Assert.Equal(0, value!.Value.GetInt32());
    }

    [Fact]
    public void Array_Length_Is_Extracted()
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        var value = JsonPath.Evaluate(document.RootElement, "$.players.length()");

        Assert.NotNull(value);
        Assert.Equal(JsonValueKind.Number, value!.Value.ValueKind);
        Assert.Equal(3, value.Value.GetInt32());
    }

    [Fact]
    public void Array_Index_Is_Extracted()
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        var value = JsonPath.Evaluate(document.RootElement, "$.players[1].name");

        Assert.NotNull(value);
        Assert.Equal("alex", value!.Value.GetString());
    }

    [Fact]
    public void Bracket_Quoted_Property_Is_Extracted()
    {
        using var document = JsonDocument.Parse("""{ "player-count": 7 }""");

        var value = JsonPath.Evaluate(document.RootElement, "$['player-count']");

        Assert.NotNull(value);
        Assert.Equal(7, value!.Value.GetInt32());
    }

    [Fact]
    public void String_Length_Is_Extracted()
    {
        using var document = JsonDocument.Parse("""{ "motd": "hello" }""");

        var value = JsonPath.Evaluate(document.RootElement, "$.motd.length()");

        Assert.NotNull(value);
        Assert.Equal(5, value!.Value.GetInt32());
    }

    [Theory]
    [InlineData("$.players[9].name")]
    [InlineData("$.missing")]
    [InlineData("$.players.missing")]
    public void Missed_Path_Returns_Null(string path)
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        Assert.Null(JsonPath.Evaluate(document.RootElement, path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("players.length()")]
    [InlineData("$players")]
    [InlineData("$.players[")]
    [InlineData("$.players[]")]
    [InlineData("$.length(")]
    public void Malformed_Path_Throws(string path)
    {
        using var document = JsonDocument.Parse(MapSettingsJson);

        Assert.ThrowsAny<ArgumentException>(() => JsonPath.Evaluate(document.RootElement, path));
    }

    [Fact]
    public void Root_Itself_Is_Returned()
    {
        using var document = JsonDocument.Parse("""{ "ok": true }""");

        var value = JsonPath.Evaluate(document.RootElement, "$");

        Assert.NotNull(value);
        Assert.Equal(JsonValueKind.Object, value!.Value.ValueKind);
    }
}
