using FeatherBrowser.Services;
using CefSharp;


namespace FeatherBrowser.Core;

/// <summary>标签的存活档位。</summary>
public enum TabLife
{
    /// <summary>BrowserView 已销毁，只剩 URL 等元数据。内存占用最低，界面显示为「休眠」。</summary>
    Cold = 0,

    /// <summary>BrowserView 存在但已被内核挂起。只在「窗口失去焦点」这类视图确实不可见的场景出现。</summary>
    Suspended = 1,

    /// <summary>BrowserView 存活且正在渲染。界面显示为「渲染中」。</summary>
    Live = 2,
}

/// <summary>主题给网页区域用的配色，由界面层注入，避免 Core 依赖 UI。</summary>
public sealed class PageTheme
{
    /// <summary>网页控件底色（深色模式下避免加载时闪白）。</summary>
    public int BackgroundArgb { get; init; } = unchecked((int)0xFFFFFFFF);

    /// <summary>是否让内核把网页强制渲染成深色。</summary>
    public bool ForceDark { get; init; }
}

/// <summary>
/// 一个标签页。
///
/// <p>这是「低内存」的核心：只有 <see cref="TabLife.Live"/> 的标签才真正持有渲染进程。
/// <list type="bullet">
///   <item>超过「同时渲染标签数」上限的标签会被 <b>休眠</b>：Dispose 掉 BrowserView，
///         渲染进程退出，内存真正归还给系统。标签本身仍在列表里，切回去按原 URL 重新加载；</item>
///   <item>窗口失去焦点时，不可见的标签会先尝试 <b>挂起</b>（TrySuspend）：页面与滚动位置保留，
///         恢复更快，也能省下一部分内存。挂起失败不影响功能，下一轮策略会直接休眠它。</item>
/// </list>
///
/// <p>因此常驻内存取决于「同时渲染的标签数」，与标签总数无关 ——
/// 开 20 个标签和开 2 个标签是同一量级。
/// </summary>
public sealed class BrowserTab : IDisposable
{
    private readonly TabManager _manager;
    private bool _disposed;
    private bool _suspendRequested;
    private Task<bool> _suspendTask;
    internal string CredentialPickToken { get; set; }
    internal string CredentialSource { get; set; }

    internal bool OwnsView(BrowserView view) => !_disposed && ReferenceEquals(View, view) && !view.IsDisposed;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public string Url { get; private set; } = UrlUtils.InternalHome;

    public string Title { get; private set; } = "新标签页";

    public bool IsIncognito { get; }

    public TabLife Life { get; private set; } = TabLife.Cold;

    public bool IsLoading { get; private set; }

    public int Progress { get; private set; }
    public bool? LastNavigationSucceeded { get; private set; }

    public bool CanGoBack => View?.CanGoBack == true;

    public bool CanGoForward => View?.CanGoForward == true;

    /// <summary>探针：本标签历史上创建过多少个 BrowserView。用于验证回收确实发生。</summary>
    public int CreatedCount { get; private set; }

    public DateTime LastUsedAt { get; private set; } = DateTime.Now;

    public BrowserView View { get; private set; }
    private int _viewGeneration;

    public event Action<BrowserTab> Changed;

    public BrowserTab(TabManager manager, string url, bool incognito)
    {
        _manager = manager;
        IsIncognito = incognito;
        Url = string.IsNullOrWhiteSpace(url) ? UrlUtils.InternalHome : url;
    }

    public void Touch() => LastUsedAt = DateTime.Now;

    /// <summary>标题的初始占位值。用它判断「这个标签还从没拿到过真实标题」。</summary>
    private const string DefaultTitle = "新标签页";

    public string DisplayTitle
    {
        get
        {
            // 注意：冷标签（从没加载过）的 Title 会一直是这个占位值，
            // 不能只判断 IsNullOrWhiteSpace，否则休眠标签会全显示成「新标签页」。
            bool hasRealTitle = !string.IsNullOrWhiteSpace(Title) &&
                                Title != "about:blank" &&
                                Title != DefaultTitle;
            if (hasRealTitle)
            {
                return Title;
            }
            if (UrlUtils.IsInternal(Url))
            {
                // 内置管理页有正式标题，别显示成「新标签页」
                return InternalPages.TitleFor(Url);
            }

            // 没有真实标题时回退到域名，比显示「新标签页」有用得多
            string host = UrlUtils.HostOf(Url);
            if (!string.IsNullOrEmpty(host))
            {
                return host;
            }
            return UrlUtils.Ellipsis(Url, 40);
        }
    }

