using System.Text.Json;
using DevicePanel.Web.Alerting;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
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

public static class AlertEndpoints
{
    private const int MaxSustainSeconds = 24 * 3600;
    private const int MaxRepeatMinutes = 24 * 60;

    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var alerts = endpoints.MapGroup("/api/alerts");

        alerts.MapGet("/settings", (IAlertSettingsStore settings) =>
        {
            var current = settings.Get();
            return Results.Ok(new
            {
                napcat = new
                {
                    baseUrl = current.NapcatBaseUrl,
                    // token 只回传"是否已设置"，明文永不离开库（面板留空即保持原值）
                    tokenSet = !string.IsNullOrEmpty(current.NapcatToken),
                    targetType = current.NapcatTargetType,
                    targetId = current.NapcatTargetId,
                },
            });
        });

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
        });

        alerts.MapGet("/queue", (IAlertOutboxStore outbox) =>
        {
            var entries = outbox.List();
            return Results.Ok(new
            {
                count = entries.Count,
                items = entries.Select(e => new
                {
                    id = e.Id,
                    createdAtUtc = e.CreatedAtUtc,
                    channel = e.Channel,
                    title = e.Message.Title,
                    content = e.Message.Content,
                    attempts = e.Attempts,
                    lastError = e.LastError,
                }).ToList(),
            });
        });

        // 首页「活跃告警」概览卡数据源：已触发且未恢复的事件数（防抖等待中不算）
        alerts.MapGet("/active-count", (IAlertStateStore states) =>
            Results.Ok(new { count = states.CountActive() }));

        var rules = endpoints.MapGroup("/api/alert-rules");

        // 规则类型目录（可插拔扩展点的 UI 发现入口）
        rules.MapGet("/types", (IEnumerable<IAlertRuleType> ruleTypes) =>
            Results.Ok(ruleTypes.Select(t => new
            {
                typeId = t.TypeId,
                displayName = t.DisplayName,
                alertTitle = t.AlertTitle,
                description = t.Description,
                supportedValueTypes = t.SupportedValueTypes.Select(v => v.ToStorage()).ToList(),
                sampleDriven = t.SampleDriven,
            })));

        rules.MapGet("/", (IAlertRuleStore store, ITargetRegistry targets, IMetricKeyRegistry metricKeys,
            [FromQuery] long? targetId, [FromQuery] string? metricKey) =>
        {
            var names = targets.List().ToDictionary(t => t.Id, t => t.Name);
            return Results.Ok(store.List(targetId, metricKey).Select(r => ToResponse(r, names, metricKeys)));
        });

        rules.MapPost("/", (
            [FromBody] CreateAlertRuleRequest request,
            IAlertRuleStore store,
            ITargetRegistry targets,
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
        });

        rules.MapPut("/{id:long}", (
            long id,
            [FromBody] UpdateAlertRuleRequest request,
            IAlertRuleStore store,
            IAlertRuleEngine engine,
            ITargetRegistry targets,
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
        });

        rules.MapDelete("/{id:long}", (long id, IAlertRuleStore store, IAlertRuleEngine engine) =>
        {
            if (!store.Delete(id))
            {
                return Results.NotFound(new { error = "规则不存在" });
            }

            engine.ResetState(id);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static object ToResponse(AlertRule rule, IReadOnlyDictionary<long, string> targetNames, IMetricKeyRegistry metricKeys)
    {
        var metric = metricKeys.Get(rule.MetricKey);
        return new
        {
            id = rule.Id,
            targetId = rule.TargetId,
            targetName = rule.TargetId is { } tid ? targetNames.GetValueOrDefault(tid, $"目标 {tid}") : "（全局）",
            metricKey = rule.MetricKey,
            metricDisplayName = metric?.DisplayName ?? rule.MetricKey,
            ruleType = rule.RuleType,
            enabled = rule.Enabled,
            parameters = JsonDocument.Parse(rule.ParametersJson).RootElement,
            sustainSeconds = rule.SustainSeconds,
            repeatMinutes = rule.RepeatMinutes,
            createdAtUtc = rule.CreatedAtUtc,
            updatedAtUtc = rule.UpdatedAtUtc,
        };
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
