using System.Text.Json;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Control;

/// <summary>
/// 控制 API（三期模块4）：类型清单（注册表表面）、按采集器读控制器声明、下发并即时回执、控制留痕查询。
/// 下发结果映射：成功 200；离线 409；agent 报错 502；超时 504（与日志按需查询同套语义）。
/// </summary>
public static class ControlEndpoints
{
    public const int DefaultLogLimit = 200;
    public const int MaxLogLimit = 1000;

    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var controls = endpoints.MapGroup("/api/controls");

        // 控制类型注册表清单（验收4 的对外表面）：新增类型 = 注册 IControlType，清单自动纳入
        controls.MapGet("/types", (ControlTypeCatalog catalog) =>
            Results.Ok(new { types = catalog.List().Select(t => new { t.Key, t.DisplayName }) }));

        // 控制留痕查询：按控制器/时间筛选（验收3）
        controls.MapGet("/logs", (
            [FromQuery] long? collectorId,
            [FromQuery] string? controllerKey,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int? limit,
            IControlLogStore logs) =>
        {
            var entries = logs.Query(collectorId, string.IsNullOrWhiteSpace(controllerKey) ? null : controllerKey.Trim(),
                from, to, Math.Clamp(limit ?? DefaultLogLimit, 1, MaxLogLimit));
            return Results.Ok(new { logs = entries });
        });

        var collectors = endpoints.MapGroup("/api/collectors/{collectorId:long}");

        // 采集器已声明的控制器实体（来自 agent 能力上报的持久化副本）
        collectors.MapGet("/controllers", (long collectorId, ICollectorRegistry collectors, IAgentRegistry agents) =>
        {
            if (collectors.Get(collectorId) is not { } collector)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            var controllers = collector.AgentId is { } agentId
                ? agents.Get(agentId)?.Controllers ?? []
                : [];
            return Results.Ok(new { controllers });
        });

        // 下发控制：{ params } 即时回执（成功/失败/超时/离线），每次真实下发全量留痕
        collectors.MapPost("/controllers/{key}/invoke", async (
            long collectorId,
            string key,
            InvokeControlRequest? body,
            HttpContext http,
            ICollectorRegistry collectors,
            ControlInvokeService service,
            CancellationToken cancellationToken) =>
        {
            if (collectors.Get(collectorId) is not { } collector)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            // 操作者身份由认证门禁写入（登录会话名）；匿名途径不可达（认证门禁拦截 /api）
            var operatorName = http.Items.TryGetValue("SessionUsername", out var username)
                ? username as string ?? string.Empty
                : string.Empty;

            try
            {
                var outcome = await service.InvokeAsync(collector, key, body?.Params ?? JsonSerializer.SerializeToElement(new { }),
                    operatorName, cancellationToken).ConfigureAwait(false);
                return outcome.Success
                    ? Results.Ok(new { status = outcome.Status, message = outcome.Message })
                    : Results.Json(new { error = outcome.Message ?? "控制下发失败", status = outcome.Status },
                        statusCode: MapStatus(outcome));
            }
            catch (ControlNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ControlValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }

    /// <summary>失败结论 → HTTP：离线 409（对齐日志按需查询）、超时 504、agent 执行失败 502。</summary>
    private static int MapStatus(ControlInvokeOutcome outcome) => outcome switch
    {
        { DeviceOffline: true } => StatusCodes.Status409Conflict,
        { Status: ControlLogStatuses.Timeout } => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway,
    };
}

/// <summary>POST /controllers/{key}/invoke 请求体：params 为该控制类型声明的下发参数（不透明 JSON）。</summary>
public sealed record InvokeControlRequest(JsonElement Params);
