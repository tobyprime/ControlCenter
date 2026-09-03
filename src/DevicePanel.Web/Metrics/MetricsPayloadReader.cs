using System.Text.Json;

namespace DevicePanel.Web.Metrics;

/// <summary>metrics.report 负载解析：五个字段必须齐备且为有限数值，否则整体拒绝（ 宁缺毋错）。</summary>
public static class MetricsPayloadReader
{
    public static bool TryParse(JsonElement payload, out MetricsPoint point)
    {
        point = new MetricsPoint(default, 0, 0, 0, 0, 0);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double? cpu = ReadNumber(payload, "cpu");
        double? mem = ReadNumber(payload, "mem");
        double? disk = ReadNumber(payload, "disk");
        double? netRx = ReadNumber(payload, "netRx");
        double? netTx = ReadNumber(payload, "netTx");
        if (cpu is not { } cpuValue || mem is not { } memValue || disk is not { } diskValue ||
            netRx is not { } netRxValue || netTx is not { } netTxValue)
        {
            return false;
        }

        point = new MetricsPoint(default, cpuValue, memValue, diskValue, netRxValue, netTxValue);
        return true;
    }

    private static double? ReadNumber(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        double number;
        try
        {
            number = value.GetDouble();
        }
        catch (FormatException)
        {
            return null;
        }

        return double.IsFinite(number) ? number : null;
    }
}
