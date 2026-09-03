using DevicePanel.Agent;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// LinuxLogsSource 单元测试：用假命令执行器喂入 systemctl/journalctl/docker 的标准输出，
/// 验证服务清单解析、journalctl JSON 行解析（时间戳/级别/消息）、docker logs 行解析与判级、
/// 名称校验与失败传播。命令本身必须是只读的（验收 5）。
/// </summary>
public class LinuxLogsSourceTests
{
    [Fact]
    public void Systemd_Units_Are_Parsed_With_Kind_And_Description()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("systemctl", exitCode: 0, stdout: string.Join('\n',
            "nginx.service   loaded active running A high performance web server",
            "ssh.service     loaded active running OpenBSD Secure Shell server",
            "user@1000.service loaded active running User Manager for UID 1000"));
        var source = new LinuxLogsSource(runner);

        var services = source.ListServices();

        var systemd = services.Where(s => s.Kind == LogsSourceNames.KindSystemd).ToList();
        Assert.Equal(3, systemd.Count);
        Assert.Equal("nginx.service", systemd[0].Name);
        Assert.Equal("A high performance web server", systemd[0].Description);
        Assert.Equal("ssh.service", systemd[1].Name);
        Assert.Equal("user@1000.service", systemd[2].Name);
    }

    [Fact]
    public void Non_Service_Unit_Lines_Are_Skipped()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("systemctl", exitCode: 0, stdout: string.Join('\n',
            "nginx.service loaded active running web",
            "session-2.scope loaded active running Session 2 of user root"));
        var source = new LinuxLogsSource(runner);

        var systemd = source.ListServices().Where(s => s.Kind == LogsSourceNames.KindSystemd).ToList();

        Assert.Single(systemd);
        Assert.Equal("nginx.service", systemd[0].Name);
    }

    [Fact]
    public void Docker_Containers_Are_Parsed_With_Image_And_Status()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("docker", exitCode: 0, stdout: "web\tnginx:1.27\tUp 2 hours\ndb\tmysql:8\tExited (0) 5 minutes ago");
        var source = new LinuxLogsSource(runner);

        var services = source.ListServices();

        var docker = services.Where(s => s.Kind == LogsSourceNames.KindDocker).ToList();
        Assert.Equal(2, docker.Count);
        Assert.Equal("web", docker[0].Name);
        Assert.Equal("nginx:1.27（Up 2 hours）", docker[0].Description);
        Assert.Equal("db", docker[1].Name);
    }

    [Fact]
    public void Systemd_Source_Listed_Before_Docker_Source()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("systemctl", exitCode: 0, stdout: "a.service loaded active running A");
        runner.Enqueue("docker", exitCode: 0, stdout: "web\tnginx\tUp");
        var source = new LinuxLogsSource(runner);

        var services = source.ListServices();

        Assert.Equal(("a.service", LogsSourceNames.KindSystemd), (services[0].Name, services[0].Kind));
        Assert.Equal(("web", LogsSourceNames.KindDocker), (services[1].Name, services[1].Kind));
    }

    [Fact]
    public void Systemd_Absent_Falls_Back_To_Docker_Only()
    {
        var runner = new FakeCommandRunner();
        runner.EnqueueFailure("systemctl", new InvalidOperationException("exec: systemctl: not found"));
        runner.Enqueue("docker", exitCode: 0, stdout: "web\tnginx\tUp");
        var source = new LinuxLogsSource(runner);

        var services = source.ListServices();

        Assert.Single(services);
        Assert.Equal(LogsSourceNames.KindDocker, services[0].Kind);
    }

    [Fact]
    public void Both_Sources_Absent_Yields_Empty_List()
    {
        var runner = new FakeCommandRunner();
        runner.EnqueueFailure("systemctl", new InvalidOperationException("not found"));
        runner.EnqueueFailure("docker", new InvalidOperationException("not found"));
        var source = new LinuxLogsSource(runner);

        Assert.Empty(source.ListServices());
    }

    [Fact]
    public void Journal_Tail_Parses_Timestamp_Level_Message()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("journalctl", exitCode: 0, stdout: string.Join('\n',
            """{"__REALTIME_TIMESTAMP":"1770000000000000","PRIORITY":"3","MESSAGE":"connect() failed (111)"}""",
            """{"__REALTIME_TIMESTAMP":"1770000001000000","PRIORITY":"4","MESSAGE":"upstream timed out"}""",
            """{"__REALTIME_TIMESTAMP":"1770000002000000","PRIORITY":"6","MESSAGE":"Configuration file test is successful"}""",
            """{"__REALTIME_TIMESTAMP":"1770000003000000","PRIORITY":"7","MESSAGE":"debug detail here"}"""));
        var source = new LinuxLogsSource(runner);

        var lines = source.ReadTail("nginx.service", LogsSourceNames.KindSystemd, 100);

        Assert.Equal(4, lines.Count);
        // 1770000000000000 μs = 2026-02-02T02:40:00Z
        Assert.Equal("2026-02-02T02:40:00.000Z", lines[0].Timestamp);
        Assert.Equal(("error", "connect() failed (111)"), (lines[0].Level, lines[0].Message));
        Assert.Equal("warn", lines[1].Level);
        Assert.Equal("info", lines[2].Level);
        Assert.Equal("debug", lines[3].Level);

        var invocation = Assert.Single(runner.Invocations, i => i.FileName == "journalctl");
        Assert.Contains("-u nginx.service", invocation.Arguments);
        Assert.Contains("-n 100", invocation.Arguments);
        Assert.Contains("--no-pager", invocation.Arguments);
    }

    [Fact]
    public void Journal_Tail_With_Non_String_Message_Is_Decoded()
    {
        var runner = new FakeCommandRunner();
        // MESSAGE 为非 UTF-8 字节数组（journalctl 对二进制消息的输出形式）
        runner.Enqueue("journalctl", exitCode: 0, stdout: """{"__REALTIME_TIMESTAMP":"1770000000000000","PRIORITY":"6","MESSAGE":[104,105,33]}""");
        var source = new LinuxLogsSource(runner);

        var lines = source.ReadTail("nginx.service", LogsSourceNames.KindSystemd, 10);

        Assert.Equal("hi!", Assert.Single(lines).Message);
    }

    [Fact]
    public void Journal_Failure_Throws_With_Stderr_Detail()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("journalctl", exitCode: 1, stderr: "Failed to add match \"ghost.service\": No such files");
        var source = new LinuxLogsSource(runner);

        var exception = Assert.Throws<InvalidOperationException>(
            () => source.ReadTail("ghost.service", LogsSourceNames.KindSystemd, 100));

        Assert.Contains("journalctl", exception.Message);
        Assert.Contains("No such files", exception.Message);
    }

    [Fact]
    public void Journal_Corrupt_Line_Is_Skipped_Not_Fatal()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("journalctl", exitCode: 0, stdout: "not-json\n" + """{"__REALTIME_TIMESTAMP":"1770000000000000","PRIORITY":"6","MESSAGE":"ok"}""");
        var source = new LinuxLogsSource(runner);

        var lines = source.ReadTail("nginx.service", LogsSourceNames.KindSystemd, 10);

        Assert.Single(lines);
        Assert.Equal("ok", lines[0].Message);
    }

    [Fact]
    public void Docker_Logs_Are_Parsed_With_Timestamp_And_Level_Heuristic()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("/bin/sh", exitCode: 0, stdout: string.Join('\n',
            "2026-09-04T08:00:00.123456789Z 172.17.0.1 - - [04/Sep/2026 08:00:00] \"GET / HTTP/1.1\" 200",
            "2026-09-04T08:00:01.000000000Z ERROR: upstream connect failed",
            "    at io.netty.channel.AbstractChannel$AbstractUnsafe.register0(AbstractChannel.java:504)"));
        var source = new LinuxLogsSource(runner);

        var lines = source.ReadTail("web", LogsSourceNames.KindDocker, 200);

        Assert.Equal(3, lines.Count);
        Assert.Equal("2026-09-04T08:00:00.123456789Z", lines[0].Timestamp);
        Assert.Equal("info", lines[0].Level);
        Assert.Equal("error", lines[1].Level);
        // 多行堆栈的续行没有时间戳前缀：ts 置空但内容保留
        Assert.Equal(string.Empty, lines[2].Timestamp);
        Assert.Contains("at io.netty", lines[2].Message);

        var invocation = Assert.Single(runner.Invocations, i => i.FileName == "/bin/sh");
        Assert.Contains("docker logs --tail 200 --timestamps 'web'", invocation.Arguments);
        Assert.Contains("2>&1", invocation.Arguments);
    }

    [Fact]
    public void Docker_Logs_Failure_Throws_With_Error_Detail()
    {
        var runner = new FakeCommandRunner();
        runner.Enqueue("/bin/sh", exitCode: 1, stdout: "", stderr: "Error response from daemon: No such container: ghost");
        var source = new LinuxLogsSource(runner);

        var exception = Assert.Throws<InvalidOperationException>(
            () => source.ReadTail("ghost", LogsSourceNames.KindDocker, 100));

        Assert.Contains("docker logs", exception.Message);
        Assert.Contains("No such container", exception.Message);
    }

    [Fact]
    public void Invalid_Service_Name_Is_Rejected_Without_Command()
    {
        var runner = new FakeCommandRunner();
        var source = new LinuxLogsSource(runner);

        Assert.Throws<ArgumentException>(() => source.ReadTail("bad; rm -rf /", LogsSourceNames.KindSystemd, 100));
        Assert.Throws<ArgumentException>(() => source.ReadTail("", LogsSourceNames.KindDocker, 100));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void Unknown_Kind_Is_Not_Supported()
    {
        var source = new LinuxLogsSource(new FakeCommandRunner());

        Assert.Throws<NotSupportedException>(() => source.ReadTail("a.service", "files", 100));
    }

    [Fact]
    public void Level_Classification_Heuristics()
    {
        Assert.Equal("error", LinuxLogsSource.ClassifyLevel("FATAL: config invalid"));
        Assert.Equal("error", LinuxLogsSource.ClassifyLevel("startup failed"));
        Assert.Equal("warn", LinuxLogsSource.ClassifyLevel("WARNING: deprecated option"));
        Assert.Equal("debug", LinuxLogsSource.ClassifyLevel("trace: entering handler"));
        Assert.Equal("info", LinuxLogsSource.ClassifyLevel("listening on port 80"));
    }

    private sealed record Invocation(string FileName, string Arguments);

    /// <summary>假命令执行器：按文件名出队预设结果，记录全部调用。</summary>
    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Queue<(string FileName, CommandResult Result)> _results = new();
        private readonly Queue<(string FileName, Exception Error)> _errors = new();

        public List<Invocation> Invocations { get; } = new();

        public void Enqueue(string fileName, int exitCode, string stdout = "", string stderr = "") =>
            _results.Enqueue((fileName, new CommandResult(exitCode, stdout, stderr)));

        public void EnqueueFailure(string fileName, Exception error) =>
            _errors.Enqueue((fileName, error));

        public CommandResult Run(string fileName, string arguments, TimeSpan timeout)
        {
            Invocations.Add(new Invocation(fileName, arguments));
            while (_errors.Count > 0 && _errors.Peek().FileName == fileName)
            {
                throw _errors.Dequeue().Error;
            }

            while (_results.Count > 0 && _results.Peek().FileName == fileName)
            {
                return _results.Dequeue().Result;
            }

            throw new InvalidOperationException($"未预设的命令调用：{fileName} {arguments}");
        }
    }
}
