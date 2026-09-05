using DevicePanel.Web.Collectors;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record PullMappingRequest(string? MetricKey, string? JsonPath, string? ValueType, string? DisplayName, string? Unit);

public sealed record PullUpsertRequest(string? Url, int? IntervalSeconds, IReadOnlyList<PullMappingRequest>? Mappings);

public sealed record PullMappingResponse(string MetricKey, string JsonPath, string ValueType, string DisplayName, string Unit);

public sealed record PullConfigResponse(
    string Url,
    int IntervalSeconds,
    IReadOnlyList<PullMappingResponse> Mappings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// pull 采集器配置请求归一化 + 校验（创建 pull 采集器与 PUT /pull 共用）。
/// 映射的未知 metric key 经注册管道自动注册（约束 A）；已注册 key 类型不一致即拒绝，防止同一指标双语义。
/// </summary>
public static class PullCollectorRequests
{
    public const int MaxMappings = 20;

    public static bool TryNormalize(
        PullUpsertRequest? request,
        ProbeOptions options,
        IMetricKeyRegistry metricKeys,
        out string url,
        out int intervalSeconds,
        out List<PullMetricMapping> mappings,
        out string error)
    {
        url = string.Empty;
        intervalSeconds = options.DefaultIntervalSeconds;
        mappings = [];

        if (request is null)
        {
            error = "pull 采集器必须配置轮询";
            return false;
        }

        url = (request.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "轮询 URL 必须是 http(s) 绝对地址";
            return false;
        }

        intervalSeconds = request.IntervalSeconds ?? options.DefaultIntervalSeconds;
        if (intervalSeconds < options.MinIntervalSeconds || intervalSeconds > options.MaxIntervalSeconds)
        {
            error = $"轮询间隔需在 {options.MinIntervalSeconds}~{options.MaxIntervalSeconds} 秒之间";
            return false;
        }

        var rawMappings = request.Mappings ?? [];
        if (rawMappings.Count > MaxMappings)
        {
            error = $"提取映射不能超过 {MaxMappings} 条";
            return false;
        }

        // 先整体校验、全部通过后再统一注册：校验失败不得在注册表留下已注册的孤儿 key（审查修复 TOB-376）
        var pendingRegistrations = new List<(string Key, MetricValueType ValueType, string DisplayName, string Unit)>();
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
                // bool 状态类指标（status 等）由轮询内置产出，映射仅开放 number/enum/string
                error = $"提取值类型仅支持 number/enum/string：{raw.ValueType}";
                return false;
            }

            var displayName = (raw.DisplayName ?? string.Empty).Trim();
            var unit = (raw.Unit ?? string.Empty).Trim();
            var existing = metricKeys.Get(key);
            if (existing is null)
            {
                pendingRegistrations.Add((key, valueType, displayName.Length > 0 ? displayName : key, unit));
            }
            else if (existing.ValueType != valueType)
            {
                error = $"指标 {key} 已注册为 {existing.ValueType.ToStorage()}，与本次映射类型 {valueType.ToStorage()} 不一致";
                return false;
            }

            mappings.Add(new PullMetricMapping(key, raw.JsonPath!.Trim(), valueType, displayName.Length > 0 ? displayName : key, unit));
        }

        foreach (var (key, valueType, displayName, unit) in pendingRegistrations)
        {
            metricKeys.Register(key, valueType, displayName, unit);
        }

        error = string.Empty;
        return true;
    }

    public static PullConfigResponse ToResponse(PullCollectorConfig config) => new(
        config.Url,
        config.IntervalSeconds,
        config.Mappings.Select(m => new PullMappingResponse(m.MetricKey, m.JsonPath, m.ValueType.ToStorage(), m.DisplayName, m.Unit)).ToList(),
        config.CreatedAtUtc,
        config.UpdatedAtUtc);
}

public static class PullCollectorEndpoints
{
    public static IEndpointRouteBuilder MapPullCollectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pulls = endpoints.MapGroup("/api/collectors/{id:long}/pull");

        pulls.MapGet("/", (long id, ICollectorRegistry registry, IPullCollectorConfigStore configs) =>
        {
            if (registry.Get(id) is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            var config = configs.Get(id);
            return config is null ? Results.NoContent() : Results.Ok(PullCollectorRequests.ToResponse(config));
        });

        pulls.MapPut("/", (
            long id,
            [FromBody] PullUpsertRequest request,
            ICollectorRegistry registry,
            IPullCollectorConfigStore configs,
            IMetricKeyRegistry metricKeys,
            ProbeOptions options) =>
        {
            var collector = registry.Get(id);
            if (collector is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            // push 采集器有 agent 通道，不归面板轮询；pull 配置只属于 agent 关联为空的采集器
            if (collector.AgentId is not null)
            {
                return Results.BadRequest(new { error = "仅 pull 采集器支持轮询配置" });
            }

            if (!PullCollectorRequests.TryNormalize(request, options, metricKeys, out var url, out var interval, out var mappings, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var saved = configs.Save(id, url, interval, mappings);
            return Results.Ok(PullCollectorRequests.ToResponse(saved));
        });

        return endpoints;
    }
}
