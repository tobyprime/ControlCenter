using System.Globalization;
using System.Text.Json;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;

namespace DevicePanel.Web.Alerting;

public enum AlertRuleAction
{
    /// <summary>无动作（可携带待持久化的状态，如防抖等待中）。</summary>
    None,

    /// <summary>触发告警（入队分发，并更新事件状态）。</summary>
    Fire,

    /// <summary>事件恢复（清除持久化状态，不发消息）。</summary>
    Clear,
}

/// <summary>规则评估结论。StateJson 在 Fire/None 时持久化（为空则不动状态），Clear 时删除状态。</summary>
public sealed record AlertRuleDecision(AlertRuleAction Action, string? StateJson = null, AlertMessage? Message = null)
{
    public static readonly AlertRuleDecision None = new(AlertRuleAction.None);
}

/// <summary>规则类型参数描述（规则管理页动态表单的数据来源）。</summary>
public sealed record AlertRuleParamDescriptor(
    string Name,
    string Type,
    bool Required,
    string? DefaultValue,
    string Description);

/// <summary>规则类型描述：展示名、指标要求与参数表单。</summary>
public sealed record AlertRuleTypeDescriptor(
    string RuleType,
    string DisplayName,
    string Description,
    bool RequiresMetric,
    bool AllowsNullMetric,
    IReadOnlyList<AlertRuleParamDescriptor> Params);

/// <summary>
/// 告警规则类型处理器（TOB-360 约束 B：规则类型可插拔）。
/// 一种规则类型 = 实现本接口 + 注册 DI；核心引擎按规则声明的类型分发，不内置任何具体告警逻辑。
/// </summary>
public interface IAlertRuleTypeHandler
{
    /// <summary>规则类型标识（alert_rules.rule_type）。</summary>
    string RuleType { get; }

    AlertRuleTypeDescriptor Describe();

    /// <summary>参数校验：合法返回 null，否则返回错误文案。</summary>
    string? ValidateParams(string paramsJson);

    /// <summary>评估一条规则。保证不抛异常的契约由引擎兜底，实现方仍应自行防御。</summary>
    AlertRuleDecision Evaluate(AlertRuleContext context);

    /// <summary>是否参与周期扫描（如无数据类规则）；纯事件驱动类型为 false。</summary>
    bool ScansOnSchedule { get; }
}

/// <summary>一次规则评估的输入：规则、目标、指标元数据、触发源（采样点或扫描）与当前持久化状态。</summary>
public sealed record AlertRuleContext(
    AlertRule Rule,
    TargetInfo Target,
    MetricKeyInfo? Metric,
    DateTimeOffset NowUtc,
    string? StateJson,
    double? SampleNum = null,
    string? SampleText = null,
    DateTimeOffset? LastDataUtc = null)
{
    private string ParamsJson => Rule.ParamsJson;

    public T ParseParams<T>(Func<JsonElement, T> parse)
    {
        using var document = JsonDocument.Parse(ParamsJson);
        return parse(document.RootElement);
    }

    /// <summary>采样值展示文本：number 一位小数，其余用文本值。</summary>
    public string SampleDisplay =>
        SampleText ?? (SampleNum?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty);
}

/// <summary>规则参数 JSON 读取（缺省项回落到默认值，类型错误抛异常由校验层拦截）。</summary>
public static class AlertRuleParamsJson
{
    public static double GetDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : fallback;

    public static int GetInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : fallback;

    public static string GetString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
}

/// <summary>
/// 防抖公共门（沿用一期"持续 N 周期"语义，参数化到规则）：
/// 首次越限记录 FirstSeen，持续满 SustainWindow 才告警；同一事件默认只发一次
/// （RepeatMinutes &gt; 0 时按间隔重发）；回落即恢复（清状态，下次是全新事件）。
/// 状态形状与一期 threshold:* 键完全一致，历史在途事件迁移后无缝续接。
/// </summary>
public static class SustainGate
{
    internal sealed record GateState(DateTimeOffset FirstSeenUtc, DateTimeOffset? LastAlertedUtc);

    public static AlertRuleDecision Evaluate(
        string? stateJson,
        bool violated,
        DateTimeOffset nowUtc,
        TimeSpan sustainWindow,
        TimeSpan repeatWindow,
        Func<DateTimeOffset, AlertMessage> messageFactory)
    {
        var state = AlertStateStore.Read<GateState>(stateJson);
        if (!violated)
        {
            return state is not null
                ? new AlertRuleDecision(AlertRuleAction.Clear)
                : AlertRuleDecision.None;
        }

        state ??= new GateState(nowUtc, null);
        if (state.LastAlertedUtc is { } lastAlerted)
        {
            if (repeatWindow <= TimeSpan.Zero || nowUtc - lastAlerted < repeatWindow)
            {
                return AlertRuleDecision.None;
            }

            return Fire(state with { LastAlertedUtc = nowUtc }, nowUtc, messageFactory);
        }

        if (nowUtc - state.FirstSeenUtc < sustainWindow)
        {
            // 尚未持续满一个窗口：继续等待
            return new AlertRuleDecision(AlertRuleAction.None, Serialize(state));
        }

        return Fire(state with { LastAlertedUtc = nowUtc }, nowUtc, messageFactory);
    }

