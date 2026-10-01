using FeatherBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FeatherBrowser.Core;

/// <summary>
/// 标签集合与 WebView2 生命周期调度器 —— 整个浏览器「省内存」的落点。
///
/// <p>它维持两条不变量：
/// <list type="number">
///   <item>处于「渲染中」的标签数不超过 <see cref="AppSettings.MaxLiveTabs"/>；</item>
///   <item>当前标签一定是渲染中的那一个，且它的 WebView2 挂在可见容器里。</item>
/// </list>
///
/// <p>超出上限的标签会被休眠（销毁 WebView2、渲染进程退出），但标签对象与网址都保留，
/// 因此随着标签数量增长，常驻内存收敛到一个常数而不是线性增长。
/// </summary>
public sealed class TabManager
{
    private readonly List<BrowserTab> _tabs = new();
    private readonly Panel _viewHost;
    private readonly Panel _parking;
    private readonly Control _uiInvoker;
    private readonly BrowserContext _context;
    private CoreWebView2Environment _environment;
    private bool _suspendedAll;
    private bool _activating;

    /// <summary>被激活/挂起/销毁时触发，供界面刷新。</summary>
    public event Action TabsChanged;

    /// <summary>内存策略内部的决策轨迹。默认关闭，自检或排查时挂上它即可看到每一步判断。</summary>
    public static Action<string> Trace { get; set; }

    /// <summary>网页区域的主题（底色与是否强制深色），由界面层在创建前注入。</summary>
    public PageTheme PageTheme { get; private set; } = new();

    /// <summary>需要打开新窗口（window.open）。</summary>
    public event Action<string> NewWindowRequested;

    public AppSettings Settings { get; }

    public AdBlocker AdBlock { get; }

    public HistoryStore History { get; }

    public BookmarkStore Bookmarks { get; }

    /// <summary>下载记录。</summary>
    public DownloadStore Downloads { get; }

    /// <summary>密码库。自动填充与「保存密码」提示都走它。</summary>
    public PasswordStore Passwords { get; }

    /// <summary>某个标签的内核进程挂了（例如被任务管理器结束），请求宿主提示。</summary>
    public event Action<BrowserTab, string, string> ProcessFailed;

    /// <summary>
    /// 网页右键被按下，宿主可以往菜单里补充自己的项。
    /// 参数：标签、内核给的菜单对象、以及该位置的上下文信息。
    /// </summary>
    public event Action<BrowserTab, CoreWebView2ContextMenuRequestedEventArgs>
        ContextMenuRequested;

    /// <summary>需要询问用户是否保存登录凭据时触发（账号, 密码）。</summary>
    public event Action<BrowserTab, string, string> SaveCredentialRequested;

    public bool IsIncognito { get; }

    /// <summary>无痕会话的临时数据目录，退出时删除。</summary>
    public string TemporaryDataFolder { get; }

    public IReadOnlyList<BrowserTab> Tabs => _tabs;

    /// <summary>最近关闭的标签（栈，末尾最新），供「恢复关闭的标签」使用。</summary>
    private readonly List<ClosedTab> _closedTabs = new();

    /// <summary>关闭历史最多留这么多条。浏览器惯例是十几个，这里给宽一点。</summary>
    private const int MaxClosedHistory = 25;

    /// <summary>还有没有可恢复的标签。</summary>
    public bool CanRestoreClosedTab => _closedTabs.Count > 0;

    /// <summary>最近关闭的标签个数。</summary>
    public int ClosedTabCount => _closedTabs.Count;

    /// <summary>
    /// 恢复最近关闭的一个标签。恢复后它会成为当前标签。
    /// </summary>
    /// <returns>恢复了就返回 true；没有可恢复的返回 false。</returns>
    public bool RestoreClosedTab()
    {
        if (_closedTabs.Count == 0)
        {
            return false;
        }

        ClosedTab last = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        Log.Info($"恢复关闭的标签: {last.Url}");
        NewTab(last.Url);
        return true;
    }

