using FeatherBrowser.Core;
using FeatherBrowser.Services;
using FeatherBrowser.UI;

namespace FeatherBrowser;

internal static class Program
{
    internal static BrowserApplicationContext Windows { get; private set; }
    [STAThread]
    private static void Main(string[] args)
    {
        int subprocessExit = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (subprocessExit >= 0) { Environment.ExitCode = subprocessExit; return; }
        RunBrowser(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunBrowser(string[] args)
    {
        // 数据目录覆盖必须在任何 AppPaths.Root 访问之前生效
        bool dataDirectorySpecified = false;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.OverrideRoot(arg["--data-dir=".Length..].Trim('"'));
                dataDirectorySpecified = true;
            }
        }

        if (!dataDirectorySpecified && args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "FeatherSelfTest_" + Guid.NewGuid().ToString("N")));
        }

        AppPaths.EnsureCreated();

        // 内置管理页（书签 / 下载 / 历史）是磁盘上的 HTML，由虚拟主机映射提供。
        // 每次启动重写一遍，页面才跟得上程序版本。
        InternalPages.Materialize();

        // 自检模式：不需要人看界面，跑完把内存数据写进文件，便于脚本化验证。
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            string output = args.Length > 1
                ? args[1]
                : Path.Combine(AppPaths.Root, "selftest.txt");
            try { SelfTest.RunAsync(output).GetAwaiter().GetResult(); }
            finally { BrowserRuntime.Shutdown(); }
            return;
        }

        // 从夸克导入数据（也可在界面里「设置 → 从夸克导入」触发）
        //   --import-quark=all|bookmarks|history|passwords[;historyLimit]
        string importSpec = null;
        string importReport = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--import-quark=", StringComparison.OrdinalIgnoreCase))
            {
                importSpec = arg["--import-quark=".Length..];
            }
            else if (arg.StartsWith("--import-report=", StringComparison.OrdinalIgnoreCase))
            {
                importReport = arg["--import-report=".Length..].Trim('"');
            }
        }
        if (importSpec != null)
        {
            // 注意：不要设置 Console.OutputEncoding —— 这是个 WinExe，
            // 在没有真实控制台句柄时会抛 IOException 直接崩掉。
            QuarkImportCli.Run(importSpec, importReport);
            return;
        }

        // 不用 ApplicationConfiguration.Initialize()：它把高 DPI 设置写在代码里，
        // 而高 DPI 感知已经在 app.manifest 里声明为 PerMonitorV2，两处同时设置会互相干扰。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 登记稳定的任务栏身份及「轻羽浏览器」显示名。
        // Chromium 子进程也使用同一个 FeatherBrowser.exe。
        AppIdentity.Initialize(null);

        Application.ThreadException += (_, e) =>
            Log.Error("界面线程未处理异常", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("未处理异常", e.ExceptionObject as Exception);

        string startUrl = null;
        string themeOverride = null;
        string uiTest = null;
        foreach (string arg in args)
        {
            // 临时覆盖主题，不改配置文件：--theme=light|dark|system
            if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                themeOverride = arg[8..].Trim().ToLowerInvariant();
                continue;
            }

            // 界面走查：--uitest=tabs|menu|settings|memory，启动后自动打开对应界面，
            // 供 tools\screenshot.ps1 自动截图核对排版，普通用户不会用到。
            if (arg.StartsWith("--uitest=", StringComparison.OrdinalIgnoreCase))
            {
                uiTest = arg[9..].Trim().ToLowerInvariant();
                continue;
            }

            // 启动时直接打开某个地址（普通用法）
            string candidate = UrlUtils.ExtractFirstUrl(arg);
            if (string.IsNullOrEmpty(candidate) && !arg.StartsWith('-'))
            {
                candidate = arg;
            }
            startUrl ??= candidate;
        }

        using var instance = new SingleInstance(AppPaths.Root);
        if (!instance.IsOwner)
        {
            try { instance.ForwardAsync(startUrl).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("无法通知已运行的浏览器", ex); }
            return;
        }
        using var form = new MainForm(startUrl, incognito: false, themeOverride, uiTest);
        using var windows = new BrowserApplicationContext();
        Windows = windows;
        windows.OpenWindow(form);
        instance.Listen(windows.RequestWindow);
        try { Application.Run(windows); }
        finally { BrowserRuntime.Shutdown(); }
    }
}
