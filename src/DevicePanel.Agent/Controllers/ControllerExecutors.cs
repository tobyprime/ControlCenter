using System.Diagnostics;
using System.Text.Json;

namespace DevicePanel.Agent;

/// <summary>控制类型名（面板 ControlTypeKeys 的镜像共识；协议层不感知具体类型，故不放在 Protocol）。</summary>
internal static class ControlTypeNames
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Input = "input";
    public const string Slider = "slider";
}

/// <summary>一次控制执行的结果：Success=false 时 Message 为失败原因（回 ctrl.error）。</summary>
internal sealed record ControllerExecutionResult(bool Success, string? Message = null)
{
    public static ControllerExecutionResult Ok(string message) => new(true, message);

    public static ControllerExecutionResult Fail(string message) => new(false, message);
}

/// <summary>
/// 控制执行器（可插拔动作，三期模块4）：一种控制类型一个执行器，按声明里的 type 路由。
/// 本期只内置演示执行器：无 command 声明时回执动作语义（不驱动真实设备）；声明了 command 时执行本机脚本/命令，
/// 下发参数 JSON 作为 $1 传入（sh -c "$CMD" cmd "$PARAMS"）。真实控制场景不在本期范围。
/// </summary>
internal interface IControllerExecutor
{
    string Type { get; }

    Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>按钮执行器：下发 { value }，回执命中的按钮文案；声明 command 时执行并把 value 作为 $1 追加。</summary>
internal sealed class ButtonControllerExecutor : IControllerExecutor
{
    private readonly ShellCommandRunner _commandRunner;

    public ButtonControllerExecutor(ShellCommandRunner commandRunner) => _commandRunner = commandRunner;

    public string Type => ControlTypeNames.Button;

    public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters, CancellationToken cancellationToken)
    {
        var value = parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        if (string.IsNullOrEmpty(value))
        {
            return Task.FromResult(ControllerExecutionResult.Fail("按钮下发参数无效（缺少 value）"));
        }

        var label = ButtonLabel(controller, value);
        return RunOrEchoAsync(controller, cancellationToken,
            runArgs: value,
            echo: $"已执行按钮「{label}」");
    }

    private static string ButtonLabel(ControllerSpec controller, string value)
    {
        if (controller.ParamsSchema.ValueKind == JsonValueKind.Object &&
            controller.ParamsSchema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v) && v.GetString() == value &&
                    item.TryGetProperty("label", out var l))
                {
                    return l.GetString() ?? value;
                }
            }
        }

        return value;
    }

    private Task<ControllerExecutionResult> RunOrEchoAsync(ControllerSpec controller, CancellationToken ct, string runArgs, string echo) =>
        controller.Command is { Length: > 0 } command
            ? Task.FromResult(_commandRunner.Run(command, runArgs, TimeSpan.FromSeconds(30)))
            : Task.FromResult(ControllerExecutionResult.Ok(echo));
}

/// <summary>开关执行器：下发 { state: bool }，回执目标状态；声明 command 时执行并把 state 作为 $1 追加。</summary>
internal sealed class ToggleControllerExecutor : IControllerExecutor
{
    private readonly ShellCommandRunner _commandRunner;

    public ToggleControllerExecutor(ShellCommandRunner commandRunner) => _commandRunner = commandRunner;

    public string Type => ControlTypeNames.Toggle;

    public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("state", out var state) ||
            state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Task.FromResult(ControllerExecutionResult.Fail("开关下发参数无效（缺少 state）"));
        }

        var on = state.GetBoolean();
        return RunOrEchoAsync(controller, cancellationToken,
            runArgs: on ? "on" : "off",
            echo: $"已切换为「{(on ? "开" : "关")}」");
    }

    private Task<ControllerExecutionResult> RunOrEchoAsync(ControllerSpec controller, CancellationToken ct, string runArgs, string echo) =>
        controller.Command is { Length: > 0 } command
            ? Task.FromResult(_commandRunner.Run(command, runArgs, TimeSpan.FromSeconds(30)))
            : Task.FromResult(ControllerExecutionResult.Ok(echo));
}

