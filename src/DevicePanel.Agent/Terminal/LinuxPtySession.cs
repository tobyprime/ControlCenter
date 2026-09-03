using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevicePanel.Agent;

/// <summary>PTY 会话工厂（扩展点）：按窗口尺寸创建 shell 会话。</summary>
internal interface IPtySessionFactory
{
    IPtySession Create(int cols, int rows);
}

/// <summary>单个终端会话：可读（shell 输出）、可写（键盘输入）、可调整窗口、可终止。</summary>
internal interface IPtySession
{
    /// <summary>阻塞读取 shell 输出；返回 0 表示会话结束（EOF）。读阻塞期间会话被 Kill 时以 EOF 返回。</summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>写入键盘输入，立即到达 shell。</summary>
    void Write(byte[] data);

    /// <summary>调整 PTY 窗口尺寸（TIOCSWINSZ），驱动 shell 重绘（vim/htop 等 TUI）。</summary>
    void SetWindowSize(int cols, int rows);

    /// <summary>终止会话：释放 PTY 主端并确保子进程被回收。</summary>
    void Kill();
}

/// <summary>
/// Linux PTY 实现：openpty 建立终端对，posix_spawn（POSIX_SPAWN_SETSID）启动交互 shell
/// （$SHELL，缺省 /bin/sh -i），slave fd 经 file_actions 复制到 0/1/2。
/// fork+exec 全程在 libc 内部完成——不在托管代码里走 fork 子分支，规避运行时状态不一致。
/// 平台不支持（如 Windows/无 libc）时 Create 抛异常，通道回 term.error，不影响 agent 存活。
/// </summary>
internal sealed class LinuxPtySessionFactory : IPtySessionFactory
{
    private const uint Tiocswinsz = 0x5414;
    private const int FSetFd = 2;
    private const int FdCloexec = 1;
    private const short PosixSpawnSetsid = 0x0040;
    private const int SigTerm = 15;
    private const int SigKill = 9;

    // 不透传给 shell 的环境变量（含面板 token，避免泄漏给 shell 子进程）
    private static readonly string[] InheritedEnvKeys = ["PATH", "HOME", "LANG", "SHELL", "USER", "LOGNAME", "TZ", "TMPDIR"];

    public IPtySession Create(int cols, int rows)
    {
        if (openpty(out var masterFd, out var slaveFd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException($"openpty 失败（errno={Marshal.GetLastWin32Error()}）");
        }

        try
        {
            // master/slave 标记 CLOEXEC，防止泄漏到本进程后续 spawn 的其他 shell；
            // file_actions 里 dup2 出来的 0/1/2 不带 CLOEXEC，shell 正常持有
            _ = fcntl(masterFd, FSetFd, FdCloexec);
            _ = fcntl(slaveFd, FSetFd, FdCloexec);
            SetWindowSize(masterFd, cols, rows);

            var pid = SpawnShell(slaveFd);
            return new LinuxPtySession(masterFd, pid);
        }
        catch
        {
            CloseFd(masterFd);
            throw;
        }
        finally
        {
            CloseFd(slaveFd); // 父进程的 slave 副本用完即关
        }
    }

    private static int SpawnShell(int slaveFd)
    {
        var shell = GetShellPath();
        var pathPtr = Marshal.StringToHGlobalAnsi(shell);
        var argv = new[]
        {
            Marshal.StringToHGlobalAnsi(shell),
            Marshal.StringToHGlobalAnsi("-i"),
            IntPtr.Zero,
        };
        var envList = BuildEnv(shell);
        var envp = new IntPtr[envList.Count + 1];
        for (var i = 0; i < envList.Count; i++)
        {
            envp[i] = Marshal.StringToHGlobalAnsi(envList[i]);
        }

        // 不透明结构体按最坏尺寸分配，仅由 libc 自身写入
        var fileActions = Marshal.AllocHGlobal(256);
        var attr = Marshal.AllocHGlobal(1024);
        try
        {
            var result = posix_spawn_file_actions_init(fileActions);
            if (result != 0)
            {
                throw new InvalidOperationException($"posix_spawn_file_actions_init 失败（{result}）");
            }

            _ = posix_spawnattr_init(attr);
            _ = posix_spawnattr_setflags(attr, PosixSpawnSetsid);
            foreach (var fd in new[] { 0, 1, 2 })
            {
                _ = posix_spawn_file_actions_adddup2(fileActions, slaveFd, fd);
            }

            var envpLocal = envp;
            var argvLocal = argv;
            result = posix_spawn(out var pid, pathPtr, fileActions, attr, argvLocal, envpLocal);
            _ = posix_spawn_file_actions_destroy(fileActions);
            _ = posix_spawnattr_destroy(attr);
            if (result != 0)
            {
                throw new InvalidOperationException($"posix_spawn 启动 {shell} 失败（errno={result}）");
            }

            return pid;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            foreach (var ptr in argv)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            foreach (var ptr in envp)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attr);
        }
    }

