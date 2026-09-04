using System.Text.Json;
using DevicePanel.Web.Metrics;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 无数据规则（时间驱动）：{"minutes": N} —— 目标超过 N 分钟无该指标的新样本即触发（防抖另计），
/// 数据恢复上报即恢复。从未上报过的指标不触发（避免误配规则即告警风暴）。
/// </summary>
public sealed class NoDataRuleType : IAlertRuleType
{
    public const string TypeIdValue = "no_data";

    public const int MinMinutes = 1;
    public const int MaxMinutes = 1440;

    public string TypeId => TypeIdValue;

    public string DisplayName => "无数据";

    public string AlertTitle => "指标无数据告警";

    public string Description => $"目标超过设定分钟数（{MinMinutes}-{MaxMinutes}）无该指标新样本即触发，数据恢复上报即恢复；从未上报过不触发";

    public IReadOnlyList<MetricValueType> SupportedValueTypes { get; } =
        [MetricValueType.Number, MetricValueType.Enum, MetricValueType.String, MetricValueType.Bool];

    public bool SampleDriven => false;

    public string? ValidateParameters(string parametersJson)
    {
        if (!TryReadMinutes(parametersJson, out _, out var error))
        {
            return error;
        }

        return null;
    }

    public bool IsViolated(string parametersJson, MetricSample sample) => false;

    public bool IsSweepViolated(string parametersJson, TimeSpan? dataAge) =>
        TryReadMinutes(parametersJson, out var minutes, out _)
        && dataAge is { } age
        && age >= TimeSpan.FromMinutes(minutes);

    public string DescribeViolation(string parametersJson, MetricSample? latestSample, string unit, TimeSpan sustained)
    {
        var lastSample = latestSample is { } sample ? sample.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "无";
        var minutes = TryReadMinutes(parametersJson, out var parsed, out _) ? parsed : 0;
        return $"已超过 {minutes} 分钟无数据上报（最后样本 {lastSample}，数据缺失已持续 {sustained.TotalMinutes:F0} 分钟）";
    }

    private static bool TryReadMinutes(string parametersJson, out int minutes, out string error)
    {
        minutes = 0;
        error = string.Empty;
        double? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(parametersJson) is { ValueKind: JsonValueKind.Object } document
                && document.TryGetProperty("minutes", out var element)
                && element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is not { } value || value < MinMinutes || value > MaxMinutes || value != Math.Floor(value))
        {
            error = $"参数不合法：minutes 必须是 {MinMinutes}-{MaxMinutes} 之间的整数";
            return false;
        }

        minutes = (int)value;
        return true;
    }
}

/// <summary>
/// 状态不符规则（采样驱动）：{"expected": "期望值"} —— 样本值与期望不符即视为违规（bool 归一为 true/false 比较），
/// 回到期望值即恢复。适用于 bool/enum/string 类型指标。
/// </summary>
public sealed class StateMismatchRuleType : IAlertRuleType
{
    public const string TypeIdValue = "state_mismatch";

    public string TypeId => TypeIdValue;

    public string DisplayName => "状态不符";

    public string AlertTitle => "目标状态告警";

    public string Description => "指标状态值与期望值不符即触发（bool 写 true/false，枚举/字符串写精确值），回到期望值即恢复";

    public IReadOnlyList<MetricValueType> SupportedValueTypes { get; } =
        [MetricValueType.Enum, MetricValueType.String, MetricValueType.Bool];

    public bool SampleDriven => true;

    public string? ValidateParameters(string parametersJson)
    {
        if (!TryReadExpected(parametersJson, out _, out var error))
        {
            return error;
        }

        return null;
    }

    public bool IsViolated(string parametersJson, MetricSample sample) =>
        TryReadExpected(parametersJson, out var expected, out _)
        && !string.Equals(sample.ValueText?.Trim(), expected, StringComparison.Ordinal);

    public bool IsSweepViolated(string parametersJson, TimeSpan? dataAge) => false;

    public string DescribeViolation(string parametersJson, MetricSample? latestSample, string unit, TimeSpan sustained) =>
        TryReadExpected(parametersJson, out var expected, out _)
            ? $"当前状态为 {latestSample?.ValueText ?? "（空）"}，与期望 {expected} 不符（已持续 {sustained.TotalSeconds:F0} 秒）"
            : $"当前状态为 {latestSample?.ValueText ?? "（空）"}，与期望值不符（已持续 {sustained.TotalSeconds:F0} 秒）";

    private static bool TryReadExpected(string parametersJson, out string expected, out string error)
    {
        expected = string.Empty;
        error = string.Empty;
        string? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(parametersJson) is { ValueKind: JsonValueKind.Object } document
                && document.TryGetProperty("expected", out var element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (string.IsNullOrWhiteSpace(parsed))
        {
            error = "参数不合法：expected 必须是非空字符串";
            return false;
        }

        expected = parsed.Trim();
        return true;
    }
}