    /// <summary>
    /// 是否显示内置首页。
    ///
    /// <para>注意只能匹配 <c>feather://home</c>，不能笼统判断 <c>feather://</c> ——
    /// 书签 / 下载 / 历史这些内置页也是 feather 协议，
    /// 笼统判断会把它们全都替换成首页内容（曾经就是这样，页面一直是首页）。</para>
    /// </summary>
    public bool IsHomePage => UrlUtils.IsHome(Url);

    internal void RaiseChanged() => Changed?.Invoke(this);

    // ---------------------------------------------------------------- 视图生命周期

    /// <summary>
    /// 取得 BrowserView；如果已被销毁则重新创建，并在内核就绪后加载 <see cref="Url"/>。
    /// 因为是异步的，调用方（TabManager）只需 fire-and-forget，界面会在事件里自然刷新。
    /// </summary>
    internal async Task<BrowserView> ObtainViewAsync(Panel host, bool forceRecreate)
    {
        if (_disposed || host.IsDisposed)
        {
            return null;
        }
        if (View != null && !forceRecreate)
        {
            return View;
        }

        if (View != null)
        {
            DestroyView();
        }

        int generation = ++_viewGeneration;
        // 合并同一轮界面消息中的快速切换；已关闭或切走的冷标签无需创建原生窗口。
        await Task.Yield();
        if (_disposed || host.IsDisposed || generation != _viewGeneration || _manager.Active != this) return null;

        BrowserView view = _manager.CreateViewFor(this);
        View = view;
        CreatedCount++;
        Life = TabLife.Live;

        host.Controls.Add(view);
        view.BringToFront();

        try
        {
            await _manager.EnsureCoreAsync(view, this);
        }
        catch (Exception ex)
        {
            if (OwnsView(view))
            {
                Log.Error($"标签 {Id} 的内核初始化失败", ex);
                DestroyView();
            }
            return null;
        }

        // 初始化期间可能已关闭、休眠或重建；旧任务不得操作新视图。
        if (!OwnsView(view))
        {
            return null;
        }
        ApplyDeferredZoom();
        ApplyThemeToView();

        if (IsHomePage)
        {
            Navigate(UrlUtils.BuildHomeHtml(_manager.Settings.Engine.Name, _manager.PageTheme.ForceDark), isHtml: true);
        }
        else
        {
            Navigate(Url, isHtml: false);
        }

        // 内核初始化是异步的，这期间用户可能已经切走了。
        // 收敛一次，把不再是当前标签的视图按策略挂起/销毁，避免刚要起来的渲染进程白占内存。
        _manager.EnforceMemoryPolicy();

        return view;
    }

    /// <summary>把当前主题套到网页区域：底色 + 是否强制深色。</summary>
    internal void ApplyThemeToView()
    {
        if (View == null)
        {
            return;
        }

        PageTheme theme = _manager.PageTheme;
        try
        {
            View.DefaultBackgroundColor = Color.FromArgb(theme.BackgroundArgb);

            View.ApplyPageTheme(theme.ForceDark);
        }
        catch
        {
            // 主题套用失败不影响浏览
        }
    }

    /// <summary>
    /// 挂起：页面与滚动位置保留在渲染进程里，能省下一部分内存，恢复也快。
    ///
    /// <p>注意：内核只在视图不可见时才会同意挂起。所以这里先把控件从可见容器里摘下来
    /// 再尝试；失败也不算错误 —— 调用方（<see cref="TabManager"/>）在标签数超限时
    /// 走的是直接销毁这条路，不依赖挂起成功。
    /// </summary>
    internal Task<bool> SuspendAsync()
    {
        _suspendRequested = true;
        if (_suspendTask is { IsCompleted: false })
        {
            return _suspendTask;
        }
        _suspendTask = SuspendCoreAsync();
        return _suspendTask;
    }

