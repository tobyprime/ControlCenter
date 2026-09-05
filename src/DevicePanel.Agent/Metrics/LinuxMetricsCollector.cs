using System.Globalization;

namespace DevicePanel.Agent;

/// <summary>单次采集的设备指标快照：百分比 0-100，网络/磁盘速率为字节/秒；内存含 used/total 字节值；温度为 CPU 相关传感器最大值（无传感器为 null）。</summary>
internal sealed record MetricsSample(
    double CpuPercent,
    double MemPercent,
    double DiskPercent,
    double NetRxBytesPerSec,
    double NetTxBytesPerSec,
    double MemUsedBytes = 0,
    double MemTotalBytes = 0,
    double DiskReadBytesPerSec = 0,
    double DiskWriteBytesPerSec = 0,
    double? TempCelsius = null,
    string? TempSensor = null);

/// <summary>单次温度读数：传感器名（hwmon 芯片名 + 标签，或 thermal_zone 类型）+ 摄氏度。</summary>
internal sealed record TempReading(string Sensor, double Celsius);

/// <summary>指标采集抽象：每次 Sample 返回当前时刻快照（CPU/网络/磁盘读写基于与上次的增量计算）。</summary>
internal interface IMetricsCollector
{
    MetricsSample Sample();
}

/// <summary>
/// Linux 指标采集：/proc/stat（CPU）、/proc/meminfo（内存）、/proc/net/dev（网络，排除 lo）、根文件系统用量（磁盘）、
/// /proc/diskstats（整机磁盘读写速率，仅统计整盘避免分区重复累计）、hwmon/thermal（CPU 相关温度取最大）。
/// CPU、网络与磁盘速率需要前后两次采样做差：首个周期报告 0，仅表现为第一个数据点数值偏保守，不影响后续曲线。
/// 无温度传感器的设备不产出温度值（指标无数据即不展示，不算异常）。
/// </summary>
internal sealed class LinuxMetricsCollector : IMetricsCollector
{
    private readonly Func<string> _procStat;
    private readonly Func<string> _memInfo;
    private readonly Func<string> _netDev;
    private readonly Func<double> _diskPercent;
    private readonly Func<string>? _diskStats;
    private readonly Func<IReadOnlyList<TempReading>>? _temperatures;
    private readonly TimeProvider _clock;
    private CpuSnapshot? _lastCpu;
    private NetSnapshot? _lastNet;
    private DateTimeOffset? _lastNetAt;
    private DiskIoSnapshot? _lastDisk;
    private DateTimeOffset? _lastDiskAt;

    public LinuxMetricsCollector()
        : this(
            procStat: () => File.ReadAllText("/proc/stat"),
            memInfo: () => File.ReadAllText("/proc/meminfo"),
            netDev: () => File.ReadAllText("/proc/net/dev"),
            diskPercent: () => ComputeRootDiskPercent(),
            diskStats: () => File.ReadAllText("/proc/diskstats"),
            temperatures: ReadTemperatures,
            clock: TimeProvider.System)
    {
    }

    internal LinuxMetricsCollector(
        Func<string> procStat,
        Func<string> memInfo,
        Func<string> netDev,
        Func<double> diskPercent,
        TimeProvider? clock = null,
        Func<string>? diskStats = null,
        Func<IReadOnlyList<TempReading>>? temperatures = null)
    {
        _procStat = procStat;
        _memInfo = memInfo;
        _netDev = netDev;
        _diskPercent = diskPercent;
        _diskStats = diskStats;
        _temperatures = temperatures;
        _clock = clock ?? TimeProvider.System;
    }

