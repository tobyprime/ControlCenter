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

    [Theory]
    [InlineData("sda", true)]
    [InlineData("sdaa", true)]
    [InlineData("hda", true)]
    [InlineData("vdb", true)]
    [InlineData("xvdf", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("mmcblk0", true)]
    [InlineData("sda1", false)]
    [InlineData("nvme0n1p2", false)]
    [InlineData("mmcblk0p1", false)]
    [InlineData("loop0", false)]
    [InlineData("ram0", false)]
    [InlineData("dm-0", false)]
    [InlineData("sr0", false)]
    [InlineData("fd0", false)]
    public void IsWholeDiskDevice_Distinguishes_Disks_From_Partitions_And_Virtual_Devices(string name, bool expected)
    {
        Assert.Equal(expected, LinuxMetricsReader.IsWholeDiskDevice(name));
    }

    [Fact]
    public void ParseDiskTotals_Sums_Whole_Disks_Only()
    {
        const string diskStats = """
            8       0 sda 1000 0 200000 100 500 0 100000 200 0 300 0 0 0 0
            8       1 sda1 400 0 80000 40 200 0 40000 80 0 120 0 0 0 0
            7       0 loop0 10 0 100 5 0 0 0 0 0 5 0 0 0 0
            253     0 dm-0 900 0 180000 90 450 0 90000 180 0 270 0 0 0 0
            259     0 nvme0n1 2000 0 400000 200 1000 0 200000 400 0 600 0 0 0 0
            259     1 nvme0n1p1 300 0 60000 30 150 0 30000 60 0 90 0 0 0 0
            1       0 ram0 5 0 10 1 0 0 0 0 0 1 0 0 0 0
            """;

        var totals = LinuxMetricsReader.ParseDiskTotals(diskStats);

        Assert.NotNull(totals);
        // 分区（sda1/nvme0n1p1）、dm、loop、ram 全部排除：sda + nvme0n1
        Assert.Equal(600000, totals!.Value.SectorsRead);
        Assert.Equal(300000, totals.Value.SectorsWritten);
    }

    [Fact]
    public void ParseDiskTotals_Malformed_Lines_Are_Skipped()
    {
        var totals = LinuxMetricsReader.ParseDiskTotals(
            "garbage\n8 0 sda 10 0 20 5 6 0 40 8 0 9 0 0 0 0\n8 1 sdb short line\n");

        Assert.NotNull(totals);
        Assert.Equal(20, totals!.Value.SectorsRead);
        Assert.Equal(40, totals.Value.SectorsWritten);
    }

    [Fact]
    public void ParseDiskTotals_No_Whole_Disk_Returns_Null()
    {
        Assert.Null(LinuxMetricsReader.ParseDiskTotals("7 0 loop0 10 0 100 5 0 0 0 0 0 5 0 0 0 0\n"));
        Assert.Null(LinuxMetricsReader.ParseDiskTotals("garbage\n"));
    }

    [Fact]
    public void ComputeDiskBytesPerSec_Proportional_To_Elapsed_Time()
    {
        var prev = new DiskIoSnapshot(SectorsRead: 600000, SectorsWritten: 300000);

        var (read, write) = LinuxMetricsReader.ComputeDiskBytesPerSec(
            prev, new DiskIoSnapshot(SectorsRead: 900000, SectorsWritten: 360000), TimeSpan.FromSeconds(30));

        // Δread 300000 扇区 × 512B / 30s = 5 MB/s
        Assert.Equal(5120000.0, read, precision: 0);
        Assert.Equal(1024000.0, write, precision: 0);
    }

    [Fact]
    public void ComputeDiskBytesPerSec_Non_Positive_Elapsed_Returns_Zero()
    {
        var snapshot = new DiskIoSnapshot(SectorsRead: 100, SectorsWritten: 100);

        var (read, write) = LinuxMetricsReader.ComputeDiskBytesPerSec(snapshot, snapshot, TimeSpan.Zero);

        Assert.Equal(0, read);
        Assert.Equal(0, write);
    }

    [Fact]
    public void SelectCpuTemperature_Takes_Max_Of_Cpu_Related_Sensors_And_Keeps_Name()
    {
        var readings = new[]
        {
            new TempReading("acpitz", 39.5),
            new TempReading("coretemp Package id 0", 61.0),
            new TempReading("coretemp Core 0", 58.5),
            new TempReading("k10temp Tdie", 63.25),
            new TempReading("cpu_thermal", 55.0),
        };

        var selected = LinuxMetricsReader.SelectCpuTemperature(readings);

        Assert.NotNull(selected);
        Assert.Equal("k10temp Tdie", selected.Sensor);
        Assert.Equal(63.25, selected.Celsius, precision: 2);
    }

    [Fact]
    public void SelectCpuTemperature_No_Cpu_Related_Sensor_Returns_Null()
    {
        Assert.Null(LinuxMetricsReader.SelectCpuTemperature([new TempReading("acpitz", 39.5)]));
        Assert.Null(LinuxMetricsReader.SelectCpuTemperature([]));
    }

    [Theory]
    [InlineData("coretemp", true)]
    [InlineData("k10temp", true)]
    [InlineData("k8temp", true)]
    [InlineData("zenpower", true)]
    [InlineData("cpu_thermal", true)]
    [InlineData("x86_pkg_temp", true)]
    [InlineData("coretemp Package id 0", true)]
    [InlineData("k10temp Tctl", true)]
    [InlineData("k10temp Tdie", true)]
    [InlineData("acpitz", false)]
    [InlineData("pch_wildcat_point", false)]
    [InlineData("nouveau", false)]
    public void IsCpuTemperatureSensor_Matches_Cpu_Related_Names_Only(string sensor, bool expected)
    {
        Assert.Equal(expected, LinuxMetricsReader.IsCpuTemperatureSensor(sensor));
    }

    [Theory]
    [InlineData("45000", 45.0)]
    [InlineData("-21000", -21.0)]
    [InlineData("27500", 27.5)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("999999", null)]
    [InlineData("-999999", null)]
    public void ParseTempMilliCelsius_Converts_And_Rejects_Implausible_Values(string text, double? expected)
    {
        // 测试值（45/-21/27.5）均为二进制可精确表示的换算结果，可直接相等断言
        Assert.Equal(expected, LinuxMetricsReader.ParseTempMilliCelsius(text));
    }
}
