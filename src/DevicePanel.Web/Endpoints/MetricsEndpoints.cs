using System.Globalization;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record SeriesResponse(
    long TargetId,
    string Granularity,
    string FromUtc,
    string ToUtc,
    IReadOnlyList<MetricSeriesResponse> Series);

public sealed record MetricSeriesResponse(string Key, IReadOnlyList<SeriesPoint> Points);

public sealed record SeriesPoint(string T, double? V);

public sealed record MetricOverviewItem(
    string Key,
    string ValueType,
    string DisplayName,
    string Unit,
    bool BuiltIn,
    DateTimeOffset? LatestTimeUtc,
    double? LatestValueNum,
    string? LatestValueText);

public sealed record RegisterMetricKeyRequest(string? Key, string? ValueType, string? DisplayName, string? Unit);

public sealed record UpdateMetricKeyRequest(string? DisplayName, string? Unit);

public static class MetricsEndpoints
{
    public const string Raw = "raw";
    public const string Hour = "hour";
    public const string Day = "day";

    private const int MaxSeriesKeys = 10;

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

        // MetricKey 注册表（约束 A）：新增一种指标 = 注册 key + 类型
        metrics.MapGet("/keys", (IMetricKeyRegistry registry) => Results.Ok(registry.List().Select(ToKeyResponse)));

        metrics.MapPost("/keys", ([FromBody] RegisterMetricKeyRequest request, IMetricKeyRegistry registry) =>
        {
            var key = MetricKeyRegistry.NormalizeKey(request.Key);
            if (key is null)
            {
                return Results.BadRequest(new { error = "指标 key 不合法：小写字母开头，仅小写字母/数字/下划线，段间用 '.'，最长 64 字符" });
            }

            if (request.ValueType is null || !MetricValueTypeExtensions.TryFromStorage(request.ValueType.Trim(), out var valueType))
            {
                return Results.BadRequest(new { error = "值类型仅支持 number / enum / string / bool" });
            }

            if (!TryNormalizeDisplay(request.DisplayName, out var displayName, out var displayError))
            {
                return Results.BadRequest(new { error = displayError });
            }

            var unit = (request.Unit ?? string.Empty).Trim();
            if (unit.Length > 20)
            {
                return Results.BadRequest(new { error = "单位不能超过 20 个字符" });
            }

            if (registry.Get(key) is not null)
            {
                return Results.BadRequest(new { error = $"指标 {key} 已注册" });
            }

            var registered = registry.Register(key, valueType, displayName, unit);
            return Results.Json(ToKeyResponse(registered), statusCode: StatusCodes.Status201Created);
        });

        metrics.MapPut("/keys/{**key}", (string key, [FromBody] UpdateMetricKeyRequest request, IMetricKeyRegistry registry) =>
        {
            var normalized = MetricKeyRegistry.NormalizeKey(key);
            if (normalized is null || registry.Get(normalized) is null)
            {
                return Results.NotFound(new { error = "指标不存在" });
            }

            if (!TryNormalizeDisplay(request.DisplayName, out var displayName, out var displayError))
            {
                return Results.BadRequest(new { error = displayError });
            }

            var unit = (request.Unit ?? string.Empty).Trim();
            if (unit.Length > 20)
            {
                return Results.BadRequest(new { error = "单位不能超过 20 个字符" });
            }

            var updated = registry.UpdateDisplay(normalized, displayName, unit);
            return updated is null ? Results.NotFound(new { error = "指标不存在" }) : Results.Ok(ToKeyResponse(updated));
        });

        metrics.MapDelete("/keys/{**key}", (
            string key,
            IMetricKeyRegistry registry,
            IMetricsStore store,
            IAlertRuleStore rules) =>
        {
            var normalized = MetricKeyRegistry.NormalizeKey(key);
            if (normalized is null || registry.Get(normalized) is null)
            {
                return Results.NotFound(new { error = "指标不存在" });
            }

            if (registry.Get(normalized)!.BuiltIn)
            {
                return Results.BadRequest(new { error = "内置指标不可删除" });
            }

            if (store.HasAnySample(normalized))
            {
                return Results.BadRequest(new { error = "该指标已有上报数据，不可删除（可先停止上报并清理数据后再删除）" });
            }

            if (rules.CountByMetricKey(normalized) > 0)
            {
                return Results.BadRequest(new { error = "该指标已配置告警规则，请先删除规则" });
            }

            return registry.Delete(normalized) ? Results.NoContent() : Results.NotFound(new { error = "指标不存在" });
        });

