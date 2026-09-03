using System.Net.WebSockets;
using System.Text.Json;
using DevicePanel.Protocol;

namespace DevicePanel.Web.Devices;

/// <summary>
/// /agent/ws 接入会话：auth 握手（token 认证）→ 注册在线连接 → 按信封 type 分发入站消息。
/// 认证失败、超时以 AuthFailed(4001) 关闭；重复接入旧连接以 DuplicateSession(4005) 关闭。
/// </summary>
public static class AgentWsEndpoints
{
    public static IEndpointRouteBuilder MapAgentWsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/agent/ws", async (
            HttpContext http,
            AgentConnectionRegistry connections,
            IDeviceRegistry devices,
            AgentMessageDispatcher dispatcher,
            AgentOptions options,
            TimeProvider clock,
            ILogger<AgentWsSession> logger) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "该端点仅接受 WebSocket 连接" });
            }

            var socket = await http.WebSockets.AcceptWebSocketAsync();
            var session = new AgentWsSession(socket, connections, devices, dispatcher, options, clock, logger);
            // 会话生命周期跟随 socket 本身：HTTP 请求令牌在 WS 握手完成后即可能被触发
            // （TestServer/部分代理如此），不能作为通道断开依据；断开由 ReceiveAsync 的关闭帧/异常驱动。
            await session.RunAsync();
            return Results.Empty;
        });

        return endpoints;
    }
}

internal sealed class AgentWsSession
{
    private readonly WebSocket _socket;
    private readonly AgentConnectionRegistry _connections;
    private readonly IDeviceRegistry _devices;
    private readonly AgentMessageDispatcher _dispatcher;
    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public AgentWsSession(
        WebSocket socket,
        AgentConnectionRegistry connections,
        IDeviceRegistry devices,
        AgentMessageDispatcher dispatcher,
        AgentOptions options,
        TimeProvider clock,
        ILogger logger)
    {
        _socket = socket;
        _connections = connections;
        _devices = devices;
        _dispatcher = dispatcher;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var connection = new AgentConnection(0, _socket);
        long deviceId = 0;
        try
        {
            var authenticated = await AuthenticateAsync(connection).ConfigureAwait(false);
            if (authenticated is null)
            {
                return;
            }

            deviceId = authenticated.Value.DeviceId;
            connection.DeviceId = deviceId;
            _connections.TryAdd(deviceId, connection);
            MarkSeen(deviceId, connection);
            await SendAsync(connection, AgentMessageTypes.AuthOk, authenticated.Value.Seq, new
            {
                deviceId,
                name = _devices.Get(deviceId)?.Name ?? string.Empty,
            }, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("设备 {DeviceId} 已接入", deviceId);

            while (connection.IsOpen)
            {
                AgentEnvelope? envelope;
                try
                {
                    envelope = await connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 传输层偶发取消读操作（TestServer/部分代理如此）：socket 未断则继续会话
                    continue;
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (envelope is null)
                {
                    break;
                }

                await _dispatcher
                    .DispatchAsync(new AgentChannelContext(connection, envelope), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogInformation(ex, "设备通道异常断开（device: {DeviceId}）", deviceId);
        }
        finally
        {
            if (deviceId != 0)
            {
                _connections.Remove(deviceId, connection);
                _logger.LogInformation("设备 {DeviceId} 连接结束", deviceId);
            }
        }
    }

    /// <summary>执行 auth 握手。返回 null 表示握手失败（已回错误信封并关闭）；成功返回设备 ID 与请求 seq。</summary>
    private async Task<(long DeviceId, long Seq)?> AuthenticateAsync(AgentConnection connection)
    {
        // auth 读取超时用真实时间：即使宿主注入 FakeTimeProvider（测试），超时行为也保持真实
        var timeoutAt = TimeProvider.System.GetUtcNow().AddSeconds(_options.AuthTimeoutSeconds);

        AgentEnvelope? first = null;
        while (true)
        {
            var remainingMs = (long)(timeoutAt - TimeProvider.System.GetUtcNow()).TotalMilliseconds;
            if (remainingMs <= 0)
            {
                await CloseAsync(connection, WebSocketCloseCodes.AuthFailed, "认证超时").ConfigureAwait(false);
                return null;
            }

            using var receiveCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(remainingMs));
            try
            {
                first = await connection.ReceiveAsync(receiveCts.Token).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (!receiveCts.IsCancellationRequested && connection.IsOpen)
            {
                // 传输层取消了读但连接仍在：重试剩余时限
                continue;
            }
            catch (OperationCanceledException)
            {
                await CloseAsync(connection, WebSocketCloseCodes.AuthFailed, "认证超时").ConfigureAwait(false);
                return null;
            }
        }

        var token = first is not null && first.Type == AgentMessageTypes.Auth && first.Payload.TryGetProperty("token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var deviceId = _devices.FindDeviceIdByToken(token ?? "");
        if (first is null || deviceId is null)
        {
            _logger.LogWarning("agent 接入认证失败：token 无效或缺失");
            await SendAsync(connection, AgentMessageTypes.AuthError, first?.Seq ?? 0, new
            {
                message = "认证失败：token 无效",
            }, CancellationToken.None).ConfigureAwait(false);
            await CloseAsync(connection, WebSocketCloseCodes.AuthFailed, "认证失败：token 无效").ConfigureAwait(false);
            return null;
        }

        return (deviceId.Value, first.Seq);
    }

    private void MarkSeen(long deviceId, IDeviceChannel channel)
    {
        var nowUtc = _clock.GetUtcNow();
        _devices.Touch(deviceId, nowUtc);
        _connections.Touch(deviceId, nowUtc);
    }

    private static Task SendAsync(IDeviceChannel connection, string type, long seq, object payload, CancellationToken ct) =>
        connection.SendAsync(AgentEnvelope.Create(type, seq, JsonSerializer.SerializeToElement(payload)), ct);

    private static Task CloseAsync(IDeviceChannel connection, WebSocketCloseCodes code, string reason) =>
        connection.CloseAsync((int)code, reason, CancellationToken.None);
}
