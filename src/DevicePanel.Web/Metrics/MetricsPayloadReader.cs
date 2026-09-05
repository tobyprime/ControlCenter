using System.Text.Json;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// metrics.report 负载解析：一期五字段（cpu/mem/disk/netRx/netTx）必须齐备且为有限数值，否则整体拒绝（宁缺毋错）；
/// 可选 extra 对象允许附加自定义指标（key → number/bool/string 标量），注册后的扩展指标经同一管道入库。
/// 一期字段映射到注册 key：cpu→cpu、mem→mem、disk→disk、netRx→net_rx、netTx→net_tx（agent 线上协议不变）。
/// </summary>
public static class MetricsPayloadReader
{
    public static bool TryParse(JsonElement payload, DateTimeOffset receivedAtUtc, out IReadOnlyList<(string Key, MetricSample Sample)> samples)
    {
        samples = [];
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var timeUtc = receivedAtUtc;
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

        var list = new List<(string, MetricSample)>
        {
            (MetricKeys.Cpu, new MetricSample(timeUtc, cpuValue, null)),
            (MetricKeys.Mem, new MetricSample(timeUtc, memValue, null)),
            (MetricKeys.Disk, new MetricSample(timeUtc, diskValue, null)),
            (MetricKeys.NetRx, new MetricSample(timeUtc, netRxValue, null)),
            (MetricKeys.NetTx, new MetricSample(timeUtc, netTxValue, null)),
        };

        if (payload.TryGetProperty("extra", out var extra))
        {
            if (extra.ValueKind != JsonValueKind.Object || !TryReadExtra(extra, timeUtc, list))
            {
                return false;
            }
        }

        samples = list;
        return true;
    }

    private static bool TryReadExtra(JsonElement extra, DateTimeOffset timeUtc, List<(string Key, MetricSample Sample)> target)
    {
        foreach (var property in extra.EnumerateObject())
        {
            var key = MetricKeyRegistry.NormalizeKey(property.Name);
            if (key is null)
            {
                return false;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number when ReadFinite(property.Value) is { } number:
                    target.Add((key, new MetricSample(timeUtc, number, null)));
                    break;
                case JsonValueKind.True:
                    target.Add((key, new MetricSample(timeUtc, 1, "true")));
                    break;
                case JsonValueKind.False:
                    target.Add((key, new MetricSample(timeUtc, 0, "false")));
                    break;
                case JsonValueKind.String:
                    target.Add((key, new MetricSample(timeUtc, null, property.Value.GetString())));
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static double? ReadNumber(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return ReadFinite(value);
    }

    private static double? ReadFinite(JsonElement value)
    {
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
