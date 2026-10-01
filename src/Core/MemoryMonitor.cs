using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using FeatherBrowser.Services;
using Microsoft.Win32.SafeHandles;

namespace FeatherBrowser.Core;

/// <summary>
/// 内存监控与主动回收。
///
/// 统计本程序及其 CEF 子进程，排除其他窗口应用的进程。
/// </summary>
public static class MemoryMonitor
{
    internal static HashSet<int> GetBrowserProcessIds()
    {
        var ids = new HashSet<int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return ids;
        var entries = new Dictionary<int, (int Parent, string Name)>();
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry)) return ids;
        do
        {
            entries[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
        } while (Process32Next(snapshot, ref entry));
        var descendants = new HashSet<int> { Environment.ProcessId };
        bool changed;
        do
        {
            changed = false;
            foreach (var process in entries)
            {
                if (descendants.Contains(process.Value.Parent) && descendants.Add(process.Key)) changed = true;
            }
        } while (changed);
        string name = Path.GetFileName(BrowserRuntime.SubprocessPath);
        foreach (int id in descendants)
            if (id != Environment.ProcessId && entries.TryGetValue(id, out var process) &&
                process.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ids.Add(id);
        return ids;
    }
    /// <summary>本进程私有字节（不含共享的渲染进程）。</summary>
    public static long PrivateBytes { get; private set; }

    /// <summary>本进程工作集。</summary>
    public static long WorkingSet { get; private set; }

    /// <summary>本应用所有 Chromium 子进程的工作集合计。</summary>
    public static long KernelWorkingSet { get; private set; }

    /// <summary>本应用 Chromium 子进程数量。</summary>
    public static int KernelProcessCount { get; private set; }

    /// <summary>系统可用物理内存。</summary>
    public static long AvailablePhysical { get; private set; }

    public static long TotalPhysical { get; private set; }

    public static string Summary()
    {
        return $"可用 {Mb(AvailablePhysical)} / 共 {Mb(TotalPhysical)} · " +
               $"本程序 私有 {Mb(PrivateBytes)}，工作集 {Mb(WorkingSet)} · " +
               $"内核进程 {KernelProcessCount} 个 共 {Mb(KernelWorkingSet)}";
    }

    public static string Mb(long bytes) => (bytes / 1024 / 1024) + " MB";

    /// <summary>刷新一次数据。由界面定时器调用，频率不高（约 2 秒一次）。</summary>
    public static void Refresh()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            PrivateBytes = self.PrivateMemorySize64;
            WorkingSet = self.WorkingSet64;
        }
        catch
        {
            // 忽略
        }

        try
        {
            long total = 0;
            int count = 0;
            foreach (int id in GetBrowserProcessIds())
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    total += process.WorkingSet64;
                    count++;
                }
                catch
                {
                    // 进程可能刚好退出
                }
            }
            KernelWorkingSet = total;
            KernelProcessCount = count;
        }
        catch
        {
            // 忽略
        }

        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
            {
                AvailablePhysical = (long)status.ullAvailPhys;
                TotalPhysical = (long)status.ullTotalPhys;
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 把已经空出来的工作集立刻还给系统。
    ///
    /// <p>.NET 的 GC 默认不会主动归还页面给操作系统，销毁大量浏览器视图之后
    /// 工作集数字仍然虚高。这里做一次紧凑回收 + 清空工作集，让任务管理器里的数字
    /// 与真实占用一致。
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);
        }
        catch
        {
            // 忽略
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            self.MinWorkingSet = (IntPtr)(2 * 1024 * 1024);
            _ = EmptyWorkingSet(self.Handle);
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
