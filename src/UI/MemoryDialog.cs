using FeatherBrowser.Core;
using FeatherBrowser.Services;

namespace FeatherBrowser.UI;

/// <summary>
/// 内存与性能面板。
///
/// <p>把「标签分档」直接摆到用户面前：渲染中 / 已休眠各有多少个、
/// 内核一共几个进程占了多少内存、本程序自身占了多少。数字每 600ms 刷新一次。
/// 面板打开期间就能看到点「立即回收」前后内存掉下去的过程。
///
/// <p>所有尺寸按 DPI 与界面倍率换算，字号比第一版整体上调一档。
/// </summary>
internal sealed class MemoryDialog : Form
{
    private readonly TabManager _tabs;
    private readonly AppSettings _settings;
    private readonly Action _reclaim;

    private readonly Label _systemLabel = new();
    private readonly Label _appLabel = new();
    private readonly Label _tabLabel = new();
    private readonly Label _kernelLabel = new();
    private readonly Label _verdictLabel = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly TrackBar _maxLive;
    private readonly Label _sliderValue = new();

    public MemoryDialog(TabManager tabs, AppSettings settings, Action onReclaim)
    {
        _tabs = tabs;
        _settings = settings;
        _reclaim = onReclaim;

        Text = "内存与性能 - 轻羽浏览器";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Theme.Sx(620), Theme.Sy(530));
        Font = Theme.UiFont;
        Padding = new Padding(Theme.Sx(24));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
        };
        for (int i = 0; i < 5; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "内存占用",
            Font = Theme.UiFontTitle,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Theme.Sy(14)),
        };
        root.Controls.Add(title, 0, 0);

        foreach (Label label in new[] { _systemLabel, _appLabel, _kernelLabel, _tabLabel })
        {
            label.AutoSize = true;
            label.ForeColor = Theme.Text;
            label.BackColor = Color.Transparent;
            label.Font = Theme.UiFont;
            label.Margin = new Padding(0, Theme.Sy(5), 0, Theme.Sy(5));
            root.Controls.Add(label, 0, 1);
        }

        _verdictLabel.AutoSize = true;
        _verdictLabel.ForeColor = Theme.TextDim;
        _verdictLabel.BackColor = Color.Transparent;
        _verdictLabel.Font = Theme.UiFontSmall;
        _verdictLabel.MaximumSize = new Size(Theme.Sx(560), 0);
        _verdictLabel.Margin = new Padding(0, Theme.Sy(16), 0, 0);
        root.Controls.Add(_verdictLabel, 0, 2);

        // 滑块：调「同时渲染标签数」
        var sliderGroup = new GroupBox
        {
            Text = "同时渲染标签数上限（越小越省内存）",
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            Dock = DockStyle.Top,
            Height = Theme.Sy(118),
            Margin = new Padding(0, Theme.Sy(18), 0, 0),
            Padding = new Padding(Theme.Sx(12)),
        };

        _sliderValue.Font = Theme.UiFontBold;
        _sliderValue.ForeColor = Theme.Accent;
        _sliderValue.BackColor = Color.Transparent;
        _sliderValue.AutoSize = true;
        _sliderValue.TextAlign = ContentAlignment.MiddleRight;
        sliderGroup.Controls.Add(_sliderValue);

        _maxLive = new TrackBar
        {
            Minimum = 1,
            Maximum = 8,
            TickFrequency = 1,
            LargeChange = 1,
            SmallChange = 1,
            Dock = DockStyle.Bottom,
            Height = Theme.Sy(48),
            Value = Math.Clamp(_settings.MaxLiveTabs, 1, 8),
        };
        _maxLive.ValueChanged += (_, _) =>
        {
            _settings.MaxLiveTabs = _maxLive.Value;
            _settings.Save();
            _tabs.EnforceMemoryPolicy();
            UpdateLabels();
        };
        sliderGroup.Controls.Add(_maxLive);
        root.Controls.Add(sliderGroup, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Theme.Background,
            Margin = new Padding(0, Theme.Sy(14), 0, 0),
        };

        var reclaim = new Button
        {
            Text = "立即回收后台标签内存",
            Width = Theme.Sx(240),
            Height = Theme.Sy(38),
            FlatStyle = FlatStyle.System,
        };
        reclaim.Click += (_, _) =>
        {
            _reclaim?.Invoke();
            UpdateLabels();
        };
        buttons.Controls.Add(reclaim);

        var close = new Button
        {
            Text = "关闭",
            Width = Theme.Sx(110),
            Height = Theme.Sy(38),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.OK,
        };
        buttons.Controls.Add(close);
        root.Controls.Add(buttons, 0, 4);

        Controls.Add(root);

        Theme.ApplyTo(this);
        Theme.Changed += OnThemeChanged;
        FormClosed += (_, _) => Theme.Changed -= OnThemeChanged;

        _timer = new System.Windows.Forms.Timer { Interval = 600 };
        _timer.Tick += (_, _) => UpdateLabels();
        _timer.Start();

        UpdateLabels();
    }

    /// <summary>主题变化时立刻重新上色。</summary>
    private void OnThemeChanged()
    {
        if (IsDisposed)
        {
            return;
        }
        Theme.ApplyTo(this);
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        WindowChrome.ApplyDarkTitleBar(this, Theme.Dark);
    }

    private void UpdateLabels()
    {
        MemoryMonitor.Refresh();

        _sliderValue.Text = $"{_settings.MaxLiveTabs} 个";
        _sliderValue.Location = new Point(
            Theme.Sx(360), Theme.Sy(6));

        _systemLabel.Text = $"系统物理内存：可用 {MemoryMonitor.Mb(MemoryMonitor.AvailablePhysical)} / " +
                            $"共 {MemoryMonitor.Mb(MemoryMonitor.TotalPhysical)}";

        _appLabel.Text = $"本程序：工作集 {MemoryMonitor.Mb(MemoryMonitor.WorkingSet)}" +
                         $"（私有 {MemoryMonitor.Mb(MemoryMonitor.PrivateBytes)}）";

        _kernelLabel.Text = $"Edge 内核：{MemoryMonitor.WebViewProcessCount} 个进程，" +
                            $"合计 {MemoryMonitor.Mb(MemoryMonitor.WebViewWorkingSet)}";

        _tabLabel.Text = $"标签：共 {_tabs.Count} 个 —— 渲染中 {_tabs.LiveCount}" +
                         $"，已挂起 {_tabs.SuspendedCount}，已休眠 {_tabs.ColdCount}" +
                         $"，历史上共创建过 {_tabs.TotalCreated} 个内核视图";

        long perTab = _tabs.LiveCount > 0
            ? MemoryMonitor.WebViewWorkingSet / _tabs.LiveCount
            : 0;
        _verdictLabel.Text =
            "说明：真正占内存的是「渲染中」的标签，按当前数据平均每个约 " +
            MemoryMonitor.Mb(perTab) + "。\r\n" +
            "把上限调小，多余的标签会被休眠（销毁渲染进程），只保留网址，因此标签开到几十个，\r\n" +
            "内存也不会线性增长。代价是切回休眠标签时需要重新加载一次页面。";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            _timer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
