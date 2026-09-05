using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record ProbeMappingRequest(string? MetricKey, string? JsonPath, string? ValueType, string? DisplayName, string? Unit);

public sealed record ProbeUpsertRequest(string? Url, int? IntervalSeconds, IReadOnlyList<ProbeMappingRequest>? Mappings);

public sealed record ProbeMappingResponse(string MetricKey, string JsonPath, string ValueType, string DisplayName, string Unit);

public sealed record ProbeConfigResponse(
    string Url,
    int IntervalSeconds,
    IReadOnlyList<ProbeMappingResponse> Mappings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 探针配置请求归一化 + 校验（创建 service 目标与 PUT /probe 共用）。
/// 映射的未知 metric key 经注册管道自动注册（约束 A）；已注册 key 类型不一致即拒绝，防止同一指标双语义。
/// </summary>
public static class ProbeRequests
{
    public const int MaxMappings = 20;

    public static bool TryNormalize(
        ProbeUpsertRequest? request,
        ProbeOptions options,
        IMetricKeyRegistry metricKeys,
        out string url,
        out int intervalSeconds,
        out List<ProbeMetricMapping> mappings,
        out string error)
    {
        url = string.Empty;
        intervalSeconds = options.DefaultIntervalSeconds;
        mappings = [];

        if (request is null)
        {
            error = "service 目标必须配置探针";
            return false;
        }

        url = (request.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "探针 URL 必须是 http(s) 绝对地址";
            return false;
        }

        intervalSeconds = request.IntervalSeconds ?? options.DefaultIntervalSeconds;
        if (intervalSeconds < options.MinIntervalSeconds || intervalSeconds > options.MaxIntervalSeconds)
        {
            error = $"探针间隔需在 {options.MinIntervalSeconds}~{options.MaxIntervalSeconds} 秒之间";
            return false;
        }

        var rawMappings = request.Mappings ?? [];
        if (rawMappings.Count > MaxMappings)
        {
            error = $"提取映射不能超过 {MaxMappings} 条";
            return false;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawMappings)
        {
            var key = MetricKeyRegistry.NormalizeKey(raw.MetricKey);
            if (key is null)
            {
                error = $"提取映射的指标名不合法：{raw.MetricKey}";
                return false;
            }

            if (!seenKeys.Add(key))
            {
                error = $"提取映射的指标名重复：{key}";
                return false;
            }

            try
            {
                JsonPath.Validate(raw.JsonPath ?? string.Empty);
            }
            catch (ArgumentException)
            {
                error = $"JSONPath 语法不合法：{raw.JsonPath}";
                return false;
            }

            if (!MetricValueTypeExtensions.TryFromStorage((raw.ValueType ?? string.Empty).Trim(), out var valueType)
                || valueType is MetricValueType.Bool)
            {
                // bool 状态类指标（status 等）由探针内置产出，映射仅开放 number/enum/string
                error = $"提取值类型仅支持 number/enum/string：{raw.ValueType}";
                return false;
            }

            var displayName = (raw.DisplayName ?? string.Empty).Trim();
            var unit = (raw.Unit ?? string.Empty).Trim();
            var existing = metricKeys.Get(key);
            if (existing is null)
            {
                metricKeys.Register(key, valueType, displayName.Length > 0 ? displayName : key, unit);
            }
            else if (existing.ValueType != valueType)
            {
                error = $"指标 {key} 已注册为 {existing.ValueType.ToStorage()}，与本次映射类型 {valueType.ToStorage()} 不一致";
                return false;
            }

            mappings.Add(new ProbeMetricMapping(key, raw.JsonPath!.Trim(), valueType, displayName.Length > 0 ? displayName : key, unit));
        }

        error = string.Empty;
        return true;
    }

    public static ProbeConfigResponse ToResponse(ProbeConfig config) => new(
        config.Url,
        config.IntervalSeconds,
        config.Mappings.Select(m => new ProbeMappingResponse(m.MetricKey, m.JsonPath, m.ValueType.ToStorage(), m.DisplayName, m.Unit)).ToList(),
        config.CreatedAtUtc,
        config.UpdatedAtUtc);
}

public static class ProbeEndpoints
{
    public static IEndpointRouteBuilder MapProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var probes = endpoints.MapGroup("/api/targets/{id:long}/probe");

        probes.MapGet("/", (long id, ITargetRegistry registry, IProbeConfigStore configs) =>
        {
            if (registry.Get(id) is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            var config = configs.Get(id);
            return config is null ? Results.NoContent() : Results.Ok(ProbeRequests.ToResponse(config));
        });

        probes.MapPut("/", (
            long id,
            [FromBody] ProbeUpsertRequest request,
            ITargetRegistry registry,
            IProbeConfigStore configs,
            IMetricKeyRegistry metricKeys,
            ProbeOptions options) =>
        {
            var target = registry.Get(id);
            if (target is null)
            {
                return Results.NotFound(new { error = "目标不存在" });
            }

            if (target.Type != TargetTypes.Service)
            {
                return Results.BadRequest(new { error = "仅 service 目标支持探针配置" });
            }

            if (!ProbeRequests.TryNormalize(request, options, metricKeys, out var url, out var interval, out var mappings, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var saved = configs.Save(id, url, interval, mappings);
            return Results.Ok(ProbeRequests.ToResponse(saved));
        });

        return endpoints;
    }
}
