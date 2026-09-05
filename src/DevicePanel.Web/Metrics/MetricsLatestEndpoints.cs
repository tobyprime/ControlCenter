using DevicePanel.Web.Collectors;
using DevicePanel.Web.Logs;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Metrics;

/// <summary>
/// 采集器按需查询 API（三期模块3）：GET /api/collectors/{id}/metrics/latest。
/// push 在线 → agent 即时采样（只读不落库）；pull → 面板侧最新样本；离线明确 409 报错不悬挂，超时 504，agent 错误 502。
/// </summary>
public static class MetricsLatestEndpoints
{
    public static IEndpointRouteBuilder MapMetricsLatestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var latest = endpoints.MapGroup("/api/collectors/{collectorId:long}/metrics");

        latest.MapGet("/latest", async (
            long collectorId,
            [FromQuery] string? keys,
            ICollectorRegistry collectors,
            MetricsQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var collector = collectors.Get(collectorId);
            if (collector is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            try
            {
                var samples = await queries.LatestAsync(collector, cancellationToken).ConfigureAwait(false);
                if (keys is { Length: > 0 })
                {
                    var requested = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    samples = samples.Where(s => requested.Contains(s.Key, StringComparer.Ordinal)).ToList();
                }

                return Results.Ok(new
                {
                    samples = samples.Select(s => new
                    {
                        key = s.Key,
                        timeUtc = s.TimeUtc,
                        valueNum = s.ValueNum,
                        valueText = s.ValueText,
                    }),
                });
            }
            catch (Exception ex) when (MapFailure(ex, out var status, out var message))
            {
                return Results.Json(new { error = message }, statusCode: status);
            }
        });

        return endpoints;
    }

    /// <summary>把按需查询失败映射为 HTTP 状态与用户可读消息；不可识别的异常返回 false（不吞编程错误）。</summary>
    private static bool MapFailure(Exception exception, out int status, out string message)
    {
        switch (exception)
        {
            case DeviceOfflineException:
                status = StatusCodes.Status409Conflict;
                message = exception.Message;
                return true;
            case AgentLogException:
                status = StatusCodes.Status502BadGateway;
                message = exception.Message;
                return true;
            case AgentTimeoutException:
                status = StatusCodes.Status504GatewayTimeout;
                message = exception.Message;
                return true;
            case InvalidOperationException:
                status = StatusCodes.Status502BadGateway;
                message = "采集器指标响应格式无效";
                return true;
            default:
                status = 0;
                message = string.Empty;
                return false;
        }
    }
}
