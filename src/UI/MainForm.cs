using System.Diagnostics;
using System.Text.Json;
using FeatherBrowser.Core;
using FeatherBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FeatherBrowser.UI;

/// <summary>
/// 主窗口：Via 风格的紧凑单窗口浏览器。
///
/// <p>布局自上而下：工具栏（后退/前进/刷新/首页 + 地址栏 + 收藏/标签/菜单）
/// → 加载进度条 → 网页区域 → 状态栏（内存与拦截统计）。
///
/// <p>省内存的三条主线都在这里被驱动：
/// <list type="number">
///   <item><see cref="TabManager"/> 负责把超限的标签休眠（销毁渲染进程）；</item>
///   <item>窗口失焦时挂起后台标签，避免看不见的页面继续占用渲染进程；</item>
///   <item>定时刷新 <see cref="MemoryMonitor"/>，把真实占用显示在状态栏里。</item>
/// </list>
///
/// <p>所有尺寸都经 <see cref="Theme.Sx"/> / <see cref="Theme.Sy"/> 换算，
/// 配色统一由 <see cref="Theme.ApplyTo"/> 递归套用，因此换主题 / 换 DPI / 改倍率
/// 都只需要调一个入口。
/// </summary>
internal sealed class MainForm : Form
{
    private const string AppTitle = "轻羽浏览器";

    private readonly AppSettings _settings;
    private readonly BrowserContext _context;
    private readonly AdBlocker _adBlock;
    private readonly HistoryStore _history;
    private readonly BookmarkStore _bookmarks;
    private readonly PasswordStore _passwords;
    private readonly DownloadStore _downloads;
    private readonly bool _incognito;
    private readonly string _temporaryDataFolder;
    private readonly string _initialUrl;
    private readonly string _uiTest;

    private TabManager _tabs;

    private Panel _toolbar;
    private Panel _addressBox;
    private TextBox _address;
    private Panel _progressHost;
    private Panel _viewHost;
    private Panel _parking;
    private StatusBar _status;
    private Panel _findBar;
    private TextBox _findInput;
    private Label _findCount;

    private ToolbarButton _btnBack;
    private ToolbarButton _btnForward;
    private ToolbarButton _btnReload;
    private ToolbarButton _btnHome;
    private ToolbarButton _btnStar;
    private ToolbarButton _btnTabs;
    private ToolbarButton _btnMenu;

    private TabsSidebar _sidebar;
    private TabStrip _tabStrip;
    private bool _sidebarShown;
    private System.Windows.Forms.Timer _memoryTimer;
    private System.Windows.Forms.Timer _progressTimer;
    private int _progressValue;
    private bool _progressActive;
    private bool _addressDirty;
    private bool _findFirstMatch = true;
    private FormWindowState _lastWindowState;
    private string _statusText = "就绪";

    public MainForm(string startUrl, bool incognito, string themeOverride = null,
        string uiTest = null)
    {
        _incognito = incognito;
        _uiTest = uiTest;

        // 同一进程内所有窗口共用设置与数据存储：各自持有副本会互相覆盖
        _context = BrowserContext.Shared;
        _settings = _context.Settings;

        // 主题与倍率要在建任何控件之前定下来，否则字体与配色会不一致。
        Theme.Scale = Math.Clamp(_settings.UiScale, 1.0f, 1.6f);
        Theme.CaptureDpi(this);
        if (themeOverride != null)
        {
            Theme.SetMode(ResolveThemeMode(themeOverride));
        }
        Theme.WatchSystemTheme();
        Theme.SystemThemeChanged += OnSystemThemeChanged;

        // 数据存储全部来自共用上下文
        _adBlock = _context.AdBlock;
        _history = _context.History;
        _bookmarks = _context.Bookmarks;
        _passwords = _context.Passwords;
        _downloads = _context.Downloads;

        if (incognito)
        {
            // 无痕窗口使用独立的临时数据目录，关闭后整体删除，不留痕迹。
            _temporaryDataFolder = Path.Combine(Path.GetTempPath(),
                "FeatherIncognito_" + Guid.NewGuid().ToString("N")[..8]);
        }

        _initialUrl = string.IsNullOrWhiteSpace(startUrl) ? null : startUrl;

        Text = AppTitle + (_incognito ? " · 无痕" : "");
        Icon = AppIcon.Create();
        Font = Theme.UiFont;
        MinimumSize = new Size(Theme.Sx(620), Theme.Sy(420));
        Width = Math.Max(Theme.Sx(620), Theme.Sx(_settings.WindowWidth / 1.15f));
        Height = Math.Max(Theme.Sy(420), Theme.Sy(_settings.WindowHeight / 1.15f));
        if (_settings.WindowMaximized && !incognito)
        {
            WindowState = FormWindowState.Maximized;
        }
        KeyPreview = true;
        DoubleBuffered = true;

        BuildUi();
        Theme.ApplyTo(this);

        Shown += async (_, _) =>
        {
            WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
            await StartAsync();
        };
        FormClosing += OnFormClosingHandler;
        FormClosed += OnFormClosedHandler;
        Resize += (_, _) => OnResized();
        Deactivate += (_, _) => OnDeactivated();
        Activated += (_, _) => OnActivatedHandler();
        KeyDown += OnFormKeyDown;
        _lastWindowState = WindowState;

        // 窗口被拖到另一块 DPI 不同的显示器时，重新换算。
        DpiChangedAfterParent += (_, _) =>
        {
            Theme.CaptureDpi(this);
            ApplyScaledMetrics();
        };
    }

    // ================================================================ 界面构建