/// <summary>输入框执行器：下发 { text }，回执已提交；声明 command 时执行并把 text 作为 $1 追加。</summary>
internal sealed class InputControllerExecutor : IControllerExecutor
{
    private readonly ShellCommandRunner _commandRunner;

    public InputControllerExecutor(ShellCommandRunner commandRunner) => _commandRunner = commandRunner;

    public string Type => ControlTypeNames.Input;

    public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters, CancellationToken cancellationToken)
    {
        var text = parameters.ValueKind == JsonValueKind.Object &&
                   parameters.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (text is null)
        {
            return Task.FromResult(ControllerExecutionResult.Fail("输入框下发参数无效（缺少 text）"));
        }

        return RunOrEchoAsync(controller, cancellationToken,
            runArgs: text,
            echo: "输入已提交");
    }

    private Task<ControllerExecutionResult> RunOrEchoAsync(ControllerSpec controller, CancellationToken ct, string runArgs, string echo) =>
        controller.Command is { Length: > 0 } command
            ? Task.FromResult(_commandRunner.Run(command, runArgs, TimeSpan.FromSeconds(30)))
            : Task.FromResult(ControllerExecutionResult.Ok(echo));
}

/// <summary>滑块执行器：下发 { value: number }，回执目标值；声明 command 时执行并把值作为 $1 追加。</summary>
internal sealed class SliderControllerExecutor : IControllerExecutor
{
    private readonly ShellCommandRunner _commandRunner;

    public SliderControllerExecutor(ShellCommandRunner commandRunner) => _commandRunner = commandRunner;

    public string Type => ControlTypeNames.Slider;

    public Task<ControllerExecutionResult> ExecuteAsync(ControllerSpec controller, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return Task.FromResult(ControllerExecutionResult.Fail("滑块下发参数无效（缺少 value）"));
        }

        var target = value.GetDouble().ToString("0.###");
        return RunOrEchoAsync(controller, cancellationToken,
            runArgs: target,
            echo: $"已设置到 {target}");
    }

    private Task<ControllerExecutionResult> RunOrEchoAsync(ControllerSpec controller, CancellationToken ct, string runArgs, string echo) =>
        controller.Command is { Length: > 0 } command
            ? Task.FromResult(_commandRunner.Run(command, runArgs, TimeSpan.FromSeconds(30)))
            : Task.FromResult(ControllerExecutionResult.Ok(echo));
}

/// <summary>
/// 控制命令运行器（本机 shell）：/bin/sh -c "<command>" cmd "<argument>"，命令内以 $1 引用下发参数。
/// 命令失败（非零退出/超时/启动失败）折算为执行失败原因，绝不向通道抛异常。
/// </summary>
internal sealed class ShellCommandRunner
{
    public ControllerExecutionResult Run(string command, string argument, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    ArgumentList = { "-c", command, "cmd", argument },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();

            // stdout/stderr 同时重定向时必须并发排空，否则缓冲写满会死锁（对齐 ProcessCommandRunner）
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // 进程可能已自行退出，无需处理
                }

                return ControllerExecutionResult.Fail($"控制命令超时（{(int)timeout.TotalSeconds}s）");
            }

            if (process.ExitCode != 0)
            {
                var error = stderr.Result.Trim();
                return ControllerExecutionResult.Fail($"控制命令失败（exit={process.ExitCode}）：{Truncate(error)}");
            }

            var output = stdout.Result.Trim();
            return ControllerExecutionResult.Ok(output.Length > 0 ? output : "控制命令执行成功");
        }
        catch (Exception ex)
        {
            return ControllerExecutionResult.Fail($"控制命令启动失败：{ex.Message}");
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
