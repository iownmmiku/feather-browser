using FeatherBrowser.Services;
using CefSharp;


namespace FeatherBrowser.Core;

/// <summary>
/// 标签集合与 BrowserView 生命周期调度器 —— 整个浏览器「省内存」的落点。
///
/// <p>它维持两条不变量：
/// <list type="number">
///   <item>处于「渲染中」的标签数不超过 <see cref="AppSettings.MaxLiveTabs"/>；</item>
///   <item>当前标签一定是渲染中的那一个，且它的 BrowserView 挂在可见容器里。</item>
/// </list>
///
/// <p>超出上限的标签会被休眠（销毁 BrowserView、渲染进程退出），但标签对象与网址都保留，
/// 因此随着标签数量增长，常驻内存收敛到一个常数而不是线性增长。
/// </summary>
public sealed class TabManager
{
    private readonly List<BrowserTab> _tabs = new();
    private readonly Panel _viewHost;
    private readonly Panel _parking;
    private readonly Control _uiInvoker;
    private readonly BrowserContext _context;
    private BrowserProfile _environment;
    private readonly HashSet<BrowserView> _views = new();
    internal Task WaitForViewsDisposedAsync() => Task.WhenAll(_views.Select(v => v.DisposalTask));
    private bool _suspendedAll;
    private bool _activating;
    private bool _shutdown;

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
    public event Action<BrowserTab, BrowserContextMenu>
        ContextMenuRequested;