    private async Task<bool> SuspendCoreAsync()
    {
        BrowserView view = View;
        if (View?.Engine == null)
        {
            return false;
        }

        try
        {
            view.Visible = false;
        }
        catch
        {
            // 忽略
        }

        try
        {
            bool ok = await view.Engine.TrySuspendAsync();
            if (!OwnsView(view))
            {
                return false;
            }
            // 用户可能在挂起请求完成前已切回这个标签。
            if (!_suspendRequested)
            {
                await view.ResumeAsync();
                Life = TabLife.Live;
                RaiseChanged();
                return false;
            }
            if (ok)
            {
                Life = TabLife.Suspended;
                RaiseChanged();
            }
            else
            {
                Log.Info($"标签 {Id} 未被内核接受挂起，保持渲染");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"挂起标签 {Id} 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>从挂起状态恢复。</summary>
    internal void Resume()
    {
        _suspendRequested = false;
        if (View?.Engine == null)
        {
            return;
        }
        try
        {
            View.Engine.Resume();
            View.Visible = this == _manager.Active;
            Life = TabLife.Live;
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复标签 {Id} 失败: {ex.Message}");
        }
    }

    /// <summary>销毁 BrowserView，真正把渲染进程的内存交还给系统。</summary>
    internal void DestroyView()
    {
        _viewGeneration++;
        BrowserView view = View;
        View = null;
        CredentialPickToken = null;
        CredentialSource = null;
        _suspendRequested = false;
        _suspendTask = null;
        Life = TabLife.Cold;
        IsLoading = false;
        Progress = 0;
        LastNavigationSucceeded = null;

        if (view == null)
        {
            return;
        }

        try
        {
            try
            {
                view.Engine?.Stop();
            }
            catch
            {
                // 忽略
            }
            view.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"销毁标签 {Id} 的 BrowserView 失败: {ex.Message}");
        }

        RaiseChanged();
    }

    // ---------------------------------------------------------------- 导航

    /// <summary>加载给定地址（会自动识别内置首页）。</summary>
    public void NavigateTo(string url)
    {
        if (_disposed) return;
        Url = string.IsNullOrWhiteSpace(url) ? UrlUtils.InternalHome : url;
        Touch();

        if (View == null)
        {
            RaiseChanged();
            return;
        }

        if (Life == TabLife.Suspended)
        {
            Resume();
        }

        if (IsHomePage)
        {
            Navigate(UrlUtils.BuildHomeHtml(_manager.Settings.Engine.Name, _manager.PageTheme.ForceDark), isHtml: true);
        }
        else
        {
            Navigate(Url, isHtml: false);
        }
    }

    private void Navigate(string target, bool isHtml)
    {
        if (View?.Engine == null)
        {
            return;
        }

        try
        {
            LastNavigationSucceeded = null;
            IsLoading = true;
            Progress = 5;
            RaiseChanged();
            if (isHtml)
            {
                Log.Info($"导航到内置首页 (HTML {target.Length} 字节)");
                View.Engine.NavigateToString(target);
            }
            else if (target.StartsWith("feather://search?q=", StringComparison.OrdinalIgnoreCase))
            {
                // 首页搜索框提交后由这里转成真正的搜索请求
                string query = Uri.UnescapeDataString(target["feather://search?q=".Length..]);
                string url = UrlUtils.Normalize(query, _manager.Settings.Engine.Template);
                Url = url;
                View.Engine.Navigate(url);
            }
            else
            {
                View.Engine.Navigate(target);
            }
        }
        catch (Exception ex)
        {
            LastNavigationSucceeded = false;
            IsLoading = false;
            Progress = 0;
            Log.Error($"导航失败: {target}", ex);
            RaiseChanged();
        }
    }

    public void GoBack()
    {
        if (View?.CanGoBack == true)
        {
            View.Back();
        }
    }

    public void GoForward()
    {
        if (View?.CanGoForward == true)
        {
            View.Forward();
        }
    }

    public void Reload()
    {
        if (View == null)
        {
            // 冷标签：让管理器把它重新激活即可
            _manager.Activate(this);
            return;
        }
        if (Life == TabLife.Suspended)
        {
            Resume();
        }
        if (View.Engine != null)
        {
            LastNavigationSucceeded = null;
            View.Engine.Reload();
        }
    }

    public void Stop()
    {
        try
        {
            View?.Engine?.Stop();
        }
        catch
        {
            // 忽略
        }
    }

    private double _zoomFactor = 1.0;

    public void SetZoom(double factor)
    {
        _zoomFactor = Math.Clamp(factor, 0.25, 5.0);
        ApplyDeferredZoom();
    }

    private void ApplyDeferredZoom()
    {
        try
        {
            // 缩放由 WinForms 控件自身暴露（它转发给内核控制器），
            // Engine 上没有 ZoomFactor 属性。
            if (View != null)
            {
                View.ZoomFactor = _zoomFactor;
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>使用 Chromium 原生页内查找，网页禁用 JavaScript 后也能查找。</summary>
    public void Find(string text, bool forward, bool firstMatch)
    {
        if (View?.Engine == null) return;
        if (string.IsNullOrEmpty(text)) View.StopFinding(true);
        else View.Find(text, forward, matchCase: false, findNext: !firstMatch);
    }
    // ---------------------------------------------------------------- 事件绑定

    /// <summary>把内核事件绑定到标签上。由 TabManager 在内核初始化完成后调用。</summary>
    internal void BindCoreEvents(BrowserView view)
    {
        view.AddressChanged += (_, _) => view.Ui(() =>
        {
            if (!OwnsView(view)) return;
            UpdateUrlFromView(view);
            RaiseChanged();
        });
        view.TitleChanged += (_, e) =>
        {
            string title = e.Title;
            view.Ui(() => { if (OwnsView(view)) { Title = title; RaiseChanged(); } });
        };
        view.LoadingStateChanged += (_, _) => view.Ui(() => { if (OwnsView(view)) RaiseChanged(); });
        view.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            string source = e.Url;
            int status = e.HttpStatusCode;
            view.Ui(() =>
            {
                if (!OwnsView(view) || !view.HasNavigated || source != view.Source || source == "about:blank") return;
                IsLoading = false;
                LastNavigationSucceeded = status == 0 || status < 400;
                Progress = 100;
                UpdateUrlFromView(view);
                Touch();
                if (LastNavigationSucceeded == true)
                {
                    _manager.NotifyNavigationCompleted(this);
                    _manager.InjectLoginHelper(this);
                }
                else _manager.NotifyNavigationFailed(this, "HTTP " + status);
                RaiseChanged();
            });
        };
        view.LoadError += (_, e) =>
        {
            if (!e.Frame.IsMain || e.ErrorCode == CefErrorCode.Aborted) return;
            string failed = e.FailedUrl;
            string error = e.ErrorText;
            view.Ui(() =>
            {
                if (!OwnsView(view) || !view.HasNavigated || failed != view.Source) return;
                IsLoading = false;
                LastNavigationSucceeded = false;
                Progress = 100;
                _manager.NotifyNavigationFailed(this, error);
                RaiseChanged();
            });
        };
        view.PageMessageReceived += (source, raw) => HandlePageMessage(view, source, raw);
    }

    internal void OnNavigationStarting(BrowserView view, string url)
    {
        if (!OwnsView(view) || !view.HasNavigated) return;
        CredentialPickToken = null;
        CredentialSource = null;
        if (url != BrowserView.HomeAddress && !url.StartsWith("about:") && !url.StartsWith("data:")) Url = url;
        IsLoading = true;
        LastNavigationSucceeded = null;
        Progress = 5;
        _manager.NotifyNavigationStarting(this, url);
        RaiseChanged();
    }

    private void HandlePageMessage(BrowserView view, string source, string raw)
    {
        try
        {
            if (!OwnsView(view) || source != view.Source || string.IsNullOrEmpty(raw)) return;
            if (raw.TrimStart().StartsWith("{"))
            {
                if (InternalPages.Handles(source)) _manager.NotifyInternalPageMessage(this, raw, source);
                return;
            }
            if (!_manager.Settings.PasswordAutofill || IsIncognito ||
                !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http")) return;
            var parsed = LoginAutofill.ParseMessage(raw);
            if (parsed == null) return;
            if (parsed.Value.Type == "pick" && !string.IsNullOrEmpty(CredentialPickToken) &&
                parsed.Value.Token == CredentialPickToken && source == CredentialSource)
                _manager.NotifyCredentialPicked(this, parsed.Value.Id);
            else if (parsed.Value.Type == "submit")
                _manager.NotifyCredentialSubmitted(this, source, parsed.Value.Username, parsed.Value.Password);
        }
        catch (Exception ex) { Log.Warn("处理页面消息失败: " + ex.Message); }
    }
    private void UpdateUrlFromView(BrowserView view)
    {
        try
        {
            string source = view.Engine?.Source;
            // NavigateToString 之后 Source 会变成 about:blank，此时应保留原来的
            // feather://home 标记，否则首页会被误判成真实网页。
            if (string.IsNullOrEmpty(source) ||
                source == BrowserView.HomeAddress || source.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Url = source;
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        _disposed = true;
        DestroyView();
        Changed = null;
    }
}