    public MetricsSample Sample()
    {
        var nowUtc = _clock.GetUtcNow();
        double cpuPercent = 0, memPercent = 0, diskPercent = 0, netRx = 0, netTx = 0;
        double memUsed = 0, memTotal = 0, diskRead = 0, diskWrite = 0;
        double? tempCelsius = null;
        string? tempSensor = null;

        try
        {
            var cpu = LinuxMetricsReader.ParseCpuSnapshot(_procStat());
            if (cpu is not null)
            {
                cpuPercent = LinuxMetricsReader.ComputeCpuPercent(_lastCpu, cpu.Value);
                _lastCpu = cpu;
            }
        }
        catch (IOException)
        {
        }

        try
        {
            var mem = LinuxMetricsReader.ParseMemInfo(_memInfo());
            if (mem is not null)
            {
                memPercent = LinuxMetricsReader.ComputeMemPercent(mem.Value.TotalKb, mem.Value.AvailableKb);
                memTotal = mem.Value.TotalKb * 1024;
                memUsed = (mem.Value.TotalKb - mem.Value.AvailableKb) * 1024;
            }
        }
        catch (IOException)
        {
        }

        try
        {
            diskPercent = _diskPercent();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (_diskStats is not null)
        {
            try
            {
                var disk = LinuxMetricsReader.ParseDiskTotals(_diskStats());
                if (disk is not null)
                {
                    if (_lastDisk is { } prevDisk && _lastDiskAt is { } prevDiskAt)
                    {
                        (diskRead, diskWrite) = LinuxMetricsReader.ComputeDiskBytesPerSec(prevDisk, disk.Value, nowUtc - prevDiskAt);
                    }

                    _lastDisk = disk;
                    _lastDiskAt = nowUtc;
                }
            }
            catch (IOException)
            {
            }
        }

        try
        {
            var net = LinuxMetricsReader.ParseNetTotals(_netDev());
            if (net is not null)
            {
                if (_lastNet is { } prev && _lastNetAt is { } prevAt)
                {
                    (netRx, netTx) = LinuxMetricsReader.ComputeNetBytesPerSec(prev, net.Value, nowUtc - prevAt);
                }

                _lastNet = net;
                _lastNetAt = nowUtc;
            }
        }
        catch (IOException)
        {
        }

        if (_temperatures is not null)
        {
            try
            {
                var cpuTemp = LinuxMetricsReader.SelectCpuTemperature(_temperatures());
                if (cpuTemp is not null)
                {
                    tempCelsius = cpuTemp.Celsius;
                    tempSensor = cpuTemp.Sensor;
                }
            }
            catch (IOException)
            {
            }
        }

        return new MetricsSample(
            Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
            Math.Round(Math.Clamp(memPercent, 0, 100), 1),
            Math.Round(Math.Clamp(diskPercent, 0, 100), 1),
            Math.Round(Math.Max(netRx, 0), 1),
            Math.Round(Math.Max(netTx, 0), 1),
            Math.Round(Math.Max(memUsed, 0), 0),
            Math.Round(Math.Max(memTotal, 0), 0),
            Math.Round(Math.Max(diskRead, 0), 1),
            Math.Round(Math.Max(diskWrite, 0), 1),
            tempCelsius is null ? null : Math.Round(tempCelsius.Value, 1),
            tempSensor);
    }

    private static double ComputeRootDiskPercent()
    {
        var root = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == "/" && d.IsReady);
        if (root is null)
        {
            return 0;
        }

        var total = root.TotalSize;
        return total > 0 ? 100.0 * (total - root.AvailableFreeSpace) / total : 0;
    }

