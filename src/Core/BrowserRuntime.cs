using CefSharp;
using CefSharp.Handler;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>整个应用只初始化一次 CEF，所有子进程均由自己的 EXE 承载。</summary>
internal static class BrowserRuntime
{
    private static bool _initialized;
    public static string SubprocessPath => File.Exists(Path.Combine(AppContext.BaseDirectory, "FeatherBrowser.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "FeatherBrowser.exe") : Environment.ProcessPath;

    public static void Initialize()
    {
        if (_initialized) return;
        CefSharpSettings.ShutdownOnExit = false;
        var settings = new CefSharp.WinForms.CefSettings
        {
            BrowserSubprocessPath = SubprocessPath,
            RootCachePath = AppPaths.BrowserDataFolder,
            CachePath = Path.Combine(AppPaths.BrowserDataFolder, "Default"),
            Locale = "zh-CN",
            AcceptLanguageList = "zh-CN,zh,en-US,en",
            LogFile = Path.Combine(AppPaths.Root, "chromium.log"),
            LogSeverity = LogSeverity.Warning,
            PersistSessionCookies = true,
        };
        settings.CefCommandLineArgs.Add("disable-background-networking");
        settings.CefCommandLineArgs.Add("disable-component-update");
        settings.CefCommandLineArgs.Add("disable-features", "Prerender2,BackForwardCache");
        if (!Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null))
            throw new InvalidOperationException("CEF 初始化失败，请检查安装目录中的 Chromium 文件。");
        _initialized = true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        BrowserContext.Shared.DisposeEnvironment();
        Cef.Shutdown();
        _initialized = false;
    }
}

internal sealed class BrowserProfile : IDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IRequestContext Context { get; }
    public bool IsPrivate { get; }
    public Task Ready => _ready.Task;

    public BrowserProfile(string cachePath, bool isPrivate)
    {
        BrowserRuntime.Initialize();
        IsPrivate = isPrivate;
        Context = new RequestContext(new RequestContextSettings
        {
            CachePath = isPrivate ? "" : cachePath,
            PersistSessionCookies = !isPrivate,
            AcceptLanguageList = "zh-CN,zh,en-US,en",
        }, new ProfileHandler(_ready));
    }

    public async Task SetJavaScriptAsync(bool enabled)
    {
        await Ready;
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!Context.SetPreference("profile.default_content_setting_values.javascript", enabled ? 1 : 2, out string error))
                Log.Warn("设置 JavaScript 策略失败: " + error);
            // 内部管理页与首页始终允许 JavaScript，不受网页设置影响。
            var allowInternal = new Dictionary<string, object>
            {
                ["https://feather.local,*"] = new Dictionary<string, object> { ["setting"] = 1 },
            };
            if (!Context.SetPreference("profile.content_settings.exceptions.javascript", allowInternal, out error))
                Log.Warn("设置内部页面 JavaScript 策略失败: " + error);
        });
    }

    public void Dispose() => Context.Dispose();

    private sealed class ProfileHandler(TaskCompletionSource ready) : RequestContextHandler
    {
        protected override void OnRequestContextInitialized(IRequestContext requestContext) => ready.TrySetResult();
    }
}
