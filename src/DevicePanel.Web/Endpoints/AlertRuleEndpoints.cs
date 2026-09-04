using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DevicePanel.Web.Endpoints;

public sealed record SaveAlertRuleRequest(
    long? TargetId,
    string? Metric,
    string? RuleType,
    JsonElement? Params,
    bool? Enabled);

public static class AlertRuleEndpoints
{
    public static IEndpointRouteBuilder MapAlertRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var rules = endpoints.MapGroup("/api/alerts/rules");

        // 规则类型目录（约束 B：规则类型可插拔，前端按描述动态渲染参数表单）
        rules.MapGet("/types", (IEnumerable<IAlertRuleTypeHandler> handlers) =>
            Results.Ok(handlers.Select(h => new
            {
                type = h.RuleType,
                displayName = h.Describe().DisplayName,
                description = h.Describe().Description,
                requiresMetric = h.Describe().RequiresMetric,
                allowsNullMetric = h.Describe().AllowsNullMetric,
                paramDescriptors = h.Describe().Params.Select(p => new
                {
                    name = p.Name,
                    type = p.Type,
                    required = p.Required,
                    defaultValue = p.DefaultValue,
                    description = p.Description,
                }).ToList(),
            }).ToList()));

        rules.MapGet("/", (
            [FromQuery] long? targetId,
            IAlertRuleStore store,
            ITargetStore targets,
            IMetricKeyRegistry metricKeys) =>
        {
            var targetNames = targets.List().ToDictionary(t => t.Id, t => t.Name);
            return Results.Ok(new
            {
                items = store.List(targetId: targetId).Select(r => new
                {
                    id = r.Id,
                    targetId = r.TargetId,
                    targetName = targetNames.GetValueOrDefault(r.TargetId, $"目标 {r.TargetId}"),
                    metric = r.Metric,
                    metricDisplayName = r.Metric is null ? null : metricKeys.Get(r.Metric)?.DisplayName ?? r.Metric,
                    ruleType = r.RuleType,
                    paramsJson = r.ParamsJson,
                    enabled = r.Enabled,
                    updatedAtUtc = r.UpdatedAtUtc,
                }).ToList(),
            });
        });

        rules.MapPost("/", (
            [FromBody] SaveAlertRuleRequest request,
            IAlertRuleStore store,
            ITargetStore targets,
            IMetricKeyRegistry metricKeys,
            IEnumerable<IAlertRuleTypeHandler> handlers) =>
            ValidateAndCreate(request, store, targets, metricKeys, handlers));

        rules.MapPut("/{id:long}", (
            long id,
            [FromBody] SaveAlertRuleRequest request,
            IAlertRuleStore store,
            ITargetStore targets,
            IMetricKeyRegistry metricKeys,
            IEnumerable<IAlertRuleTypeHandler> handlers) =>
            ValidateAndUpdate(id, request, store, targets, metricKeys, handlers));

        rules.MapPut("/{id:long}/enabled", (
            long id,
            [FromBody] EnabledRequest request,
            IAlertRuleStore store) =>
            store.SetEnabled(id, request.Enabled)
                ? Results.NoContent()
                : Results.NotFound(new { error = "规则不存在" }));

        rules.MapDelete("/{id:long}", (long id, IAlertRuleStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "规则不存在" }));

        return endpoints;
    }

    public sealed record EnabledRequest(bool Enabled);

    private static IResult ValidateAndCreate(
        SaveAlertRuleRequest request,
        IAlertRuleStore store,
        ITargetStore targets,
        IMetricKeyRegistry metricKeys,
        IEnumerable<IAlertRuleTypeHandler> handlers)
    {
        var (handler, metric, paramsJson, error) = Validate(request, targets, metricKeys, handlers);
        if (error is not null || handler is null || paramsJson is null)
        {
            return Results.BadRequest(new { error = error ?? "请求不合法" });
        }

        if (store.Find(request.TargetId!.Value, metric, handler.RuleType) is not null)
        {
            return Results.Conflict(new { error = "该目标在此指标上已存在同类型规则" });
        }

        var created = store.Create(request.TargetId.Value, metric, handler.RuleType, paramsJson, request.Enabled ?? true);
        return Results.Json(new { id = created.Id }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult ValidateAndUpdate(
        long id,
        SaveAlertRuleRequest request,
        IAlertRuleStore store,
        ITargetStore targets,
        IMetricKeyRegistry metricKeys,
        IEnumerable<IAlertRuleTypeHandler> handlers)
    {
        var existing = store.Get(id);
        if (existing is null)
        {
            return Results.NotFound(new { error = "规则不存在" });
        }

        // 未提供的字段保持原值（PUT 局部语义：params/targetId 不传不改）
        var merged = new SaveAlertRuleRequest(
            request.TargetId ?? existing.TargetId,
            request.Metric ?? existing.Metric,
            request.RuleType ?? existing.RuleType,
            request.Params,
            request.Enabled ?? existing.Enabled);
        var (handler, metric, paramsJson, error) = Validate(merged, targets, metricKeys, handlers);
        if (error is not null || handler is null || paramsJson is null)
        {
            return Results.BadRequest(new { error = error ?? "请求不合法" });
        }

        var duplicate = store.Find(merged.TargetId!.Value, metric, handler.RuleType);
        if (duplicate is { } found && found.Id != id)
        {
            return Results.Conflict(new { error = "该目标在此指标上已存在同类型规则" });
        }

        store.Update(id, metric, handler.RuleType, paramsJson, merged.Enabled ?? true);
        return Results.NoContent();
    }

    private static (IAlertRuleTypeHandler? Handler, string? Metric, string? ParamsJson, string? Error) Validate(
        SaveAlertRuleRequest request,
        ITargetStore targets,
        IMetricKeyRegistry metricKeys,
        IEnumerable<IAlertRuleTypeHandler> handlers)
    {
        if (request.TargetId is not { } targetId || targets.Get(targetId) is not { } target)
        {
            return (null, null, null, "目标不存在");
        }

        var ruleType = (request.RuleType ?? string.Empty).Trim();
        var handler = handlers.FirstOrDefault(h => h.RuleType == ruleType);
        if (handler is null)
        {
            return (null, null, null, $"不支持的规则类型：{ruleType}");
        }

        var metric = request.Metric is { Length: > 0 } m ? m.Trim() : null;
        if (metric is null)
        {
            if (!handler.Describe().AllowsNullMetric)
            {
                return (null, null, null, "该规则类型必须指定指标");
            }

            if (!target.IsDevice)
            {
                // 服务目标没有心跳语义，metric=null 的无数据规则不可评估
                return (null, null, null, "服务目标的无数据规则必须指定指标");
            }
        }
        else if (metricKeys.Get(metric) is null)
        {
            return (null, null, null, $"指标 {metric} 未注册");
        }

        var paramsJson = SerializeParams(request.Params);
        if (paramsJson is null)
        {
            return (null, null, null, "params 必须是 JSON 对象");
        }

        var validationError = handler.ValidateParams(paramsJson);
        return validationError is not null
            ? (null, null, null, validationError)
            : (handler, metric, paramsJson, null);
    }

    private static string? SerializeParams(JsonElement? raw)
    {
        if (raw is not { } element)
        {
            return "{}";
        }

        return element.ValueKind == JsonValueKind.Object
            ? element.GetRawText()
            : null;
    }
}