    /// <summary>
    /// 读取温度传感器：/sys/class/hwmon/hwmon*/（芯片名 name + tempN_input，标签 tempN_label 优先）
    /// 与 /sys/class/thermal/thermal_zone*/（type + temp）。单个文件读不到/数值不合法即跳过，不抛异常。
    /// </summary>
    internal static IReadOnlyList<TempReading> ReadTemperatures()
    {
        var readings = new List<TempReading>();
        try
        {
            foreach (var chipDir in Directory.EnumerateDirectories("/sys/class/hwmon", "hwmon*"))
            {
                var chip = TryReadFile(Path.Combine(chipDir, "name"));
                foreach (var inputFile in Directory.EnumerateFiles(chipDir, "temp*_input"))
                {
                    var celsius = LinuxMetricsReader.ParseTempMilliCelsius(TryReadFile(inputFile) ?? string.Empty);
                    if (celsius is null)
                    {
                        continue;
                    }

                    var label = TryReadFile(Path.Combine(chipDir, TempLabelFileName(Path.GetFileName(inputFile))));
                    var sensor = !string.IsNullOrEmpty(label) ? $"{chip} {label}".Trim() : chip;
                    readings.Add(new TempReading(
                        string.IsNullOrEmpty(sensor) ? Path.GetFileName(inputFile) : sensor, celsius.Value));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var zoneDir in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                var celsius = LinuxMetricsReader.ParseTempMilliCelsius(TryReadFile(Path.Combine(zoneDir, "temp")) ?? string.Empty);
                if (celsius is null)
                {
                    continue;
                }

                var type = TryReadFile(Path.Combine(zoneDir, "type"));
                readings.Add(new TempReading(
                    string.IsNullOrEmpty(type) ? Path.GetFileName(zoneDir) : type, celsius.Value));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return readings;
    }

    /// <summary>temp1_input → temp1_label。</summary>
    private static string TempLabelFileName(string inputFileName)
    {
        var stem = inputFileName.EndsWith("_input", StringComparison.Ordinal)
            ? inputFileName[..^"_input".Length]
            : inputFileName;
        return $"{stem}_label";
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal readonly record struct CpuSnapshot(long Idle, long Total);

internal readonly record struct NetSnapshot(long RxBytes, long TxBytes);

internal readonly record struct DiskIoSnapshot(long SectorsRead, long SectorsWritten);

/// <summary>/proc 文本解析（纯函数）：单独拆出便于测试，不引入运行时依赖。</summary>
internal static class LinuxMetricsReader
{
    public static CpuSnapshot? ParseCpuSnapshot(string procStat) => ParseCpuStatLine(procStat);

    public static CpuSnapshot? ParseCpuStatLine(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                return null;
            }

            long total = 0, idle = 0;
            for (var i = 1; i < fields.Length; i++)
            {
                if (!long.TryParse(fields[i], out var value))
                {
                    return null;
                }

                total += value;
                if (i == 4 || i == 5) // idle + iowait
                {
                    idle += value;
                }
            }

            return new CpuSnapshot(idle, total);
        }

        return null;
    }

    public static double ComputeCpuPercent(CpuSnapshot? previous, CpuSnapshot current)
    {
        if (previous is not { } prev)
        {
            return 0;
        }

        var totalDelta = current.Total - prev.Total;
        var idleDelta = current.Idle - prev.Idle;
        if (totalDelta <= 0)
        {
            return 0;
        }

        var idleRatio = Math.Clamp((double)idleDelta / totalDelta, 0, 1);
        return 100.0 * (1 - idleRatio);
    }

    public static (long TotalKb, long AvailableKb)? ParseMemInfo(string memInfo)
    {
        long totalKb = 0, availableKb = 0;
        var hasTotal = false;
        var hasAvailable = false;
        foreach (var line in memInfo.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var valuePart = line[(separator + 1)..].Trim();
            var spaceIndex = valuePart.IndexOf(' ');
            var number = spaceIndex > 0 ? valuePart[..spaceIndex] : valuePart;
            if (!long.TryParse(number, out var value))
            {
                continue;
            }

            if (name == "MemTotal")
            {
                totalKb = value;
                hasTotal = true;
            }
            else if (name == "MemAvailable")
            {
                availableKb = value;
                hasAvailable = true;
            }
        }

        return hasTotal && hasAvailable ? (totalKb, availableKb) : null;
    }

    public static double ComputeMemPercent(long totalKb, long availableKb)
    {
        if (totalKb <= 0)
        {
            return 0;
        }

        return 100.0 * (totalKb - availableKb) / totalKb;
    }

    public static NetSnapshot? ParseNetTotals(string procNetDev)
    {
        long rx = 0, tx = 0;
        var found = false;
        foreach (var line in procNetDev.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (name == "lo")
            {
                continue;
            }

            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 ||
                !long.TryParse(fields[0], out var rxBytes) ||
                !long.TryParse(fields[8], out var txBytes))
            {
                continue;
            }

            rx += rxBytes;
            tx += txBytes;
            found = true;
        }

        return found ? new NetSnapshot(rx, tx) : null;
    }

    public static (double RxBytesPerSec, double TxBytesPerSec) ComputeNetBytesPerSec(
        NetSnapshot previous, NetSnapshot current, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return (0, 0);
        }

        var seconds = elapsed.TotalSeconds;
        return ((current.RxBytes - previous.RxBytes) / seconds, (current.TxBytes - previous.TxBytes) / seconds);
    }

    /// <summary>/proc/diskstats 总量：仅统计整盘（排除分区/dm/loop/ram 等虚拟与派生设备，避免重复累计）。</summary>
    public static DiskIoSnapshot? ParseDiskTotals(string procDiskStats)
    {
        long read = 0, written = 0;
        var found = false;
        foreach (var line in procDiskStats.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || !IsWholeDiskDevice(fields[2]))
            {
                continue;
            }

            if (!long.TryParse(fields[5], out var sectorsRead) || !long.TryParse(fields[9], out var sectorsWritten))
            {
                continue;
            }

            read += sectorsRead;
            written += sectorsWritten;
            found = true;
        }

        return found ? new DiskIoSnapshot(read, written) : null;
    }