    private static AlertRuleDecision Fire(GateState state, DateTimeOffset nowUtc, Func<DateTimeOffset, AlertMessage> messageFactory) =>
        new(AlertRuleAction.Fire, Serialize(state), messageFactory(state.FirstSeenUtc));

    internal static string Serialize(GateState state) => JsonSerializer.Serialize(state);
}

/// <summary>规则消息与展示的公共小件（设备/服务称谓、指标展示名、单位后缀）。</summary>
public static class AlertRuleText
{
    public static string Noun(TargetInfo target) => target.IsDevice ? "设备" : "服务";

    public static string MetricDisplay(MetricKeyInfo? metric) => metric?.DisplayName ?? "指标";

    public static string UnitSuffix(MetricKeyInfo? metric) => metric?.Unit ?? string.Empty;

    public static string Number(double value, MetricKeyInfo? metric) =>
        value.ToString("F1", CultureInfo.InvariantCulture) + UnitSuffix(metric);
}

/// <summary>
/// 阈值上限规则：采样值持续高于阈值（默认 60s，可配）才告警；同一事件恢复前默认只发一次。
/// 参数：threshold（必填）、sustainSeconds、repeatMinutes。
/// </summary>
public sealed class ThresholdAboveRuleHandler : IAlertRuleTypeHandler
{
    public const string TypeName = "threshold_above";

    private readonly AlertOptions _options;

    public ThresholdAboveRuleHandler(AlertOptions options) => _options = options;

    public string RuleType => TypeName;

    public bool ScansOnSchedule => false;

    public AlertRuleTypeDescriptor Describe() => new(
        TypeName,
        "阈值上限",
        "指标持续高于阈值时告警",
        RequiresMetric: true,
        AllowsNullMetric: false,
        Params:
        [
            new AlertRuleParamDescriptor("threshold", "number", true, null, "越限阈值"),
            new AlertRuleParamDescriptor("sustainSeconds", "number", false, _options.SustainSeconds.ToString(), "持续秒数（防抖）"),
            new AlertRuleParamDescriptor("repeatMinutes", "number", false, _options.RepeatMinutes.ToString(), "重发间隔（分钟，0 = 事件内不重发）"),
        ]);

    public string? ValidateParams(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        var threshold = AlertRuleParamsJson.GetDouble(document.RootElement, "threshold", double.NaN);
        if (double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            return "threshold 必须是有限数值";
        }

        var sustain = AlertRuleParamsJson.GetInt(document.RootElement, "sustainSeconds", _options.SustainSeconds);
        var repeat = AlertRuleParamsJson.GetInt(document.RootElement, "repeatMinutes", _options.RepeatMinutes);
        if (sustain < 0 || repeat < 0)
        {
            return "sustainSeconds / repeatMinutes 不能为负数";
        }

        return null;
    }

    public AlertRuleDecision Evaluate(AlertRuleContext context)
    {
        if (context.SampleNum is not { } value)
        {
            return AlertRuleDecision.None;
        }

        return context.ParseParams(root => new
        {
            Threshold = AlertRuleParamsJson.GetDouble(root, "threshold", double.NaN),
            Sustain = TimeSpan.FromSeconds(AlertRuleParamsJson.GetInt(root, "sustainSeconds", _options.SustainSeconds)),
            Repeat = TimeSpan.FromMinutes(AlertRuleParamsJson.GetInt(root, "repeatMinutes", _options.RepeatMinutes)),
        }) switch
        {
            var p when double.IsNaN(p.Threshold) => AlertRuleDecision.None,
            var p => SustainGate.Evaluate(
                context.StateJson,
                value > p.Threshold,
                context.NowUtc,
                p.Sustain,
                p.Repeat,
                firstSeenUtc => new AlertMessage(
                    "指标越限告警",
                    $"{AlertRuleText.Noun(context.Target)}「{context.Target.Name}」{AlertRuleText.MetricDisplay(context.Metric)}当前 {AlertRuleText.Number(value, context.Metric)}，超过阈值 {AlertRuleText.Number(p.Threshold, context.Metric)}（已持续 {(context.NowUtc - firstSeenUtc).TotalSeconds:F0} 秒）")),
        };
    }
}

