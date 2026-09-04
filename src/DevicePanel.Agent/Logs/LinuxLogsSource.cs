using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevicePanel.Agent;

/// <summary>目标机上可查看日志的一个服务（systemd unit / docker 容器）。</summary>
internal sealed record LogService(string Name, string Kind, string Description);

/// <summary>结构化的一行日志：timestamp 为 ISO-8601 UTC（无法解析时为空串），level ∈ error/warn/info/debug。</summary>
internal sealed record LogLine(string Timestamp, string Level, string Message);

/// <summary>日志源名称（服务名）校验：systemd unit 名与 docker 容器名的字符白名单，杜绝参数注入。</summary>
internal static class LogsSourceNames
{
    public const string KindSystemd = "systemd";
    public const string KindDocker = "docker";

    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._@:-]{0,199}$", RegexOptions.Compiled);

    public static bool IsValidName(string name) => NamePattern.IsMatch(name);
}

/// <summary>日志源抽象（扩展点）：列出可查看日志的服务 / 只读拉取尾部 N 行。实现方须保证调用为只读。</summary>
internal interface ILogsSource
{
    /// <summary>列出目标机当前可查看日志的服务；单个来源不可用（未装 systemd/docker）时跳过该来源。</summary>
    IReadOnlyList<LogService> ListServices();

    /// <summary>只读拉取指定服务尾部 N 行；服务不存在或命令失败时抛异常（由通道折算成 logs.error）。</summary>
    IReadOnlyList<LogLine> ReadTail(string service, string kind, int lines);
}

/// <summary>外部命令执行抽象：一次运行，捕获 stdout/stderr 与退出码；超时杀进程。</summary>
internal interface ICommandRunner
{
    CommandResult Run(string fileName, string arguments, TimeSpan timeout);
}

internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>System.Diagnostics.Process 实现（NativeAOT 兼容）。</summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, string arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();

        // stdout/stderr 同时重定向时必须并发排空，否则缓冲写满会死锁
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

            throw new TimeoutException($"命令执行超时（{(int)timeout.TotalSeconds}s）：{fileName}");
        }

        return new CommandResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

/// <summary>
/// Linux 日志源：systemd（systemctl list-units / journalctl）与 docker（docker ps / docker logs）。
/// 服务清单动态发现（按需执行只读命令，不落任何状态）：
/// - systemctl 不可用（非 systemd 主机）→ 跳过 systemd 来源；
/// - docker 不可用 → 跳过 docker 来源；两者都不可用 → 空清单（面板提示无可查看服务）。
/// 取舍：动态发现零配置、如实反映目标机当前状态，代价是清单非实时缓存（每次打开日志页查询一次，频率可忽略）；
/// 不采用静态配置（易过期、需维护）与 list-unit-files（包含从未启动、无日志可看的单元）。
/// </summary>
internal sealed partial class LinuxLogsSource : ILogsSource
{
    /// <summary>journalctl 按行输出 JSON，journalctl 对大日志也应快速返回；超时按失败处理并回 logs.error。</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly ICommandRunner _runner;

    public LinuxLogsSource(ICommandRunner runner)
    {
        _runner = runner;
    }

    public IReadOnlyList<LogService> ListServices()
    {
        var services = new List<LogService>();
        services.AddRange(ListSystemdServices());
        services.AddRange(ListDockerServices());
        return services;
    }

    public IReadOnlyList<LogLine> ReadTail(string service, string kind, int lines)
    {
        if (!LogsSourceNames.IsValidName(service))
        {
            throw new ArgumentException("服务名包含非法字符", nameof(service));
        }

        return kind switch
        {
            LogsSourceNames.KindSystemd => ReadJournalTail(service, lines),
            LogsSourceNames.KindDocker => ReadDockerTail(service, lines),
            _ => throw new NotSupportedException($"不支持的日志来源：{kind}"),
        };
    }

    private IReadOnlyList<LogService> ListSystemdServices()
    {
        CommandResult result;
        try
        {
            result = _runner.Run("systemctl", "list-units --type=service --no-legend --no-pager", CommandTimeout);
        }
        catch (Exception)
        {
            return []; // systemctl 不可用/超时：跳过 systemd 来源，不影响 docker 来源
        }

        if (result.ExitCode != 0)
        {
            return [];
        }

        // 行格式：UNIT LOAD ACTIVE SUB DESCRIPTION（--no-legend 去表头；各列以空白分隔，描述自身可含空格）
        var services = new List<LogService>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 4 || !columns[0].EndsWith(".service", StringComparison.Ordinal))
            {
                continue;
            }

