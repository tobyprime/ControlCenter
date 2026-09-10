using System.Text;
using DevicePanel.Agent;
using DevicePanel.Protocol;
using Xunit;

namespace DevicePanel.Agent.Tests;

/// <summary>
/// 终端启动噪声过滤器（TOB-403 F8）单元测试：会话开头的已知良性警告行（bash 无法设置进程组/无作业控制）
/// 被白名单丢弃，其余输出原样透传；过滤器在第一段非白名单内容后永久失效，不影响会话后续输出。
/// </summary>
public class TerminalStartupNoiseFilterTests
{
    private const string WarningLine1 = "bash: cannot set terminal process group (-1): Inappropriate ioctl for device\r\n";
    private const string WarningLine2 = "bash: no job control in this shell\r\n";

    [Fact]
    public void Startup_Warning_Lines_Are_Dropped_And_Prompt_Passes()
    {
        var filter = new TerminalStartupNoiseFilter();

        var output = Encoding.UTF8.GetString(filter.Feed(Encoding.UTF8.GetBytes(WarningLine1 + WarningLine2 + "root@host:~# ")));

        Assert.Equal("root@host:~# ", output);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Warning_Line_Split_Across_Feeds_Is_Still_Filtered()
    {
        var filter = new TerminalStartupNoiseFilter();

        var first = filter.Feed(Encoding.UTF8.GetBytes(WarningLine1[..20]));
        var second = filter.Feed(Encoding.UTF8.GetBytes(WarningLine1[20..]));
        var third = Encoding.UTF8.GetString(filter.Feed(Encoding.UTF8.GetBytes(WarningLine2 + "$ ")));

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal("$ ", third);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Prompt_Without_Newline_Is_Flushed_Immediately()
    {
        var filter = new TerminalStartupNoiseFilter();

        var output = Encoding.UTF8.GetString(filter.Feed(Encoding.UTF8.GetBytes("root@host:~# ")));

        // 非白名单前缀的内容不允许被无期限缓冲：立即透传并关闭过滤器
        Assert.Equal("root@host:~# ", output);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Whitelist_Prefix_Is_Buffered_Until_Line_Completes()
    {
        var filter = new TerminalStartupNoiseFilter();

        Assert.Empty(filter.Feed(Encoding.UTF8.GetBytes("bash: can")));

        // EOF 收尾：缓冲的前缀必须原样补发，不允许吞字
        Assert.Equal("bash: can", Encoding.UTF8.GetString(filter.FlushPending()));
    }

    [Fact]
    public void Normal_First_Output_Passes_And_Disables_Filter()
    {
        var filter = new TerminalStartupNoiseFilter();

        var output = Encoding.UTF8.GetString(filter.Feed(Encoding.UTF8.GetBytes("hello\nworld")));

        Assert.Equal("hello\nworld", output);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void Warning_After_First_Real_Output_Is_Kept()
    {
        var filter = new TerminalStartupNoiseFilter();
        filter.Feed(Encoding.UTF8.GetBytes("hello\n"));

        var output = Encoding.UTF8.GetString(filter.Feed(Encoding.UTF8.GetBytes(WarningLine2)));

        Assert.Equal(WarningLine2, output);
    }

    [Fact]
    public async Task Channel_Drops_Bash_Warnings_At_Session_Start()
    {
        var (channel, downlink, factory) = TerminalChannelTestFakes.CreateChannel();
        await channel.HandleAsync(TermOpen("s1"), CancellationToken.None);
        var pty = factory.LastSession!;

        pty.EnqueueOutput(Encoding.UTF8.GetBytes(WarningLine1 + WarningLine2 + "ready"));
        var first = await downlink.WaitForAsync("output", "s1");

        Assert.Equal("ready", Encoding.UTF8.GetString((byte[])first!));
    }

    private static AgentEnvelope TermOpen(string sessionId) =>
        AgentEnvelope.Create(AgentMessageTypes.TermOpen, 1,
            System.Text.Json.JsonSerializer.SerializeToElement(new TermOpenPayload(sessionId, 80, 24),
                AgentJsonContext.Default.TermOpenPayload));
}
