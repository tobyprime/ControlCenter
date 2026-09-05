using System.Text.Json;

namespace DevicePanel.Agent;

/// <summary>
/// 一个控制器的本机声明（三期模块4）：面板可见的字段（key/type/label/tags/paramsSchema）随 agent.capabilities 上报；
/// command 是本机私有动作（脚本/命令，下发参数经 $1 传入），只在本机执行，绝不外发。
/// </summary>
internal sealed record ControllerSpec(
    string Key,
    string Type,
    string Label,
    IReadOnlyList<string> Tags,
    JsonElement ParamsSchema,
    string? Command);

/// <summary>
/// 控制器声明文件加载器：JSON 数组，每项 { key, type, label?, tags?, paramsSchema?, command? }。
/// 文件缺失/损坏/单条无效都降级为跳过或空列表——声明问题绝不影响 agent 连接与心跳（对齐通道契约）；
/// key 重复保留第一条（与面板 Normalize 规则一致）。缺省路径为程序目录下 controllers.json，
/// 可经 --controllers 或 PANEL_CONTROLLERS 指定。
/// </summary>
internal static class ControllerSpecFile
{
    public static IReadOnlyList<ControllerSpec> Load(string path, TextWriter? output = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                output?.WriteLine($"控制器声明文件无效，忽略（{path}）：根节点必须是 JSON 数组");
                return [];
            }

            using var emptySchema = JsonDocument.Parse("{}");
            var specs = new List<ControllerSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                index++;
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    output?.WriteLine($"控制器声明第 {index} 项无效，已跳过：必须是 JSON 对象");
                    continue;
                }

                var key = entry.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                    ? keyElement.GetString()
                    : null;
                var type = entry.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(type))
                {
                    output?.WriteLine($"控制器声明第 {index} 项无效，已跳过：缺少 key 或 type");
                    continue;
                }

                if (!seen.Add(key))
                {
                    output?.WriteLine($"控制器声明第 {index} 项无效，已跳过：key 重复（{key}）");
                    continue;
                }

                var label = entry.TryGetProperty("label", out var labelElement)
                            && labelElement.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(labelElement.GetString())
                    ? labelElement.GetString()!
                    : key;
                var tags = entry.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                    ? tagsElement.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString() ?? string.Empty)
                        .Where(t => t.Length > 0)
                        .Distinct()
                        .ToList()
                    : [];
                var schema = entry.TryGetProperty("paramsSchema", out var schemaElement)
                             && schemaElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? schemaElement.Clone()
                    : emptySchema.RootElement.Clone();
                var command = entry.TryGetProperty("command", out var commandElement)
                              && commandElement.ValueKind == JsonValueKind.String
                              && commandElement.GetString() is { Length: > 0 } value
                    ? value
                    : null;
                specs.Add(new ControllerSpec(key, type, label, tags, schema, command));
            }

            return specs;
        }
        catch (Exception ex)
        {
            output?.WriteLine($"控制器声明文件加载失败，忽略（{path}）：{ex.Message}");
            return [];
        }
    }
}