/// <summary>
/// 阈值下限规则：采样值持续低于阈值才告警。参数与阈值上限一致。
/// </summary>
public sealed class ThresholdBelowRuleHandler : IAlertRuleTypeHandler
{
    public const string TypeName = "threshold_below";

    private readonly AlertOptions _options;

    public ThresholdBelowRuleHandler(AlertOptions options) => _options = options;

    public string RuleType => TypeName;

    public bool ScansOnSchedule => false;

    public AlertRuleTypeDescriptor Describe() => new(
        TypeName,
        "阈值下限",
        "指标持续低于阈值时告警",
        RequiresMetric: true,
        AllowsNullMetric: false,
        Params:
        [
            new AlertRuleParamDescriptor("threshold", "number", true, null, "越限阈值"),
            new AlertRuleParamDescriptor("sustainSeconds", "number", false, _options.SustainSeconds.ToString(), "持续秒数（防抖）"),
            new AlertRuleParamDescriptor("repeatMinutes", "number", false, _options.RepeatMinutes.ToString(), "重发间隔（分钟，0 = 事件内不重发）"),
        ]);

    public string? ValidateParams(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        var threshold = AlertRuleParamsJson.GetDouble(document.RootElement, "threshold", double.NaN);
        if (double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            return "threshold 必须是有限数值";
        }

        var sustain = AlertRuleParamsJson.GetInt(document.RootElement, "sustainSeconds", _options.SustainSeconds);
        var repeat = AlertRuleParamsJson.GetInt(document.RootElement, "repeatMinutes", _options.RepeatMinutes);
        if (sustain < 0 || repeat < 0)
        {
            return "sustainSeconds / repeatMinutes 不能为负数";
        }

        return null;
    }

    public AlertRuleDecision Evaluate(AlertRuleContext context)
    {
        if (context.SampleNum is not { } value)
        {
            return AlertRuleDecision.None;
        }

        return context.ParseParams(root => new
        {
            Threshold = AlertRuleParamsJson.GetDouble(root, "threshold", double.NaN),
            Sustain = TimeSpan.FromSeconds(AlertRuleParamsJson.GetInt(root, "sustainSeconds", _options.SustainSeconds)),
            Repeat = TimeSpan.FromMinutes(AlertRuleParamsJson.GetInt(root, "repeatMinutes", _options.RepeatMinutes)),
        }) switch
        {
            var p when double.IsNaN(p.Threshold) => AlertRuleDecision.None,
            var p => SustainGate.Evaluate(
                context.StateJson,
                value < p.Threshold,
                context.NowUtc,
                p.Sustain,
                p.Repeat,
                firstSeenUtc => new AlertMessage(
                    "指标低于阈值告警",
                    $"{AlertRuleText.Noun(context.Target)}「{context.Target.Name}」{AlertRuleText.MetricDisplay(context.Metric)}当前 {AlertRuleText.Number(value, context.Metric)}，低于阈值 {AlertRuleText.Number(p.Threshold, context.Metric)}（已持续 {(context.NowUtc - firstSeenUtc).TotalSeconds:F0} 秒）")),
        };
    }
}

/// <summary>
/// 状态不符规则：enum/string/bool 指标当前值不等于期望值即告警（sustainSeconds 默认 0 = 立即）。
/// 参数：expectedValue（必填）、sustainSeconds、repeatMinutes。
/// </summary>
public sealed class StatusMismatchRuleHandler : IAlertRuleTypeHandler
{
    public const string TypeName = "status_mismatch";

    private readonly AlertOptions _options;

    public StatusMismatchRuleHandler(AlertOptions options) => _options = options;

    public string RuleType => TypeName;

    public bool ScansOnSchedule => false;

    public AlertRuleTypeDescriptor Describe() => new(
        TypeName,
        "状态不符",
        "指标当前值不等于期望值时告警（适用于状态类指标）",
        RequiresMetric: true,
        AllowsNullMetric: false,
        Params:
        [
            new AlertRuleParamDescriptor("expectedValue", "string", true, null, "期望值（完全匹配）"),
            new AlertRuleParamDescriptor("sustainSeconds", "number", false, "0", "持续秒数（防抖，0 = 立即）"),
            new AlertRuleParamDescriptor("repeatMinutes", "number", false, _options.RepeatMinutes.ToString(), "重发间隔（分钟，0 = 事件内不重发）"),
        ]);

