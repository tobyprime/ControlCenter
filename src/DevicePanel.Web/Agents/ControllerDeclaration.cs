using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevicePanel.Web.Agents;

/// <summary>
/// 控制器实体（三期模块4）：由 agent 随能力声明上报、面板持久化展示。
/// type 为面板侧控制类型注册表中的 key；paramsSchema 为该类型的声明参数（语义由 IControlType 解释）；
/// tags 为自由文本标签（与采集器标签同风格，用于主页控制卡筛选组合）。
/// </summary>
public sealed record ControllerDeclaration(
    string Key,
    string Type,
    string Label,
    IReadOnlyList<string> Tags,
    [property: JsonPropertyName("paramsSchema")] JsonElement ParamsSchema);

/// <summary>控制器声明 JSON 的归一化：解析/清洗 agent 上报（非法条目丢弃），落库与读出的唯一通道。</summary>
public static class ControllerDeclarationList
{
    /// <summary>落库/读出共用的序列化配置：camelCase 与面板 API 输出一致。</summary>
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web);

    /// <summary>解析 controllers_json；损坏内容视为未声明返回空清单（不抛出，避免历史脏数据打断管理页）。</summary>
    public static IReadOnlyList<ControllerDeclaration> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Normalize(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>清洗 agent 上报的声明数组：非法条目（缺 key/type、类型未知）整条丢弃，由调用方记日志。</summary>
    public static IReadOnlyList<ControllerDeclaration> Normalize(JsonElement payload, Func<string, bool>? typeKnown = null)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var declarations = new List<ControllerDeclaration>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in payload.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = entry.TryGetProperty("key", out var k) ? k.GetString()?.Trim() : null;
            var type = entry.TryGetProperty("type", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(type))
            {
                continue;
            }

            if (typeKnown is not null && !typeKnown(type))
            {
                continue;
            }

            if (!seenKeys.Add(key))
            {
                continue; // 同一 agent 内 key 重复：保留首条
            }

            var label = entry.TryGetProperty("label", out var l) ? l.GetString()?.Trim() : null;
            var tags = entry.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                ? tagsElement.EnumerateArray()
                    .Select(e => e.GetString()?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Cast<string>()
                    .Distinct()
                    .ToList()
                : [];
            var schema = entry.TryGetProperty("paramsSchema", out var schemaElement) &&
                         schemaElement.ValueKind is not JsonValueKind.Undefined
                ? schemaElement.Clone()
                : JsonSerializer.SerializeToElement(new { });

            declarations.Add(new ControllerDeclaration(key, type, string.IsNullOrEmpty(label) ? key : label, tags, schema));
        }

        return declarations;
    }

    public static string Serialize(IReadOnlyList<ControllerDeclaration> declarations) =>
        JsonSerializer.Serialize(declarations, StorageOptions);
}
