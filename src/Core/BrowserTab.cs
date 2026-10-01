using FeatherBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FeatherBrowser.Core;

/// <summary>标签的存活档位。</summary>
public enum TabLife
{
    /// <summary>WebView2 已销毁，只剩 URL 等元数据。内存占用最低，界面显示为「休眠」。</summary>
    Cold = 0,

    /// <summary>WebView2 存在但已被内核挂起。只在「窗口失去焦点」这类视图确实不可见的场景出现。</summary>
    Suspended = 1,

    /// <summary>WebView2 存活且正在渲染。界面显示为「渲染中」。</summary>
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
///   <item>超过「同时渲染标签数」上限的标签会被 <b>休眠</b>：Dispose 掉 WebView2，
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

    internal bool OwnsView(WebView2 view) => !_disposed && ReferenceEquals(View, view) && !view.IsDisposed;

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

    /// <summary>探针：本标签历史上创建过多少个 WebView2。用于验证回收确实发生。</summary>
    public int CreatedCount { get; private set; }

    public DateTime LastUsedAt { get; private set; } = DateTime.Now;

    public WebView2 View { get; private set; }

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
    /// 取得 WebView2；如果已被销毁则重新创建，并在内核就绪后加载 <see cref="Url"/>。
    /// 因为是异步的，调用方（TabManager）只需 fire-and-forget，界面会在事件里自然刷新。
    /// </summary>
    internal async Task<WebView2> ObtainViewAsync(Panel host, bool forceRecreate)
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

        WebView2 view = _manager.CreateViewFor(this);
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