    public string? ValidateParams(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        var expected = AlertRuleParamsJson.GetString(document.RootElement, "expectedValue", string.Empty);
        if (expected.Length == 0)
        {
            return "expectedValue 必填";
        }

        var sustain = AlertRuleParamsJson.GetInt(document.RootElement, "sustainSeconds", 0);
        var repeat = AlertRuleParamsJson.GetInt(document.RootElement, "repeatMinutes", _options.RepeatMinutes);
        if (sustain < 0 || repeat < 0)
        {
            return "sustainSeconds / repeatMinutes 不能为负数";
        }

        return null;
    }

    public AlertRuleDecision Evaluate(AlertRuleContext context)
    {
        if (context.SampleText is not { } actual)
        {
            return AlertRuleDecision.None;
        }

        return context.ParseParams(root => new
        {
            Expected = AlertRuleParamsJson.GetString(root, "expectedValue", string.Empty),
            Sustain = TimeSpan.FromSeconds(AlertRuleParamsJson.GetInt(root, "sustainSeconds", 0)),
            Repeat = TimeSpan.FromMinutes(AlertRuleParamsJson.GetInt(root, "repeatMinutes", _options.RepeatMinutes)),
        }) switch
        {
            var p when p.Expected.Length == 0 => AlertRuleDecision.None,
            var p => SustainGate.Evaluate(
                context.StateJson,
                !string.Equals(actual, p.Expected, StringComparison.Ordinal),
                context.NowUtc,
                p.Sustain,
                p.Repeat,
                _ => new AlertMessage(
                    "状态不符告警",
                    $"{AlertRuleText.Noun(context.Target)}「{context.Target.Name}」{AlertRuleText.MetricDisplay(context.Metric)}当前为「{actual}」，不符合期望「{p.Expected}」")),
        };
    }
}

/// <summary>
/// 无数据规则：指标超过窗口无新数据即告警（一次事件一条，恢复即关态，重启不重复）。
/// metric 为空 = 目标级心跳无数据（device 目标，语义与一期设备离线告警一致，迁移来源）。
/// </summary>
public sealed class NoDataRuleHandler : IAlertRuleTypeHandler
{
    public const string TypeName = "no_data";

    private readonly AgentOptions _agentOptions;

    public NoDataRuleHandler(AgentOptions agentOptions) => _agentOptions = agentOptions;

    public string RuleType => TypeName;

    public bool ScansOnSchedule => true;

    public AlertRuleTypeDescriptor Describe() => new(
        TypeName,
        "无数据",
        "指标持续无新数据时告警（不选指标 = 设备心跳离线告警）",
        RequiresMetric: false,
        AllowsNullMetric: true,
        Params:
        [
            new AlertRuleParamDescriptor("windowSeconds", "number", false, ((int)_agentOptions.OfflineAfter.TotalSeconds).ToString(), "无数据判定窗口（秒）"),
        ]);

    public string? ValidateParams(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        var window = AlertRuleParamsJson.GetInt(document.RootElement, "windowSeconds", (int)_agentOptions.OfflineAfter.TotalSeconds);
        return window <= 0 ? "windowSeconds 必须大于 0" : null;
    }

    public AlertRuleDecision Evaluate(AlertRuleContext context)
    {
        var windowSeconds = context.ParseParams(root =>
            AlertRuleParamsJson.GetInt(root, "windowSeconds", (int)_agentOptions.OfflineAfter.TotalSeconds));
        var window = TimeSpan.FromSeconds(windowSeconds);
        var state = AlertStateStore.Read<NoDataState>(context.StateJson);

        // 从未上报过数据：没有"断流"可言，不告警（与一期从未接入的设备不告警一致）
        if (context.LastDataUtc is not { } lastDataUtc)
        {
            return AlertRuleDecision.None;
        }

        if (context.NowUtc - lastDataUtc <= window)
        {
            // 数据恢复：关闭事件
            return state is not null
                ? new AlertRuleDecision(AlertRuleAction.Clear)
                : AlertRuleDecision.None;
        }

        if (state is not null)
        {
            // 已告警过（重启后也不重复）
            return AlertRuleDecision.None;
        }

        var message = context.Rule.Metric is null
            ? new AlertMessage(
                "设备离线告警",
                $"{AlertRuleText.Noun(context.Target)}「{context.Target.Name}」已离线（超过 {window.TotalSeconds:F0} 秒未上报心跳）")
            : new AlertMessage(
                "指标无数据告警",
                $"{AlertRuleText.Noun(context.Target)}「{context.Target.Name}」指标 {AlertRuleText.MetricDisplay(context.Metric)} 已超过 {window.TotalSeconds:F0} 秒无数据");
        return new AlertRuleDecision(
            AlertRuleAction.Fire,
            JsonSerializer.Serialize(new NoDataState(context.NowUtc)),
            message);
    }

    private sealed record NoDataState(DateTimeOffset AlertedAtUtc);
}
