using System.Text.Json;
using CefSharp;
using CefSharp.DevTools;
using CefSharp.DevTools.Page;
using CefSharp.WinForms;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>CEF 控件及脚本、冻结、缩放的统一入口。</summary>
public sealed class BrowserView : ChromiumWebBrowser, CefSharp.Internals.IWebBrowserInternal
{
    internal const string HomeAddress = "https://feather.local/home.html";
    private readonly BrowserProfile _profile;
    private readonly bool _cacheEnabled;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposeRequested;
    private ILifeSpanHandler _lifeSpan;
    // 基类 Dispose 会清空公开 handler；原生 CloseBrowser 回调晚于 Dispose。
    // 接口持续提供关闭处理器，防止 CEF 默认向浏览器主窗体发送 WM_CLOSE。
    ILifeSpanHandler IWebBrowser.LifeSpanHandler { get => _lifeSpan; set => _lifeSpan = value; }
    internal event Action<string, string> PageMessageReceived;
    internal Task DisposalTask => _disposed.Task;
    private double _zoom = 1;
    internal string HomeHtml { get; private set; }
    internal bool HasNavigated { get; private set; }
    public BrowserView Engine => IsBrowserInitialized && !IsDisposed ? this : null;
    public string Source => Address;
    public bool IsSuspended { get; private set; }
    public BrowserEngineSettings Settings { get; }

    internal BrowserView(BrowserProfile profile, TabManager manager, BrowserTab tab)
        : base("about:blank", profile.Context)
    {
        _profile = profile;
        _cacheEnabled = manager.Settings.UseDiskCache;
        Settings = new BrowserEngineSettings(profile, manager.Settings.JavaScriptEnabled,
            () => Source == HomeAddress || InternalPages.Handles(Source));
        Dock = DockStyle.Fill;
        RequestHandler = new BrowserRequestHandler(this, manager, tab);
        _lifeSpan = new BrowserLifeSpanHandler(this, manager, tab);
        LifeSpanHandler = _lifeSpan;
        MenuHandler = new BrowserContextMenuHandler(this, manager, tab);
        DownloadHandler = new BrowserDownloadHandler(this, manager);
        IsBrowserInitializedChanged += (_, _) =>
        {
            if (IsBrowserInitialized) _initialized.TrySetResult();
            if (_disposeRequested) Ui(() => Dispose());
        };
        Disposed += (_, _) => _initialized.TrySetCanceled();
    }

    protected override void Dispose(bool disposing)
    {
        // CEF 创建窗口是异步的。父 HWND 必须保留到创建完成，否则原生线程会崩溃，
        // 或在 handler 已被清空后向宿主发送 WM_CLOSE。
        if (disposing && IsHandleCreated && !IsBrowserInitialized && !IsDisposed)
        {
            _disposeRequested = true;
            Visible = false;
            return;
        }
        base.Dispose(disposing);
        _disposed.TrySetResult();
    }

    void CefSharp.Internals.IWebBrowserInternal.SetJavascriptMessageReceived(JavascriptMessageReceivedEventArgs args)
    {
        // 原生 frame 包装只在当前 CEF 回调期间有效，先复制来源再派发到界面线程。
        if (!args.Frame.IsMain || args.Message is not string raw) return;
        string source = args.Frame.Url;
        Ui(() => PageMessageReceived?.Invoke(source, raw));
    }

    internal async Task InitializeAsync()
    {
        await _profile.SetJavaScriptAsync(Settings.WebsiteScriptEnabled);
        if (!IsBrowserInitialized) await _initialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!IsDisposed && !_cacheEnabled)
        {
            using var client = this.GetDevToolsClient();
            await client.Network.SetCacheDisabledAsync(true);
        }
    }

    internal void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() => { if (!IsDisposed) action(); });
        }
        catch (InvalidOperationException) { }
    }

    public void Navigate(string url)
    {
        HasNavigated = true;
        this.Load(url);
    }

    public void NavigateToString(string html)
    {
        HomeHtml = html;
        Navigate(HomeAddress);
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (!IsBrowserInitialized || IsDisposed) throw new InvalidOperationException("浏览器视图尚未就绪");
        using var frame = this.GetMainFrame();
        var result = await frame.EvaluateScriptAsync(script);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return JsonSerializer.Serialize(result.Result);
    }

    public double ZoomFactor
    {
        get => _zoom;
        set
        {
            _zoom = value;
            if (IsBrowserInitialized) this.SetZoomLevel(Math.Log(value, 1.2));
        }
    }

    public Color DefaultBackgroundColor { get => BackColor; set => BackColor = value; }

    public async Task<bool> TrySuspendAsync()
    {
        if (!IsBrowserInitialized || IsDisposed) return false;
        using var client = this.GetDevToolsClient();
        var result = await client.Page.SetWebLifecycleStateAsync(SetWebLifecycleStateState.Frozen);
        if (!IsDisposed) IsSuspended = result.Success;
        return result.Success;
    }

    public void Resume() => _ = ResumeAsync();

    public async Task ResumeAsync()
    {
        if (!IsBrowserInitialized || IsDisposed) return;
        try
        {
            IsSuspended = false;
            using var client = this.GetDevToolsClient();
            await client.Page.SetWebLifecycleStateAsync(SetWebLifecycleStateState.Active);
            if (!IsDisposed) IsSuspended = false;
        }
        catch (Exception ex) { if (!IsDisposed) Log.Warn("恢复页面失败: " + ex.Message); }
    }

    internal async void ApplyPageTheme(bool dark)
    {
        if (!IsBrowserInitialized || IsDisposed) return;
        try
        {
            using var client = this.GetDevToolsClient();
            await client.Emulation.SetAutoDarkModeOverrideAsync(dark);
            await client.Emulation.SetEmulatedMediaAsync(features: new[]
            {
                new CefSharp.DevTools.Emulation.MediaFeature { Name = "prefers-color-scheme", Value = dark ? "dark" : "light" },
            });
        }
        catch (Exception ex) { if (!IsDisposed) Log.Warn("网页主题更新失败: " + ex.Message); }
    }
}

public sealed class BrowserEngineSettings
{
    private readonly BrowserProfile _profile;
    private bool _enabled;
    private readonly Func<bool> _isInternal;
    internal bool WebsiteScriptEnabled => _enabled;
    internal BrowserEngineSettings(BrowserProfile profile, bool enabled, Func<bool> isInternal)
    { _profile = profile; _enabled = enabled; _isInternal = isInternal; }
    public bool IsScriptEnabled
    {
        get => _enabled || _isInternal();
        set { _enabled = value; _ = ApplyAsync(value); }
    }
    private async Task ApplyAsync(bool value)
    {
        try { await _profile.SetJavaScriptAsync(value); }
        catch (Exception ex) { Log.Warn("更新网页脚本设置失败: " + ex.Message); }
    }
}