    /// <summary>整盘设备：sd/sdaa…、hd、vd、xvd（前缀 + 纯小写字母）、nvme0n1（nvme+数字+n+数字）、mmcblk0。</summary>
    public static bool IsWholeDiskDevice(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (IsLowercaseDiskName(name, "sd") || IsLowercaseDiskName(name, "hd") ||
            IsLowercaseDiskName(name, "vd") || IsLowercaseDiskName(name, "xvd"))
        {
            return true;
        }

        if (name.StartsWith("nvme", StringComparison.Ordinal))
        {
            return MatchesNumberedNode(name, 4, 'n');
        }

        if (name.StartsWith("mmcblk", StringComparison.Ordinal))
        {
            return AllDigits(name, 6);
        }

        return false;
    }

    /// <summary>前缀后全为小写字母才算整盘（分区名带数字，如 sda1）。</summary>
    private static bool IsLowercaseDiskName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length)
        {
            return false;
        }

        for (var i = prefix.Length; i < name.Length; i++)
        {
            if (name[i] is < 'a' or > 'z')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>nvme0n1：起始下标后"数字 + 分隔符 + 数字"收尾（分区 nvme0n1p1 在数字后多一段 p+数字）。</summary>
    private static bool MatchesNumberedNode(string name, int start, char separator)
    {
        var i = start;
        while (i < name.Length && char.IsAsciiDigit(name[i]))
        {
            i++;
        }

        if (i == start || i >= name.Length || name[i] != separator)
        {
            return false;
        }

        i++;
        var tailStart = i;
        while (i < name.Length && char.IsAsciiDigit(name[i]))
        {
            i++;
        }

        return i == name.Length && i > tailStart;
    }

    private static bool AllDigits(string name, int start)
    {
        if (name.Length <= start)
        {
            return false;
        }

        for (var i = start; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    private const long SectorBytes = 512;

    /// <summary>磁盘读写速率（B/s）：扇区差 × 512B / 间隔秒数。</summary>
    public static (double ReadBytesPerSec, double WriteBytesPerSec) ComputeDiskBytesPerSec(
        DiskIoSnapshot previous, DiskIoSnapshot current, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return (0, 0);
        }

        var seconds = elapsed.TotalSeconds;
        return ((current.SectorsRead - previous.SectorsRead) * SectorBytes / seconds,
            (current.SectorsWritten - previous.SectorsWritten) * SectorBytes / seconds);
    }

    /// <summary>毫摄氏度文本 → ℃；非数值或量程外（<-100 / >200，含驱动占位值）返回 null。</summary>
    public static double? ParseTempMilliCelsius(string text)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
        {
            return null;
        }

        var celsius = milli / 1000.0;
        return celsius is >= -100 and <= 200 ? celsius : null;
    }

    /// <summary>CPU 相关传感器：名含 cpu/package id/tdie/tctl，或常见 CPU 温度芯片/区域名。</summary>
    public static bool IsCpuTemperatureSensor(string sensor)
    {
        var lower = sensor.ToLowerInvariant();
        return lower.Contains("cpu") ||
            lower.Contains("package id") || lower.Contains("tdie") || lower.Contains("tctl") ||
            lower is "coretemp" or "k10temp" or "k8temp" or "zenpower" or "x86_pkg_temp" or "soc_thermal" or "soc-thermal";
    }

    /// <summary>CPU 相关传感器取最大值并保留传感器名；无 CPU 相关读数返回 null。</summary>
    public static TempReading? SelectCpuTemperature(IReadOnlyList<TempReading> readings)
    {
        TempReading? best = null;
        foreach (var reading in readings)
        {
            if (!IsCpuTemperatureSensor(reading.Sensor))
            {
                continue;
            }

            if (best is not { } current || reading.Celsius > current.Celsius)
            {
                best = reading;
            }
        }

        return best;
    }
}
