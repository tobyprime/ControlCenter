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
            diskPercent: () => throw new IOException("no disk"),
            diskStats: () => throw new IOException("no diskstats"),
            temperatures: () => throw new IOException("no hwmon"));

        var sample = collector.Sample();

        Assert.Equal(0, sample.CpuPercent);
        Assert.Equal(0, sample.MemPercent);
        Assert.Equal(0, sample.DiskPercent);
        Assert.Equal(0, sample.NetRxBytesPerSec);
        Assert.Equal(0, sample.NetTxBytesPerSec);
        Assert.Equal(0, sample.MemUsedBytes);
        Assert.Equal(0, sample.MemTotalBytes);
        Assert.Equal(0, sample.DiskReadBytesPerSec);
        Assert.Equal(0, sample.DiskWriteBytesPerSec);
        Assert.Null(sample.TempCelsius);
        Assert.Null(sample.TempSensor);
    }

    [Fact]
    public void First_Sample_Reports_Mem_Used_Total_Temp_And_Zero_Disk_Rates()
    {
        var clock = new TestClock();
        var sources = new MetricSources
        {
            MemInfo = "MemTotal: 16384000 kB\nMemAvailable: 8192000 kB",
            Temperatures = [new TempReading("acpitz", 39), new TempReading("coretemp Package id 0", 61.5)],
            DiskStats = "8 0 sda 100 0 200 10 50 0 100 5 0 15 0 0 0 0",
        };
        var collector = new LinuxMetricsCollector(
            sources.ReadProcStat, sources.ReadMemInfo, sources.ReadNetDev, sources.ReadDiskPercent,
            clock, sources.ReadDiskStats, sources.ReadTemperatures);

        var sample = collector.Sample();

        Assert.Equal(50, sample.MemPercent, precision: 1);
        Assert.Equal(16384000L * 1024, sample.MemTotalBytes, precision: 0);
        Assert.Equal(8192000L * 1024, sample.MemUsedBytes, precision: 0);
        // CPU 相关传感器取最大值，保留传感器名
        Assert.Equal(61.5, sample.TempCelsius!.Value, precision: 1);
        Assert.Equal("coretemp Package id 0", sample.TempSensor);
        Assert.Equal(0, sample.DiskReadBytesPerSec);
        Assert.Equal(0, sample.DiskWriteBytesPerSec);
    }

    [Fact]
    public void Second_Sample_Computes_Disk_Rates_From_Deltas()
    {
        var clock = new TestClock();
        var sources = new MetricSources
        {
            DiskStats = "8 0 sda 100 0 200 10 50 0 100 5 0 15 0 0 0 0",
        };
        var collector = new LinuxMetricsCollector(
            sources.ReadProcStat, sources.ReadMemInfo, sources.ReadNetDev, sources.ReadDiskPercent,
            clock, sources.ReadDiskStats, sources.ReadTemperatures);
        collector.Sample();

        clock.Now = clock.Now.AddSeconds(30);
        // Δread 300 扇区 × 512B / 30s = 5120 B/s；Δwrite 60 扇区 × 512B / 30s = 1024 B/s
        sources.DiskStats = "8 0 sda 500 0 500 10 80 0 160 5 0 45 0 0 0 0";

        var sample = collector.Sample();

        Assert.Equal(5120, sample.DiskReadBytesPerSec, precision: 0);
        Assert.Equal(1024, sample.DiskWriteBytesPerSec, precision: 0);
    }

    [Fact]
    public void Sample_Without_Cpu_Sensor_Reports_No_Temperature()
    {
        var sources = new MetricSources
        {
            Temperatures = [new TempReading("acpitz", 39)],
        };
        var collector = new LinuxMetricsCollector(
            sources.ReadProcStat, sources.ReadMemInfo, sources.ReadNetDev, sources.ReadDiskPercent,
            clock: null, diskStats: sources.ReadDiskStats, temperatures: sources.ReadTemperatures);

        var sample = collector.Sample();

        Assert.Null(sample.TempCelsius);
        Assert.Null(sample.TempSensor);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>模拟 /proc、磁盘与温度读数：字段可改写，读取委托即时取当前值。</summary>
    private sealed class MetricSources
    {
        public string ProcStat = "cpu  0 0 0 0 0 0 0 0 0 0";
        public string MemInfo = "MemTotal: 1000 kB\nMemAvailable: 500 kB";
        public string NetDev = "  eth0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";
        public double DiskPercentValue = 25;
        public string DiskStats = "8 0 sda 0 0 0 0 0 0 0 0 0 0 0 0 0";
        public IReadOnlyList<TempReading> Temperatures = [];

        public Func<string> ReadProcStat => () => ProcStat;
        public Func<string> ReadMemInfo => () => MemInfo;
        public Func<string> ReadNetDev => () => NetDev;
        public Func<double> ReadDiskPercent => () => DiskPercentValue;
        public Func<string> ReadDiskStats => () => DiskStats;
        public Func<IReadOnlyList<TempReading>> ReadTemperatures => () => Temperatures;
    }
}
