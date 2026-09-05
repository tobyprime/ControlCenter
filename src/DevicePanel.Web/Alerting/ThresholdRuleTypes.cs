using System.Text.Json;
using DevicePanel.Web.Metrics;

namespace DevicePanel.Web.Alerting;

/// <summary>参数载体：{"threshold": 数值}（百分比/绝对值语义由指标 unit 决定，规则类型不解释）。</summary>
public sealed class ThresholdAboveRuleType : IAlertRuleType
{
    public const string TypeIdValue = "threshold_above";

    public string TypeId => TypeIdValue;

    public string DisplayName => "阈值上越限";

    public string AlertTitle => "指标越限告警";

    public string Description => "指标值持续超过设定阈值（严格大于）即触发，回落即恢复";

    public IReadOnlyList<MetricValueType> SupportedValueTypes { get; } = [MetricValueType.Number];

    public bool SampleDriven => true;

    public string? ValidateParameters(string parametersJson)
    {
        if (!TryReadThreshold(parametersJson, out _, out var error))
        {
            return error;
        }

        return null;
    }

    public bool IsViolated(string parametersJson, MetricSample sample) =>
        TryReadThreshold(parametersJson, out var threshold, out _)
        && sample.ValueNum is { } value
        && double.IsFinite(value)
        && value > threshold;

    public bool IsSweepViolated(string parametersJson, TimeSpan? dataAge) => false;

    public string DescribeViolation(string parametersJson, MetricSample? latestSample, string unit, TimeSpan sustained) =>
        TryReadThreshold(parametersJson, out var threshold, out _)
            ? $"当前 {Format(latestSample)}{unit}，超过阈值 {threshold:F1}{unit}（已持续 {sustained.TotalSeconds:F0} 秒）"
            : $"当前 {Format(latestSample)}{unit}，超过阈值（已持续 {sustained.TotalSeconds:F0} 秒）";

    private static string Format(MetricSample? sample) => sample?.ValueNum is { } value ? value.ToString("F1") : "?";

    private static bool TryReadThreshold(string parametersJson, out double threshold, out string error)
    {
        threshold = 0;
        error = string.Empty;
        double? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(parametersJson) is { ValueKind: JsonValueKind.Object } document
                && document.TryGetProperty("threshold", out var element)
                && element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is not { } value || !double.IsFinite(value))
        {
            error = "参数不合法：threshold 必须是数值";
            return false;
        }

        threshold = value;
        return true;
    }
}

/// <summary>参数载体：{"threshold": 数值}。持续低于阈值触发，回升即恢复。</summary>
public sealed class ThresholdBelowRuleType : IAlertRuleType
{
    public const string TypeIdValue = "threshold_below";

    public string TypeId => TypeIdValue;

    public string DisplayName => "阈值下越限";

    public string AlertTitle => "指标越限告警";

    public string Description => "指标值持续低于设定阈值（严格小于）即触发，回升即恢复";

    public IReadOnlyList<MetricValueType> SupportedValueTypes { get; } = [MetricValueType.Number];

    public bool SampleDriven => true;

    public string? ValidateParameters(string parametersJson)
    {
        if (!TryReadThreshold(parametersJson, out _, out var error))
        {
            return error;
        }

        return null;
    }

    public bool IsViolated(string parametersJson, MetricSample sample) =>
        TryReadThreshold(parametersJson, out var threshold, out _)
        && sample.ValueNum is { } value
        && double.IsFinite(value)
        && value < threshold;

    public bool IsSweepViolated(string parametersJson, TimeSpan? dataAge) => false;

    public string DescribeViolation(string parametersJson, MetricSample? latestSample, string unit, TimeSpan sustained) =>
        TryReadThreshold(parametersJson, out var threshold, out _)
            ? $"当前 {Format(latestSample)}{unit}，低于阈值 {threshold:F1}{unit}（已持续 {sustained.TotalSeconds:F0} 秒）"
            : $"当前 {Format(latestSample)}{unit}，低于阈值（已持续 {sustained.TotalSeconds:F0} 秒）";

    private static string Format(MetricSample? sample) => sample?.ValueNum is { } value ? value.ToString("F1") : "?";

    private static bool TryReadThreshold(string parametersJson, out double threshold, out string error)
    {
        threshold = 0;
        error = string.Empty;
        double? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(parametersJson) is { ValueKind: JsonValueKind.Object } document
                && document.TryGetProperty("threshold", out var element)
                && element.ValueKind == JsonValueKind.Number
                ? element.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is not { } value || !double.IsFinite(value))
        {
            error = "参数不合法：threshold 必须是数值";
            return false;
        }

        threshold = value;
        return true;
    }
}
