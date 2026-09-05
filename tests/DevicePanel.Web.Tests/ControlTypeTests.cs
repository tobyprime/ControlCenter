using System.Text.Json;
using DevicePanel.Web.Control;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 控制类型注册表（三期模块4）：内置四类（button/toggle/input/slider）注册即用、
/// 声明 schema 与下发参数校验语义、未知类型不可下发（新增类型 = 注册 IControlType，核心管道零改动）。
/// </summary>
public class ControlTypeTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---------- 注册表 ----------

    [Fact]
    public void Catalog_Collects_Four_Builtin_Types()
    {
        var catalog = new ControlTypeCatalog(new IControlType[]
        {
            new SliderControlType(), new ButtonControlType(), new ToggleControlType(), new InputControlType(),
        });

        var types = catalog.List();
        Assert.Equal(4, types.Count);
        // 按 key 排序，清单稳定（注册顺序无关）
        Assert.Equal(["button", "input", "slider", "toggle"], types.Select(t => t.Key).ToArray());
        Assert.Equal("按钮", catalog.Find("button")!.DisplayName);
        Assert.Null(catalog.Find("nope"));
    }

    // ---------- button ----------

    [Fact]
    public void Button_Schema_Requires_Non_Empty_Unique_Items()
    {
        var button = new ButtonControlType();
        Assert.Null(button.ValidateDeclarationSchema(Json("""{"items":[{"label":"重启","value":"restart"}]}""")));

        Assert.Contains("items", button.ValidateDeclarationSchema(Json("{}")));
        Assert.Contains("items", button.ValidateDeclarationSchema(Json("""{"items":[]}""")));
        Assert.Contains("value", button.ValidateDeclarationSchema(Json("""{"items":[{"label":"a","value":"x"},{"label":"b","value":"x"}]}""")));
        Assert.Contains("items", button.ValidateDeclarationSchema(Json("[]")));
    }

    [Fact]
    public void Button_Params_Must_Hit_Declared_Items()
    {
        var button = new ButtonControlType();
        var schema = Json("""{"items":[{"label":"重启","value":"restart"},{"label":"停止","value":"stop"}]}""");

        Assert.Null(button.ValidateInvokeParams(schema, Json("""{"value":"restart"}""")));
        Assert.NotNull(button.ValidateInvokeParams(Json("{}"), Json("""{"value":"anything"}"""))); // schema 缺 items 一律拒绝（声明校验已前置拦截）
        Assert.Contains("value", button.ValidateInvokeParams(schema, Json("{}")));
        Assert.Contains("清单", button.ValidateInvokeParams(schema, Json("""{"value":"ghost"}""")));
    }

    // ---------- toggle ----------

    [Fact]
    public void Toggle_Params_Require_Boolean_State()
    {
        var toggle = new ToggleControlType();
        Assert.Null(toggle.ValidateDeclarationSchema(Json("{}"))); // 开关无声明参数，schema 可省略
        Assert.NotNull(toggle.ValidateDeclarationSchema(Json("[1,2]")));
        Assert.Null(toggle.ValidateInvokeParams(Json("{}"), Json("""{"state":true}""")));
        Assert.Null(toggle.ValidateInvokeParams(Json("{}"), Json("""{"state":false}""")));
        Assert.NotNull(toggle.ValidateInvokeParams(Json("{}"), Json("""{"state":"on"}""")));
        Assert.NotNull(toggle.ValidateInvokeParams(Json("{}"), Json("{}")));
    }

    // ---------- input ----------

    [Fact]
    public void Input_Schema_Restricts_InputType_And_Params_Follow_It()
    {
        var input = new InputControlType();
        Assert.Null(input.ValidateDeclarationSchema(Json("{}")));
        Assert.Null(input.ValidateDeclarationSchema(Json("""{"inputType":"password"}""")));
        Assert.Contains("inputType", input.ValidateDeclarationSchema(Json("""{"inputType":"color"}""")));

        Assert.Null(input.ValidateInvokeParams(Json("{}"), Json("""{"text":"hello"}""")));
        Assert.Null(input.ValidateInvokeParams(Json("""{"inputType":"password"}"""), Json("""{"text":"s3cret"}""")));
        Assert.Null(input.ValidateInvokeParams(Json("""{"inputType":"number"}"""), Json("""{"text":"42"}""")));
        Assert.NotNull(input.ValidateInvokeParams(Json("""{"inputType":"number"}"""), Json("""{"text":"42abc"}""")));
        Assert.NotNull(input.ValidateInvokeParams(Json("{}"), Json("""{"text":42}""")));
        Assert.NotNull(input.ValidateInvokeParams(Json("{}"), Json("{}")));
    }

    // ---------- slider ----------

    [Fact]
    public void Slider_Schema_Requires_Min_Max_And_Optional_Step()
    {
        var slider = new SliderControlType();
        Assert.Null(slider.ValidateDeclarationSchema(Json("""{"min":0,"max":100}""")));
        Assert.Null(slider.ValidateDeclarationSchema(Json("""{"min":0,"max":100,"step":5}""")));
        Assert.Contains("min", slider.ValidateDeclarationSchema(Json("""{"max":100}""")));
        Assert.Contains("step", slider.ValidateDeclarationSchema(Json("""{"min":0,"max":10,"step":0}""")));
        Assert.Contains("max", slider.ValidateDeclarationSchema(Json("""{"min":100,"max":0}""")));
    }

    [Fact]
    public void Slider_Params_Must_Be_In_Range_And_Aligned_To_Step()
    {
        var slider = new SliderControlType();
        var schema = Json("""{"min":0,"max":100,"step":5}""");

        Assert.Null(slider.ValidateInvokeParams(schema, Json("""{"value":35}""")));
        Assert.Null(slider.ValidateInvokeParams(Json("""{"min":0,"max":1}"""), Json("""{"value":0.35}""")));
        Assert.Contains("范围", slider.ValidateInvokeParams(schema, Json("""{"value":120}""")));
        Assert.Contains("步长", slider.ValidateInvokeParams(schema, Json("""{"value":33}""")));
        Assert.NotNull(slider.ValidateInvokeParams(schema, Json("""{"value":"30"}""")));
    }

    [Fact]
    public void Slider_Step_Alignment_Tolerates_Floating_Point_Noise()
    {
        var slider = new SliderControlType();
        var schema = Json("""{"min":0,"max":1,"step":0.1}""");
        Assert.Null(slider.ValidateInvokeParams(schema, Json("""{"value":0.7}""")));
    }
}
