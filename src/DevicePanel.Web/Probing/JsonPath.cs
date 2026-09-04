using System.Text.Json;

namespace DevicePanel.Web.Probing;

/// <summary>
/// 探针用 JSONPath 极简求值器（模块2）：仅支持 $ 根、.name / ['name'] 属性、[n] 数组索引、.length() 长度，
/// 覆盖 map.zenoxs.cn/tiles/settings.json（Pl3xMap）类响应的取值需求，不做完整 JSONPath 规范。
/// 未命中返回 null；路径非法抛 ArgumentException（配置保存时校验语法，采集时按提取失败丢点处理）。
/// </summary>
public static class JsonPath
{
    public static JsonElement? Evaluate(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in Parse(path))
        {
            if (!segment.TryResolve(ref current))
            {
                return null;
            }
        }

        return current.Clone();
    }

    /// <summary>路径语法校验：不合法抛 ArgumentException（配置保存时调用，避免坏路径流入采集）。</summary>
    public static void Validate(string path) => Parse(path);

    private static List<Segment> Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("JSONPath 不能为空");
        }

        if (path[0] != '$')
        {
            throw new ArgumentException($"JSONPath 必须以 $ 开头：{path}");
        }

        var segments = new List<Segment>();
        var i = 1;
        while (i < path.Length)
        {
            switch (path[i])
            {
                case '.':
                    i++;
                    var start = i;
                    while (i < path.Length && path[i] is not ('.' or '['))
                    {
                        i++;
                    }

                    segments.Add(ParseProperty(path[start..i], path));
                    break;
                case '[':
                    i++;
                    var end = path.IndexOf(']', i);
                    if (end < 0)
                    {
                        throw new ArgumentException($"JSONPath 括号未闭合：{path}");
                    }

                    var token = path[i..end];
                    if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'')
                    {
                        segments.Add(ParseProperty(token[1..^1], path));
                    }
                    else if (int.TryParse(token, out var index))
                    {
                        segments.Add(new Segment(SegmentKind.Index, string.Empty, index));
                    }
                    else
                    {
                        throw new ArgumentException($"JSONPath 仅支持 [n] 索引或 ['name'] 形式：{path}");
                    }

                    i = end + 1;
                    break;
                default:
                    throw new ArgumentException($"JSONPath 语法不支持（位置 {i}）：{path}");
            }
        }

        return segments;
    }

    private static Segment ParseProperty(string name, string path)
    {
        if (name.Length == 0)
        {
            throw new ArgumentException($"JSONPath 属性名不能为空：{path}");
        }

        if (name == "length()")
        {
            return new Segment(SegmentKind.Length, string.Empty, 0);
        }

        if (name.Contains('('))
        {
            // 极简子集仅认 length() 函数，带括号的其他写法一律拒绝（如截断的 $.x.length( ）
            throw new ArgumentException($"JSONPath 仅支持 length() 函数：{path}");
        }

        return new Segment(SegmentKind.Property, name, 0);
    }

    private enum SegmentKind
    {
        Property,
        Index,
        Length,
    }

    private readonly record struct Segment(SegmentKind Kind, string Name, int Index)
    {
        public bool TryResolve(ref JsonElement current)
        {
            switch (Kind)
            {
                case SegmentKind.Property:
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(Name, out var value))
                    {
                        return false;
                    }

                    current = value;
                    return true;
                case SegmentKind.Index:
                    if (current.ValueKind != JsonValueKind.Array || Index < 0 || Index >= current.GetArrayLength())
                    {
                        return false;
                    }

                    current = current[Index];
                    return true;
                case SegmentKind.Length:
                    switch (current.ValueKind)
                    {
                        case JsonValueKind.Array:
                            current = JsonSerializer.SerializeToElement(current.GetArrayLength());
                            return true;
                        case JsonValueKind.Object:
                            current = JsonSerializer.SerializeToElement(current.EnumerateObject().Count());
                            return true;
                        case JsonValueKind.String:
                            current = JsonSerializer.SerializeToElement(current.GetString()!.Length);
                            return true;
                        default:
                            return false;
                    }
                default:
                    return false;
            }
        }
    }
}
