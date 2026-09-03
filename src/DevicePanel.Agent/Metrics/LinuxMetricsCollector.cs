namespace DevicePanel.Agent;

/// <summary>单次采集的设备指标快照：百分比 0-100，网络为字节/秒。</summary>
internal sealed record MetricsSample(
    double CpuPercent,
    double MemPercent,
    double DiskPercent,
    double NetRxBytesPerSec,
    double NetTxBytesPerSec);

/// <summary>指标采集抽象：每次 Sample 返回当前时刻快照（CPU/网络基于与上次的增量计算）。</summary>
internal interface IMetricsCollector
{
    MetricsSample Sample();
}

/// <summary>
/// Linux 指标采集：/proc/stat（CPU）、/proc/meminfo（内存）、/proc/net/dev（网络，排除 lo）、根文件系统用量（磁盘）。
/// CPU 与网络速率需要前后两次采样做差：首个周期报告 0，仅表现为第一个数据点数值偏保守，不影响后续曲线。
/// </summary>
internal sealed class LinuxMetricsCollector : IMetricsCollector
{
    private readonly Func<string> _procStat;
    private readonly Func<string> _memInfo;
    private readonly Func<string> _netDev;
    private readonly Func<double> _diskPercent;
    private readonly TimeProvider _clock;
    private CpuSnapshot? _lastCpu;
    private NetSnapshot? _lastNet;
    private DateTimeOffset? _lastNetAt;

    public LinuxMetricsCollector()
        : this(
            procStat: () => File.ReadAllText("/proc/stat"),
            memInfo: () => File.ReadAllText("/proc/meminfo"),
            netDev: () => File.ReadAllText("/proc/net/dev"),
            diskPercent: () => ComputeRootDiskPercent(),
            TimeProvider.System)
    {
    }

    internal LinuxMetricsCollector(
        Func<string> procStat,
        Func<string> memInfo,
        Func<string> netDev,
        Func<double> diskPercent,
        TimeProvider? clock = null)
    {
        _procStat = procStat;
        _memInfo = memInfo;
        _netDev = netDev;
        _diskPercent = diskPercent;
        _clock = clock ?? TimeProvider.System;
    }

    public MetricsSample Sample()
    {
        var nowUtc = _clock.GetUtcNow();
        double cpuPercent = 0, memPercent = 0, diskPercent = 0, netRx = 0, netTx = 0;

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

        return new MetricsSample(
            Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
            Math.Round(Math.Clamp(memPercent, 0, 100), 1),
            Math.Round(Math.Clamp(diskPercent, 0, 100), 1),
            Math.Round(Math.Max(netRx, 0), 1),
            Math.Round(Math.Max(netTx, 0), 1));
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
}

internal readonly record struct CpuSnapshot(long Idle, long Total);

internal readonly record struct NetSnapshot(long RxBytes, long TxBytes);

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
}