    private static string GetShellPath()
    {
        var env = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(env) ? "/bin/sh" : env;
    }

    /// <summary>构造 shell 的最小环境：TERM 固定 xterm-256color，其余白名单继承，缺省 PATH 兜底。</summary>
    private static List<string> BuildEnv(string shell)
    {
        var env = new List<string> { "TERM=xterm-256color" };
        foreach (var key in InheritedEnvKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                env.Add($"{key}={value}");
            }
        }

        env.Add($"SHELL={shell}");
        if (!env.Any(e => e.StartsWith("PATH=", StringComparison.Ordinal)))
        {
            env.Add("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        }

        if (!env.Any(e => e.StartsWith("HOME=", StringComparison.Ordinal)))
        {
            env.Add("HOME=/root");
        }

        return env;
    }

    private static void SetWindowSize(int fd, int cols, int rows)
    {
        var winsize = new Winsize
        {
            ws_col = (ushort)Math.Clamp(cols, 2, 500),
            ws_row = (ushort)Math.Clamp(rows, 2, 200),
        };
        _ = ioctl(fd, Tiocswinsz, ref winsize);
    }

    private static void CloseFd(int fd)
    {
        if (fd > 0)
        {
            _ = close(fd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int openpty(out int amaster, out int aslave, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn(out int pid, IntPtr path, IntPtr fileActions, IntPtr attrp, IntPtr[] argv, IntPtr[] envp);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int fd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref Winsize winsize);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    /// <summary>一条已建立的 PTY 会话。</summary>
    private sealed class LinuxPtySession : IPtySession
    {
        private readonly int _pid;
        private readonly int _masterFd;
        private volatile bool _killed;

        public LinuxPtySession(int masterFd, int pid)
        {
            _masterFd = masterFd;
            _pid = pid;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                // 直接对 fd 同步读：避免 FileStream 的读写互斥状态（终端读写天然并发）
                var read = LibcRead(_masterFd, buffer, offset, count);
                if (read > 0)
                {
                    return read;
                }

                if (read == 0)
                {
                    return 0; // EOF：shell 退出且对端关闭
                }

                if (_killed)
                {
                    return 0; // 会话被终止，按 EOF 收尾
                }

                // EIO：slave 侧已全部关闭（shell 退出）——Linux 上读主端的正常 EOF 表现
                var errno = Marshal.GetLastWin32Error();
                if (errno == Eio || errno == Ebadf)
                {
                    return 0;
                }

                return 0; // 其他读错误同样按会话结束处理，泵会发送 term.closed
            }
        }

        public void Write(byte[] data)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var written = LibcWrite(_masterFd, data, offset, data.Length - offset);
                if (written <= 0)
                {
                    return; // 写失败（会话已结束/对端关闭）：丢弃本段输入
                }

                offset += written;
            }
        }

        public void SetWindowSize(int cols, int rows)
        {
            var winsize = new Winsize
            {
                ws_col = (ushort)Math.Clamp(cols, 2, 500),
                ws_row = (ushort)Math.Clamp(rows, 2, 200),
            };
            _ = ioctl(_masterFd, Tiocswinsz, ref winsize);
        }

        public void Kill()
        {
            if (_killed)
            {
                return;
            }

            _killed = true;
            CloseFd(_masterFd); // 释放主端：slave 上的读写立即出错，shell 得知会话结束；阻塞中的 read 以 EIO/EBADF 返回

            // 确保回收：SIGTERM → 宽限 2s → SIGKILL，waitpid 防僵尸（异常一律吞掉）
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = kill(_pid, SigTerm);
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (waitpid(_pid, out _, WNoHang) == _pid)
                        {
                            return;
                        }

                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    _ = kill(_pid, SigKill);
                    _ = waitpid(_pid, out _, 0);
                }
                catch (Exception)
                {
                    // 进程可能已退出/被回收，无需处理
                }
            });
        }
    }

    private const int WNoHang = 1;
    private const int Eio = 5;
    private const int Ebadf = 9;

    /// <summary>libc read(2)：返回读取字节数，0=EOF，-1=错误（errno）。byte[] 参数由互操作自动钉住。</summary>
    private static int LibcRead(int fd, byte[] buffer, int offset, int count)
    {
        // 非 0 偏移的数组段需要固定地址：用 unsafe 之外的方式——拷贝到临时数组代价高，
        // 这里约定 offset=0（泵每次整缓冲读取），offset>0 场景用 GCHandle 处理
        if (offset == 0)
        {
            return (int)read(fd, buffer, (nuint)count);
        }

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return (int)read(fd, handle.AddrOfPinnedObject() + offset, (nuint)count);
        }
        finally
        {
            handle.Free();
        }
    }

    private static int LibcWrite(int fd, byte[] buffer, int offset, int count)
    {
        if (offset == 0)
        {
            return (int)write(fd, buffer, (nuint)count);
        }

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return (int)write(fd, handle.AddrOfPinnedObject() + offset, (nuint)count);
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, IntPtr buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, IntPtr buffer, nuint count);
}
