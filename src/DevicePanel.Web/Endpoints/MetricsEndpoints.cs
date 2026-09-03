using System.Globalization;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record SeriesResponse(
    long DeviceId,
    string Granularity,
    string FromUtc,
    string ToUtc,
    IReadOnlyList<SeriesPoint> Points);

public sealed record SeriesPoint(string T, double Cpu, double Mem, double Disk, double NetRx, double NetTx);

public static class MetricsEndpoints
{
    public const string Raw = "raw";
    public const string Hour = "hour";
    public const string Day = "day";

    /// <summary>明细/聚合联动的默认策略：≤6h 看明细，≤10 天用小时聚合，更长用天聚合。</summary>
    public static string ResolveGranularity(TimeSpan span, string? requested)
    {
        if (!string.IsNullOrEmpty(requested) && requested != "auto")
        {
            return requested;
        }

        return span <= TimeSpan.FromHours(6) ? Raw
            : span <= TimeSpan.FromDays(10) ? Hour
            : Day;
    }

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var metrics = endpoints.MapGroup("/api/metrics");

        metrics.MapGet("/{deviceId:long}/series", (
            long deviceId,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? granularity,
            IMetricsStore store,
            IDeviceRegistry devices,
            TimeProvider clock) =>
        {
            if (devices.Get(deviceId) is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            if (!TryParseRange(from, to, clock, out var fromUtc, out var toUtc, out var error))
            {
                return Results.BadRequest(new { error });
            }

            if (granularity is not (null or "auto" or Raw or Hour or Day))
            {
                return Results.BadRequest(new { error = "granularity 仅支持 auto/raw/hour/day" });
            }

            var resolved = ResolveGranularity(toUtc - fromUtc, granularity);
            var points = resolved switch
            {
                Hour => store.QueryHourly(deviceId, fromUtc, toUtc).Select(ToPoint).ToList(),
                Day => store.QueryDaily(deviceId, fromUtc, toUtc).Select(ToPoint).ToList(),
                _ => store.QueryRaw(deviceId, fromUtc, toUtc).Select(ToPoint).ToList(),
            };

            return Results.Ok(new SeriesResponse(
                deviceId,
                resolved,
                FormatUtc(fromUtc),
                FormatUtc(toUtc),
                points));
        });

        return endpoints;
    }

    private static SeriesPoint ToPoint(MetricsPoint p) => new(FormatUtc(p.TimeUtc), p.Cpu, p.Mem, p.Disk, p.NetRx, p.NetTx);

    private static SeriesPoint ToPoint(MetricsBucket b) => new(FormatUtc(b.TimeUtc), b.CpuAvg, b.MemAvg, b.DiskAvg, b.NetRxAvg, b.NetTxAvg);

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
