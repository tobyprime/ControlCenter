using DevicePanel.Agent;
using Xunit;

namespace DevicePanel.Agent.Tests;

public class LinuxMetricsReaderTests
{
    private const string SampleProcStat = """
        cpu  100 0 200 700 100 0 0 0 0 0
        cpu0 50 0 100 350 50 0 0 0 0 0
        cpu1 50 0 100 350 50 0 0 0 0 0
        intr 12345
        """;

    private const string SampleMemInfo = """
        MemTotal:       16384000 kB
        MemFree:         4096000 kB
        MemAvailable:    8192000 kB
        Buffers:          204800 kB
        Cached:          1024000 kB
        SwapTotal:             0 kB
        SwapFree:              0 kB
        """;

    private const string SampleNetDev = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo: 9999999    9999    0    0    0     0          0         0  9999999    9999    0    0    0     0       0          0
          eth0: 1000000    1000    0    0    0     0          0         0   500000     800    0    0    0     0       0          0
        wlan0: 3000000    3000    0    0    0     0          0         0   1500000    2500    0    0    0     0       0          0
        """;

    [Fact]
    public void ParseCpuSnapshot_Sums_First_Cpu_Line()
    {
        var snapshot = LinuxMetricsReader.ParseCpuSnapshot(SampleProcStat);

        Assert.NotNull(snapshot);
        // idle = idle(700) + iowait(100)；total = 各字段之和（100+0+200+700+100）
        Assert.Equal(800, snapshot!.Value.Idle);
        Assert.Equal(1100, snapshot.Value.Total);
    }

    [Fact]
    public void ParseCpuSnapshot_Missing_Cpu_Line_Returns_Null()
    {
        Assert.Null(LinuxMetricsReader.ParseCpuStatLine("intr 12345"));
    }

    [Fact]
    public void ComputeCpuPercent_Proportional_To_Idle_Delta()
    {
        var prev = new CpuSnapshot(Idle: 800, Total: 1100);
        var cur = new CpuSnapshot(Idle: 1300, Total: 2100); // Δidle=500，Δtotal=1000

        var percent = LinuxMetricsReader.ComputeCpuPercent(prev, cur);

        Assert.Equal(50.0, percent, precision: 1);
    }

    [Fact]
    public void ComputeCpuPercent_Non_Positive_Delta_Returns_Zero()
    {
        var snapshot = new CpuSnapshot(Idle: 800, Total: 1100);

        Assert.Equal(0, LinuxMetricsReader.ComputeCpuPercent(snapshot, snapshot));
        Assert.Equal(0, LinuxMetricsReader.ComputeCpuPercent(snapshot, new CpuSnapshot(Idle: 100, Total: 50)));
    }

    [Fact]
    public void ParseMemInfo_Reads_Total_And_Available()
    {
        var mem = LinuxMetricsReader.ParseMemInfo(SampleMemInfo);

        Assert.NotNull(mem);
        Assert.Equal(16384000, mem!.Value.TotalKb);
        Assert.Equal(8192000, mem.Value.AvailableKb);
    }

    [Fact]
    public void ParseMemInfo_Missing_Fields_Returns_Null()
    {
        Assert.Null(LinuxMetricsReader.ParseMemInfo("SwapTotal: 0 kB"));
    }

    [Fact]
    public void MemPercent_Uses_Total_And_Available()
    {
        // (16384000 - 8192000) / 16384000 = 50%
        Assert.Equal(50.0, LinuxMetricsReader.ComputeMemPercent(16384000, 8192000), precision: 1);
    }

    [Fact]
    public void ParseNetTotals_Sums_Physical_Interfaces_Excluding_Loopback()
    {
        var totals = LinuxMetricsReader.ParseNetTotals(SampleNetDev);

        Assert.NotNull(totals);
        // lo 排除：rx = 1000000 + 3000000，tx = 500000 + 1500000
        Assert.Equal(4000000, totals!.Value.RxBytes);
        Assert.Equal(2000000, totals.Value.TxBytes);
    }

    [Fact]
    public void ComputeNetBytesPerSec_Proportional_To_Elapsed_Time()
    {
        var prev = new NetSnapshot(RxBytes: 4000000, TxBytes: 2000000);

        var (rx, tx) = LinuxMetricsReader.ComputeNetBytesPerSec(
            prev, new NetSnapshot(RxBytes: 4600000, TxBytes: 2030000), TimeSpan.FromSeconds(30));

        Assert.Equal(20000.0, rx, precision: 0);
        Assert.Equal(1000.0, tx, precision: 0);
    }

    [Fact]
    public void ComputeNetBytesPerSec_Non_Positive_Elapsed_Returns_Zero()
    {
        var snapshot = new NetSnapshot(RxBytes: 100, TxBytes: 100);

        var (rx, tx) = LinuxMetricsReader.ComputeNetBytesPerSec(snapshot, snapshot, TimeSpan.Zero);

        Assert.Equal(0, rx);
        Assert.Equal(0, tx);
    }

    [Fact]
    public void ParseNetTotals_Malformed_Lines_Are_Skipped()
    {
        var totals = LinuxMetricsReader.ParseNetTotals("garbage line without colon\n  eth0: 10 20 0 0 0 0 0 0 30 40 0 0 0 0 0 0\n");

        Assert.NotNull(totals);
        Assert.Equal(10, totals!.Value.RxBytes);
        Assert.Equal(30, totals.Value.TxBytes);
    }
}