    /// <summary>命令行临时覆盖主题（不改配置）；传空则用配置里的设置。</summary>
    private ThemeMode ResolveThemeMode(string themeOverride)
    {
        return themeOverride switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            "system" => ThemeMode.System,
            _ => (ThemeMode)Math.Clamp(_settings.ThemeMode, 0, 2),
        };
    }

    private void BuildUi()
    {
        _viewHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PageBackground,
        };

        _parking = new Panel
        {
            Visible = false,
            Size = new Size(1, 1),
            Location = new Point(Theme.Sx(-20), Theme.Sy(-20)),
        };

        _status = new StatusBar { Font = Theme.UiFontSmall };
        _status.FitToFont();

        _findBar = BuildFindBar();

        _progressHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Sy(4),
            BackColor = Theme.Toolbar,
        };
        _progressHost.Paint += OnProgressPaint;

        _toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Sy(52),
            BackColor = Theme.Toolbar,
        };
        _toolbar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
        };

        BuildToolbarChildren();

        // 顶部横向标签栏（像桌面浏览器那样）。
        // 放在标题栏之下、工具栏之上，是整个窗口最显眼的一行。
        _tabStrip = new TabStrip
        {
            Dock = DockStyle.Top,
            Height = Theme.Sy(38),
            ActivateTab = index => _tabs?.Activate(index),
            CloseTab = index => CloseTabAt(index),
            NewTab = () => _tabs?.NewTab(UrlUtils.InternalHome),
            MoveTab = (from, to) => _tabs?.MoveTab(from, to),
            ShowTabMenu = ShowTabContextMenu,
        };

        // 标签侧边栏：画在主窗口内的常规控件，Dock=Right，默认收成 0 宽隐藏
        _sidebar = new TabsSidebar
        {
            ActivateTab = index => _tabs?.Activate(index),
            CloseTab = index => CloseTabAt(index),
            NewTab = () => _tabs?.NewTab(UrlUtils.InternalHome),
            CloseAll = CloseAllTabs,
            ShowMemoryDialog = ShowMemoryDialog,
        };

        // 注意控件添加顺序：Dock=Top/Right 的控件按添加顺序从外到内堆叠。
        // 所以最外层（标签栏）要放在最前面，最内层（网页容器）放最后。
        Controls.Add(_tabStrip);
        Controls.Add(_viewHost);
        Controls.Add(_findBar);
        Controls.Add(_progressHost);
        Controls.Add(_toolbar);
        Controls.Add(_status);
        Controls.Add(_parking);
        Controls.Add(_sidebar);

        _toolbar.Resize += (_, _) => LayoutToolbar();
        LayoutToolbar();
    }

    /// <summary>
    /// 展开 / 收起标签侧边栏。
    ///
    /// <p>做法是「侧边栏与网页容器二选一显示」，而不是让两者并存。
    /// 一开始想用「宽度归零 + Dock 自动让位」，但那样需要一次布局才能算出尺寸，
    /// 展开瞬间会拿到 Height=0 而画不出来。直接切 Visible 最可靠、也没有过渡态问题。
    /// </summary>
    private void ToggleSidebar()
    {
        if (_tabs == null)
        {
            return;
        }
        _sidebarShown = !_sidebarShown;

        if (_sidebarShown)
        {
            UpdateSidebar();
            _sidebar.Width = _sidebar.ExpandedWidth;
            _sidebar.Visible = true;
            _sidebar.BringToFront();
            _viewHost.Visible = false;
        }
        else
        {
            _sidebar.Visible = false;
            _viewHost.Visible = true;
            _viewHost.BringToFront();
        }

        _btnTabs.Active = _sidebarShown;
        _btnTabs.Invalidate();
    }

    /// <summary>点「关闭全部」：留一个当前标签，其余关掉。</summary>
    private void CloseAllTabs()
    {
        if (_tabs == null)
        {
            return;
        }
        int others = _tabs.Count - 1;
        if (others <= 0)
        {
            SetStatus("只有一个标签，无需关闭");
            return;
        }

        _tabs.CloseAllExcept(_tabs.Active);
        SetStatus($"已关闭 {others} 个标签");
        UpdateChrome();
    }

    /// <summary>把当前标签状态同步给侧边栏与顶部标签栏。</summary>
    private void UpdateSidebar()
    {
        if (_tabs == null)
        {
            return;
        }

        _tabStrip?.Update(_tabs.Tabs, _tabs.ActiveIndex);

        if (_sidebar == null)
        {
            return;
        }
        _sidebar.Update(_tabs.Tabs, _tabs.ActiveIndex, _tabs.LiveCount, _tabs.ColdCount,
            _settings.MaxLiveTabs, MemoryMonitor.Summary());
    }

    private void BuildToolbarChildren()
    {
        _btnBack = MakeToolButton("\u2190", "后退 (Alt+←)", GoBack);
        _btnForward = MakeToolButton("\u2192", "前进 (Alt+→)", GoForward);
        _btnReload = MakeToolButton("\u21BB", "刷新 (F5)", Reload);
        _btnHome = MakeToolButton("\u2302", "打开首页", GoHome);

        _addressBox = new Panel { BackColor = Theme.Surface };
        _addressBox.Paint += (_, e) => PaintAddressBox(e.Graphics);

        _address = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            PlaceholderText = "搜索或输入网址",
        };
        _address.KeyDown += OnAddressKeyDown;
        _address.Enter += (_, _) => _address.SelectAll();
        _address.TextChanged += (_, _) => _addressDirty = true;
        _addressBox.Controls.Add(_address);

        _btnStar = MakeToolButton("\u2606", "收藏此页 (Ctrl+D)", ToggleBookmark);
        _btnTabs = MakeToolButton("\u25A6", "标签列表 (Ctrl+Shift+T)", ShowTabList);
        _btnMenu = MakeToolButton("\u2630", "菜单 (Alt+F)", ShowMenu);

        _toolbar.Controls.Add(_btnBack);
        _toolbar.Controls.Add(_btnForward);
        _toolbar.Controls.Add(_btnReload);
        _toolbar.Controls.Add(_btnHome);
        _toolbar.Controls.Add(_addressBox);
        _toolbar.Controls.Add(_btnStar);
        _toolbar.Controls.Add(_btnTabs);
        _toolbar.Controls.Add(_btnMenu);
    }

    /// <summary>地址栏底色与描边。用圆角胶囊，与图标按钮的圆角风格统一。</summary>
    private void PaintAddressBox(Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle rect = new(0, 0, _addressBox.Width, _addressBox.Height);

        using (var back = new SolidBrush(Theme.Surface))
        {
            g.FillRectangle(back, rect);
        }

        // 胶囊形：半径取高度一半
        Theme.FillRounded(g, new Rectangle(0, 0, rect.Width - 1, rect.Height - 1),
            rect.Height / 2, Theme.Surface);
        Theme.DrawRounded(g, new Rectangle(0, 0, rect.Width, rect.Height),
            rect.Height / 2, Theme.Border);
    }

    private ToolbarButton MakeToolButton(string glyph, string tip, Action action)
    {
        var button = new ToolbarButton { Glyph = glyph }.AsToolbarIcon();
        button.BackColor = Theme.Toolbar;
        button.ClickAction = (_, _) => action();
        var tipHolder = new ToolTip
        {
            InitialDelay = 700,
            ReshowDelay = 200,
            AutoPopDelay = 8000,
        };
        tipHolder.SetToolTip(button, tip);
        // 把 ToolTip 挂在按钮上，随按钮一起释放
        button.Disposed += (_, _) => tipHolder.Dispose();
        return button;
    }

    private Panel BuildFindBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Sy(48),
            BackColor = Theme.Toolbar,
            Visible = false,
        };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        _findInput = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            PlaceholderText = "在此页查找",
        };
        _findInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                FindNext(!e.Shift);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideFindBar();
                e.Handled = true;
            }
        };
        _findInput.TextChanged += (_, _) =>
        {
            _findFirstMatch = true;
            FindNext(true);
        };
        bar.Controls.Add(_findInput);

        _findCount = new Label
        {
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "",
            BackColor = Color.Transparent,
        };
        bar.Controls.Add(_findCount);

        var btnPrev = new ToolbarButton { Glyph = "\u2191", ShowBackground = false }.AsSmallIcon();
        btnPrev.BackColor = Theme.Toolbar;
        btnPrev.ClickAction = (_, _) => FindNext(false);
        bar.Controls.Add(btnPrev);

        var btnNext = new ToolbarButton { Glyph = "\u2193", ShowBackground = false }.AsSmallIcon();
        btnNext.BackColor = Theme.Toolbar;
        btnNext.ClickAction = (_, _) => FindNext(true);
        bar.Controls.Add(btnNext);

        var btnClose = new ToolbarButton { Glyph = "\u00D7", ShowBackground = false }.AsSmallIcon();
        btnClose.BackColor = Theme.Toolbar;
        btnClose.ClickAction = (_, _) => HideFindBar();
        bar.Controls.Add(btnClose);

        bar.Resize += (_, _) =>
        {
            int right = bar.Width - Theme.Sx(12);
            int buttonWidth = btnClose.Width;
            btnClose.Location = new Point(right - buttonWidth, (bar.Height - btnClose.Height) / 2);
            btnNext.Location = new Point(btnClose.Left - buttonWidth, (bar.Height - btnNext.Height) / 2);
            btnPrev.Location = new Point(btnNext.Left - buttonWidth, (bar.Height - btnPrev.Height) / 2);

            _findCount.Location = new Point(btnPrev.Left - Theme.Sx(80), (bar.Height - Theme.Sy(20)) / 2);
            _findCount.Size = new Size(Theme.Sx(72), Theme.Sy(20));

            _findInput.Location = new Point(Theme.Sx(16), (bar.Height - _findInput.Height) / 2);
            _findInput.Width = Math.Max(Theme.Sx(140), btnPrev.Left - Theme.Sx(100));
        };
        return bar;
    }

    private void LayoutToolbar()
    {
        if (_toolbar == null || _btnBack == null)
        {
            return;
        }

        int gap = Theme.Sx(6);
        int x = gap;
        int navY = (_toolbar.Height - _btnBack.Height) / 2;

        foreach (ToolbarButton button in new[] { _btnBack, _btnForward, _btnReload, _btnHome })
        {
            button.SetBounds(x, navY, button.Width, button.Height);
            x += button.Width;
        }

        x += gap;
        int rightEdge = _toolbar.Width - gap;
        int actionWidth = _btnStar.Width * 3;
        int addressWidth = Math.Max(Theme.Sx(180), rightEdge - actionWidth - x - gap);
        int addressHeight = _btnBack.Height;

        _addressBox.SetBounds(x, (_toolbar.Height - addressHeight) / 2, addressWidth, addressHeight);
        _address.SetBounds(Theme.Sx(16), (_addressBox.Height - _address.Height) / 2,
            Math.Max(Theme.Sx(60), addressWidth - Theme.Sx(32)), _address.Height);

        int ax = _addressBox.Right + gap;
        foreach (ToolbarButton button in new[] { _btnStar, _btnTabs, _btnMenu })
        {
            button.SetBounds(ax, navY, button.Width, button.Height);
            ax += button.Width;
        }
    }

    /// <summary>DPI 或界面倍率变化后，重新套用所有尺寸相关的东西。</summary>
    private void ApplyScaledMetrics()
    {
        Font = Theme.UiFont;
        MinimumSize = new Size(Theme.Sx(620), Theme.Sy(420));

        _toolbar.Height = Theme.Sy(52);
        _progressHost.Height = Theme.Sy(4);
        _findBar.Height = Theme.Sy(48);
        _status.Font = Theme.UiFontSmall;
        _status.FitToFont();

        _address.Font = Theme.UiFont;
        _findInput.Font = Theme.UiFont;
        _findCount.Font = Theme.UiFontSmall;

        foreach (ToolbarButton button in new[]
                 {
                     _btnBack, _btnForward, _btnReload, _btnHome,
                     _btnStar, _btnTabs, _btnMenu,
                 })
        {
            button.AsToolbarIcon();
        }
        foreach (Control child in _findBar.Controls)
        {
            if (child is ToolbarButton icon)
            {
                icon.AsSmallIcon();
            }
        }

        // 侧边栏与顶部标签栏的尺寸跟着界面倍率走
        if (_sidebar != null)
        {
            _sidebar.Width = _sidebarShown ? _sidebar.ExpandedWidth : 0;
            _sidebar.Invalidate();
        }
        _tabStrip?.Update(_tabs?.Tabs ?? Array.Empty<BrowserTab>(),
            _tabs?.ActiveIndex ?? -1);

        Theme.ApplyTo(this);
        LayoutToolbar();
        UpdateChrome();
    }

    /// <summary>主题切换：递归换色，并同步网页区域与标题栏。</summary>
    private void OnSystemThemeChanged()
    {
        if (IsDisposed)
        {
            return;
        }
        Theme.ApplyToAllForms();
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
        ApplyPageTheme();
    }

    /// <summary>把当前主题同步到 WebView2 环境（底色 + 站点配色偏好）。</summary>
    private void ApplyPageTheme()
    {
        _viewHost.BackColor = Theme.PageBackground;
        int argb = Theme.PageBackground.ToArgb();
        _tabs?.UpdatePageTheme(argb, Theme.Dark);
    }

    // ================================================================ 启动

    private async Task StartAsync()
    {
        _tabs = new TabManager(_context, _viewHost, _parking, this,
            _incognito, _temporaryDataFolder);
        _tabs.TabsChanged += () => UpdateChrome();
        _tabs.NewWindowRequested += url =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => _tabs.NewTab(url));
            }
        };
        // 内置管理页（书签 / 下载 / 历史）发来的操作请求
        _tabs.InternalPageMessage += (tab, json) =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => OnInternalPageMessage(tab, json));
            }
        };
        // 网页右键：在内核菜单上补充桌面浏览器的常规项
        _tabs.ContextMenuRequested += OnContextMenuRequested;
        // 登录表单提交后询问是否保存
        _tabs.SaveCredentialRequested += (tab, username, password) =>
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => PromptSaveCredential(tab, username, password));
            }
        };
        // 内核进程被外部结束（例如在任务管理器里点了「结束任务」）：        // TabManager 已经把受影响的标签降为冷态并按需重建，这里只把情况告诉用户，
        // 避免出现「浏览器看着还在、网页却永远白屏」的死状态。
        _tabs.ProcessFailed += (tab, kind, reason) =>
        {
            if (!IsHandleCreated)
            {
                return;
            }
            bool wholeBrowser = kind.Contains("BrowserProcess", StringComparison.OrdinalIgnoreCase);
            string message = wholeBrowser
                ? "浏览器内核进程被外部结束，已自动重建并重新加载页面"
                : "网页进程被外部结束，已自动重新加载该标签";
            BeginInvoke(() => SetStatus(message));
        };


        _adBlock.Enabled = _settings.AdBlockEnabled;

        try
        {
            _status.SetLeft("正在启动浏览器内核…");
            await _tabs.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("初始化 WebView2 失败", ex);
            MessageBox.Show(this,
                "无法启动浏览器内核（WebView2 运行时）。\r\n\r\n" +
                "请确认系统已安装 Microsoft Edge WebView2 Runtime。\r\n" +
                "错误信息：" + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        RestoreTabs();
        ApplyPageTheme();

        _memoryTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _memoryTimer.Tick += (_, _) => UpdateMemoryReadout();
        _memoryTimer.Start();

        _progressTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _progressTimer.Tick += (_, _) => StepProgress();

        UpdateMemoryReadout();
        UpdateChrome();

        RunUiTest();
    }

    /// <summary>
    /// 界面走查模式下，等页面渲染出来再弹层的延时。
    /// 可用环境变量 FEATHER_UITEST_DELAY 覆盖（毫秒），方便截图脚本控制时机。
    /// </summary>
    private static int UiTestDelayMs()
    {
        string value = Environment.GetEnvironmentVariable("FEATHER_UITEST_DELAY");
        if (int.TryParse(value, out int ms) && ms >= 200 && ms <= 30000)
        {
            return ms;
        }
        return 5000;
    }

    /// <summary>
    /// 界面走查模式（--uitest=...）。启动后按脚本打开若干界面，
    /// 交给 tools\screenshot.ps1 自动截图核对排版。普通使用不会走到这里。
    /// </summary>
    private void RunUiTest()
    {
        if (string.IsNullOrEmpty(_uiTest))
        {
            return;
        }

        // 多标签场景：先补几个后台标签，才能看出标签列表与徽标
        if (_uiTest is "tabs" or "menu" or "switch")
        {
            _tabs.NewTab("https://www.bing.com/", activate: false);
            _tabs.NewTab("https://www.baidu.com/", activate: false);
            _tabs.NewTab("https://github.com/", activate: false);
            _tabs.Activate(0);
        }

        var timer = new System.Windows.Forms.Timer { Interval = UiTestDelayMs() };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            try
            {
                switch (_uiTest)
                {
                    case "tabs":
                        ShowTabList();
                        // 自检：把侧边栏状态与若干坐标的命中判定写进日志。
                        // 不移动鼠标就能验证命中区算得对不对。
                        BeginInvoke(() =>
                        {
                            Log.Info("侧边栏自检: " + _sidebar.DescribeState());
                            int w = _sidebar.Width;
                            int h = _sidebar.Height;
                            foreach (Point probe in new[]
                                     {
                                         new Point(w - Theme.Sx(30), Theme.Sy(28)),
                                         new Point(w - Theme.Sx(70), Theme.Sy(28)),
                                         new Point(w / 2, Theme.Sy(86)),
                                         new Point(w / 2, Theme.Sy(140)),
                                         new Point(w - Theme.Sx(20), Theme.Sy(140)),
                                         new Point(w / 2, h - Theme.Sy(20)),
                                     })
                            {
                                Log.Info("  命中判定 " + _sidebar.DescribeHit(probe));
                            }
                        });
                        break;
                    case "menu":
                        ShowMenu();
                        break;
                    case "settings":
                        ShowSettings();
                        break;
                    case "memory":
                        ShowMemoryDialog();
                        break;
                    case "find":
                        ShowFindBar();
                        _findInput.Text = "浏览器";
                        break;
                    case "switch":
                        // 切到第二个标签，验证休眠/恢复与标签列表的刷新
                        _tabs.Activate(1);
                        ShowTabList();
                        break;
                    case "multi":
                        // 多窗口：再开一个普通窗口和一个无痕窗口，验证共用环境
                        OpenNewWindow();
                        OpenIncognitoWindow();
                        break;
                    case "page":
                        // 内置管理页自检：打开当前地址对应页面并 dump 文本，便于自动核对
                        BeginInvoke(async () =>
                        {
                            try
                            {
                                string url = _tabs.Active?.Url ?? "";
                                Log.Info($"内置页自检: url={url} Handles={InternalPages.Handles(url)}");
                                // 页面异步加载并等宿主推数据，要给它时间
                                await Task.Delay(3000);
                                string text = await _tabs.Active.View.CoreWebView2
                                    .ExecuteScriptAsync("document.body.innerText");
                                Log.Info("内置页文本: " + text);
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("内置页自检失败: " + ex.Message);
                            }
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("--uitest 执行失败: " + ex.Message);
            }
        };
        timer.Start();
    }



    /// <summary>恢复上次的标签，或者打开初始地址 / 首页。</summary>
    private void RestoreTabs()
    {
        List<string> urls = new();

        if (!string.IsNullOrEmpty(_initialUrl))
        {
            urls.Add(_initialUrl);
        }
        else if (_incognito)
        {
            urls.Add(UrlUtils.InternalHome);
        }
        else if (_settings.SessionUrls is { Count: > 0 })
        {
            // 最多恢复 20 个标签，避免上次开太多导致启动即卡顿。
            urls.AddRange(_settings.SessionUrls.Take(20));
        }

        if (urls.Count == 0)
        {
            urls.Add(_settings.HomeUrl);
        }

        foreach (string url in urls)
        {
            _tabs.NewTab(url, activate: false);
        }

        int active = Math.Clamp(_settings.SessionActiveIndex, 0, urls.Count - 1);
        _tabs.Activate(active);
    }

    // ================================================================ 界面刷新

    private void UpdateChrome()
    {
        if (_tabs == null)
        {
            return;
        }

        BrowserTab tab = _tabs.Active;

        _btnBack.Enabled = tab?.CanGoBack == true;
        _btnForward.Enabled = tab?.CanGoForward == true;
        _btnTabs.Active = _sidebarShown;
        _btnTabs.Badge = _tabs.Count > 1 ? _tabs.Count : 0;

        if (tab == null)
        {
            return;
        }

        if (!_addressDirty || !_address.Focused)
        {
            string display = tab.IsHomePage ? "" : UrlUtils.PrettyForBar(tab.Url);
            if (_address.Text != display)
            {
                _address.Text = display;
            }
            _addressDirty = false;
        }

        SetProgress(tab.IsLoading ? Math.Max(tab.Progress, 4) : 0, tab.IsLoading);

        bool starred = _bookmarks.Contains(tab.Url);
        _btnStar.Glyph = starred ? "\u2605" : "\u2606";

        Text = (string.IsNullOrEmpty(tab.DisplayTitle) ? AppTitle : tab.DisplayTitle + " - " + AppTitle)
               + (_incognito ? " · 无痕" : "")
               + (_tabs.Count > 1 ? $"  [{_tabs.Count} 标签]" : "");

        _status.SetLeft(_statusText);
        UpdateSidebar();
    }

    private void UpdateMemoryReadout()
    {
        if (_tabs == null)
        {
            return;
        }

        MemoryMonitor.Refresh();
        string text =
            $"标签 {_tabs.Count}（渲染 {_tabs.LiveCount} / 休眠 {_tabs.ColdCount}）" +
            $" · 内核 {MemoryMonitor.WebViewProcessCount} 进程 {MemoryMonitor.Mb(MemoryMonitor.WebViewWorkingSet)}" +
            $" · 本程序 {MemoryMonitor.Mb(MemoryMonitor.WorkingSet)}" +
            $" · 拦截 {_adBlock.BlockedCount}";
        _status.SetRight(text);

        // 侧边栏底部的内存读数也一起刷新
        if (_sidebarShown)
        {
            UpdateSidebar();
        }
    }

    private void SetStatus(string text)
    {
        _statusText = text ?? "";
        _status.SetLeft(_statusText);
    }

    // ================================================================ 进度条

    private void SetProgress(int value, bool active)
    {
        _progressActive = active;
        _progressValue = Math.Clamp(value, 0, 100);

        // 注意：恢复标签时也会走到这里，而那时计时器可能还没创建，必须判空。
        if (_progressTimer == null)
        {
            _progressHost?.Invalidate();
            return;
        }

        if (active && !_progressTimer.Enabled)
        {
            _progressTimer.Start();
        }
        else if (!active && _progressTimer.Enabled)
        {
            _progressTimer.Stop();
        }
        _progressHost.Invalidate();
    }

    private void StepProgress()
    {
        if (!_progressActive)
        {
            _progressTimer?.Stop();
            return;
        }
        if (_progressValue < 92)
        {
            _progressValue += 2;
            _progressHost.Invalidate();
        }
    }

    private void OnProgressPaint(object sender, PaintEventArgs e)
    {
        e.Graphics.Clear(_progressHost.BackColor);
        if (!_progressActive || _progressValue <= 0)
        {
            return;
        }
        int width = (int)(_progressHost.Width * (_progressValue / 100f));

        // 圆角进度：右端做成圆头，与整体圆润风格一致
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Theme.Accent);
        using var path = Theme.RoundedRect(
            new Rectangle(0, 0, Math.Max(Theme.Sy(4), width), _progressHost.Height),
            _progressHost.Height / 2);
        e.Graphics.FillPath(brush, path);
    }

    // ================================================================ 导航动作

    private void NavigateFromAddress()
    {
        if (_tabs?.Active == null)
        {
            return;
        }

        string input = _address.Text.Trim();
        if (input.Length == 0)
        {
            return;
        }

        string url = UrlUtils.Normalize(input, _settings.Engine.Template);
        _addressDirty = false;
        _address.SelectAll();
        _tabs.Active.NavigateTo(url);
        _viewHost.Focus();
    }

    private void GoBack()
    {
        if (_tabs?.Active?.CanGoBack == true)
        {
            _tabs.Active.GoBack();
        }
    }

    private void GoForward()
    {
        if (_tabs?.Active?.CanGoForward == true)
        {
            _tabs.Active.GoForward();
        }
    }

    private void Reload()
    {
        _tabs?.Active?.Reload();
    }

    private void GoHome()
    {
        if (_tabs?.Active == null)
        {
            return;
        }
        _tabs.Active.NavigateTo(_settings.HomeUrl);
    }

    private void ToggleBookmark()
    {
        BrowserTab tab = _tabs?.Active;
        if (tab == null || tab.IsHomePage)
        {
            return;
        }
        bool added = _bookmarks.Toggle(tab.Url, tab.DisplayTitle);
        SetStatus(added ? "已加入书签" : "已移除书签");
        UpdateChrome();
    }

    /// <summary>复制文本到剪贴板并提示。剪贴板偶尔会被别的程序占住，所以失败要兜住。</summary>
    private void CopyToClipboard(string text, string statusWhenDone)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetStatus(statusWhenDone);
        }
        catch (Exception ex)
        {
            Log.Warn("写入剪贴板失败: " + ex.Message);
            SetStatus("复制失败，剪贴板被其它程序占用");
        }
    }

    /// <summary>恢复最近关闭的标签。</summary>
    private bool RestoreClosedTab()
    {
        if (_tabs?.RestoreClosedTab() == true)
        {
            SetStatus($"已恢复关闭的标签（还可恢复 {_tabs.ClosedTabCount} 个）");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 标签上的右键菜单（桌面浏览器的常规操作都在这里）。
    /// </summary>
    private void ShowTabContextMenu(int index, Point screenPoint)
    {
        if (_tabs == null || index < 0 || index >= _tabs.Count)
        {
            return;
        }
        BrowserTab tab = _tabs.Tabs[index];
        bool isActive = index == _tabs.ActiveIndex;

        var items = new List<MenuEntry>
        {
            new() { Text = "新建标签页", Action = () => _tabs.NewTab(UrlUtils.InternalHome) },
            new()
            {
                Text = "重新加载", StartsGroup = true,
                Action = () => tab.Reload(),
            },
            new()
            {
                Text = "复制标签页网址",
                Action = () => CopyToClipboard(tab.Url, "已复制标签页网址"),
            },
            new()
            {
                Text = "关闭标签页", Shortcut = "Ctrl+W",
                Action = () => CloseTabAt(index),
            },
            new()
            {
                Text = "关闭其它标签页",
                Action = () => CloseOthers(index),
            },
            new()
            {
                Text = "关闭右侧标签页",
                Action = () => CloseToRight(index),
            },
        };

        if (_tabs.CanRestoreClosedTab)
        {
            items.Add(new MenuEntry
            {
                Text = $"恢复关闭的标签（{_tabs.ClosedTabCount}）", Shortcut = "Ctrl+Shift+T",
                StartsGroup = true,
                Action = () => RestoreClosedTab(),
            });
        }

        if (!isActive)
        {
            items.Insert(0, new MenuEntry
            {
                Text = "切换到该标签页",
                Action = () => _tabs.Activate(index),
            });
        }

        using var menu = new PopupMenu(items, Theme.UiFont, Theme.UiFontSmall);
        menu.ShowAtScreen(screenPoint);
    }

    private void CloseOthers(int keepIndex)
    {
        if (_tabs == null || keepIndex < 0 || keepIndex >= _tabs.Count)
        {
            return;
        }
        _tabs.CloseAllExcept(_tabs.Tabs[keepIndex]);
        SetStatus("已关闭其它标签页");
        UpdateChrome();
    }

    private void CloseToRight(int index)
    {
        if (_tabs == null)
        {
            return;
        }
        for (int i = _tabs.Count - 1; i > index; i--)
        {
            _tabs.CloseTab(_tabs.Tabs[i]);
        }
        SetStatus("已关闭右侧标签页");
        UpdateChrome();
    }

    private void CloseTabAt(int index)
    {
        if (_tabs == null || index < 0 || index >= _tabs.Count)
        {
            return;
        }
        _tabs.CloseTab(_tabs.Tabs[index]);
    }

    /// <summary>打开一个内置管理页（已打开就切过去，不重复开）。</summary>
    private void OpenInternalPage(string url)
    {
        if (_tabs == null)
        {
            return;
        }
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (string.Equals(_tabs.Tabs[i].Url, url, StringComparison.OrdinalIgnoreCase))
            {
                _tabs.Activate(i);
                return;
            }
        }
        _tabs.NewTab(url);
    }

    // ================================================================ 内置管理页

    /// <summary>
    /// 处理内置管理页发来的请求，并把最新数据推回去。
    ///
    /// <para>页面只发「要做什么」，数据由宿主查好再推回，页面不做任何持久化。
    /// 这样页面即使被注入脚本也无法直接改数据。</para>
    /// </summary>
    private void OnInternalPageMessage(BrowserTab tab, string json)
    {
        string kind;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("feather", out JsonElement k))
            {
                return;
            }
            kind = k.GetString();
        }
        catch (Exception ex)
        {
            Log.Warn("内置页面消息解析失败: " + ex.Message);
            return;
        }

        int Index(JsonElement root, string name)
        {
            try
            {
                return root.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i)
                    ? i : -1;
            }
            catch
            {
                return -1;
            }
        }

        using var doc2 = JsonDocument.Parse(json);
        JsonElement root2 = doc2.RootElement;
        int index = Index(root2, "index");

        switch (kind)
        {
            case "bookmarks-load":
                PushBookmarks(tab);
                return;
            case "bookmarks-open":
                OpenFromList(_bookmarks.Items.Select(b => b.Url), index);
                return;
            case "bookmarks-copy":
            {
                var items = _bookmarks.Items;
                if (index >= 0 && index < items.Count)
                {
                    CopyToClipboard(items[index].Url, "已复制书签网址");
                }
                return;
            }
            case "bookmarks-delete":
                _bookmarks.RemoveAt(index);
                PushBookmarks(tab);
                SetStatus("已删除书签");
                return;
            case "bookmarks-clear":
                _bookmarks.Clear();
                PushBookmarks(tab);
                SetStatus("已清空全部书签");
                return;

            case "history-load":
                PushHistory(tab);
                return;
            case "history-open":
                OpenFromList(_history.Snapshot().Select(h => h.Url), index);
                return;
            case "history-delete":
                _history.RemoveAt(index);
                PushHistory(tab);
                SetStatus("已删除该条历史");
                return;
            case "history-clear":
                _history.Clear();
                PushHistory(tab);
                SetStatus("已清空全部历史");
                return;

            case "downloads-load":
                PushDownloads(tab);
                return;
            case "downloads-open":
            case "downloads-reveal":
                RevealDownload(index, open: true);
                return;
            case "downloads-folder":
                RevealDownloadFolder();
                return;
            case "downloads-remove":
                if (_downloads.RemoveAt(index))
                {
                    PushDownloads(tab);
                }
                return;
            case "downloads-clear":
                _downloads.ClearFinished();
                PushDownloads(tab);
                return;
        }
    }

    /// <summary>按下标从一组地址里打开某一个（下标与页面里的顺序一一对应）。</summary>
    private void OpenFromList(IEnumerable<string> urls, int index)
    {
        if (index < 0)
        {
            return;
        }
        var list = urls as IList<string> ?? urls.ToList();
        if (index < list.Count && !string.IsNullOrEmpty(list[index]))
        {
            _tabs?.NewTab(list[index]);
        }
    }

    private void PushBookmarks(BrowserTab tab)
    {
        IReadOnlyList<BookmarkEntry> items = _bookmarks.Items;
        var payload = items.Select((b, i) => new
        {
            index = i,
            title = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title,
            url = b.Url,
        }).ToList();
        _tabs?.PushToInternalPage(tab, JsonSerializer.Serialize(new { items = payload }, JsonOpts));
    }

    private void PushHistory(BrowserTab tab)
    {
        List<HistoryEntry> items = _history.Snapshot();
        var payload = items.Select((h, i) => new
        {
            index = i,
            title = h.DisplayTitle,
            url = h.Url,
            when = h.VisitedAt,
        }).ToList();
        _tabs?.PushToInternalPage(tab, JsonSerializer.Serialize(new { items = payload }, JsonOpts));
    }

    private void PushDownloads(BrowserTab tab)
    {
        var payload = _downloads.Snapshot().Select((d, i) => new
        {
            index = i,
            fileName = d.FileName,
            url = d.Url,
            path = d.Path,
            state = d.State,
            percent = d.Percent,
            received = d.ReceivedBytes,
            total = d.TotalBytes,
        }).ToList();
        _tabs?.PushToInternalPage(tab, JsonSerializer.Serialize(new { items = payload }, JsonOpts));
    }

    private void RevealDownload(int index, bool open)
    {
        DownloadItem item = _downloads.Get(index);
        if (item == null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            SetStatus("文件不存在或已被移动");
            return;
        }
        try
        {
            if (open)
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            else
            {
                Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("打开下载文件失败: " + ex.Message);
            SetStatus("打不开该文件");
        }
    }

    private void RevealDownloadFolder()
    {
        try
        {
            string folder = _downloads.Folder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开下载目录失败: " + ex.Message);
            SetStatus("打不开下载目录");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 中文不转义成 \uXXXX，页面里直接可读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ================================================================ 网页右键菜单

    /// <summary>
    /// 在内核的右键菜单上补充桌面浏览器的常规项。
    ///
    /// <para>刻意保留内核自带的项（复制、粘贴、检查等）—— 全部自绘的收益不大，
    /// 却会丢掉拼写检查、输入法、无障碍这些成熟行为。这里只做加法。</para>
    /// </summary>
    private void OnContextMenuRequested(BrowserTab tab,
        CoreWebView2ContextMenuRequestedEventArgs e)
    {
        try
        {
            IList<CoreWebView2ContextMenuItem> menu = e.MenuItems;
            string link = e.ContextMenuTarget?.LinkUri;
            string selection = e.ContextMenuTarget?.SelectionText;

            var added = new List<CoreWebView2ContextMenuItem>();

            if (!string.IsNullOrEmpty(link))
            {
                added.Add(MakeMenuItem("在新标签页中打开链接", () =>
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(() => _tabs?.NewTab(link));
                    }
                }));
                added.Add(MakeMenuItem("在新窗口中打开链接", () =>
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(() =>
                        {
                            var window = new MainForm(link, incognito: false);
                            window.Show(this);
                        });
                    }
                }));
                added.Add(MakeMenuItem("复制链接地址", () =>
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(() => CopyToClipboard(link, "已复制链接地址"));
                    }
                }));
            }
            else if (!string.IsNullOrEmpty(selection))
            {
                string text = selection.Trim();
                if (text.Length <= 60 && !text.Contains('\n'))
                {
                    added.Add(MakeMenuItem($"用 {_settings.Engine.Name} 搜索「{Shorten(text)}」", () =>
                    {
                        if (IsHandleCreated)
                        {
                            BeginInvoke(() => _tabs?.NewTab(
                                _settings.Engine.Template.Replace("%s",
                                    Uri.EscapeDataString(text))));
                        }
                    }));
                }
                added.Add(MakeMenuItem("复制选中文字", () =>
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(() => CopyToClipboard(selection, "已复制选中文字"));
                    }
                }));
            }

            if (added.Count == 0)
            {
                return;
            }

            // 前面插一条分隔线，视觉上和内核自带项分开
            var separator = tab.View.CoreWebView2.Environment
                .CreateContextMenuItem("轻羽", null, CoreWebView2ContextMenuItemKind.Separator);
            menu.Add(separator);
            foreach (CoreWebView2ContextMenuItem item in added)
            {
                menu.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("构建右键菜单失败: " + ex.Message);
        }
    }

    private CoreWebView2ContextMenuItem MakeMenuItem(string label, Action action)
    {
        CoreWebView2ContextMenuItem item = _tabs.Active.View.CoreWebView2.Environment
            .CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warn("右键菜单动作失败: " + ex.Message);
            }
        };
        return item;
    }

    private static string Shorten(string text) =>
        text.Length <= 18 ? text : text[..18] + "…";

    private void PushCurrentInternalPage()
    {
        BrowserTab tab = _tabs?.Active;
        if (tab == null)
        {
            return;
        }
        if (string.Equals(tab.Url, InternalPages.BookmarksUrl, StringComparison.OrdinalIgnoreCase))
        {
            PushBookmarks(tab);
        }
        else if (string.Equals(tab.Url, InternalPages.DownloadsUrl, StringComparison.OrdinalIgnoreCase))
        {
            PushDownloads(tab);
        }
        else if (string.Equals(tab.Url, InternalPages.HistoryUrl, StringComparison.OrdinalIgnoreCase))
        {
            PushHistory(tab);
        }
    }

    // ================================================================ 弹层

    private void ShowTabList()
    {
        ToggleSidebar();
    }

    private void ShowMenu()
    {
        if (_tabs == null)
        {
            return;
        }

        var items = new List<MenuEntry>
        {
            new()
            {
                Text = "新建标签页", Shortcut = "Ctrl+T",
                Action = () => _tabs.NewTab(UrlUtils.InternalHome),
            },
            new()
            {
                Text = "新建窗口", Shortcut = "Ctrl+N",
                Action = OpenNewWindow,
            },
            new()
            {
                Text = "新建无痕窗口", Shortcut = "Ctrl+Shift+N",
                Action = OpenIncognitoWindow,
            },
            new()
            {
                Text = "复制当前网址", Shortcut = "Ctrl+Shift+C",
                Action = CopyCurrentUrl, DividerBefore = true, StartsGroup = true,
            },
            new()
            {
                Text = "在默认浏览器中打开",
                Action = OpenInSystemBrowser,
            },
            new()
            {
                Text = "书签管理", Shortcut = "Ctrl+Shift+O",
                Action = () => OpenInternalPage(InternalPages.BookmarksUrl), StartsGroup = true,
            },
            new()
            {
                Text = "下载内容", Shortcut = "Ctrl+J",
                Action = () => OpenInternalPage(InternalPages.DownloadsUrl),
            },
            new()
            {
                Text = "历史记录", Shortcut = "Ctrl+H",
                Action = () => OpenInternalPage(InternalPages.HistoryUrl),
            },
            new()
            {
                Text = "页内查找", Shortcut = "Ctrl+F",
                Action = ShowFindBar, StartsGroup = true,
            },
            new()
            {
                Text = "广告拦截",
                Value = _settings.AdBlockEnabled ? "已开启" : "已关闭",
                Action = ToggleAdBlock, StartsGroup = true,
            },
            new()
            {
                Text = "加载图片",
                Value = _settings.LoadImages ? "开" : "关",
                Action = ToggleImages,
            },
            new()
            {
                Text = "启用 JavaScript",
                Value = _settings.JavaScriptEnabled ? "开" : "关",
                Action = ToggleJavaScript,
            },
            new()
            {
                Text = "搜索引擎",
                Value = _settings.Engine.Name,
                Action = NextSearchEngine, StartsGroup = true,
            },
            new()
            {
                Text = "主题",
                Value = ThemeModeLabel(),
                Action = NextThemeMode, StartsGroup = true,
            },
            new()
            {
                Text = "界面放大",
                Value = $"{_settings.UiScale * 100:0}%",
                Action = NextUiScale,
            },
            new()
            {
                Text = "立即回收标签内存",
                Action = ReclaimMemory, StartsGroup = true,
            },
            new()
            {
                Text = "从夸克导入…",
                Action = ShowQuarkImport, StartsGroup = true,
            },
            new()
            {
                Text = "管理保存的密码",
                Value = _passwords.Count > 0 ? $"{_passwords.Count} 条" : "空",
                Action = ShowPasswordManager,
            },
            new()
            {
                Text = "内存与性能…",
                Action = ShowMemoryDialog,
            },
            new()
            {
                Text = "清除缓存与 Cookie",
                Action = ClearCache,
            },
            new()
            {
                Text = "设置…", Shortcut = "Ctrl+,",
                Action = ShowSettings, StartsGroup = true,
            },
            new()
            {
                Text = "关于轻羽浏览器",
                Action = ShowAbout,
            },
        };

        var menu = new PopupMenu(items, Theme.UiFont, Theme.UiFontSmall);
        menu.ShowAt(_btnMenu);
    }

    // ================================================================ 菜单动作

    private string ThemeModeLabel() => Theme.Mode switch
    {
        ThemeMode.Light => "浅色",
        ThemeMode.Dark => "深色",
        _ => "跟随系统" + (Theme.Dark ? "（当前深色）" : "（当前浅色）"),
    };

    /// <summary>循环切换主题：跟随系统 → 浅色 → 深色 → 跟随系统。</summary>
    private void NextThemeMode()
    {
        ThemeMode next = Theme.Mode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        ApplyThemeMode(next);
    }

    private void ApplyThemeMode(ThemeMode mode)
    {
        _settings.ThemeMode = (int)mode;
        _settings.Save();

        Theme.SetMode(mode);
        Theme.ApplyToAllForms();
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
        ApplyPageTheme();

        // 主题换了，侧边栏配色也要跟着换
        _sidebar?.Invalidate();

        SetStatus("主题：" + ThemeModeLabel());
    }

    /// <summary>
    /// 新开一个浏览器窗口（同进程）。
    ///
    /// <para>刻意不用「再启动一个 exe」的办法：那样会各自建一套 WebView2 环境，
    /// 磁盘缓存与 Cookie 分裂，内存也直接翻倍。同进程多窗口共用环境与数据存储，
    /// 在任务管理器里也更容易看总量。</para>
    /// </summary>
    private void OpenNewWindow()
    {
        var window = new MainForm(null, incognito: false)
        {
            // 让新窗口稍微错开，避免完全盖住原窗口
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Left + Theme.Sx(28), Top + Theme.Sy(28)),
        };
        window.Show(this);
    }

    private void OpenIncognitoWindow()
    {
        var form = new MainForm(null, incognito: true);
        form.Show(this);
    }

    private void CopyCurrentUrl()
    {
        BrowserTab tab = _tabs?.Active;
        if (tab == null || tab.IsHomePage)
        {
            return;
        }
        try
        {
            Clipboard.SetText(tab.Url);
            SetStatus("网址已复制到剪贴板");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message);
        }
    }

    private void OpenInSystemBrowser()
    {
        BrowserTab tab = _tabs?.Active;
        if (tab == null || tab.IsHomePage)
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tab.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开失败：" + ex.Message);
        }
    }

    private void ToggleAdBlock()
    {
        _settings.AdBlockEnabled = !_settings.AdBlockEnabled;
        _adBlock.Enabled = _settings.AdBlockEnabled;
        _settings.Save();
        SetStatus(_settings.AdBlockEnabled ? "广告拦截已开启" : "广告拦截已关闭");
    }

    private void ToggleImages()
    {
        _settings.LoadImages = !_settings.LoadImages;
        _settings.Save();
        // 图片显示由注入的 CSS 控制，重新加载当前页即可立刻看到效果。
        _tabs?.Active?.Reload();
        SetStatus(_settings.LoadImages ? "已开启图片加载" : "已关闭图片加载（重新加载页面生效）");
    }

    private void ToggleJavaScript()
    {
        _settings.JavaScriptEnabled = !_settings.JavaScriptEnabled;
        _settings.Save();
        if (_tabs != null)
        {
            foreach (BrowserTab tab in _tabs.Tabs)
            {
                try
                {
                    if (tab.View?.CoreWebView2 != null)
                    {
                        tab.View.CoreWebView2.Settings.IsScriptEnabled = _settings.JavaScriptEnabled;
                    }
                }
                catch
                {
                    // 忽略
                }
            }
        }
        SetStatus(_settings.JavaScriptEnabled ? "已启用 JavaScript" : "已禁用 JavaScript");
    }

    private void NextSearchEngine()
    {
        _settings.SearchEngineIndex =
            (_settings.SearchEngineIndex + 1) % SearchEngine.BuiltIn.Length;
        _settings.Save();
        SetStatus("搜索引擎已切换为 " + _settings.Engine.Name);
    }

    /// <summary>循环切换界面倍率。字体与尺寸都要重建，所以立刻重新套用。</summary>
    private void NextUiScale()
    {
        float[] steps = { 1.0f, 1.15f, 1.3f, 1.5f };
        int index = Array.FindIndex(steps, s => Math.Abs(s - _settings.UiScale) < 0.01f);
        index = index < 0 ? 1 : (index + 1) % steps.Length;

        _settings.UiScale = steps[index];
        _settings.Save();
        Theme.Scale = steps[index];
        Theme.RefreshFonts();
        ApplyScaledMetrics();
        SetStatus($"界面已放大到 {steps[index] * 100:0}%");
    }

    private void ReclaimMemory()
    {
        if (_tabs == null)
        {
            return;
        }
        _tabs.ReclaimNow();
        MemoryMonitor.Refresh();
        UpdateMemoryReadout();
        SetStatus($"已回收后台标签内存（当前 {MemoryMonitor.Mb(MemoryMonitor.WebViewWorkingSet)}）");
    }

    // ================================================================ 密码与导入

    /// <summary>登录表单提交后询问是否保存这条凭据。</summary>
    private void PromptSaveCredential(BrowserTab tab, string username, string password)
    {
        if (tab == null)
        {
            return;
        }

        string origin = UrlUtils.OriginOf(tab.Url);
        string site = UrlUtils.HostOf(tab.Url);
        string shownUser = string.IsNullOrEmpty(username) ? "（无用户名）" : username;

        DialogResult answer = MessageBox.Show(this,
            $"是否让轻羽保存这个网站的登录信息？\r\n\r\n" +
            $"网站：{site}\r\n" +
            $"账号：{shownUser}\r\n\r\n" +
            "密码会用当前 Windows 账户加密后保存在本机，不会明文落盘。",
            "保存密码", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _passwords.Save(string.IsNullOrEmpty(origin) ? tab.Url : origin, username, password,
            source: "");
        SetStatus($"已保存 {site} 的登录信息");
    }

    /// <summary>从夸克导入书签 / 历史 / 密码。</summary>
    private void ShowQuarkImport()
    {
        if (!QuarkImporter.IsAvailable())
        {
            MessageBox.Show(this,
                "没有找到夸克的数据目录：\r\n" + QuarkImporter.QuarkUserDataPath +
                "\r\n\r\n请确认本机安装过夸克浏览器并至少使用过一次。",
                "从夸克导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ImportDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        UseWaitCursor = true;
        ImportResult result;
        try
        {
            var importer = new QuarkImporter(_bookmarks, _history, _passwords);
            result = importer.Import(dialog.ImportBookmarks, dialog.ImportHistory,
                dialog.ImportPasswords, dialog.HistoryLimit);
        }
        finally
        {
            UseWaitCursor = false;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine(result.Summary());
        foreach (string note in result.Notes)
        {
            text.AppendLine();
            text.AppendLine("· " + note);
        }

        MessageBox.Show(this, text.ToString().TrimEnd(),
            result.AnySuccess ? "导入完成" : "导入结束",
            MessageBoxButtons.OK,
            result.AnySuccess ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        SetStatus("导入完成：" + result.Summary());
        UpdateChrome();
    }

    /// <summary>管理保存的密码。</summary>
    private void ShowPasswordManager()
    {
        using var dialog = new PasswordsDialog(_passwords);
        dialog.ShowDialog(this);
        SetStatus($"密码库现有 {_passwords.Count} 条记录");
    }

    private void ClearCache()
    {
        try
        {
            string folder = AppPaths.WebViewDataFolder;
            if (Directory.Exists(folder))
            {
                // 只删缓存目录，保留登录状态所需的 Cookie 数据库
                foreach (string sub in new[] { "EBWebView\\Default\\Cache", "Default\\Cache", "Cache" })
                {
                    string path = Path.Combine(folder, sub);
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
            }
            SetStatus("缓存已清除");
        }
        catch (Exception ex)
        {
            SetStatus("清除缓存失败：" + ex.Message);
        }
    }

    private void ShowMemoryDialog()
    {
        if (_tabs == null)
        {
            return;
        }
        using var dialog = new MemoryDialog(_tabs, _settings, () => ReclaimMemory());
        dialog.ShowDialog(this);
        UpdateMemoryReadout();
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(_settings);
        DialogResult result = dialog.ShowDialog(this);

        // 设置在对话框里可能改了主题 / 倍率，这里统一重新套用一次。
        Theme.Scale = Math.Clamp(_settings.UiScale, 1.0f, 1.6f);
        Theme.SetMode((ThemeMode)Math.Clamp(_settings.ThemeMode, 0, 2));
        Theme.RefreshFonts();
        ApplyScaledMetrics();
        ApplyPageTheme();

        if (result == DialogResult.OK)
        {
            _adBlock.Enabled = _settings.AdBlockEnabled;
            if (_tabs != null)
            {
                _tabs.EnforceMemoryPolicy();
            }
            UpdateChrome();
            SetStatus("设置已保存");
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "轻羽浏览器 1.0\r\n\r\n" +
            "Windows 原生浏览器，界面参考安卓端 Via 浏览器的极简形态。\r\n" +
            "内核：系统自带的 Microsoft Edge WebView2 运行时（不额外打包 Chromium）。\r\n\r\n" +
            "低内存做法：\r\n" +
            "  · 标签分档：渲染中 / 已休眠，只有渲染中的标签占内存\r\n" +
            "  · 超出「同时渲染标签数」的标签自动销毁渲染进程，只保留网址\r\n" +
            "  · 窗口失焦时挂起后台标签，切回来立即恢复\r\n" +
            "  · 内置首页为本地 HTML，零网络与渲染开销\r\n" +
            "  · 域名级广告拦截，HashSet 常数时间匹配\r\n\r\n" +
            "界面：尺寸按屏幕 DPI 自动换算，圆角统一；\r\n" +
            "主题支持浅色 / 深色 / 跟随系统，也可以在菜单里手动切换。\r\n\r\n" +
            "数据目录：" + AppPaths.Root,
            AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================ 查找

    private void ShowFindBar()
    {
        _findBar.Visible = true;
        _findInput.Focus();
        _findInput.SelectAll();
    }

    private void HideFindBar()
    {
        _findBar.Visible = false;
        _findFirstMatch = true;
        _findCount.Text = "";
        _tabs?.Active?.Find("", true, true);
        _viewHost.Focus();
    }

    private void FindNext(bool forward)
    {
        BrowserTab tab = _tabs?.Active;
        if (tab == null)
        {
            return;
        }
        string text = _findInput.Text;
        tab.Find(text, forward, _findFirstMatch);
        _findFirstMatch = false;
        _findCount.Text = string.IsNullOrEmpty(text) ? "" : (forward ? "↓" : "↑");
    }

    // ================================================================ 键盘

    private void OnFormKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = e.Control;
        bool shift = e.Shift;

        if (ctrl && e.KeyCode == Keys.T && !shift)
        {
            _tabs?.NewTab(UrlUtils.InternalHome);
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.T && shift)
        {
            // 桌面浏览器惯例：Ctrl+Shift+T 恢复刚关掉的标签。
            // 侧边栏改用 Ctrl+Shift+E（见下），不再占用这个键位。
            if (!RestoreClosedTab())
            {
                SetStatus("没有可恢复的标签");
            }
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.E && shift)
        {
            ShowTabList();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.W)
        {
            if (_tabs?.Active != null)
            {
                _tabs.CloseTab(_tabs.Active);
            }
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.N && shift)
        {
            OpenIncognitoWindow();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.N)
        {
            OpenNewWindow();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.L)
        {
            _address.Focus();
            _address.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.D)
        {
            ToggleBookmark();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.F)
        {
            ShowFindBar();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.R)
        {
            Reload();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.Oemcomma)
        {
            ShowSettings();
            e.Handled = true;
        }
        else if (ctrl && (e.KeyCode == Keys.Tab || e.KeyCode == Keys.PageDown))
        {
            CycleTab(1);
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.PageUp)
        {
            CycleTab(-1);
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.Oemplus)
        {
            // Ctrl+= 放大界面，Ctrl+0 复位，方便高分屏用户自己调
            NextUiScale();
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.D0)
        {
            _settings.UiScale = 1.15f;
            _settings.Save();
            Theme.Scale = 1.15f;
            Theme.RefreshFonts();
            ApplyScaledMetrics();
            SetStatus("界面倍率已复位");
            e.Handled = true;
        }
        else if (ctrl && e.KeyCode == Keys.J)
        {
            NextThemeMode();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F5)
        {
            Reload();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            if (_findBar.Visible)
            {
                HideFindBar();
            }
            else
            {
                _tabs?.Active?.Stop();
            }
            e.Handled = true;
        }
        else if (e.Alt && e.KeyCode == Keys.Left)
        {
            GoBack();
            e.Handled = true;
        }
        else if (e.Alt && e.KeyCode == Keys.Right)
        {
            GoForward();
            e.Handled = true;
        }
        else if (ctrl && (e.KeyCode == Keys.Add || e.KeyCode == Keys.Subtract))
        {
            AdjustZoom(e.KeyCode == Keys.Add ? 0.1 : -0.1);
            e.Handled = true;
        }
    }

    private void CycleTab(int delta)
    {
        if (_tabs == null || _tabs.Count <= 1)
        {
            return;
        }
        int index = (_tabs.ActiveIndex + delta + _tabs.Count) % _tabs.Count;
        _tabs.Activate(index);
    }

    private double _zoom = 1.0;

    private void AdjustZoom(double delta)
    {
        _zoom = Math.Clamp(_zoom + delta, 0.25, 5.0);
        _tabs?.Active?.SetZoom(_zoom);
        SetStatus($"网页缩放 {_zoom * 100:0}%");
    }

    private bool _fullscreen;

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _toolbar.Visible = false;
            _progressHost.Visible = false;
            _status.Visible = false;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            _toolbar.Visible = true;
            _progressHost.Visible = true;
            _status.Visible = true;
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            NavigateFromAddress();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _addressDirty = false;
            UpdateChrome();
            _viewHost.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // ================================================================ 窗口事件

    private void OnResized()
    {
        if (WindowState != _lastWindowState)
        {
            _lastWindowState = WindowState;
        }
        LayoutToolbar();
    }

    private void OnDeactivated()
    {
        // 窗口不再是最前台时，后台标签继续渲染没有任何意义，直接挂起。
        _tabs?.SuspendBackgroundTabs();
    }

    private void OnActivatedHandler()
    {
        _tabs?.ResumeFromSuspend();
    }

    private void OnFormClosingHandler(object sender, FormClosingEventArgs e)
    {
        // 关掉正在运行的实例前先落盘设置，避免丢失
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("关闭时保存设置失败: " + ex.Message);
        }

        if (_tabs != null)
        {
            _tabs.Shutdown();
        }

        if (_incognito && !string.IsNullOrEmpty(_temporaryDataFolder))
        {
            // 无痕窗口：删掉整个临时数据目录
            TryDeleteFolder(_temporaryDataFolder);
        }
    }

    /// <summary>
    /// 窗口关闭后的收尾：解绑事件、清理内存，并在**最后一个窗口**关闭时退出进程。
    ///
    /// <para>多窗口下不能每个窗口一关就退出，所以这里数一遍还剩几个同类型窗口。
    /// 最后一个窗口退出前把会话写盘（标签列表、窗口尺寸）。</para>
    /// </summary>
    private void OnFormClosedHandler(object sender, FormClosedEventArgs e)
    {
        Theme.SystemThemeChanged -= OnSystemThemeChanged;

        int remaining = 0;
        foreach (Form form in Application.OpenForms)
        {
            if (form is MainForm)
            {
                remaining++;
            }
        }

        if (remaining > 0)
        {
            return;
        }

        // 最后一个窗口：保存会话后退出
        try
        {
            PersistSession();
        }
        catch (Exception ex)
        {
            Log.Warn("退出前保存会话失败: " + ex.Message);
        }

        MemoryMonitor.TrimWorkingSet();
        Application.Exit();
    }

    private static void TryDeleteFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch
            {
                // 内核进程可能还没完全退出，稍等再试
                Thread.Sleep(250);
            }
        }
        Log.Warn("无痕数据目录未能删除: " + path);
    }

    /// <summary>把当前标签列表写回设置，供下次启动恢复。</summary>
    public void PersistSession()
    {
        if (_tabs == null || _incognito)
        {
            return;
        }

        // 自动化测试/诊断模式（--uitest=...）启动的窗口只开一两个临时标签，
        // 如果把它的标签列表当会话存下去，用户真实的标签页就被这一两个临时页覆盖了。
        // 这个坑真的踩过：用 --uitest=page 验证内置页面，结果把用户的会话冲掉了。
        if (!string.IsNullOrEmpty(_uiTest))
        {
            Log.Info("诊断模式（--uitest）不写入会话，避免覆盖真实标签列表");
            return;
        }

        _settings.SessionUrls = _tabs.Tabs.Select(t => t.Url).ToList();
        _settings.SessionActiveIndex = _tabs.ActiveIndex;
        if (WindowState == FormWindowState.Normal)
        {
            // 存的是逻辑尺寸，换到别的 DPI 屏幕上恢复时不会过大。
            _settings.WindowWidth = (int)Math.Round(Width / (Theme.DpiScale * Theme.Scale) * 1.15f);
            _settings.WindowHeight = (int)Math.Round(Height / (Theme.DpiScale * Theme.Scale) * 1.15f);
            _settings.WindowMaximized = false;
        }
        else if (WindowState == FormWindowState.Maximized)
        {
            _settings.WindowMaximized = true;
        }
        _settings.Save();
        _history.SaveNow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memoryTimer?.Dispose();
            _progressTimer?.Dispose();
            _adBlock.Enabled = false;
        }
        base.Dispose(disposing);
    }
}
