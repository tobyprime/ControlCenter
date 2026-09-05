using System.Globalization;
using System.Net.WebSockets;
using DevicePanel.Web.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Terminal;

public sealed record TerminalSessionResponse(
    string Id,
    long DeviceId,
    string DeviceName,
    string Operator,
    string OpenedAtUtc,
    string? ClosedAtUtc,
    string? CloseReason);

public sealed record TerminalEntryResponse(long Id, string SessionId, string Direction, string Data, string RecordedAtUtc);

/// <summary>浏览器终端 WS 入口 + 留痕查询 API（均走面板登录会话认证，/api 前缀由登录拦截统一把关）。</summary>
public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/devices/{deviceId:long}/terminal", async (
            HttpContext http,
            long deviceId,
            ICollectorRegistry devices,
            AgentConnectionRegistry connections,
            TerminalSessionRegistry sessions,
            ITerminalStore store,
            TimeProvider clock,
            ILogger<TerminalRelay> logger) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "该端点仅接受 WebSocket 连接" });
            }

            if (devices.Get(deviceId) is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            var agentChannel = connections.GetChannel(deviceId);
            if (agentChannel is null)
            {
                return Results.Conflict(new { error = "设备离线，无法打开终端" });
            }

            var (cols, rows) = ParseSize(http);
            var socket = await http.WebSockets.AcceptWebSocketAsync();
            var relay = new TerminalRelay(
                Guid.NewGuid().ToString("N"),
                deviceId,
                http.Items.TryGetValue("SessionUsername", out var username) ? username as string ?? string.Empty : string.Empty,
                cols,
                rows,
                agentChannel,
                socket,
                store,
                sessions,
                connections,
                clock,
                logger);
            sessions.TryAdd(relay);
            await relay.RunAsync();
            return Results.Empty;
        });

        var terminal = endpoints.MapGroup("/api/terminal");

        terminal.MapGet("/sessions", (
            [FromQuery] long? deviceId,
            [FromQuery] string? from,
            [FromQuery] string? to,
            ITerminalStore store,
            ICollectorRegistry devices) =>
        {
            if (deviceId is { } id && devices.Get(id) is null)
            {
                return Results.NotFound(new { error = "设备不存在" });
            }

            if (!TryParseRange(from, to, out var fromUtc, out var toUtc, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var list = store.QuerySessions(deviceId, fromUtc, toUtc)
                .Select(s => new TerminalSessionResponse(
                    s.Id,
                    s.DeviceId,
                    devices.Get(s.DeviceId)?.Name ?? "（已删除）",
                    s.Operator,
                    FormatUtc(s.OpenedAtUtc),
                    s.ClosedAtUtc is { } closed ? FormatUtc(closed) : null,
                    s.CloseReason))
                .ToList();
            return Results.Ok(list);
        });

        terminal.MapGet("/sessions/{sessionId}/records", (string sessionId, ITerminalStore store) =>
        {
            if (store.GetSession(sessionId) is null)
            {
                return Results.NotFound(new { error = "会话不存在" });
            }

            return Results.Ok(store.QueryEntries(sessionId).Select(e => new TerminalEntryResponse(
                e.Id,
                e.SessionId,
                e.Direction,
                e.Data,
                FormatUtc(e.RecordedAtUtc))).ToList());
        });

        return endpoints;
    }

    private static (int Cols, int Rows) ParseSize(HttpContext http)
    {
        static int Clamp(string? raw, int fallback)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 2, 500)
                : fallback;
        }

        return (Clamp(http.Request.Query["cols"], 80), Clamp(http.Request.Query["rows"], 24));
    }

    private static bool TryParseRange(string? from, string? to, out DateTimeOffset fromUtc, out DateTimeOffset toUtc, out string error)
    {
        fromUtc = DateTimeOffset.MinValue;
        toUtc = DateTimeOffset.MaxValue;
        error = string.Empty;
        if (!string.IsNullOrEmpty(from) && !DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out fromUtc))
        {
            error = "from 不是有效时间（ISO-8601，如 2026-09-01T00:00:00Z）";
            return false;
        }

        if (!string.IsNullOrEmpty(to) && !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out toUtc))
        {
            error = "to 不是有效时间（ISO-8601，如 2026-09-03T00:00:00Z）";
            return false;
        }

        if (fromUtc > toUtc)
        {
            error = "时间范围无效：from 必须早于 to";
            return false;
        }

        return true;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
