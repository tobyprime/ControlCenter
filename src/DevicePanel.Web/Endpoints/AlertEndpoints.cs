using System.Text.Json;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

public sealed record SaveAlertSettingsRequest(string? BaseUrl, string? Token, string? TargetType, string? TargetId);

public sealed record CreateAlertRuleRequest(
    long? TargetId,
    string? MetricKey,
    string? RuleType,
    JsonElement? Parameters,
    int? SustainSeconds,
    int? RepeatMinutes,
    bool? Enabled);

public sealed record UpdateAlertRuleRequest(
    JsonElement? Parameters,
    int? SustainSeconds,
    int? RepeatMinutes,
    bool? Enabled);

// 以下为文档化响应类型标注（TOB-401）：JSON 形状与原匿名对象逐一对应（camelCase 策略统一）
public sealed record NapcatSettingsResponse(string? BaseUrl, bool TokenSet, string? TargetType, string? TargetId);

public sealed record AlertSettingsResponse(NapcatSettingsResponse Napcat);

public sealed record AlertQueueItemResponse(
    long Id,
    DateTimeOffset CreatedAtUtc,
    string Channel,
    string Title,
    string Content,
    int Attempts,
    string? LastError);

public sealed record AlertQueueResponse(int Count, IReadOnlyList<AlertQueueItemResponse> Items);

public sealed record ActiveAlertCountResponse(int Count);

// 告警事件历史（TOB-403 F2）：只读查询，规则/目标信息为写入时快照
public sealed record AlertEventSampleResponse(DateTimeOffset TimeUtc, double? ValueNum, string? ValueText);

public sealed record AlertEventResponse(
    long Id,
    DateTimeOffset CreatedAtUtc,
    long? RuleId,
    string RuleType,
    long? TargetId,
    string TargetName,
    string MetricKey,
    string MetricDisplayName,
    string Kind,
    string Title,
    string Content,
    AlertEventSampleResponse? Sample,
    string DeliveryStatus,
    DateTimeOffset? DeliveredAtUtc,
    string? DeliveryError);

public sealed record AlertEventListResponse(int Count, IReadOnlyList<AlertEventResponse> Items);

public sealed record AlertRuleTypeResponse(
    string TypeId,
    string DisplayName,
    string AlertTitle,
    string Description,
    IReadOnlyList<string> SupportedValueTypes,
    bool SampleDriven);

