using System.Text.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>net8.0 没有 JsonElement.DeepEquals：两侧用同一序列化选项输出后比较，做结构化 JSON 断言。</summary>
internal static class JsonElementAssertions
{
    public static void JsonEquals(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }
}
