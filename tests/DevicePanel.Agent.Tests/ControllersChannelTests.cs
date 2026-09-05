using System.Text.Json;
using DevicePanel.Agent;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// ControllersChannel 单元测试（三期模块4）：用假下行链路与桩执行器，验证 ctrl.invoke.request 处理语义——
/// 按请求 seq 回包（成功 ctrl.invoke.response / 失败 ctrl.error）、控制器不存在、类型无执行器、
/// 非法负载与异常兜底不外抛、非 ctrl.* 信封忽略、内置四类执行器回执与 command 脚本执行。
/// </summary>
public class ControllersChannelTests
{
    // ---------- 通道路由 ----------

    [Fact]
    public async Task Invoke_Runs_Executor_And_Responds_With_Echoed_Seq()
    {
        var (channel, downlink) = CreateChannel(
            Spec("restart", ControlTypeNames.Button),
            new StubExecutor(ControlTypeNames.Button, ControllerExecutionResult.Ok("已执行按钮「重启」")));

        await channel.HandleAsync(Invoke(seq: 7, "restart", ControlTypeNames.Button, """{"value":"restart"}"""),
            CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlInvokeResponse);

        Assert.Equal(7, reply.Seq);
        Assert.Equal("已执行按钮「重启」", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unknown_Controller_Responds_Control_Error()
    {
        var (channel, downlink) = CreateChannel(Spec("restart", ControlTypeNames.Button), new StubExecutor(ControlTypeNames.Button));

        await channel.HandleAsync(Invoke(seq: 3, "ghost", ControlTypeNames.Button, "{}"), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlError);

        Assert.Equal(3, reply.Seq);
        Assert.Contains("ghost", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unknown_Type_Responds_Control_Error_Without_Executor()
    {
        // 声明了未注册执行器的类型（如未来新增类型尚未在 agent 侧实现）：明确报错，不误路由
        var (channel, downlink) = CreateChannel(Spec("lift", "teleport"), new StubExecutor(ControlTypeNames.Button));

        await channel.HandleAsync(Invoke(seq: 5, "lift", "teleport", "{}"), CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlError);

        Assert.Equal(5, reply.Seq);
        Assert.Contains("teleport", reply.Payload.GetProperty("message").GetString());
        Assert.Contains("执行器", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Executor_Failure_Responds_Control_Error_With_Reason()
    {
        var (channel, downlink) = CreateChannel(Spec("power", ControlTypeNames.Toggle),
            new StubExecutor(ControlTypeNames.Toggle, ControllerExecutionResult.Fail("继电器无响应")));

        await channel.HandleAsync(Invoke(seq: 9, "power", ControlTypeNames.Toggle, """{"state":true}"""),
            CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlError);

        Assert.Equal(9, reply.Seq);
        Assert.Contains("继电器无响应", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Executor_Exception_Folds_Into_Control_Error()
    {
        var (channel, downlink) = CreateChannel(Spec("fan", ControlTypeNames.Slider),
            new ThrowingExecutor(ControlTypeNames.Slider, new InvalidOperationException("驱动串口打开失败")));

        await channel.HandleAsync(Invoke(seq: 4, "fan", ControlTypeNames.Slider, """{"value":60}"""),
            CancellationToken.None);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlError);

        Assert.Equal(4, reply.Seq);
        Assert.Contains("驱动串口打开失败", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Malformed_Payload_Responds_Error_Without_Throwing()
    {
        var (channel, downlink) = CreateChannel(Spec("restart", ControlTypeNames.Button), new StubExecutor(ControlTypeNames.Button));

        var exception = await Record.ExceptionAsync(() => channel.HandleAsync(
            AgentEnvelope.Create(AgentMessageTypes.ControlInvokeRequest, 6,
                JsonSerializer.SerializeToElement(new { key = 42 })), CancellationToken.None));

        Assert.Null(exception);
        var reply = await downlink.WaitForAsync(AgentMessageTypes.ControlError);
        Assert.Equal(6, reply.Seq);
        Assert.Contains("负载无效", reply.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Non_Control_Envelope_Is_Ignored()
    {
        var (channel, downlink) = CreateChannel(Spec("restart", ControlTypeNames.Button), new StubExecutor(ControlTypeNames.Button));

        await channel.HandleAsync(AgentEnvelope.Create(AgentMessageTypes.MetricsLatestRequest, 2), CancellationToken.None);
        await Task.Delay(100, CancellationToken.None); // 给潜在误处理留窗口

        Assert.Empty(downlink.Sent);
    }

    [Fact]
    public void HandleAsync_Returns_Immediately_Execution_Is_Backgrounded()
    {
        // 慢执行器（如脚本 30s）：HandleAsync 不得等待执行完成——后台化是心跳/指标节拍的保障（TOB-338 契约）
        var pending = new TaskCompletionSource<ControllerExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (channel, _) = CreateChannel(Spec("restart", ControlTypeNames.Button),
            new StubExecutor(ControlTypeNames.Button, pending.Task));

        var task = channel.HandleAsync(Invoke(seq: 1, "restart", ControlTypeNames.Button, """{"value":"r"}"""),
            CancellationToken.None);

        Assert.Equal(Task.CompletedTask, task);
        pending.TrySetResult(ControllerExecutionResult.Ok("released")); // 释放后台执行，避免收尾后仍挂起
    }

    // ---------- 内置执行器回执语义 ----------

    [Fact]
    public async Task Button_Executor_Echoes_Item_Label_And_Fails_Without_Value()
    {
        var runner = new ShellCommandRunner();
        var executor = new ButtonControllerExecutor(runner);
        var schema = Json("""{"items":[{"label":"重启","value":"restart"}]}""");

        var hit = await executor.ExecuteAsync(Spec("restart", ControlTypeNames.Button, paramsSchema: schema),
            Json("""{"value":"restart"}"""), CancellationToken.None);
        Assert.True(hit.Success);
        Assert.Equal("已执行按钮「重启」", hit.Message);

        var missing = await executor.ExecuteAsync(Spec("restart", ControlTypeNames.Button, paramsSchema: schema),
            Json("{}"), CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Contains("value", missing.Message);
    }

    [Fact]
    public async Task Toggle_Executor_Echoes_Target_State()
    {
        var executor = new ToggleControllerExecutor(new ShellCommandRunner());
        var spec = Spec("power", ControlTypeNames.Toggle);

        var on = await executor.ExecuteAsync(spec, Json("""{"state":true}"""), CancellationToken.None);
        Assert.Equal("已切换为「开」", on.Message);
        var off = await executor.ExecuteAsync(spec, Json("""{"state":false}"""), CancellationToken.None);
        Assert.Equal("已切换为「关」", off.Message);
        var bad = await executor.ExecuteAsync(spec, Json("""{"state":"on"}"""), CancellationToken.None);
        Assert.False(bad.Success);
    }

    [Fact]
    public async Task Input_And_Slider_Executors_Echo_Submission_And_Target()
    {
        var input = new InputControllerExecutor(new ShellCommandRunner());
        var inputResult = await input.ExecuteAsync(Spec("remark", ControlTypeNames.Input),
            Json("""{"text":"机房巡检"}"""), CancellationToken.None);
        Assert.True(inputResult.Success);
        Assert.Equal("输入已提交", inputResult.Message);

        var slider = new SliderControllerExecutor(new ShellCommandRunner());
        var sliderResult = await slider.ExecuteAsync(Spec("fan", ControlTypeNames.Slider),
            Json("""{"value":12.5}"""), CancellationToken.None);
        Assert.Equal("已设置到 12.5", sliderResult.Message);
        var bad = await slider.ExecuteAsync(Spec("fan", ControlTypeNames.Slider), Json("{}"), CancellationToken.None);
        Assert.False(bad.Success);
    }

    // ---------- command 脚本执行（本机私有动作） ----------

    [Fact]
    public async Task Command_Declaration_Runs_Script_With_Params_As_Argument()
    {
        // command 是本机私有动作：$1 引用下发参数，stdout 作为回执说明
        var executor = new ButtonControllerExecutor(new ShellCommandRunner());
        var spec = Spec("restart", ControlTypeNames.Button, command: """printf '已执行:%s' "$1" """);

        var result = await executor.ExecuteAsync(spec, Json("""{"value":"restart"}"""), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("restart", result.Message);
    }

    [Fact]
    public async Task Command_Failure_Folds_Into_Failed_Result()
    {
        var executor = new ButtonControllerExecutor(new ShellCommandRunner());
        var spec = Spec("restart", ControlTypeNames.Button, command: "echo 出错 >&2; exit 3");

        var result = await executor.ExecuteAsync(spec, Json("""{"value":"restart"}"""), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("exit=3", result.Message);
    }

    // ---------- ControllerSpecFile ----------

    [Fact]
    public void Spec_File_Missing_Returns_Empty()
    {
        Assert.Empty(ControllerSpecFile.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Spec_File_Parses_Entries_With_Defaults_And_Skips_Invalid()
    {
        var path = WriteTempSpec("""
            [
              { "key": "restart", "type": "button", "label": "重启服务", "tags": ["运维", "运维", ""],
                "paramsSchema": { "items": [ { "label": "重启", "value": "restart" } ] },
                "command": "systemctl restart app" },
              { "type": "toggle", "label": "缺 key" },
              { "key": "a", "type": "toggle", "label": "首条" },
              { "key": "a", "type": "toggle", "label": "重复" },
              { "key": "fan", "type": "slider" }
            ]
            """);

        var specs = ControllerSpecFile.Load(path);

        Assert.Equal(3, specs.Count);
        Assert.Equal("restart", specs[0].Key);
        Assert.Equal("systemctl restart app", specs[0].Command);
        Assert.Equal(["运维"], specs[0].Tags); // 空串与重复标签剔除
        Assert.Equal("a", specs[1].Key); // key 重复保留第一条
        Assert.Equal("首条", specs[1].Label);
        Assert.Null(specs[1].Command);
        Assert.Equal("fan", specs[2].Key);
        Assert.Equal("{}", specs[2].ParamsSchema.GetRawText()); // paramsSchema 缺省 {}
    }

    [Fact]
    public void Spec_File_Corrupt_Or_Non_Array_Degrades_To_Empty()
    {
        Assert.Empty(ControllerSpecFile.Load(WriteTempSpec("{broken")));
        Assert.Empty(ControllerSpecFile.Load(WriteTempSpec("""{"key":"k"}""")));
    }

    // ---------- helpers ----------

    private static (ControllersChannel Channel, FakeControllersDownlink Downlink) CreateChannel(
        ControllerSpec spec, IControllerExecutor executor)
    {
        var downlink = new FakeControllersDownlink();
        var channel = new ControllersChannel(downlink, [spec],
            executors: new Dictionary<string, IControllerExecutor>(StringComparer.Ordinal)
            {
                [ControlTypeNames.Button] = executor,
                [ControlTypeNames.Toggle] = executor,
                [ControlTypeNames.Input] = executor,
                [ControlTypeNames.Slider] = executor,
            });
        return (channel, downlink);
    }

    private static ControllerSpec Spec(string key, string type, JsonElement? paramsSchema = null, string? command = null) =>
        new(key, type, key, [], paramsSchema ?? JsonSerializer.SerializeToElement(new { }), command);

    private static AgentEnvelope Invoke(long seq, string key, string type, string paramsJson) =>
        AgentEnvelope.Create(AgentMessageTypes.ControlInvokeRequest, seq,
            JsonSerializer.SerializeToElement(
                new ControlInvokeRequestPayload(key, type, JsonSerializer.Deserialize<JsonElement>(paramsJson)),
                AgentJsonContext.Default.ControlInvokeRequestPayload));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string WriteTempSpec(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"controllers-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>假下行链路：记录 (type, seq) 与负载，支持按类型等待（执行在后台任务完成）。</summary>
    private sealed class FakeControllersDownlink : IControllersDownlink
    {
        public List<(string Type, long Seq, JsonElement Payload)> Sent { get; } = new();

        public async Task<AgentEnvelope> WaitForAsync(string type)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (Sent)
                {
                    var entry = Sent.FirstOrDefault(s => s.Type == type);
                    if (entry.Type is not null)
                    {
                        return new AgentEnvelope { Type = entry.Type, Seq = entry.Seq, Payload = entry.Payload.Clone() };
                    }
                }

                await Task.Delay(20, CancellationToken.None);
            }

            throw new TimeoutException($"等待 {type} 发出超时");
        }

        public Task SendInvokeResponseAsync(long seq, string? message, CancellationToken cancellationToken)
        {
            Add(AgentMessageTypes.ControlInvokeResponse, seq,
                JsonSerializer.SerializeToElement(new ControlInvokeResponsePayload(message), AgentJsonContext.Default.ControlInvokeResponsePayload));
            return Task.CompletedTask;
        }

        public Task SendControlErrorAsync(long seq, string message, CancellationToken cancellationToken)
        {
            Add(AgentMessageTypes.ControlError, seq,
                JsonSerializer.SerializeToElement(new ControlErrorPayload(message), AgentJsonContext.Default.ControlErrorPayload));
            return Task.CompletedTask;
        }

        private void Add(string type, long seq, JsonElement payload)
        {
            lock (Sent)
            {
                Sent.Add((type, seq, payload.Clone()));
            }
        }
    }

    private sealed class StubExecutor : IControllerExecutor
    {
        private readonly string _type;
        private readonly Task<ControllerExecutionResult> _result;

        public StubExecutor(string type) : this(type, ControllerExecutionResult.Ok("ok")) { }

        public StubExecutor(string type, ControllerExecutionResult result) : this(type, Task.FromResult(result)) { }

        public StubExecutor(string type, Task<ControllerExecutionResult> result) => (_type, _result) = (type, result);

        public string Type => _type;

        public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters,
            CancellationToken cancellationToken) => _result;
    }

    private sealed class ThrowingExecutor(string type, Exception exception) : IControllerExecutor
    {
        public string Type => type;

        public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters,
            CancellationToken cancellationToken) => throw exception;
    }

    /// <summary>阻塞执行器：验证 HandleAsync 不等待执行完成（立即返回）。</summary>
    private sealed class BlockingExecutor(string type, ManualResetEventSlim release) : IControllerExecutor
    {
        public string Type => type;

        public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters,
            CancellationToken cancellationToken)
        {
            release.Wait(cancellationToken);
            return Task.FromResult(ControllerExecutionResult.Ok("released"));
        }
    }
}