public sealed record AlertRuleResponse(
    long Id,
    long? TargetId,
    string TargetName,
    string MetricKey,
    string MetricDisplayName,
    string RuleType,
    bool Enabled,
    JsonElement Parameters,
    int SustainSeconds,
    int RepeatMinutes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class AlertEndpoints
{
    private const int MaxSustainSeconds = 24 * 3600;
    private const int MaxRepeatMinutes = 24 * 60;
    private const int DefaultEventLimit = 100;
    private const int MaxEventLimit = 500;

    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/alerts");

        alerts.MapGet("/settings", (IAlertSettingsStore settings) =>
        {
            var current = settings.Get();
            return Results.Ok(new AlertSettingsResponse(new NapcatSettingsResponse(
                current.NapcatBaseUrl,
                // token 只回传"是否已设置"，明文永不离开库（面板留空即保持原值）
                !string.IsNullOrEmpty(current.NapcatToken),
                current.NapcatTargetType,
                current.NapcatTargetId)));
        }).Produces<AlertSettingsResponse>();

        alerts.MapPut("/settings", ([FromBody] SaveAlertSettingsRequest request, IAlertSettingsStore settings) =>
        {
            var current = settings.Get();
            var baseUrl = request.BaseUrl ?? current.NapcatBaseUrl;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                var validBase = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
                    && parsed.Scheme is "http" or "https";
                if (!validBase)
                {
                    return Results.BadRequest(new { error = "napcat 地址必须是有效的 http(s) URL" });
                }
            }

            var targetType = request.TargetType ?? current.NapcatTargetType;
            if (targetType is { Length: > 0 } type && type is not (NapcatNotifier.TargetPrivate or NapcatNotifier.TargetGroup))
            {
                return Results.BadRequest(new { error = "通知目标类型仅支持 private（私聊）或 group（群聊）" });
            }

            var targetId = request.TargetId ?? current.NapcatTargetId;
            if (!string.IsNullOrEmpty(targetType))
            {
                if (string.IsNullOrEmpty(targetId) || !long.TryParse(targetId, out _))
                {
                    return Results.BadRequest(new { error = "已选择通知目标类型时必须填写数字 ID（QQ 号或群号）" });
                }
            }
            else if (!string.IsNullOrEmpty(targetId))
            {
                return Results.BadRequest(new { error = "请先选择通知目标类型（私聊或群聊）" });
            }

            settings.Save(new AlertDeliverySettings(
                baseUrl,
                // 未传 token = 保持原值；传空串 = 清除
                request.Token is null ? current.NapcatToken : (request.Token.Length == 0 ? null : request.Token),
                targetType,
                targetId));
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        alerts.MapGet("/queue", (IAlertOutboxStore outbox) =>
        {
            var entries = outbox.List();
            return Results.Ok(new AlertQueueResponse(
                entries.Count,
                entries.Select(e => new AlertQueueItemResponse(
                    e.Id,
                    e.CreatedAtUtc,
                    e.Channel,
                    e.Message.Title,
                    e.Message.Content,
                    e.Attempts,
                    e.LastError)).ToList()));
        }).Produces<AlertQueueResponse>();

        // 首页「活跃告警」概览卡数据源：已触发且未恢复的事件数（防抖等待中不算）
        alerts.MapGet("/active-count", (IAlertStateStore states) =>
            Results.Ok(new ActiveAlertCountResponse(states.CountActive())))
            .Produces<ActiveAlertCountResponse>();

        // 告警事件历史（TOB-403 F2）：触发/恢复时间、规则快照、当时值与投递结果；按目标与时间窗口筛选
        var alertEvents = endpoints.MapGroup("/api/alert-events");
        alertEvents.MapGet("/", (
            IAlertEventStore store,
            [FromQuery] long? targetId,
            [FromQuery] DateTimeOffset? fromUtc,
            [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] int? limit) =>
        {
            var boundedLimit = limit is { } requested && requested > 0 ? Math.Min(requested, MaxEventLimit) : DefaultEventLimit;
            var events = store.List(targetId, fromUtc, toUtc, boundedLimit);
            return Results.Ok(new AlertEventListResponse(
                events.Count,
                events.Select(ToResponse).ToList()));
        }).Produces<AlertEventListResponse>();

        var rules = endpoints.MapGroup("/api/alert-rules");

        // 规则类型目录（可插拔扩展点的 UI 发现入口）
        rules.MapGet("/types", (IEnumerable<IAlertRuleType> ruleTypes) =>
            Results.Ok(ruleTypes.Select(t => new AlertRuleTypeResponse(
                t.TypeId,
                t.DisplayName,
                t.AlertTitle,
                t.Description,
                t.SupportedValueTypes.Select(v => v.ToStorage()).ToList(),
                t.SampleDriven)).ToList()))
            .Produces<AlertRuleTypeResponse[]>();

        rules.MapGet("/", (IAlertRuleStore store, ICollectorRegistry targets, IMetricKeyRegistry metricKeys,
            [FromQuery] long? targetId, [FromQuery] string? metricKey) =>
        {
            var names = targets.List().ToDictionary(t => t.Id, t => t.Name);
            return Results.Ok(store.List(targetId, metricKey).Select(r => ToResponse(r, names, metricKeys)).ToList());
        }).Produces<AlertRuleResponse[]>();

        rules.MapPost("/", (
            [FromBody] CreateAlertRuleRequest request,
            IAlertRuleStore store,
            ICollectorRegistry targets,
            IMetricKeyRegistry metricKeys,
            IEnumerable<IAlertRuleType> ruleTypes,
            AlertOptions options) =>
        {
            var targetId = request.TargetId;
            if (targetId is { } tid && targets.Get(tid) is null)
            {
                return Results.BadRequest(new { error = "目标不存在" });
            }

            var metricKey = (request.MetricKey ?? string.Empty).Trim();
            var metric = metricKeys.Get(metricKey);
            if (metric is null)
            {
                return Results.BadRequest(new { error = "指标未注册，请先在指标注册表中登记" });
            }

            var ruleType = ruleTypes.FirstOrDefault(t => t.TypeId == (request.RuleType ?? string.Empty).Trim());
            if (ruleType is null)
            {
                return Results.BadRequest(new { error = "未知的规则类型" });
            }

            if (!ruleType.SupportedValueTypes.Contains(metric.ValueType))
            {
                return Results.BadRequest(new { error = $"规则类型「{ruleType.DisplayName}」不适用于 {metric.ValueType.ToStorage()} 类型指标" });
            }

            if (!TryNormalizeParameters(request.Parameters, ruleType, out var parametersJson, out var parametersError))
            {
                return Results.BadRequest(new { error = parametersError });
            }

            var sustainSeconds = request.SustainSeconds ?? options.SustainSeconds;
            if (sustainSeconds is < 0 or > MaxSustainSeconds)
            {
                return Results.BadRequest(new { error = $"持续窗口必须在 0-{MaxSustainSeconds} 秒之间" });
            }

            var repeatMinutes = request.RepeatMinutes ?? options.RepeatMinutes;
            if (repeatMinutes is < 0 or > MaxRepeatMinutes)
            {
                return Results.BadRequest(new { error = $"重发间隔必须在 0-{MaxRepeatMinutes} 分钟之间" });
            }

            if (store.Find(targetId, metricKey, ruleType.TypeId) is not null)
            {
                return Results.BadRequest(new { error = "同一目标的同一指标已存在同类型规则" });
            }

            try
            {
                var created = store.Create(targetId, metricKey, ruleType.TypeId, parametersJson, sustainSeconds, repeatMinutes, request.Enabled ?? true);
                var names = targets.List().ToDictionary(t => t.Id, t => t.Name);
                return Results.Json(ToResponse(created, names, metricKeys), statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<AlertRuleResponse>(StatusCodes.Status201Created);

        rules.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateAlertRuleRequest request,
            IAlertRuleStore store,
            IAlertRuleEngine engine,
            ICollectorRegistry targets,
            IMetricKeyRegistry metricKeys,
            IEnumerable<IAlertRuleType> ruleTypes) =>
        {
            var rule = store.Get(id);
            if (rule is null)
            {
                return Results.NotFound(new { error = "规则不存在" });
            }

            var ruleType = ruleTypes.First(t => t.TypeId == rule.RuleType);
            var parametersJson = rule.ParametersJson;
            if (request.Parameters.HasValue
                && !TryNormalizeParameters(request.Parameters, ruleType, out parametersJson, out var parametersError))
            {
                return Results.BadRequest(new { error = parametersError });
            }

            var sustainSeconds = request.SustainSeconds ?? rule.SustainSeconds;
            if (sustainSeconds is < 0 or > MaxSustainSeconds)
            {
                return Results.BadRequest(new { error = $"持续窗口必须在 0-{MaxSustainSeconds} 秒之间" });
            }

            var repeatMinutes = request.RepeatMinutes ?? rule.RepeatMinutes;
            if (repeatMinutes is < 0 or > MaxRepeatMinutes)
            {
                return Results.BadRequest(new { error = $"重发间隔必须在 0-{MaxRepeatMinutes} 分钟之间" });
            }

            var updated = store.Update(id, parametersJson, sustainSeconds, repeatMinutes, request.Enabled ?? rule.Enabled);
            if (updated is null)
            {
                return Results.NotFound(new { error = "规则不存在" });
            }

            // 参数/启停变化后丢弃旧事件状态：停用的规则不再触发，重新启用按全新事件计
            engine.ResetState(id);
            var names = targets.List().ToDictionary(t => t.Id, t => t.Name);
            return Results.Ok(ToResponse(updated, names, metricKeys));
        }).Produces<AlertRuleResponse>();

        rules.MapDelete("/{id:long}", (long id, IAlertRuleStore store, IAlertRuleEngine engine) =>
        {
            if (!store.Delete(id))
            {
                return Results.NotFound(new { error = "规则不存在" });
            }

            engine.ResetState(id);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static AlertEventResponse ToResponse(AlertEvent e) =>
        new(
            e.Id,
            e.CreatedAtUtc,
            e.RuleId,
            e.RuleType,
            e.TargetId,
            e.TargetName,
            e.MetricKey,
            e.MetricDisplayName,
            e.Kind,
            e.Title,
            e.Content,
            e.Sample is null ? null : new AlertEventSampleResponse(e.Sample.TimeUtc, e.Sample.ValueNum, e.Sample.ValueText),
            e.DeliveryStatus,
            e.DeliveredAtUtc,
            e.DeliveryError);

    private static AlertRuleResponse ToResponse(AlertRule rule, IReadOnlyDictionary<long, string> targetNames, IMetricKeyRegistry metricKeys)
    {
        var metric = metricKeys.Get(rule.MetricKey);
        return new AlertRuleResponse(
            rule.Id,
            rule.TargetId,
            rule.TargetId is { } tid ? targetNames.GetValueOrDefault(tid, $"目标 {tid}") : "（全局）",
            rule.MetricKey,
            metric?.DisplayName ?? rule.MetricKey,
            rule.RuleType,
            rule.Enabled,
            JsonDocument.Parse(rule.ParametersJson).RootElement,
            rule.SustainSeconds,
            rule.RepeatMinutes,
            rule.CreatedAtUtc,
            rule.UpdatedAtUtc);
    }

    private static bool TryNormalizeParameters(JsonElement? parameters, IAlertRuleType ruleType, out string parametersJson, out string error)
    {
        error = string.Empty;
        parametersJson = string.Empty;
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            error = "参数必须是 JSON 对象";
            return false;
        }

        var json = parameters.Value.GetRawText();
        var validationError = ruleType.ValidateParameters(json);
        if (validationError is not null)
        {
            error = validationError;
            return false;
        }

        parametersJson = json;
        return true;
    }
}