        // 目标已上报指标总览（最新值 + 注册元数据），供目标详情与规则创建使用
        metrics.MapGet("/{targetId:long}/overview", (long targetId, IMetricsStore store, IMetricKeyRegistry registry, ITargetRegistry targets) =>
        {
            if (targets.Get(targetId) is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            var items = store.ListReportedKeys(targetId)
                .Select(key =>
                {
                    var info = registry.Get(key);
                    var latest = store.GetLatest(targetId, key);
                    return new MetricOverviewItem(
                        key,
                        info?.ValueType.ToStorage() ?? "number",
                        info?.DisplayName ?? key,
                        info?.Unit ?? string.Empty,
                        info?.BuiltIn ?? false,
                        latest?.TimeUtc,
                        latest?.ValueNum,
                        latest?.ValueText);
                })
                .ToList();
            return Results.Ok(items);
        });

        // 按来源可用指标（TOB-374 ①）：优先该来源已上报且已注册的 key；无上报数据时按目标类型回退到内置 key
        metrics.MapGet("/{targetId:long}/available", (long targetId, IMetricsStore store, IMetricKeyRegistry registry, ITargetRegistry targets) =>
        {
            var target = targets.Get(targetId);
            if (target is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            var reported = store.ListReportedKeys(targetId).Where(key => registry.Get(key) is not null).ToList();
            var keys = reported.Count > 0
                ? reported
                : MetricKeys.ForTargetType(target.Type).Where(key => registry.Get(key) is not null).ToList();
            return Results.Ok(keys.Select(key => ToKeyResponse(registry.Get(key)!)));
        });

        metrics.MapGet("/{targetId:long}/series", (
            long targetId,
            [FromQuery] string? keys,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromQuery] string? granularity,
            IMetricsStore store,
            IMetricKeyRegistry registry,
            ITargetRegistry targets,
            TimeProvider clock) =>
        {
            if (targets.Get(targetId) is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            if (string.IsNullOrWhiteSpace(keys))
            {
                return Results.BadRequest(new { error = "请指定要查询的指标 key（逗号分隔）" });
            }

            var requestedKeys = keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxSeriesKeys)
                .ToList();
            if (requestedKeys.Count == 0)
            {
                return Results.BadRequest(new { error = "请指定要查询的指标 key（逗号分隔）" });
            }

            foreach (var key in requestedKeys)
            {
                if (registry.Get(key) is null)
                {
                    return Results.BadRequest(new { error = $"指标 {key} 未注册" });
                }
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
            var series = requestedKeys.Select(key =>
            {
                var points = resolved switch
                {
                    Hour => store.QueryHourly(targetId, key, fromUtc, toUtc).Select(b => new SeriesPoint(FormatUtc(b.TimeUtc), b.Avg)),
                    Day => store.QueryDaily(targetId, key, fromUtc, toUtc).Select(b => new SeriesPoint(FormatUtc(b.TimeUtc), b.Avg)),
                    _ => store.QueryRaw(targetId, key, fromUtc, toUtc).Select(s => new SeriesPoint(FormatUtc(s.TimeUtc), s.ValueNum)),
                };
                return new MetricSeriesResponse(key, points.ToList());
            });

            return Results.Ok(new SeriesResponse(
                targetId,
                resolved,
                FormatUtc(fromUtc),
                FormatUtc(toUtc),
                series.ToList()));
        });

        return endpoints;
    }

    private static object ToKeyResponse(MetricKeyInfo info) => new
    {
        key = info.Key,
        valueType = info.ValueType.ToStorage(),
        displayName = info.DisplayName,
        unit = info.Unit,
        builtIn = info.BuiltIn,
        createdAtUtc = info.CreatedAtUtc,
        updatedAtUtc = info.UpdatedAtUtc,
    };

    private static bool TryNormalizeDisplay(string? displayName, out string normalized, out string error)
    {
        normalized = (displayName ?? string.Empty).Trim();
        if (normalized.Length is 0 or > 50)
        {
            error = "显示名必填且不能超过 50 个字符";
            return false;
        }

        error = string.Empty;
        return true;
    }

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
