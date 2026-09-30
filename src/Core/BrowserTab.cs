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

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public string Url { get; private set; } = UrlUtils.InternalHome;

    public string Title { get; private set; } = "新标签页";

    public bool IsIncognito { get; }

    public TabLife Life { get; private set; } = TabLife.Cold;

    public bool IsLoading { get; private set; }

    public int Progress { get; private set; }

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
                return DefaultTitle;
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

    /// <summary>是否显示内置首页而不是真实网页。</summary>
    public bool IsHomePage => UrlUtils.IsInternal(Url);

    internal void RaiseChanged() => Changed?.Invoke(this);

    // ---------------------------------------------------------------- 视图生命周期

    /// <summary>
    /// 取得 WebView2；如果已被销毁则重新创建，并在内核就绪后加载 <see cref="Url"/>。
    /// 因为是异步的，调用方（TabManager）只需 fire-and-forget，界面会在事件里自然刷新。
    /// </summary>
    internal async Task<WebView2> ObtainViewAsync(Panel host, bool forceRecreate)
    {
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
            Log.Error($"标签 {Id} 的内核初始化失败", ex);
            return view;
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
    internal async Task<bool> SuspendAsync()
    {
        if (View?.CoreWebView2 == null)
        {
            return false;
        }

        try
        {
            View.Visible = false;
        }
        catch
        {
            // 忽略
        }

        try
        {
            bool ok = await View.CoreWebView2.TrySuspendAsync();
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
        if (View?.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            View.CoreWebView2.Resume();
            View.Visible = true;
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
        Life = TabLife.Cold;
        IsLoading = false;
        Progress = 0;

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
            view.Stop();
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
        MemoryMonitor.TrimWorkingSet();
    }

    // ---------------------------------------------------------------- 导航

    /// <summary>加载给定地址（会自动识别内置首页）。</summary>
    public void NavigateTo(string url)
    {
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
            if (isHtml)
            {
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
            Log.Error($"导航失败: {target}", ex);
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
        View.CoreWebView2?.Reload();
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
            IsLoading = true;
            Progress = 5;
            _manager.NotifyNavigationStarting(this, e.Uri);
            RaiseChanged();
        };

        core.SourceChanged += (_, _) =>
        {
            try
            {
                string source = core.Source;
                if (!string.IsNullOrEmpty(source) &&
                    !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
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
            IsLoading = false;
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

        // 图片开关：用 CSS 隐藏而不是拦截请求。
        // 拦截请求仍然会消耗流量与带宽，隐藏则是零网络代价。
        core.DOMContentLoaded += async (_, _) =>
        {
            if (_manager.Settings.LoadImages)
            {
                return;
            }
            try
            {
                await core.ExecuteScriptAsync(
                    "(function(){var s=document.getElementById('__feather_noimg');" +
                    "if(s)return;s=document.createElement('style');s.id='__feather_noimg';" +
                    "s.textContent='img,picture,video,[style*=\"background-image\"]{display:none !important}';" +
                    "document.documentElement.appendChild(s);})()");
            }
            catch
            {
                // 注入失败不影响网页本身
            }
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _manager.NotifyNewWindowRequested(this, e.Uri);
        };

        core.WindowCloseRequested += (_, _) => _manager.NotifyWindowCloseRequested(this);

        // 登录表单自动填充：页面发回的消息在这里处理
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                var parsed = LoginAutofill.ParseMessage(e.TryGetWebMessageAsString());
                if (parsed == null)
                {
                    return;
                }
                switch (parsed.Value.Type)
                {
                    case "pick":
                        _manager.NotifyCredentialPicked(this, parsed.Value.Id);
                        break;
                    case "submit":
                        _manager.NotifyCredentialSubmitted(this, parsed.Value.Username,
                            parsed.Value.Password);
                        break;
                }
            }
            catch
            {
                // 页面消息格式不对就忽略，不影响浏览
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
                if (!_manager.AdBlock.ShouldBlock(e.Request.Uri, false))
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

    public void Dispose() => DestroyView();
}