    /// <summary>需要询问用户是否保存登录凭据时触发（账号, 密码）。</summary>
    public event Action<BrowserTab, string, string, string> SaveCredentialRequested;

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
        Control uiInvoker, bool incognito, string temporaryDataFolder, DownloadStore downloads = null)
    {
        _context = context;
        Settings = context.Settings;
        AdBlock = context.AdBlock;
        History = context.History;
        Bookmarks = context.Bookmarks;
        Passwords = context.Passwords;
        Downloads = downloads ?? (incognito ? new DownloadStore(persist: false) : context.Downloads);
        _viewHost = viewHost;
        _parking = parking;
        _uiInvoker = uiInvoker;
        IsIncognito = incognito;
        TemporaryDataFolder = temporaryDataFolder;
    }

    /// <summary>
    /// 创建一个 CEF 请求上下文。
    ///
    /// <para>抽成静态方法是为了让多窗口共用：普通窗口都拿 <see cref="BrowserContext"/>
    /// 里的同一个环境，只有无痕窗口才单独建一个（它使用独立的内存 Cookie 与缓存）。</para>
    /// </summary>
    internal static async Task<BrowserProfile> CreateEnvironmentAsync(string userDataFolder, bool forceDarkPages,
        bool isPrivate = false)
    {
        var profile = new BrowserProfile(isPrivate ? "" : userDataFolder, isPrivate);
        await profile.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        return profile;
    }

    public async Task InitializeAsync()
    {
        if (_shutdown || _environment != null) return;
        _environment = IsIncognito
            ? await CreateEnvironmentAsync("", PageTheme.ForceDark, isPrivate: true)
            : await _context.GetEnvironmentAsync(PageTheme.ForceDark);
    }

    public void UpdatePageTheme(int backgroundArgb, bool forceDark)
    {
        PageTheme = new PageTheme { BackgroundArgb = backgroundArgb, ForceDark = forceDark };
        foreach (BrowserTab tab in _tabs) tab.ApplyThemeToView();
    }
    public bool IsReady => _environment != null;

    internal BrowserProfile Environment => _environment;

    // ---------------------------------------------------------------- 标签操作

    public BrowserTab NewTab(string url, bool activate = true)
    {
        ObjectDisposedException.ThrowIf(_shutdown, this);
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
        if (_shutdown || tab == null || !_tabs.Contains(tab))
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
                    other.View.Visible = false;
                }
            }

            // 在创建之前腾出预算，快速切换期间也不额外驻留视图。
            EnforceMemoryPolicy();

            // 创建/唤起 BrowserView。这里是异步的（要等内核就绪），
            // 因此不 await：界面先切过去，页面在几十毫秒后开始加载。
            bool needCreate = tab.View == null;
            if (needCreate || tab.Life == TabLife.Cold)
            {
                _ = tab.ObtainViewAsync(_viewHost, forceRecreate: true);
            }
            else
            {
                BrowserView view = tab.View;
                if (view.Parent != _viewHost)
                {
                    _viewHost.Controls.Add(view);
                }
                view.BringToFront();
                tab.Resume();
                view.Visible = true;
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
    /// 所以这里只做确定的事 —— 超出上限的标签把 BrowserView 整个销毁，渲染进程退出、
    /// 内存真正归还；标签本身和网址都留着，切回去按 URL 重新加载。
    ///
    /// <p>算法是「填桶」：当前标签先占一个名额，剩下的按最近使用时间从新到旧填，填满即停。
    /// </summary>
    public void EnforceMemoryPolicy()
    {
        if (_shutdown || _tabs.Count == 0)
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
            // 尚未加载的后台标签不占驻留名额。
            if (tab != active && tab.View == null) continue;
            // 关键：当前标签即使「视图还没建好」也要占住名额。
            // 它马上会在 ObtainViewAsync 里创建内核，如果这里跳过不计，
            // 等它创建完就会悄悄多出一个渲染中的标签，突破上限。
            bool wantsLive = tab == active || liveUsed < liveBudget;

            if (wantsLive)
            {
                if (tab == active && tab.View != null)
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

    /// <summary>CEF 请求上下文的释放由窗口负责；这里只销毁各标签的控件。</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        foreach (BrowserTab tab in _tabs)
        {
            tab.Dispose();
        }
        _tabs.Clear();
        ActiveIndex = -1;
    }

    internal Task ReleasePrivateEnvironmentAsync()
    {
        if (IsIncognito) { _environment?.Dispose(); _environment = null; }
        return Task.CompletedTask;
    }

    internal BrowserView CreateViewFor(BrowserTab tab)
    {
        if (_environment == null) throw new InvalidOperationException("CEF 环境尚未就绪");
        var view = new BrowserView(_environment, this, tab);
        _views.Add(view);
        view.Disposed += (_, _) => _views.Remove(view);
        return view;
    }

    internal async Task<BrowserView> EnsureCoreAsync(BrowserView view, BrowserTab tab)
    {
        tab.BindCoreEvents(view);
        await view.InitializeAsync();
        return !_shutdown && tab.OwnsView(view) ? view.Engine : null;
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
        if (!IsIncognito && !UrlUtils.IsInternal(tab.Url))
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
        if (tab == null || IsIncognito || tab.View?.Engine == null)
        {
            return;
        }
        if (!Settings.PasswordAutofill)
        {
            return;
        }

        try
        {
            string source = tab.View.Engine.Source;
            if (source == tab.CredentialSource && tab.CredentialPickToken != null) return;
            var matches = Passwords.FindForUrl(source);
            var accounts = matches
                .Select(e => (Id: e.Username + "\u0001" + e.Domain, Username: e.Username))
                .ToList();

            string token = Guid.NewGuid().ToString("N");
            string script = LoginAutofill.BuildScript(accounts, token);
            if (script.Length == 0)
            {
                return;
            }
            tab.CredentialSource = source;
            tab.CredentialPickToken = token;
            _ = tab.View.Engine.ExecuteScriptAsync(
                "if(window.location.href===" + JsQuote(source) + "){" + script + "}");
        }
        catch (Exception ex)
        {
            Log.Warn("注入登录辅助脚本失败: " + ex.Message);
        }
    }

    /// <summary>用户在页面的账号下拉里选了某一项。</summary>
    internal void NotifyCredentialPicked(BrowserTab tab, string accountId)
    {
        if (IsIncognito || !Settings.PasswordAutofill || tab?.View?.Engine == null)
        {
            return;
        }

        try
        {
            string source = tab.CredentialSource;
            if (string.IsNullOrEmpty(source) || source != tab.View.Engine.Source) return;
            var matches = Passwords.FindForUrl(tab.Url);
            PasswordEntry target = matches.FirstOrDefault(e =>
                string.Equals(e.Username + "\u0001" + e.Domain, accountId, StringComparison.Ordinal));

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
                "if(window.location.href===" + JsQuote(source) + "){" +
                "window.__featherFillPassword&&window.__featherFillPassword(" +
                JsQuote(target.Username) + "," + JsQuote(password) + ");}";
            _ = tab.View.Engine.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn("填充密码失败: " + ex.Message);
        }
    }

    /// <summary>用户在登录表单里提交了，问一下要不要保存。</summary>
    internal void NotifyCredentialSubmitted(BrowserTab tab, string source, string username, string password)
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
        var existing = Passwords.FindForUrl(source);
        if (existing.Any(e => string.Equals(e.Username, username ?? "",
                StringComparison.Ordinal)))
        {
            return;
        }

        SaveCredentialRequested?.Invoke(tab, source, username ?? "", password);
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
        BrowserContextMenu e) =>
        ContextMenuRequested?.Invoke(tab, e);

    /// <summary>内置管理页发来的消息（JSON）。宿主负责解析与响应。</summary>
    public event Action<BrowserTab, string, string> InternalPageMessage;

    internal void NotifyInternalPageMessage(BrowserTab tab, string json, string source) =>
        InternalPageMessage?.Invoke(tab, json, source);

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
            BrowserView view = tab?.View;
            if (view?.Engine == null || !InternalPages.Handles(view.Engine.Source))
            {
                Log.Warn("推数据给内置页失败：视图还没就绪");
                return;
            }
            string source = view.Engine.Source;
            string script = "if(window.location.href===" + JsQuote(source) + ")" +
                $"{{window.featherUpdate({json});}}";
            Log.Info($"推数据给内置页: {json.Length} 字节 JSON");
            await view.Engine.ExecuteScriptAsync(script);
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
    internal void NotifyDownloadsChanged() => DownloadsChanged?.Invoke();

    /// <summary>下载状态有变化（开始 / 完成 / 中断），宿主可据此刷新下载页。</summary>
    public event Action DownloadsChanged;

    /// <summary>
    /// 某个标签的内核进程异常退出。
    ///
    /// <p>不能静默忽略：这时标签持有的 BrowserView 已经失效，用户看到的会是白屏或卡死。
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
        if (_shutdown || !_tabs.Contains(tab)) return;
        Log.Warn($"标签 {tab.Id} 的内核进程异常退出：{kind} / {reason}");

        bool wholeBrowser = kind.Contains("BrowserProcess", StringComparison.OrdinalIgnoreCase);
        BrowserTab active = Active;

        if (kind is not ("BrowserProcessExited" or "RenderProcessExited" or "FrameRenderProcessExited"))
        {
            ProcessFailed?.Invoke(tab, kind, reason);
            return;
        }

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
        if (active != null && (wholeBrowser || active == tab))
        {
            // 避免在内核回调内部销毁后立即重建 COM 控制器。
            if (_uiInvoker.IsHandleCreated && !_uiInvoker.IsDisposed)
                _uiInvoker.BeginInvoke(() => Activate(active));
        }
    }
}