            if (View.CoreWebView2?.Profile != null)
            {
                View.CoreWebView2.Profile.PreferredColorScheme = theme.ForceDark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
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
        WebView2 view = View;
        if (View?.CoreWebView2 == null)
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
            bool ok = await view.CoreWebView2.TrySuspendAsync();
            if (!OwnsView(view))
            {
                return false;
            }
            // 用户可能在挂起请求完成前已切回这个标签。
            if (!_suspendRequested)
            {
                view.CoreWebView2.Resume();
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
        if (View?.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            View.CoreWebView2.Resume();
            View.Visible = this == _manager.Active;
            Life = TabLife.Live;
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复标签 {Id} 失败: {ex.Message}");
        }
    }

    /// <summary>销毁 WebView2，真正把渲染进程的内存交还给系统。</summary>
    internal void DestroyView()
    {
        WebView2 view = View;
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
                view.CoreWebView2?.Stop();
            }
            catch
            {
                // 忽略
            }
            if (view.Parent is Control parent)
            {
                parent.Controls.Remove(view);
            }
            view.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"销毁标签 {Id} 的 WebView2 失败: {ex.Message}");
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
        if (View?.CoreWebView2 == null)
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
                View.CoreWebView2.NavigateToString(target);
            }
            else if (target.StartsWith("feather://search?q=", StringComparison.OrdinalIgnoreCase))
            {
                // 首页搜索框提交后由这里转成真正的搜索请求
                string query = Uri.UnescapeDataString(target["feather://search?q=".Length..]);
                string url = UrlUtils.Normalize(query, _manager.Settings.Engine.Template);
                Url = url;
                View.CoreWebView2.Navigate(url);
            }
            else
            {
                View.CoreWebView2.Navigate(target);
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
            View.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (View?.CanGoForward == true)
        {
            View.CoreWebView2.GoForward();
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
        if (View.CoreWebView2 != null)
        {
            LastNavigationSucceeded = null;
            View.CoreWebView2.Reload();
        }
    }

    public void Stop()
    {
        try
        {
            View?.CoreWebView2?.Stop();
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
            // CoreWebView2 上没有 ZoomFactor 属性。
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

    /// <summary>
    /// 页内查找。
    ///
    /// <p>官方托管 API 的 Find 系列在这个版本的 WebView2 SDK 里并不存在，
    /// 因此改为注入一段小脚本自己实现：用 window.find 定位，配合高亮选中，
    /// 逻辑只有十几行，也不会给页面留下常驻对象。
    /// </summary>
    public void Find(string text, bool forward, bool firstMatch)
    {
        if (View?.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(text))
            {
                _ = View.CoreWebView2.ExecuteScriptAsync(
                    "try{window.getSelection().removeAllRanges();}catch(e){}");
                return;
            }

            string escaped = text
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "")
                .Replace("\n", "");
            string script = firstMatch
                ? "(function(){window.__featherFind='" + escaped + "';" +
                  "return window.find(window.__featherFind,false," +
                  (forward ? "false" : "true") + ",true,false,false,false);})()"
                : "(function(){return window.find(window.__featherFind||'" + escaped + "',false," +
                  (forward ? "false" : "true") + ",true,false,false,false);})()";
            _ = View.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            // 忽略查找失败
        }
    }

    // ---------------------------------------------------------------- 事件绑定

    /// <summary>把内核事件绑定到标签上。由 TabManager 在内核初始化完成后调用。</summary>
    internal void BindCoreEvents(CoreWebView2 core)
    {
        WebView2 view = View;
        if (view == null || core == null)
        {
            return;
        }

        core.NavigationStarting += (_, e) =>
        {
            if (!OwnsView(view)) return;
            CredentialPickToken = null;
            CredentialSource = null;
            if (e.Uri.StartsWith("feather://search?q=", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                string query = Uri.UnescapeDataString(e.Uri["feather://search?q=".Length..]);
                string target = UrlUtils.Normalize(query, _manager.Settings.Engine.Template);
                view.BeginInvoke(() => { if (OwnsView(view)) NavigateTo(target); });
                return;
            }
            core.Settings.IsScriptEnabled = _manager.Settings.JavaScriptEnabled ||
                UrlUtils.IsInternal(e.Uri) || (IsHomePage && e.Uri == "about:blank");
            IsLoading = true;
            LastNavigationSucceeded = null;
            Progress = 5;
            _manager.NotifyNavigationStarting(this, e.Uri);
            RaiseChanged();
        };

        core.SourceChanged += (_, _) =>
        {
            if (!OwnsView(view)) return;
            try
            {
                string source = core.Source;
                if (!string.IsNullOrEmpty(source) &&
                    !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                    !(IsHomePage && source.Equals("about:blank", StringComparison.OrdinalIgnoreCase)))
                {
                    Url = source;
                }
            }
            catch
            {
                // 忽略
            }
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            if (!OwnsView(view)) return;
            try
            {
                string title = core.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Title = title;
                }
            }
            catch
            {
                // 忽略
            }
            RaiseChanged();
        };

        core.NavigationCompleted += (_, e) =>
        {
            if (!OwnsView(view)) return;
            IsLoading = false;
            LastNavigationSucceeded = e.IsSuccess;
            Progress = 100;
            if (e.IsSuccess)
            {
                UpdateUrlFromView(view);
                Touch();
                _manager.NotifyNavigationCompleted(this);
            }
            else
            {
                _manager.NotifyNavigationFailed(this, e.WebErrorStatus.ToString());
            }
            RaiseChanged();
        };

        core.FaviconChanged += (_, _) => RaiseChanged();

        core.HistoryChanged += (_, _) => RaiseChanged();

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _manager.NotifyNewWindowRequested(this, e.Uri);
        };

        core.WindowCloseRequested += (_, _) => _manager.NotifyWindowCloseRequested(this);

        // 内置管理页（书签 / 下载 / 历史）走虚拟主机映射：把 feather.local 指到页面目录。
        // 曾经试过 feather:// 自定义协议 + WebResourceRequested，实测处理器根本不会被调用
        // （WebView2 不为自定义协议触发该事件），导航直接 ConnectionAborted，页面全空白。
        TabManager.MapInternalPages(core);

        // 右键菜单：在内核自带的菜单上补充桌面浏览器的常规操作
        core.ContextMenuRequested += (_, e) => _manager.NotifyContextMenuRequested(this, e);

        // 下载：交给内核按浏览器默认方式存盘，我们只记录状态供下载页展示。
        // 不接管字节流是有意的 —— 接管会让大文件多一次拷贝，也更容易出错。
        core.DownloadStarting += (_, e) => _manager.HandleDownloadStarting(e);

        // 内核进程意外消失时上报。
        // 用户可能用任务管理器单独结束了某个渲染进程 —— 那时外壳还在，
        // 但那个标签已经是死壳子。必须让宿主知道，才能自动恢复或给出提示，
        // 而不是留一个「点了没反应也说不清哪坏了」的界面。
        core.ProcessFailed += (_, e) =>
        {
            if (OwnsView(view))
                _manager.NotifyProcessFailed(this, e.ProcessFailedKind.ToString(), e.Reason.ToString());
        };

        // 登录表单自动填充与内置管理页：页面发回的消息都在这里分流
        core.WebMessageReceived += (_, e) =>
        {
            // 先无条件记录「事件到了」这件事本身。
            // 之前这一步在 try 里面，处理器内部一抛异常就被空 catch 吞掉，
            // 结果看上去像「页面的消息从来没发出来」，排查方向完全被带偏。
            bool debug = Environment.GetEnvironmentVariable("FEATHER_DEBUG_MSG") == "1";
            if (debug)
            {
                Log.Info("WebMessageReceived 事件已触发");
            }

            try
            {
                if (!OwnsView(view) || !string.Equals(e.Source, core.Source, StringComparison.Ordinal)) return;
                string raw = e.TryGetWebMessageAsString();
                if (debug)
                {
                    Log.Info($"页面消息长度: {raw?.Length ?? 0}");
                }

                // 内置管理页的消息是 JSON 对象（以 { 开头），自动填充的是自己的紧凑格式
                if (!string.IsNullOrEmpty(raw) && raw.TrimStart().StartsWith("{"))
                {
                    if (InternalPages.Handles(e.Source))
                        _manager.NotifyInternalPageMessage(this, raw, e.Source);
                    return;
                }

                if (!_manager.Settings.PasswordAutofill || IsIncognito ||
                    !Uri.TryCreate(e.Source, UriKind.Absolute, out var sourceUri) ||
                    (sourceUri.Scheme != "https" && sourceUri.Scheme != "http")) return;
                var parsed = LoginAutofill.ParseMessage(raw);
                if (parsed == null)
                {
                    return;
                }
                switch (parsed.Value.Type)
                {
                    case "pick":
                        if (!string.IsNullOrEmpty(CredentialPickToken) &&
                            parsed.Value.Token == CredentialPickToken && e.Source == CredentialSource)
                            _manager.NotifyCredentialPicked(this, parsed.Value.Id);
                        break;
                    case "submit":
                        _manager.NotifyCredentialSubmitted(this, e.Source, parsed.Value.Username,
                            parsed.Value.Password);
                        break;
                }
            }
            catch (Exception ex)
            {
                // 以前这里是空 catch：处理器自己出错时完全没有痕迹，
                // 表现成「页面的消息没到」，非常容易被误导。现在一定留下日志。
                Log.Warn($"处理页面消息失败: {ex.GetType().Name} / {ex.Message}");
            }
        };

        // 每次页面加载完成都注入一次：SPA 的登录框常常是后渲染出来的，
        // 脚本自身幂等（window.__featherLogin 标记），重复注入没有副作用。
        core.DOMContentLoaded += (_, _) => _manager.InjectLoginHelper(this);

        // 拦截钩子：满足条件时返回空响应
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    return;
                }
                bool blockImage = !_manager.Settings.LoadImages &&
                    e.ResourceContext == CoreWebView2WebResourceContext.Image;
                if (!blockImage && !_manager.AdBlock.ShouldBlock(e.Request.Uri, false))
                {
                    return;
                }
                e.Response = core.Environment.CreateWebResourceResponse(
                    null, 200, "OK", "Content-Type: text/plain; charset=utf-8");
            }
            catch
            {
                // 拦截失败就让请求正常走
            }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
    }

    private void UpdateUrlFromView(WebView2 view)
    {
        try
        {
            string source = view.CoreWebView2?.Source;
            // NavigateToString 之后 Source 会变成 about:blank，此时应保留原来的
            // feather://home 标记，否则首页会被误判成真实网页。
            if (string.IsNullOrEmpty(source) ||
                source.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
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
