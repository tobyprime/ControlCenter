using System.Collections.Concurrent;
using System.Text.Json;
using DevicePanel.Protocol;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Collectors;

namespace DevicePanel.Web.Control;

/// <summary>
/// 面板 → agent 的控制下发服务（三期模块4）：把 REST 下发折算成 ctrl.invoke.request 下行信封，
/// 复用采集器链路的请求-响应/seq 关联与超时模式（对齐 LogQueryService）。
/// - 响应/错误沿用请求 seq，按 (通道, seq) 关联；通道绑定防跨设备/陈旧连接串扰；
/// - 采集器离线立即失败（不悬挂）；等待超时按 timeout 结论收尾；
/// - 每次真实下发（含离线/超时/错误）全量留痕；留痕存储故障只记日志，不影响下发结论与 HTTP 结果
///   （参照 TerminalStore 契约：调用方兜异常，存储失败不阻断主链路）。
/// </summary>
public sealed class ControlInvokeService
{
    private readonly AgentConnectionRegistry _connections;
    private readonly IAgentRegistry _agents;
    private readonly ControlTypeCatalog _controlTypes;
    private readonly IControlLogStore _logs;
    private readonly ControlOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ControlInvokeService> _logger;
    private readonly ConcurrentDictionary<(IDeviceChannel Channel, long Seq), TaskCompletionSource<AgentEnvelope>> _pending = new();
    private long _seq;

    public ControlInvokeService(AgentConnectionRegistry connections, IAgentRegistry agents,
        ControlTypeCatalog controlTypes, IControlLogStore logs, ControlOptions options, TimeProvider clock,
        ILogger<ControlInvokeService> logger)
    {
        _connections = connections;
        _agents = agents;
        _controlTypes = controlTypes;
        _logs = logs;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// 下发一次控制：离线判定 → 解析声明 → 校验参数 → 下行请求 → 等回执。返回结论（已留痕）；
    /// 输入类错误（控制器不存在/参数不合法）抛异常由端点映射 404/400，未发生下发不留痕。
    /// 离线优先于声明/参数校验：设备不在线时先给明确的 409，不暴露控制器细节。
    /// </summary>
    public async Task<ControlInvokeOutcome> InvokeAsync(CollectorInfo collector, string controllerKey,
        JsonElement parameters, string operatorName, CancellationToken cancellationToken)
    {
        if (collector.AgentId is not { } agentId)
        {
            throw new ControlNotFoundException("采集器未关联 agent，无可用控制器");
        }

        var channel = _connections.GetChannel(collector.Id);
        if (channel is null || !channel.IsOpen)
        {
            var offline = _agents.Get(agentId)?.Controllers?.FirstOrDefault(c => c.Key == controllerKey)
                ?? new ControllerDeclaration(controllerKey, "unknown", controllerKey, [], JsonSerializer.SerializeToElement(new { }));
            return RecordAndReturn(collector, offline, operatorName, parameters,
                ControlLogStatuses.Failure, "设备离线，控制未送达", deviceOffline: true);
        }

        var agent = _agents.Get(agentId);
        var declaration = agent?.Controllers?.FirstOrDefault(c => c.Key == controllerKey);
        if (agent is null || declaration is null)
        {
            throw new ControlNotFoundException($"控制器不存在：{controllerKey}");
        }

        var controlType = _controlTypes.Find(declaration.Type);
        if (controlType is null)
        {
            throw new ControlValidationException($"控制类型未注册：{declaration.Type}");
        }

        if (controlType.ValidateDeclarationSchema(declaration.ParamsSchema) is { } schemaError)
        {
            throw new ControlValidationException($"控制器声明 schema 不合法：{schemaError}");
        }

        if (controlType.ValidateInvokeParams(declaration.ParamsSchema, parameters) is { } paramsError)
        {
            throw new ControlValidationException(paramsError);
        }

        var seq = Interlocked.Increment(ref _seq);
        var pending = new TaskCompletionSource<AgentEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(channel, seq)] = pending;
        try
        {
            await channel.SendAsync(AgentEnvelope.Create(AgentMessageTypes.ControlInvokeRequest, seq,
                    JsonSerializer.SerializeToElement(new
                    {
                        key = declaration.Key,
                        type = declaration.Type,
                        @params = parameters,
                    })), cancellationToken).ConfigureAwait(false);

            var completed = await Task.WhenAny(pending.Task,
                    Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)), cancellationToken))
                .ConfigureAwait(false);
            if (completed != pending.Task)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return RecordAndReturn(collector, declaration, operatorName, parameters,
                    ControlLogStatuses.Timeout, $"设备响应控制请求超时（{_options.RequestTimeoutSeconds}s）");
            }

            var envelope = await pending.Task.ConfigureAwait(false);
            if (envelope.Type == AgentMessageTypes.ControlError)
            {
                var message = envelope.Payload.ValueKind == JsonValueKind.Object &&
                              envelope.Payload.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : null;
                return RecordAndReturn(collector, declaration, operatorName, parameters,
                    ControlLogStatuses.Failure, message ?? "设备无法执行控制请求");
            }

            var receipt = envelope.Payload.ValueKind == JsonValueKind.Object &&
                          envelope.Payload.TryGetProperty("message", out var rm)
                ? rm.GetString()
                : null;
            return RecordAndReturn(collector, declaration, operatorName, parameters,
                ControlLogStatuses.Success, receipt);
        }
        finally
        {
            _pending.TryRemove((channel, seq), out _);
        }
    }

    /// <summary>ctrl.* 响应处理器入口：按 (通道, seq) 完成挂起的下发；无匹配（陈旧响应）则忽略。</summary>
    public void Complete(IDeviceChannel channel, AgentEnvelope envelope)
    {
        if (!_pending.TryRemove((channel, envelope.Seq), out var pending))
        {
            _logger.LogDebug("忽略无法关联的控制回执：type={Type}, seq={Seq}（陈旧连接或超时后迟到）", envelope.Type, envelope.Seq);
            return;
        }

        pending.TrySetResult(envelope);
    }

    /// <summary>留痕并返回结论：留痕写入失败只记日志（存储故障不阻断下发，TerminalStore 契约）。</summary>
    private ControlInvokeOutcome RecordAndReturn(CollectorInfo collector, ControllerDeclaration declaration,
        string operatorName, JsonElement parameters, string status, string? message, bool deviceOffline = false)
    {
        try
        {
            _logs.Append(collector.Id, declaration.Key, declaration.Type, declaration.Label, operatorName,
                JsonSerializer.Serialize(parameters), status, message, _clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "控制留痕写入失败（不影响下发结论）：collector={CollectorId}, controller={Key}",
                collector.Id, declaration.Key);
        }

        return new ControlInvokeOutcome(status, message, deviceOffline);
    }
}
