using FeatherBrowser.Services;

namespace FeatherBrowser.UI;

/// <summary>
/// 设置窗口。控件全部用代码创建，不引入 designer 文件与资源；
/// 布局用 TableLayoutPanel，避免手写坐标在不同 DPI 下错位。
///
/// <p>输入类控件用的是 <see cref="ThemedTextBox"/> / <see cref="ThemedComboBox"/> /
/// <see cref="ThemedNumericUpDown"/>：原生控件在深色主题下会留白底，
/// 这几个替换件自己绘制，能跟着主题走。主题切换时窗口实时变色。
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;

    private readonly ThemedTextBox _homePage = new();
    private readonly ThemedComboBox _engine = new();
    private readonly ThemedComboBox _theme = new();
    private readonly ThemedComboBox _uiScale = new();
    private readonly CheckBox _adBlock = new();
    private readonly CheckBox _images = new();
    private readonly CheckBox _script = new();
    private readonly CheckBox _desktopUa = new();
    private readonly CheckBox _suspendOnDeactivate = new();
    private readonly ThemedNumericUpDown _maxLiveTabs = new();

    private static readonly float[] ScaleSteps = { 1.0f, 1.15f, 1.3f, 1.5f };

    /// <summary>下拉框里的主题选项顺序与 <see cref="ThemeMode"/> 的取值一致。</summary>
    private static readonly string[] ThemeNames = { "跟随系统", "浅色", "深色" };

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;

        Text = "设置 - 轻羽浏览器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.UiFont;
        Padding = new Padding(Theme.Sx(24));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(Theme.Sx(600), Theme.Sy(620));
        LoadValues();

        Theme.ApplyTo(this);
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => Theme.Changed -= OnThemeChanged;

        // AutoSize + Dock=Fill 的组合在自动测量上偶尔会算小，导致底部按钮被裁掉。
        // 显示之前按内容实际需要的尺寸再兜一次，保证「保存/取消」一定看得见。
        Shown += (_, _) =>
        {
            int neededWidth = Theme.Sx(600);
            int neededHeight = Theme.Sy(620);
            if (ClientSize.Width < neededWidth || ClientSize.Height < neededHeight)
            {
                ClientSize = new Size(
                    Math.Max(ClientSize.Width, neededWidth),
                    Math.Max(ClientSize.Height, neededHeight));
            }
        };
    }

    /// <summary>主题变化时立刻重新上色，让用户在设置里能马上看到效果。</summary>
    private void OnThemeChanged()
    {
        if (IsDisposed)
        {
            return;
        }
        Theme.ApplyTo(this);
        _homePage.Invalidate(true);
        _engine.Invalidate();
        _theme.Invalidate();
        _uiScale.Invalidate();
        _maxLiveTabs.RefreshTheme();
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
    }

    private Control BuildHeader()
    {
        return new Label
        {
            Text = "设置",
            Font = Theme.UiFontTitle,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Theme.Sy(16)),
        };
    }

    private Control BuildBody()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Sx(200)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        _homePage.PlaceholderText = "留空则打开内置首页";
        AddRow(grid, row++, "启动页", _homePage, Theme.Sx(360), Theme.Sy(36));

        foreach (SearchEngine engine in SearchEngine.BuiltIn)
        {
            _engine.Items.Add(engine.Name);
        }
        AddRow(grid, row++, "搜索引擎", _engine, Theme.Sx(240), Theme.Sy(34));

        foreach (string name in ThemeNames)
        {
            _theme.Items.Add(name);
        }
        _theme.SelectedIndexChanged += (_, _) => PreviewTheme();
        AddRow(grid, row++, "主题", _theme, Theme.Sx(240), Theme.Sy(34));

        foreach (float step in ScaleSteps)
        {
            _uiScale.Items.Add($"{step * 100:0}%" + (Math.Abs(step - 1.15f) < 0.01f ? "（推荐）" : ""));
        }
        AddRow(grid, row++, "界面放大", _uiScale, Theme.Sx(240), Theme.Sy(34));

        AddCheck(grid, row++, "广告与追踪拦截", _adBlock,
            "按域名黑名单拦截，常数时间匹配，几乎不占内存");
        AddCheck(grid, row++, "加载图片", _images,
            "关闭后用 CSS 隐藏图片，省流量也省解码内存");
        AddCheck(grid, row++, "启用 JavaScript", _script,
            "关闭后部分网站无法正常显示");
        AddCheck(grid, row++, "请求桌面版网站", _desktopUa,
            "对国内视频网站通常无效");
        AddCheck(grid, row++, "窗口失焦时挂起后台标签", _suspendOnDeactivate,
            "切到别的程序时立刻释放后台标签的内存，切回来自动恢复");

        _maxLiveTabs.Minimum = 1;
        _maxLiveTabs.Maximum = 8;
        AddRow(grid, row++, "同时渲染标签数", _maxLiveTabs, Theme.Sx(100), Theme.Sy(34));

        var hint = new Label
        {
            Text = "同时渲染的标签越少，内存占用越低。超出的标签会被休眠（销毁渲染进程），\r\n" +
                   "只保留网址，切回去时自动重新加载。",
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, Theme.Sy(2), 0, Theme.Sy(10)),
        };
        grid.Controls.Add(new Label { Text = "", AutoSize = true }, 0, row);
        grid.Controls.Add(hint, 1, row);
        row++;

        var themeHint = new Label
        {
            Text = "「跟随系统」会随 Windows 的深浅色设置自动切换；深色模式下网页也会按深色渲染。",
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            MaximumSize = new Size(Theme.Sx(350), 0),
            Margin = new Padding(0, 0, 0, Theme.Sy(10)),
        };
        grid.Controls.Add(new Label { Text = "", AutoSize = true }, 0, row);
        grid.Controls.Add(themeHint, 1, row);
        row++;

        var dataHint = new Label
        {
            Text = "数据目录：" + AppPaths.Root,
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, Theme.Sy(14), 0, 0),
        };
        grid.Controls.Add(dataHint, 0, row);
        grid.SetColumnSpan(dataHint, 2);

        return grid;
    }

    /// <summary>主题下拉框变化时立刻预览，不必等点保存。</summary>
    private void PreviewTheme()
    {
        int index = Math.Clamp(_theme.SelectedIndex, 0, 2);
        Theme.SetMode((ThemeMode)index);
        Theme.ApplyToAllForms();
    }

    private Control BuildFooter()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, Theme.Sy(18), 0, 0),
        };

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = Theme.Sx(116),
            Height = Theme.Sy(42),
            FlatStyle = FlatStyle.System,
        };
        // 取消时把主题还原回打开设置之前的样子
        cancel.Click += (_, _) => Theme.SetMode((ThemeMode)Math.Clamp(_settings.ThemeMode, 0, 2));

        var ok = new Button
        {
            Text = "保存",
            Width = Theme.Sx(116),
            Height = Theme.Sy(42),
            FlatStyle = FlatStyle.System,
        };
        ok.Click += (_, _) => SaveValues();

        panel.Controls.Add(cancel);
        panel.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        return panel;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control field,
        int width, int height)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Font = Theme.UiFont,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, Theme.Sy(10), Theme.Sx(10), Theme.Sy(10)),
        }, 0, row);

        field.Font = Theme.UiFont;
        field.Width = width;
        field.Height = height;
        field.Margin = new Padding(0, Theme.Sy(7), 0, Theme.Sy(7));
        grid.Controls.Add(field, 1, row);
    }

    private static void AddCheck(TableLayoutPanel grid, int row, string text, CheckBox box, string tip)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Text = text;
        box.AutoSize = true;
        box.Font = Theme.UiFont;
        box.ForeColor = Theme.Text;
        box.BackColor = Color.Transparent;
        box.Margin = new Padding(0, Theme.Sy(10), Theme.Sx(8), 0);

        var tipHolder = new ToolTip { InitialDelay = 500, AutoPopDelay = 10000 };
        tipHolder.SetToolTip(box, tip);
        box.Disposed += (_, _) => tipHolder.Dispose();

        grid.Controls.Add(box, 0, row);
        grid.SetColumnSpan(box, 2);
    }

    private void LoadValues()
    {
        _homePage.TextValue = _settings.HomePage ?? "";
        _engine.SelectedIndex = Math.Clamp(_settings.SearchEngineIndex, 0, SearchEngine.BuiltIn.Length - 1);
        _theme.SelectedIndex = Math.Clamp(_settings.ThemeMode, 0, 2);

        int scaleIndex = Array.FindIndex(ScaleSteps,
            s => Math.Abs(s - _settings.UiScale) < 0.01f);
        _uiScale.SelectedIndex = scaleIndex < 0 ? 1 : scaleIndex;

        _adBlock.Checked = _settings.AdBlockEnabled;
        _images.Checked = _settings.LoadImages;
        _script.Checked = _settings.JavaScriptEnabled;
        _desktopUa.Checked = _settings.DesktopUserAgent;
        _suspendOnDeactivate.Checked = _settings.SuspendOnDeactivate;
        _maxLiveTabs.Value = Math.Clamp(_settings.MaxLiveTabs, 1, 8);
    }

    private void SaveValues()
    {
        _settings.HomePage = _homePage.TextValue.Trim();
        _settings.SearchEngineIndex = Math.Max(0, _engine.SelectedIndex);
        _settings.ThemeMode = Math.Clamp(_theme.SelectedIndex, 0, 2);
        _settings.UiScale = ScaleSteps[Math.Clamp(_uiScale.SelectedIndex, 0, ScaleSteps.Length - 1)];
        _settings.AdBlockEnabled = _adBlock.Checked;
        _settings.LoadImages = _images.Checked;
        _settings.JavaScriptEnabled = _script.Checked;
        _settings.DesktopUserAgent = _desktopUa.Checked;
        _settings.SuspendOnDeactivate = _suspendOnDeactivate.Checked;
        _settings.MaxLiveTabs = (int)_maxLiveTabs.Value;
        _settings.Save();

        DialogResult = DialogResult.OK;
        Close();
    }
}
