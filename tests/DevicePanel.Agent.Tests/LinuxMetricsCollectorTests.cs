using DevicePanel.Agent;
using Xunit;

namespace DevicePanel.Agent.Tests;

public class LinuxMetricsCollectorTests
{
    [Fact]
    public void First_Sample_Reports_Zero_For_Cpu_And_Net_Rates()
    {
        var sources = new MetricSources();
        var collector = new LinuxMetricsCollector(sources.ReadProcStat, sources.ReadMemInfo, sources.ReadNetDev, sources.ReadDiskPercent, new TestClock());

        var sample = collector.Sample();

        Assert.Equal(0, sample.CpuPercent);
        Assert.Equal(0, sample.NetRxBytesPerSec);
        Assert.Equal(0, sample.NetTxBytesPerSec);
        Assert.Equal(50, sample.MemPercent, precision: 1);
        Assert.Equal(25, sample.DiskPercent, precision: 1);
    }

    [Fact]
    public void Second_Sample_Computes_Cpu_And_Net_From_Deltas()
    {
        var clock = new TestClock();
        var sources = new MetricSources();
        var collector = new LinuxMetricsCollector(sources.ReadProcStat, sources.ReadMemInfo, sources.ReadNetDev, sources.ReadDiskPercent, clock);

        sources.ProcStat = "cpu  100 0 200 700 100 0 0 0 0 0";
        sources.NetDev = "  eth0: 4000000 1000 0 0 0 0 0 0 2000000 800 0 0 0 0 0 0";
        collector.Sample();

        clock.Now = clock.Now.AddSeconds(30);
        // Δidle = 1300-800 = 500，Δtotal = 2100-1100 = 1000 → CPU 50%
        sources.ProcStat = "cpu  400 0 400 1300 0 0 0 0 0 0";
        sources.NetDev = "  eth0: 4600000 1600 0 0 0 0 0 0 2030000 900 0 0 0 0 0 0"; // rx 20KB/s，tx 1KB/s

        var sample = collector.Sample();

        Assert.Equal(50, sample.CpuPercent, precision: 1);
        Assert.Equal(20000, sample.NetRxBytesPerSec, precision: 0);
        Assert.Equal(1000, sample.NetTxBytesPerSec, precision: 0);
    }

    [Fact]
    public void Sample_Survives_Unreadable_Sources()
    {
        var collector = new LinuxMetricsCollector(
            procStat: () => throw new IOException("no proc"),
            memInfo: () => throw new IOException("no meminfo"),
            netDev: () => throw new IOException("no netdev"),
            diskPercent: () => throw new IOException("no disk"));

        var sample = collector.Sample();

        Assert.Equal(0, sample.CpuPercent);
        Assert.Equal(0, sample.MemPercent);
        Assert.Equal(0, sample.DiskPercent);
        Assert.Equal(0, sample.NetRxBytesPerSec);
        Assert.Equal(0, sample.NetTxBytesPerSec);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>模拟 /proc 与磁盘读数：字段可改写，读取委托即时取当前值。</summary>
    private sealed class MetricSources
    {
        public string ProcStat = "cpu  0 0 0 0 0 0 0 0 0 0";
        public string MemInfo = "MemTotal: 1000 kB\nMemAvailable: 500 kB";
        public string NetDev = "  eth0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
        public double DiskPercentValue = 25;

        public Func<string> ReadProcStat => () => ProcStat;
        public Func<string> ReadMemInfo => () => MemInfo;
        public Func<string> ReadNetDev => () => NetDev;
        public Func<double> ReadDiskPercent => () => DiskPercentValue;
    }
}
