using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 内存监控与主动回收。
///
/// <p>WebView2 的内存分布在多个子进程里（浏览器进程 + 每个渲染进程），
/// 只看本进程的 WorkingSet 会严重低估。这里把所有同名的 WebView2 子进程都统计进来，
/// 这样「标签回收到底省了多少」是可观测的，而不是靠感觉。
/// </summary>
public static class MemoryMonitor
{
    /// <summary>本进程私有字节（不含共享的渲染进程）。</summary>
    public static long PrivateBytes { get; private set; }

    /// <summary>本进程工作集。</summary>
    public static long WorkingSet { get; private set; }

    /// <summary>所有 WebView2 子进程的工作集合计。</summary>
    public static long WebViewWorkingSet { get; private set; }

    /// <summary>WebView2 进程数量。每个标签的渲染进程会体现在这里。</summary>
    public static int WebViewProcessCount { get; private set; }

    /// <summary>系统可用物理内存。</summary>
    public static long AvailablePhysical { get; private set; }

    public static long TotalPhysical { get; private set; }

    public static string Summary()
    {
        return $"可用 {Mb(AvailablePhysical)} / 共 {Mb(TotalPhysical)} · " +
               $"本程序 私有 {Mb(PrivateBytes)}，工作集 {Mb(WorkingSet)} · " +
               $"内核进程 {WebViewProcessCount} 个 共 {Mb(WebViewWorkingSet)}";
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
            foreach (var process in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    total += process.WorkingSet64;
                    count++;
                }
                catch
                {
                    // 进程可能刚好退出
                }
                finally
                {
                    process.Dispose();
                }
            }
            WebViewWorkingSet = total;
            WebViewProcessCount = count;
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
    /// <p>.NET 的 GC 默认不会主动归还页面给操作系统，销毁大量 WebView2 之后
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
