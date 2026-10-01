using FeatherBrowser.Core;
using FeatherBrowser.Services;
using FeatherBrowser.UI;

namespace FeatherBrowser;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 数据目录覆盖必须在任何 AppPaths.Root 访问之前生效
        foreach (string arg in args)
        {
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.OverrideRoot(arg["--data-dir=".Length..].Trim('"'));
            }
        }

        AppPaths.EnsureCreated();

        // 自检模式：不需要人看界面，跑完把内存数据写进文件，便于脚本化验证。
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            string output = args.Length > 1
                ? args[1]
                : Path.Combine(AppPaths.Root, "selftest.txt");
            SelfTest.RunAsync(output).GetAwaiter().GetResult();
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

        // 登记程序身份：让任务管理器/任务栏显示「轻羽浏览器」，
        // 而不是把网页子进程显示成 msedgewebview2 或「WebView2」。
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

        using var form = new MainForm(startUrl, incognito: false, themeOverride, uiTest);
        Application.Run(form);

        // 退出时保存会话与历史
        try
        {
            form.PersistSession();
        }
        catch (Exception ex)
        {
            Log.Error("退出时保存会话失败", ex);
        }
    }
}
