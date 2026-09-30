using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 截图工具用的 Win32 辅助类。放在独立 .cs 文件里，
/// 避免 PowerShell here-string 与 C# 里的引号序列互相打架。
/// </summary>
public class WinShot
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    public delegate bool EnumProc(IntPtr h, IntPtr p);

    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// 收集某个进程里值得单独截图的顶层窗口。
    ///
    /// <p>过滤条件刻意放宽：WinForms 在 ShowInTaskbar=false 时并不给窗口加 WS_POPUP，
    /// 只按 WS_POPUP 判断会把菜单和标签列表漏掉。这里改成
    /// 「可见 + 有实际尺寸 + 类名是 WindowsForms + 不是主窗口的子窗口」。
    /// </summary>
    public static List<IntPtr> WindowsOf(int processId, IntPtr mainWindow)
    {
        List<IntPtr> list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != (uint)processId)
            {
                return true;
            }
            if (h == mainWindow)
            {
                return true;
            }
            if (!IsWindowVisible(h))
            {
                return true;
            }

            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
            {
                return true;
            }

            // 主窗口的弹层/子窗口不重复截，它们已经包含在主窗口的截图里
            for (IntPtr up = GetParent(h); up != IntPtr.Zero; up = GetParent(up))
            {
                if (up == mainWindow)
                {
                    return true;
                }
            }

            RECT r;
            GetWindowRect(h, out r);
            if (r.Right - r.Left < 20 || r.Bottom - r.Top < 20)
            {
                return true;
            }

            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string TitleOf(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>PW_RENDERFULLCONTENT，WebView2 需要这个标志才能被 PrintWindow 抓到内容。</summary>
    public static bool Grab(IntPtr h, IntPtr hdc)
    {
        return PrintWindow(h, hdc, 2);
    }
}
