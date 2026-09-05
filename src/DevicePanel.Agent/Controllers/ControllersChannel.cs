using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DevicePanel.Protocol;

namespace DevicePanel.Agent;

/// <summary>ctrl.invoke.request 负载（面板 → agent）：key 为控制器标识，type 为控制类型，params 为下发参数（不透明 JSON）。</summary>
internal sealed record ControlInvokeRequestPayload(string Key, string Type, JsonElement Params);

/// <summary>ctrl.invoke.response 负载（agent → 面板）：执行成功回执说明（可省略）。</summary>
internal sealed record ControlInvokeResponsePayload(string? Message);

/// <summary>ctrl.error 负载（agent → 面板）：控制器不存在/类型无执行器/执行失败等原因。</summary>
internal sealed record ControlErrorPayload(string Message);

/// <summary>agent.capabilities 对象形态里的单个控制器声明（paramsSchema 透传原始 JSON；command 等本机私有字段不出现在上报）。</summary>
internal sealed record ControllerReportPayload(string Key, string Type, string Label, IReadOnlyList<string> Tags, JsonElement ParamsSchema);

/// <summary>
/// agent.capabilities 对象形态负载（三期模块4）：capabilities 沿用旧版能力名列表，
/// controllers 携带控制器声明（面板侧向后兼容：数组形态仍按旧版解析）。
/// </summary>
internal sealed record CapabilitiesReportPayload(IReadOnlyList<string> Capabilities, IReadOnlyList<ControllerReportPayload> Controllers);

/// <summary>
/// agent 侧控制下行发送原语：ctrl.* 响应按请求 seq 回包（请求-响应关联，与 logs.* 一致）。
/// 连接已断开时发送为 no-op（尽力而为，不打断会话清理）。
/// </summary>
internal interface IControllersDownlink
{
    Task SendInvokeResponseAsync(long seq, string? message, CancellationToken cancellationToken);

    Task SendControlErrorAsync(long seq, string message, CancellationToken cancellationToken);
}

/// <summary>
/// agent 侧控制通道（扩展点）：处理面板下行的 ctrl.invoke.request。
/// 实现约定与终端/日志通道一致：HandleAsync 不向消息循环抛异常；执行一律后台化——
/// 慢控制不阻塞心跳/指标节拍（TOB-338 回归契约）。
/// </summary>
internal interface IControllersChannel
{
    /// <summary>本 agent 声明的控制器（随 agent.capabilities 上报；command 等本机私有字段不外发）。</summary>
    IReadOnlyList<ControllerSpec> Declarations { get; }

    Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// 控制通道：ctrl.invoke.request → 按 key 找声明 → 按 type 路由执行器 → 执行并按请求 seq 回执
/// （成功 ctrl.invoke.response，失败/不存在 ctrl.error）。
/// 请求处理立即后台化；连接断开后发送自动降级为 no-op。
/// </summary>
internal sealed class ControllersChannel : IControllersChannel
{
    private readonly IControllersDownlink _downlink;
    private readonly IReadOnlyDictionary<string, IControllerExecutor> _executors;
    private readonly TextWriter _output;

    public ControllersChannel(IControllersDownlink downlink, IReadOnlyList<ControllerSpec> declarations,
        TextWriter? output = null, IReadOnlyDictionary<string, IControllerExecutor>? executors = null)
    {
        _downlink = downlink;
        Declarations = declarations;
        _output = output ?? TextWriter.Null;
        _executors = executors ?? DefaultExecutors();
    }

    public IReadOnlyList<ControllerSpec> Declarations { get; }

    public Task HandleAsync(AgentEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            if (envelope.Type != AgentMessageTypes.ControlInvokeRequest)
            {
                return Task.CompletedTask;
            }

            var request = TryDeserialize(envelope.Payload, AgentJsonContext.Default.ControlInvokeRequestPayload);
            if (request is null)
            {
                _ = SendErrorAsync(envelope.Seq, "控制下发请求负载无效");
                return Task.CompletedTask;
            }

            // 后台化执行：控制可能耗时（脚本/命令），绝不阻塞心跳/指标节拍
            _ = ExecuteAsync(envelope.Seq, request);
        }
        catch (Exception ex)
        {
            // 消息循环契约：下行处理失败绝不打断心跳/指标节拍
            _ = WriteOutputAsync($"控制消息处理失败（{envelope.Type}）：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>请求执行体：路由执行器并回包；任何失败都折算成 ctrl.error（按请求 seq）。</summary>
    private async Task ExecuteAsync(long seq, ControlInvokeRequestPayload request)
    {
        try
        {
            var spec = Declarations.FirstOrDefault(d => d.Key == request.Key);
            if (spec is null)
            {
                await SendErrorAsync(seq, $"控制器不存在：{request.Key}").ConfigureAwait(false);
                return;
            }

            if (!_executors.TryGetValue(spec.Type, out var executor))
            {
                await SendErrorAsync(seq, $"控制类型暂无执行器：{spec.Type}").ConfigureAwait(false);
                return;
            }

            var result = await executor.ExecuteAsync(spec, request.Params, CancellationToken.None).ConfigureAwait(false);
            if (result.Success)
            {
                await _downlink.SendInvokeResponseAsync(seq, result.Message, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await SendErrorAsync(seq, result.Message ?? "控制执行失败").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await SendErrorAsync(seq, $"控制执行异常：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>内置执行器集：按类型路由（演示实现回执动作语义；command 声明的控制器经脚本执行）。</summary>
    private IReadOnlyDictionary<string, IControllerExecutor> DefaultExecutors()
    {
        var commandRunner = new ShellCommandRunner();
        return new Dictionary<string, IControllerExecutor>(StringComparer.Ordinal)
        {
            [ControlTypeNames.Button] = new ButtonControllerExecutor(commandRunner),
            [ControlTypeNames.Toggle] = new ToggleControllerExecutor(commandRunner),
            [ControlTypeNames.Input] = new InputControllerExecutor(commandRunner),
            [ControlTypeNames.Slider] = new SliderControllerExecutor(commandRunner),
        };
    }

    private async Task SendErrorAsync(long seq, string message)
    {
        try
        {
            await _downlink.SendControlErrorAsync(seq, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteOutputAsync($"控制错误回包失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task WriteOutputAsync(string message) => await _output.WriteLineAsync(message).ConfigureAwait(false);

    private static T? TryDeserialize<T>(JsonElement payload, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(payload.GetRawText(), typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
