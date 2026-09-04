using System.Globalization;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

/// <summary>统一序列响应：单指标单序列（与一期 /api/metrics/{deviceId}/series 的多列形状解耦）。</summary>
public sealed record TargetSeriesResponse(
    long TargetId,
    string Metric,
    string Granularity,
    string FromUtc,
    string ToUtc,
    IReadOnlyList<TargetSeriesPoint> Points);

public sealed record TargetSeriesPoint(string T, double? Value, string? Text);

public static class TargetSeriesEndpoints
{
    /// <summary>legacy 五键（设备目标专属）→ 一期列式存储；其余注册键 → 通用类型化序列存储。</summary>
    public static bool IsLegacyMetric(string metric) => metric is "cpu" or "mem" or "disk" or "net_rx" or "net_tx";

    public static IEndpointRouteBuilder MapTargetSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/targets/{targetId:long}/series", (
            long targetId,
            [FromQuery] string? metric,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? granularity,
            ITargetStore targets,
            IDeviceRegistry devices,
            IMetricsStore metricsStore,
            IMetricValueStore metricValues,
            IMetricKeyRegistry metricKeys,
            TimeProvider clock) =>
        {
            var target = targets.Get(targetId);
            if (target is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            if (string.IsNullOrWhiteSpace(metric))
            {
                return Results.BadRequest(new { error = "metric 必填（指标键见 /api/metric-keys）" });
            }

            if (!TryParseRange(from, to, clock, out var fromUtc, out var toUtc, out var error))
            {
                return Results.BadRequest(new { error });
            }

            if (granularity is not (null or "auto" or MetricsEndpoints.Raw or MetricsEndpoints.Hour or MetricsEndpoints.Day))
            {
                return Results.BadRequest(new { error = "granularity 仅支持 auto/raw/hour/day" });
            }

            var resolved = MetricsEndpoints.ResolveGranularity(toUtc - fromUtc, granularity);
            List<TargetSeriesPoint> points;
            if (target.IsDevice && IsLegacyMetric(metric))
            {
                // legacy 五键走一期列式存储（历史数据无损，聚合口径不变）
                var deviceId = target.DeviceId!.Value;
                points = resolved switch
                {
                    MetricsEndpoints.Hour => metricsStore.QueryHourly(deviceId, fromUtc, toUtc)
                        .Select(b => FromLegacyBucket(metric, b)).ToList(),
                    MetricsEndpoints.Day => metricsStore.QueryDaily(deviceId, fromUtc, toUtc)
                        .Select(b => FromLegacyBucket(metric, b)).ToList(),
                    _ => metricsStore.QueryRaw(deviceId, fromUtc, toUtc)
                        .Select(p => new TargetSeriesPoint(FormatUtc(p.TimeUtc), SelectLegacy(metric, p), null)).ToList(),
                };
            }
            else
            {
                if (metricKeys.Get(metric) is null)
                {
                    return Results.NotFound(new { error = $"指标 {metric} 未注册" });
                }

                points = resolved switch
                {
                    MetricsEndpoints.Hour => metricValues.QueryBucketed(targetId, metric, MetricValueStore.GranularityHour, fromUtc, toUtc)
                        .Select(b => new TargetSeriesPoint(FormatUtc(b.TimeUtc), b.AvgNum, b.LastText)).ToList(),
                    MetricsEndpoints.Day => metricValues.QueryBucketed(targetId, metric, MetricValueStore.GranularityDay, fromUtc, toUtc)
                        .Select(b => new TargetSeriesPoint(FormatUtc(b.TimeUtc), b.AvgNum, b.LastText)).ToList(),
                    _ => metricValues.QueryRaw(targetId, metric, fromUtc, toUtc)
                        .Select(p => new TargetSeriesPoint(FormatUtc(p.TimeUtc), p.NumValue, p.TextValue)).ToList(),
                };
            }

            return Results.Ok(new TargetSeriesResponse(
                targetId, metric, resolved, FormatUtc(fromUtc), FormatUtc(toUtc), points));
        });

        return endpoints;
    }

    private static double? SelectLegacy(string metric, MetricsPoint p) => metric switch
    {
        "cpu" => p.Cpu,
        "mem" => p.Mem,
        "disk" => p.Disk,
        "net_rx" => p.NetRx,
        "net_tx" => p.NetTx,
        _ => null,
    };

    private static TargetSeriesPoint FromLegacyBucket(string metric, MetricsBucket b) => new(
        FormatUtc(b.TimeUtc),
        metric switch
        {
            "cpu" => b.CpuAvg,
            "mem" => b.MemAvg,
            "disk" => b.DiskAvg,
            "net_rx" => b.NetRxAvg,
            "net_tx" => b.NetTxAvg,
            _ => null,
        },
        null);

    private static bool TryParseRange(string? from, string? to, TimeProvider clock, out DateTimeOffset fromUtc, out DateTimeOffset toUtc, out string error)
    {
        var nowUtc = clock.GetUtcNow();
        toUtc = nowUtc;
        fromUtc = nowUtc.AddHours(-24);
        error = string.Empty;

        if (!string.IsNullOrEmpty(from) && !TryParseUtc(from, out fromUtc))
        {
            error = "from 不是有效时间（ISO-8601，如 2026-09-01T00:00:00Z）";
            return false;
        }

        if (!string.IsNullOrEmpty(to) && !TryParseUtc(to, out toUtc))
        {
            error = "to 不是有效时间（ISO-8601，如 2026-09-03T00:00:00Z）";
            return false;
        }

        if (fromUtc >= toUtc)
        {
            error = "时间范围无效：from 必须早于 to";
            return false;
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(366))
        {
            error = "时间范围过长：最长 366 天";
            return false;
        }

        return true;
    }

    private static bool TryParseUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed);

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
