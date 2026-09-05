using System.Text.RegularExpressions;
using DevicePanel.Web.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Logs;

/// <summary>日志 API（三期模块3 归并为采集器数据类型）：按采集器列出可查看服务 / 按需拉取尾部日志（只读，面板不落库）。</summary>
public static partial class LogEndpoints
{
    public const int DefaultLines = 200;
    public const int MaxLines = 1000;

    public const string KindSystemd = "systemd";
    public const string KindDocker = "docker";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@:-]{0,199}$")]
    private static partial Regex ServiceNameRegex();

    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var logs = endpoints.MapGroup("/api/collectors/{collectorId:long}/logs");

        logs.MapGet("/services", async (
            long collectorId,
            ICollectorRegistry collectors,
            LogQueryService queries,
            CancellationToken cancellationToken) =>
        {
            if (collectors.Get(collectorId) is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            try
            {
                var services = await queries.ListServicesAsync(collectorId, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { services });
            }
            catch (Exception ex) when (MapFailure(ex, out var status, out var message))
            {
                return Results.Json(new { error = message }, statusCode: status);
            }
        });

        logs.MapGet("/tail", async (
            long collectorId,
            [FromQuery] string? service,
            [FromQuery] string? kind,
            [FromQuery] int? lines,
            ICollectorRegistry collectors,
            LogQueryService queries,
            CancellationToken cancellationToken) =>
        {
            if (collectors.Get(collectorId) is null)
            {
                return Results.NotFound(new { error = "采集器不存在" });
            }

            if (string.IsNullOrWhiteSpace(service))
            {
                return Results.BadRequest(new { error = "缺少 service 参数" });
            }

            if (!ServiceNameRegex().IsMatch(service))
            {
                return Results.BadRequest(new { error = "服务名包含非法字符" });
            }

            if (kind is not (KindSystemd or KindDocker))
            {
                return Results.BadRequest(new { error = "kind 仅支持 systemd/docker" });
            }

            try
            {
                var result = await queries
                    .TailAsync(collectorId, service, kind, Math.Clamp(lines ?? DefaultLines, 1, MaxLines), cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(new { lines = result });
            }
            catch (Exception ex) when (MapFailure(ex, out var status, out var message))
            {
                return Results.Json(new { error = message }, statusCode: status);
            }
        });

        return endpoints;
    }

    /// <summary>把日志查询失败映射为 HTTP 状态与用户可读消息；不可识别的异常返回 false（不吞编程错误）。</summary>
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
                message = "采集器日志响应格式无效";
                return true;
            default:
                status = 0;
                message = string.Empty;
                return false;
        }
    }
}