            var description = columns.Length > 4 ? string.Join(' ', columns[4..]) : string.Empty;
            services.Add(new LogService(columns[0], LogsSourceNames.KindSystemd, description));
        }

        return services;
    }

    private IReadOnlyList<LogService> ListDockerServices()
    {
        CommandResult result;
        try
        {
            result = _runner.Run("docker", "ps -a --format '{{.Names}}\\t{{.Image}}\\t{{.Status}}'", CommandTimeout);
        }
        catch (Exception)
        {
            return []; // docker 不可用/超时：跳过 docker 来源
        }

        if (result.ExitCode != 0)
        {
            return [];
        }

        var services = new List<LogService>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('\t', 3, StringSplitOptions.TrimEntries);
            if (columns.Length == 0 || columns[0].Length == 0)
            {
                continue;
            }

            services.Add(new LogService(columns[0], LogsSourceNames.KindDocker,
                columns.Length >= 3 ? $"{columns[1]}（{columns[2]}）" : string.Empty));
        }

        return services;
    }

    /// <summary>journalctl -o json 按行输出 JSON 对象：__REALTIME_TIMESTAMP（μs epoch）+ PRIORITY（0-7）+ MESSAGE。</summary>
    private IReadOnlyList<LogLine> ReadJournalTail(string service, int lines)
    {
        var result = _runner.Run("journalctl",
            $"-u {service} -n {lines} --no-pager --output=json", CommandTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"journalctl 失败（exit={result.ExitCode}）：{FirstLine(result.Stderr)}");
        }

        var entries = new List<LogLine>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement entry;
            try
            {
                entry = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                continue; // 单行损坏不中断整次拉取
            }

            entries.Add(new LogLine(
                Timestamp: ParseJournalTimestamp(entry),
                Level: ParseJournalLevel(entry),
                Message: ParseJournalMessage(entry)));
        }

        return entries;
    }

    /// <summary>docker logs --timestamps：stdout/stderr 经 sh 合并后逐行 “ISO 时间 消息”。</summary>
    private IReadOnlyList<LogLine> ReadDockerTail(string service, int lines)
    {
        var quoted = $"'{service.Replace("'", string.Empty)}'";
        var result = _runner.Run("/bin/sh", $"-c \"docker logs --tail {lines} --timestamps {quoted} 2>&1\"", CommandTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker logs 失败（exit={result.ExitCode}）：{FirstLine(result.Stdout + result.Stderr)}");
        }

        var entries = new List<LogLine>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // 无时间戳前缀的行（容器应用自己输出的多行堆栈）：ts 置空，按内容判级
            var match = DockerTimestampRegex().Match(line);
            var timestamp = match.Success ? match.Groups[1].Value : string.Empty;
            var message = match.Success ? line[match.Length..].TrimStart() : line;
            entries.Add(new LogLine(timestamp, ClassifyLevel(message), message));
        }

        return entries;
    }

    internal static string ParseJournalTimestamp(JsonElement entry) =>
        entry.TryGetProperty("__REALTIME_TIMESTAMP", out var raw) &&
        raw.GetString() is { } micros &&
        long.TryParse(micros, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value / 1000).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : string.Empty;

    internal static string ParseJournalLevel(JsonElement entry)
    {
        if (!entry.TryGetProperty("PRIORITY", out var priority) || priority.GetString() is not { } text ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            return "info";
        }

        return level switch
        {
            <= 3 => "error", // emerg/alert/crit/err
            4 => "warn",     // warning
            7 => "debug",    // debug
            _ => "info",     // notice/info
        };
    }

    internal static string ParseJournalMessage(JsonElement entry)
    {
        if (!entry.TryGetProperty("MESSAGE", out var message))
        {
            return string.Empty;
        }

        if (message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? string.Empty;
        }

        // 非 UTF-8 消息 journalctl 以字节数组输出：尽力还原
        if (message.ValueKind == JsonValueKind.Array)
        {
            var bytes = new byte[message.GetArrayLength()];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)message[i].GetInt32();
            }

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        return message.GetRawText();
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)\s")]
    private static partial Regex DockerTimestampRegex();

    /// <summary>消息内容判级：docker 日志无统一级别字段，按常见关键词启发式归类。</summary>
    internal static string ClassifyLevel(string message)
    {
        if (Regex.IsMatch(message, @"\b(error|err|fatal|critical|panic|failed|failure|exception)\b", RegexOptions.IgnoreCase))
        {
            return "error";
        }

        if (Regex.IsMatch(message, @"\b(warn|warning)\b", RegexOptions.IgnoreCase))
        {
            return "warn";
        }

        if (Regex.IsMatch(message, @"\b(debug|trace)\b", RegexOptions.IgnoreCase))
        {
            return "debug";
        }

        return "info";
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline > 0 ? trimmed[..newline].Trim() : trimmed;
    }
}
