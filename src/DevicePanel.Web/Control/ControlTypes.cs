using System.Text.Json;

namespace DevicePanel.Web.Control;

/// <summary>
/// 控制类型抽象（三期模块4，对齐 ICollectorDataType 注册模式）：新增一种控制类型 = 注册一个 IControlType 实现，
/// 类型清单与下发校验经 DI 自动纳入，核心管道（信封/下发/留痕）零改动。
/// </summary>
public interface IControlType
{
    string Key { get; }

    string DisplayName { get; }

    /// <summary>校验 agent 声明的参数 schema（controllers_json.paramsSchema）；返回 null 表示合法，否则为错误说明。</summary>
    string? ValidateDeclarationSchema(JsonElement schema);

    /// <summary>校验一次下发的 params 是否匹配声明 schema；返回 null 表示合法，否则为错误说明。</summary>
    string? ValidateInvokeParams(JsonElement schema, JsonElement parameters);
}

/// <summary>内置控制类型：按钮（声明时自定义按钮清单，下发时选定其中一项）。</summary>
public sealed class ButtonControlType : IControlType
{
    public string Key => ControlTypeKeys.Button;

    public string DisplayName => "按钮";

    public string? ValidateDeclarationSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            return "按钮声明须含非空 items 数组";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return "按钮 items 项须为对象（label + value）";
            }

            var label = item.TryGetProperty("label", out var l) ? l.GetString() : null;
            var value = item.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            {
                return "按钮 items 项的 label 与 value 均不能为空";
            }

            if (!seen.Add(value))
            {
                return $"按钮 value 重复：{value}";
            }
        }

        return null;
    }

    public string? ValidateInvokeParams(JsonElement schema, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return "按钮下发参数须为 { value: string }";
        }

        var requested = value.GetString();
        var valid = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out var items) &&
                    items.EnumerateArray().Any(i => i.TryGetProperty("value", out var v) && v.GetString() == requested);
        return valid ? null : $"按钮值不在声明清单内：{requested}";
    }
}

/// <summary>内置控制类型：开关（布尔状态切换）。</summary>
public sealed class ToggleControlType : IControlType
{
    public string Key => ControlTypeKeys.Toggle;

    public string DisplayName => "开关";

    public string? ValidateDeclarationSchema(JsonElement schema) =>
        schema.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined
            ? null
            : "开关声明须为对象（可省略）";

    public string? ValidateInvokeParams(JsonElement schema, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("state", out var state) ||
            state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return "开关下发参数须为 { state: bool }";
        }

        return null;
    }
}

/// <summary>内置控制类型：输入框（text / number / password，声明时指定）。</summary>
public sealed class InputControlType : IControlType
{
    private static readonly string[] AllowedInputTypes = ["text", "number", "password"];

    public string Key => ControlTypeKeys.Input;

    public string DisplayName => "输入框";

    public string? ValidateDeclarationSchema(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null; // 缺省 = text
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "输入框声明须为对象";
        }

        if (schema.TryGetProperty("inputType", out var inputType) &&
            inputType.ValueKind == JsonValueKind.String &&
            !AllowedInputTypes.Contains(inputType.GetString()))
        {
            return "输入框 inputType 仅支持 text / number / password";
        }

        return null;
    }

    public string? ValidateInvokeParams(JsonElement schema, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return "输入框下发参数须为 { text: string }";
        }

        var inputType = schema.ValueKind == JsonValueKind.Object &&
                        schema.TryGetProperty("inputType", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : "text";
        if (inputType == "number" && !double.TryParse(text.GetString(), out _))
        {
            return "输入框声明为 number，下发文本须为数字";
        }

        return null;
    }
}

/// <summary>内置控制类型：滑块（min/max/step 范围内取值）。</summary>
public sealed class SliderControlType : IControlType
{
    public string Key => ControlTypeKeys.Slider;

    public string DisplayName => "滑块";

    public string? ValidateDeclarationSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("min", out var min) || min.ValueKind != JsonValueKind.Number ||
            !schema.TryGetProperty("max", out var max) || max.ValueKind != JsonValueKind.Number)
        {
            return "滑块声明须为 { min: number, max: number, step?: number }";
        }

        if (min.GetDouble() > max.GetDouble())
        {
            return "滑块 min 不能大于 max";
        }

        if (schema.TryGetProperty("step", out var step) && step.ValueKind == JsonValueKind.Number && step.GetDouble() <= 0)
        {
            return "滑块 step 须为正数";
        }

        return null;
    }

    public string? ValidateInvokeParams(JsonElement schema, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return "滑块下发参数须为 { value: number }";
        }

        var v = value.GetDouble();
        var min = schema.GetProperty("min").GetDouble();
        var max = schema.GetProperty("max").GetDouble();
        if (v < min || v > max)
        {
            return $"滑块值超出声明范围 [{min}, {max}]：{v}";
        }

        if (schema.TryGetProperty("step", out var step) && step.ValueKind == JsonValueKind.Number)
        {
            var s = step.GetDouble();
            var offset = Math.Abs((v - min) / s - Math.Round((v - min) / s));
            if (offset > 1e-6)
            {
                return $"滑块值未按步长 {s} 对齐：{v}";
            }
        }

        return null;
    }
}

/// <summary>控制类型 key 常量（协议层不感知具体类型；面板与 agent 共识的内置清单）。</summary>
public static class ControlTypeKeys
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Input = "input";
    public const string Slider = "slider";
}

/// <summary>控制类型清单：收集 DI 中全部 IControlType 注册（新增类型零改动出现在清单），并承担下发校验路由。</summary>
public sealed class ControlTypeCatalog
{
    private readonly IReadOnlyDictionary<string, IControlType> _types;

    public ControlTypeCatalog(IEnumerable<IControlType> types) =>
        _types = types.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public IControlType? Find(string key) => _types.GetValueOrDefault(key);

    public IReadOnlyList<(string Key, string DisplayName)> List() =>
        _types.Values.Select(t => (t.Key, t.DisplayName)).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
}