    /// <summary>
    /// 调整标签顺序（拖动标签栏时用）。
    /// </summary>
    /// <param name="from">原位置。</param>
    /// <param name="to">目标位置。</param>
    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to)
        {
            return;
        }

        BrowserTab moved = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, moved);

        // 当前标签跟着它自己走，而不是跟着下标
        int active = ActiveIndex;
        if (active == from)
        {
            ActiveIndex = to;
        }
        else if (from < active && to >= active)
        {
            ActiveIndex = active - 1;
        }
        else if (from > active && to <= active)
        {
            ActiveIndex = active + 1;
        }

        // 只改了顺序，不需要重建任何视图
        TabsChanged?.Invoke();
    }

    /// <summary>被关闭的标签记录。</summary>
    private sealed record ClosedTab(string Url, string Title);

    public int Count => _tabs.Count;

    public int ActiveIndex { get; private set; } = -1;

    public BrowserTab Active =>
        ActiveIndex >= 0 && ActiveIndex < _tabs.Count ? _tabs[ActiveIndex] : null;

    public int LiveCount => _tabs.Count(t => t.Life == TabLife.Live);

    public int SuspendedCount => _tabs.Count(t => t.Life == TabLife.Suspended);

    public int ColdCount => _tabs.Count(t => t.Life == TabLife.Cold);

    public int TotalCreated => _tabs.Sum(t => t.CreatedCount);

    /// <summary>诊断用：把每个标签的档位与最近使用时间打成一行，便于核对内存策略是否生效。</summary>
    public string DescribeState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"上限 {Settings.MaxLiveTabs}：");
        foreach (BrowserTab tab in _tabs.OrderByDescending(t => t == Active)
                     .ThenByDescending(t => t.LastUsedAt))
        {
            string life = tab.Life switch
            {
                TabLife.Live => "渲染",
                TabLife.Suspended => "挂起",
                _ => "休眠",
            };
            sb.Append($" [{tab.Id} {(tab == Active ? "*" : " ")}{life} 视图{(tab.View != null ? "有" : "无")}]");
        }
        return sb.ToString();
    }

    public TabManager(BrowserContext context, Panel viewHost, Panel parking,
        Control uiInvoker, bool incognito, string temporaryDataFolder)
    {
        _context = context;
        Settings = context.Settings;
        AdBlock = context.AdBlock;
        History = context.History;
        Bookmarks = context.Bookmarks;
        Passwords = context.Passwords;
        Downloads = context.Downloads;
        _viewHost = viewHost;
        _parking = parking;
        _uiInvoker = uiInvoker;
        IsIncognito = incognito;
        TemporaryDataFolder = temporaryDataFolder;
    }

    /// <summary>
    /// 创建一个 WebView2 环境。
    ///
    /// <para>抽成静态方法是为了让多窗口共用：普通窗口都拿 <see cref="BrowserContext"/>
    /// 里的同一个环境，只有无痕窗口才单独建一个（它需要独立的临时数据目录）。</para>
    /// </summary>
    public static async Task<CoreWebView2Environment> CreateEnvironmentAsync(
        string userDataFolder, bool forceDarkPages)
    {
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection " +
            "--disable-background-timer-throttling=false " +
            // 让内核子进程继承本程序的身份，任务管理器里才会归到「轻羽浏览器」名下，
            // 而不是显示成一堆 msedgewebview2
            $"--app-user-model-id={AppIdentity.AppUserModelId}")
        {
            Language = "zh-CN",
        };

        // 深色主题：让内核把网页也按深色渲染（Chromium 的自动深色模式）。
        // 这是内核启动参数，只能在创建环境时给，运行中改主题需要重建环境。
        if (forceDarkPages)
        {
            options.AdditionalBrowserArguments += " --enable-features=WebContentsForceDark";
        }

        // 关于自定义 scheme（记录一个踩过的坑）：
        // 曾想用 CoreWebView2CustomSchemeRegistration 把 feather.local 声明成安全 scheme，
        // 但这个 SDK 版本上 options.CustomSchemeRegistrations 是只读属性且**默认是 null**，
        // 调 Add 必抛 NullReference，也无法给它赋值（编译不过）。
        // 实测不需要它：SetVirtualHostNameToFolderMapping + https:// 前缀的虚拟主机
        // 本身就能正常加载页面（已用 ExecuteScriptAsync 取页面文本验证过）。

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: options);

        Log.Info($"WebView2 环境就绪，用户数据目录: {userDataFolder}");
        return environment;
    }

    /// <summary>
    /// 把内置管理页的虚拟主机指到磁盘上的页面目录。
    /// 映射是实例级的（不是环境级），所以每个 WebView2 都要设一次。
    /// </summary>
    internal static void MapInternalPages(CoreWebView2 core)
    {
        try
        {
            string folder = InternalPages.Folder;
            Directory.CreateDirectory(folder);
            core.SetVirtualHostNameToFolderMapping(
                InternalPages.Host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        }
        catch (Exception ex)
        {
            Log.Warn("映射内置页面目录失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 取得本窗口要用的 WebView2 环境。
    ///
    /// <para>普通窗口从 <see cref="BrowserContext"/> 拿共用环境（缓存与 Cookie 不分裂）；
    /// 无痕窗口用自己独立的环境与临时目录。</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_environment != null)
        {
            return;
        }

        if (IsIncognito && !string.IsNullOrEmpty(TemporaryDataFolder))
        {
            _environment = await CreateEnvironmentAsync(TemporaryDataFolder, PageTheme.ForceDark);
        }
        else
        {
            _environment = await _context.GetEnvironmentAsync(PageTheme.ForceDark);
        }
    }

    /// <summary>
    /// 主题切换后更新网页区域。
    ///
    /// <p>底色可以立刻生效；「强制深色」是内核启动参数、不可热改，
    /// 这里退一步用 Profile.PreferredColorScheme 影响站点自身配色，
    /// 已有页面刷新后观感基本一致。
    /// </summary>
    public void UpdatePageTheme(int backgroundArgb, bool forceDark)
    {
        PageTheme = new PageTheme
        {
            BackgroundArgb = backgroundArgb,
            ForceDark = forceDark,
        };

        foreach (BrowserTab tab in _tabs)
        {
            tab.ApplyThemeToView();
        }
    }

    public bool IsReady => _environment != null;

    internal CoreWebView2Environment Environment => _environment;

    // ---------------------------------------------------------------- 标签操作

    public BrowserTab NewTab(string url, bool activate = true)
    {
        var tab = new BrowserTab(this, url, IsIncognito);
        tab.Changed += _ => TabsChanged?.Invoke();
        _tabs.Add(tab);

        if (activate || ActiveIndex < 0)
        {
            Activate(_tabs.Count - 1);
        }
        else
        {
            TabsChanged?.Invoke();
        }
        return tab;
    }

    /// <summary>按索引切换标签。</summary>
    public void Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        if (ActiveIndex >= 0 && ActiveIndex < _tabs.Count && ActiveIndex != index)
        {
            _tabs[ActiveIndex].Touch();
        }

        ActiveIndex = index;
        Activate(_tabs[index]);
    }

    /// <summary>
    /// 把某个标签变成当前标签：把它提到可见容器，并按内存策略收敛其他标签。
    /// </summary>
    public void Activate(BrowserTab tab)
    {
        if (tab == null || !_tabs.Contains(tab))
        {
            return;
        }

        int index = _tabs.IndexOf(tab);
        if (index >= 0)
        {
            ActiveIndex = index;
        }
        tab.Touch();

        if (_activating)
        {
            // 防止递归激活（ObtainView 内部可能触发事件）
            return;
        }

        _activating = true;
        try
        {
            _suspendedAll = false;

            // 先把不是目标的标签全部移出可见区域
            foreach (BrowserTab other in _tabs)
            {
                if (other == tab || other.View == null)
                {
                    continue;
                }
                if (other.View.Parent == _viewHost)
                {
                    _viewHost.Controls.Remove(other.View);
                    _parking.Controls.Add(other.View);
                }
            }

            // 创建/唤起 WebView2。这里是异步的（要等内核就绪），
            // 因此不 await：界面先切过去，页面在几十毫秒后开始加载。
            bool needCreate = tab.View == null;
            if (needCreate || tab.Life == TabLife.Cold)
            {
                _ = tab.ObtainViewAsync(_viewHost, forceRecreate: true);
            }
            else
            {
                WebView2 view = tab.View;
                if (view.Parent != _viewHost)
                {
                    if (view.Parent is Control parent)
                    {
                        parent.Controls.Remove(view);
                    }
                    _viewHost.Controls.Add(view);
                }
                view.BringToFront();
                if (tab.Life == TabLife.Suspended)
                {
                    tab.Resume();
                }
            }
        }
        finally
        {
            _activating = false;
        }

        EnforceMemoryPolicy();
        TabsChanged?.Invoke();
    }

    /// <summary>关闭标签。关掉最后一个标签会自动新建首页标签。</summary>
    public void CloseTab(BrowserTab tab)
    {
        if (tab == null)
        {
            return;
        }

        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);
        tab.Dispose();

        // 记下来，供「恢复关闭的标签」(Ctrl+Shift+T) 用。
        // 空地址不记（例如刚建出来还没导航的标签），恢复它没有意义。
        if (!string.IsNullOrWhiteSpace(tab.Url) && !UrlUtils.IsInternal(tab.Url))
        {
            _closedTabs.Add(new ClosedTab(tab.Url, tab.Title));
            while (_closedTabs.Count > MaxClosedHistory)
            {
                _closedTabs.RemoveAt(0);
            }
        }

        if (_tabs.Count == 0)
        {
            NewTab(UrlUtils.InternalHome);
            return;
        }

        if (ActiveIndex == index)
        {
            ActiveIndex = Math.Min(index, _tabs.Count - 1);
            Activate(ActiveIndex);
        }
        else
        {
            if (ActiveIndex > index)
            {
                ActiveIndex--;
            }
            EnforceMemoryPolicy();
            TabsChanged?.Invoke();
        }
    }

    public void CloseAllExcept(BrowserTab keep)
    {
        foreach (BrowserTab tab in _tabs.ToList())
        {
            if (tab == keep)
            {
                continue;
            }
            _tabs.Remove(tab);
            tab.Dispose();
        }

        if (_tabs.Count == 0)
        {
            NewTab(UrlUtils.InternalHome);
            return;
        }

        Activate(_tabs.IndexOf(_tabs[0]));
    }

    // ---------------------------------------------------------------- 内存策略

    /// <summary>
    /// 按「同时渲染上限」收敛标签。
    ///
    /// <p>只保留两档，故意不用 TrySuspendAsync 做中间态：实测它要求视图处于不可见状态
    /// 才会成功，用在「标签数超限」这种场景失败率很高，而一次失败就意味着上限形同虚设。
    /// 所以这里只做确定的事 —— 超出上限的标签把 WebView2 整个销毁，渲染进程退出、
    /// 内存真正归还；标签本身和网址都留着，切回去按 URL 重新加载。
    ///
    /// <p>算法是「填桶」：当前标签先占一个名额，剩下的按最近使用时间从新到旧填，填满即停。
    /// </summary>
    public void EnforceMemoryPolicy()
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        int liveBudget = Math.Clamp(Settings.MaxLiveTabs, 1, 8);
        BrowserTab active = Active;

        // 当前标签永远优先占一个名额
        var ordered = new List<BrowserTab>(_tabs.Count);
        if (active != null)
        {
            ordered.Add(active);
        }
        ordered.AddRange(_tabs
            .Where(t => t != active)
            .OrderByDescending(t => t.LastUsedAt));

        Trace?.Invoke($"策略执行：同时渲染上限 {liveBudget}，当前 {active?.Id ?? "无"}");

        int liveUsed = 0;

        foreach (BrowserTab tab in ordered)
        {
            // 关键：当前标签即使「视图还没建好」也要占住名额。
            // 它马上会在 ObtainViewAsync 里创建内核，如果这里跳过不计，
            // 等它创建完就会悄悄多出一个渲染中的标签，突破上限。
            bool wantsLive = tab == active || liveUsed < liveBudget;

            if (wantsLive)
            {
                if (tab.Life == TabLife.Suspended && tab.View != null)
                {
                    tab.Resume();
                }
                liveUsed++;
                Trace?.Invoke($"  {tab.Id} → 保持渲染（第 {liveUsed} 个）");
            }
            else if (tab.View != null)
            {
                tab.DestroyView();
                Trace?.Invoke($"  {tab.Id} → 休眠（销毁渲染进程）");
            }
        }
    }

    /// <summary>
    /// 窗口失去焦点 / 最小化时调用：把非当前标签的渲染进程挂起。
    /// 此场景下视图确实处于不可见状态，TrySuspendAsync 通常能成功，
    /// 失败也无所谓（下一轮策略会直接把它销毁）。
    /// </summary>
    public void SuspendBackgroundTabs()
    {
        if (!Settings.SuspendOnDeactivate || _tabs.Count <= 1)
        {
            return;
        }

        BrowserTab active = Active;
        int suspended = 0;
        foreach (BrowserTab tab in _tabs)
        {
            if (tab == active || tab.View == null || tab.Life != TabLife.Live)
            {
                continue;
            }
            // 当前标签的视图留在可见容器里，其余都在 parking 里（不可见）
            if (tab.View.Parent == _viewHost)
            {
                continue;
            }
            _ = tab.SuspendAsync();
            suspended++;
        }

        if (suspended > 0)
        {
            _suspendedAll = true;
            TabsChanged?.Invoke();
        }
    }

    /// <summary>窗口重新获得焦点：当前标签恢复，并重新收敛。</summary>
    public void ResumeFromSuspend()
    {
        if (!_suspendedAll)
        {
            return;
        }
        _suspendedAll = false;

        Active?.Resume();
        EnforceMemoryPolicy();
        TabsChanged?.Invoke();
    }

    /// <summary>立刻把所有非当前标签降为冷标签（菜单里的「立即回收内存」）。</summary>
    public void ReclaimNow(bool includeActive = false)
    {
        foreach (BrowserTab tab in _tabs)
        {
            if (!includeActive && tab == Active)
            {
                continue;
            }
            tab.DestroyView();
        }
        MemoryMonitor.TrimWorkingSet();
        TabsChanged?.Invoke();
    }

    /// <summary>WebView2 环境的释放由窗口负责；这里只销毁各标签的控件。</summary>
    public void Shutdown()
    {
        foreach (BrowserTab tab in _tabs)
        {
            tab.Dispose();
        }
        _tabs.Clear();
    }

    // ---------------------------------------------------------------- 视图创建

    /// <summary>为一个标签创建 WebView2 控件（此时还没初始化内核，也还没导航）。</summary>
    internal WebView2 CreateViewFor(BrowserTab tab)
    {
        if (_environment == null)
        {
            throw new InvalidOperationException("WebView2 环境尚未初始化完成");
        }

        return new WebView2
        {
            Dock = DockStyle.Fill,
        };
    }

    /// <summary>
    /// 初始化某个标签的 WebView2 内核，然后套用设置并绑定事件。
    /// 用 <c>EnsureCoreWebView2Async</c> 而不是让控件自己隐式初始化，
    /// 这样能明确知道「内核什么时候可用」，避免导航发生在初始化之前。
    /// </summary>
    internal async Task<CoreWebView2> EnsureCoreAsync(WebView2 view, BrowserTab tab)
    {
        await view.EnsureCoreWebView2Async(_environment);
        CoreWebView2 core = view.CoreWebView2;
        ApplyCoreSettings(core, tab);
        tab.BindCoreEvents(core);
        return core;
    }

    /// <summary>把用户设置套用到内核上。</summary>
    private void ApplyCoreSettings(CoreWebView2 core, BrowserTab tab)
    {
        try
        {
            var s = core.Settings;
            s.IsScriptEnabled = Settings.JavaScriptEnabled;
            s.AreDefaultScriptDialogsEnabled = true;
            // 自动填充要用 WebMessage 和页面通信（页面把用户点选的账号、
            // 以及提交时的输入值发回来），所以必须打开
            s.IsWebMessageEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            s.IsBuiltInErrorPageEnabled = true;
            s.IsPasswordAutosaveEnabled = true;
            s.IsGeneralAutofillEnabled = true;
            s.IsPinchZoomEnabled = true;
            // 省内存：把「预渲染」与「后台挂起」策略交给 WebView2 自己管
            s.IsSwipeNavigationEnabled = false;
            s.IsReputationCheckingRequired = false;
            s.IsNonClientRegionSupportEnabled = false;

            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            Log.Warn("应用内核设置失败: " + ex.Message);
        }

        try
        {
            // 无痕：内核级别关闭磁盘缓存写入
            if (IsIncognito && core.Profile != null)
            {
                // 无痕窗口使用独立的用户数据目录，本身就不会留下痕迹
            }
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- 事件转发

    internal void NotifyNavigationStarting(BrowserTab tab, string uri)
    {
        if (tab == Active)
        {
            TabsChanged?.Invoke();
        }
    }

    internal void NotifyNavigationCompleted(BrowserTab tab)
    {
        // 记录历史（首页不记）
        if (!tab.IsHomePage)
        {
            History.Record(tab.Url, tab.Title);
        }

        TabsChanged?.Invoke();

        // 后台标签加载完了，正好按策略收敛一次，让内存尽早回落
        if (tab != Active)
        {
            EnforceMemoryPolicy();
        }
    }

    internal void NotifyNavigationFailed(BrowserTab tab, string error)
    {
        Log.Warn($"标签 {tab.Id} 加载失败: {error} ({tab.Url})");
        TabsChanged?.Invoke();
    }

    internal void NotifyNewWindowRequested(BrowserTab tab, string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return;
        }
        NewWindowRequested?.Invoke(uri);
    }

    // ---------------------------------------------------------------- 自动填充

    /// <summary>
    /// 把登录辅助脚本注入某个标签的页面。
    ///
    /// <p>注入的内容**只含用户名**：密码要等用户在页面上的下拉里点选之后，
    /// 由 <see cref="NotifyCredentialPicked"/> 单独注入那一条。
    /// </summary>
    internal void InjectLoginHelper(BrowserTab tab)
    {
        if (tab == null || IsIncognito || tab.View?.CoreWebView2 == null)
        {
            return;
        }
        if (!Settings.PasswordAutofill)
        {
            return;
        }

        try
        {
            var matches = Passwords.FindForUrl(tab.Url);
            var accounts = matches
                .Select(e => (Id: e.Username + "\u0001" + e.Domain, Username: e.Username))
                .ToList();

            // 站点上没有已保存账号时 BuildScript 会返回空串，等价于不注入
            string script = LoginAutofill.BuildScript(accounts);
            if (script.Length == 0)
            {
                return;
            }
            _ = tab.View.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("注入登录辅助脚本失败: " + ex.Message);
        }
    }

    /// <summary>用户在页面的账号下拉里选了某一项。</summary>
    internal void NotifyCredentialPicked(BrowserTab tab, string accountId)
    {
        if (tab?.View?.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            var matches = Passwords.FindForUrl(tab.Url);
            PasswordEntry target = null;

            if (!string.IsNullOrEmpty(accountId))
            {
                // id 由「用户名 \u0001 域名」拼成，用第一个命中项即可
                string[] parts = accountId.Split('\u0001');
                string wantUser = parts.Length > 0 ? parts[0] : "";
                target = matches.FirstOrDefault(e =>
                    string.Equals(e.Username, wantUser, StringComparison.Ordinal));
            }
            target ??= matches.FirstOrDefault();

            if (target == null)
            {
                return;
            }

            string password = Passwords.RevealPassword(target);
            if (string.IsNullOrEmpty(password))
            {
                Log.Warn("这条凭据的密码无法解开（可能来自另一个 Windows 账户）");
                return;
            }

            // 明文只在这一瞬间存在于这条脚本字符串里，不写日志、不缓存
            string script =
                "window.__featherFillPassword&&window.__featherFillPassword(" +
                JsQuote(target.Username) + "," + JsQuote(password) + ")";
            _ = tab.View.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("填充密码失败: " + ex.Message);
        }
    }

    /// <summary>用户在登录表单里提交了，问一下要不要保存。</summary>
    internal void NotifyCredentialSubmitted(BrowserTab tab, string username, string password)
    {
        if (tab == null || string.IsNullOrEmpty(password))
        {
            return;
        }
        if (!Settings.PasswordAutofill || IsIncognito)
        {
            return;
        }

        // 已经有同站点同账号的记录了就不打扰
        var existing = Passwords.FindForUrl(tab.Url);
        if (existing.Any(e => string.Equals(e.Username, username ?? "",
                StringComparison.Ordinal)))
        {
            return;
        }

        SaveCredentialRequested?.Invoke(tab, username ?? "", password);
    }

    /// <summary>把字符串转成 JS 字面量（用于把用户名/密码拼进脚本）。</summary>
    private static string JsQuote(string text)
    {
        if (text == null)
        {
            return "''";
        }
        var sb = new System.Text.StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    internal void NotifyWindowCloseRequested(BrowserTab tab) => CloseTab(tab);

    internal void NotifyContextMenuRequested(BrowserTab tab,
        CoreWebView2ContextMenuRequestedEventArgs e) =>
        ContextMenuRequested?.Invoke(tab, e);

    /// <summary>内置管理页发来的消息（JSON）。宿主负责解析与响应。</summary>
    public event Action<BrowserTab, string> InternalPageMessage;

    internal void NotifyInternalPageMessage(BrowserTab tab, string json) =>
        InternalPageMessage?.Invoke(tab, json);

    /// <summary>
    /// 把数据推给内置管理页。
    ///
    /// <para>用 <c>ExecuteScriptAsync</c> 调页面上的 <c>window.featherUpdate</c> 回调。
    /// 数据走 JSON 序列化后拼进脚本，所以必须转义，否则文件名里的引号会破坏脚本。</para>
    /// </summary>
    internal async void PushToInternalPage(BrowserTab tab, string json)
    {
        try
        {
            WebView2 view = tab?.View;
            if (view?.CoreWebView2 == null)
            {
                Log.Warn("推数据给内置页失败：视图还没就绪");
                return;
            }
            string script = $"window.featherUpdate({json});";
            Log.Info($"推数据给内置页: {json.Length} 字节 JSON");
            await view.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("向内置页面推送数据失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 内核开始一次下载。
    ///
    /// <para>只记录状态，不接管字节流：内核会按浏览器默认行为把文件写进下载目录，
    /// 我们订阅它的进度回调更新记录。这样大文件不会多一次拷贝。</para>
    /// </summary>
    internal void HandleDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            CoreWebView2DownloadOperation op = e.DownloadOperation;
            string fileName = "未命名文件";
            try
            {
                string fromUri = Path.GetFileName(new Uri(op.Uri).LocalPath);
                if (!string.IsNullOrWhiteSpace(fromUri))
                {
                    fileName = Uri.UnescapeDataString(fromUri);
                }
            }
            catch
            {
                // Uri 解析失败就用兜底名字
            }

            DownloadItem item = Downloads.Begin(op.Uri, fileName);
            Log.Info($"开始下载: {fileName} <- {op.Uri}");

            op.BytesReceivedChanged += (_, _) =>
                Downloads.UpdateProgress(item, (long)op.BytesReceived, (long)op.TotalBytesToReceive);

            op.StateChanged += (_, _) =>
            {
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        Downloads.Complete(item, op.ResultFilePath,
                            (long)op.TotalBytesToReceive);
                        Log.Info($"下载完成: {op.ResultFilePath}");
                        DownloadsChanged?.Invoke();
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        Downloads.Fail(item, op.InterruptReason.ToString());
                        Log.Warn($"下载中断: {item.FileName} / {op.InterruptReason}");
                        DownloadsChanged?.Invoke();
                        break;
                }
            };

            DownloadsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("处理下载开始事件失败: " + ex.Message);
        }
    }

    /// <summary>下载状态有变化（开始 / 完成 / 中断），宿主可据此刷新下载页。</summary>
    public event Action DownloadsChanged;

    /// <summary>
    /// 某个标签的内核进程异常退出。
    ///
    /// <p>不能静默忽略：这时标签持有的 WebView2 已经失效，用户看到的会是白屏或卡死。
    /// 处理办法是把受影响的标签降为冷态（丢掉失效引用）再按需重建。
    ///
    /// <p>两种情况要区别对待：
    /// <list type="bullet">
    ///   <item><c>RenderProcessExited</c> —— 只有某个标签的渲染进程挂了，
    ///         重建那一个即可；</item>
    ///   <item><c>BrowserProcessExited</c> —— 整个内核进程没了，**所有**标签的视图都失效了。
    ///         必须把全部标签降为冷态，否则其它标签会永远打不开。</item>
    /// </list>
    /// </summary>
    internal void NotifyProcessFailed(BrowserTab tab, string kind, string reason)
    {
        Log.Warn($"标签 {tab.Id} 的内核进程异常退出：{kind} / {reason}");

        bool wholeBrowser = kind.Contains("BrowserProcess", StringComparison.OrdinalIgnoreCase);
        bool wasActive = tab == Active;

        if (wholeBrowser)
        {
            // 内核整体退出：所有视图都失效，全部降为冷态
            foreach (BrowserTab other in _tabs)
            {
                other.DestroyView();
            }
        }
        else
        {
            tab.DestroyView();
        }

        TabsChanged?.Invoke();
        ProcessFailed?.Invoke(tab, kind, reason);

        // 当前标签重建，让用户感觉不到中断（其它冷标签在切过去时自然重建）
        if (wasActive && _tabs.Contains(tab))
        {
            Activate(tab);
        }
    }
}
