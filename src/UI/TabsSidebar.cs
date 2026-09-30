using System.Drawing.Drawing2D;
using FeatherBrowser.Core;

namespace FeatherBrowser.UI;

/// <summary>
/// 标签侧边栏。
///
/// <p>它是一个画在主窗口内部的常规控件，**不是独立弹窗**。
/// 之前的实现想用弹出窗口做标签列表，结果既没接上线，也无法被截图工具抓取核对。
/// 改成主窗口内的面板后：渲染可控、能被自动截图验证、也不会因为抢焦点被系统关掉。
///
/// <p>侧边栏平时隐藏（宽度收成 0），点标签按钮或按 Ctrl+Shift+T 时展开。
/// 展开时会把网页容器挡住一部分，这是有意的：看标签时不需要看网页。
/// </summary>
internal sealed class TabsSidebar : Control
{
    /// <summary>侧边栏宽度（逻辑像素，最终会乘 DPI 与界面倍率）。</summary>
    private const int PanelWidth = 300;

    private const int RowHeight = 54;

    private readonly List<RowHit> _rows = new();
    private List<BrowserTab> _tabs = new();
    private int _activeIndex = -1;
    private int _hotCount;
    private int _coldCount;
    private int _maxLive;
    private string _memoryText = "";
    private int _scrollOffset;
    private int _contentHeight;
    private int _hoverRow = -1;
    private Rectangle _newTabRect;
    private Rectangle _closeAllRect;

    /// <summary>交互回调。</summary>
    public Action<int> ActivateTab { get; set; }

    public Action<int> CloseTab { get; set; }

    public Action NewTab { get; set; }

    public Action CloseAll { get; set; }

    public Action ShowMemoryDialog { get; set; }

    public TabsSidebar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Right;
        Width = 0;
        Visible = false;
        Font = Theme.UiFont;
    }

    /// <summary>把当前标签状态灌进来并重绘。</summary>
    public void Update(IReadOnlyList<BrowserTab> tabs, int activeIndex, int hotCount,
        int coldCount, int maxLive, string memoryText)
    {
        _tabs = tabs != null ? new List<BrowserTab>(tabs) : new List<BrowserTab>();
        _activeIndex = activeIndex;
        _hotCount = hotCount;
        _coldCount = coldCount;
        _maxLive = maxLive;
        _memoryText = memoryText ?? "";

        _contentHeight = HeaderHeight + _tabs.Count * Theme.Sy(RowHeight) + FooterHeight;
        ClampScroll();
        Invalidate();
    }

    /// <summary>侧边栏宽度（已按 DPI 与倍率换算）。</summary>
    public int ExpandedWidth => Theme.Sx(PanelWidth);

    private int HeaderHeight => Theme.Sy(56);

    private int FooterHeight => Theme.Sy(64);

    private int RowHeightPx => Theme.Sy(RowHeight);

    // ---------------------------------------------------------------- 滚动

    private int ViewportHeight => Math.Max(1, Height - HeaderHeight - FooterHeight);

    private void ClampScroll()
    {
        int max = Math.Max(0, _contentHeight - Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, max);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_tabs.Count == 0)
        {
            return;
        }
        _scrollOffset -= e.Delta / 3;
        ClampScroll();
        Invalidate();
        base.OnMouseWheel(e);
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // 全部在 OnPaint 里画，避免闪烁
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var full = new Rectangle(0, 0, Width, Height);
        using (var back = new SolidBrush(Theme.Background))
        {
            g.FillRectangle(back, full);
        }
        // 左侧一条分隔线，和网页区域分开
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, 0, 0, 0, Height);
        }

        DrawHeader(g);
        DrawRows(g);
        DrawFooter(g);

        // 内容超出时画一个细滚动条
        if (_contentHeight > Height)
        {
            int trackTop = HeaderHeight;
            int trackHeight = Math.Max(10, Height - HeaderHeight - FooterHeight);
            int thumbHeight = Math.Max(24,
                (int)(trackHeight * (trackHeight / (float)_contentHeight)));
            int max = Math.Max(1, _contentHeight - Height);
            int thumbTop = trackTop +
                (int)((trackHeight - thumbHeight) * (_scrollOffset / (float)max));
            Rectangle thumb = new(Width - Theme.Sx(5), thumbTop,
                Theme.Sx(3), thumbHeight);
            Theme.FillRounded(g, thumb, Theme.Sx(2), Theme.Border);
        }
    }

    private void DrawHeader(Graphics g)
    {
        int pad = Theme.Sx(14);
        int h = HeaderHeight;

        TextRenderer.DrawText(g, $"标签 · {_tabs.Count}", Theme.UiFontLarge, 
            new Rectangle(pad, 0, Width - pad * 2, h), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // 右上角两个图标按钮：新建标签 / 关闭全部
        int buttonSize = Theme.Sy(30);
        int y = (h - buttonSize) / 2;
        int x = Width - pad - buttonSize;

        _closeAllRect = new Rectangle(x, y, buttonSize, buttonSize);
        DrawIconButton(g, _closeAllRect, "\u2715", _hoverRow == -2);

        x -= buttonSize + Theme.Sx(4);
        _newTabRect = new Rectangle(x, y, buttonSize, buttonSize);
        DrawIconButton(g, _newTabRect, "\uFF0B", _hoverRow == -1);

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, h - 1, Width, h - 1);
    }

    private void DrawIconButton(Graphics g, Rectangle rect, string glyph, bool hover)
    {
        if (hover)
        {
            Theme.FillRounded(g, rect, Theme.RadiusSmall, Theme.AccentSoft);
        }
        TextRenderer.DrawText(g, glyph, Theme.IconFontSmall, rect,
            hover ? Theme.Text : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    private void DrawRows(Graphics g)
    {
        _rows.Clear();

        int rowHeight = RowHeightPx;
        int y = HeaderHeight - _scrollOffset;
        int pad = Theme.Sx(10);

        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle row = new(pad, y, Width - pad * 2, rowHeight - Theme.Sy(4));
            _rows.Add(new RowHit(row, i));

            // 超出可视区域的行不画（但命中区仍记录，滚回来就能点）
            bool visible = row.Bottom > HeaderHeight && row.Top < Height - FooterHeight;
            if (visible)
            {
                DrawRow(g, row, _tabs[i], i == _activeIndex, _hoverRow == i);
            }

            y += rowHeight;
        }
    }

    private void DrawRow(Graphics g, Rectangle row, BrowserTab tab, bool active, bool hover)
    {
        // 底色：当前标签用强调色柔光，悬停用浅灰
        Color fill = active ? Theme.AccentSoft : hover ? Theme.SurfaceHover : Theme.Background;
        if (active || hover)
        {
            Theme.FillRounded(g, row, Theme.Radius, fill);
        }

        // 档位色点：绿=渲染中，灰=已休眠（黄=已挂起）
        Color dot = tab.Life switch
        {
            TabLife.Live => Color.FromArgb(76, 187, 118),
            TabLife.Suspended => Color.FromArgb(231, 176, 60),
            _ => Theme.Dark ? Color.FromArgb(120, 126, 138) : Color.FromArgb(178, 183, 192),
        };
        int dotSize = Theme.Sx(7);
        g.FillEllipse(new SolidBrush(dot),
            row.Left + Theme.Sx(11), row.Top + row.Height / 2 - dotSize / 2, dotSize, dotSize);

        int left = row.Left + Theme.Sx(26);
        int textWidth = row.Width - Theme.Sx(26) - Theme.Sx(30);

        Rectangle titleRect = new(left, row.Top + Theme.Sy(7), textWidth, Theme.UiFont.Height + 2);
        TextRenderer.DrawText(g, UrlUtils.Ellipsis(tab.DisplayTitle, 40),
            active ? Theme.UiFontBold : Theme.UiFont, titleRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        string sub = tab.IsHomePage
            ? LifeText(tab.Life) + " · 内置首页"
            : LifeText(tab.Life) + " · " + (UrlUtils.HostOf(tab.Url) is { Length: > 0 } host
                ? host
                : UrlUtils.Ellipsis(tab.Url, 30));
        Rectangle subRect = new(left, titleRect.Bottom + Theme.Sy(1), textWidth,
            Theme.UiFontSmall.Height + 2);
        TextRenderer.DrawText(g, sub, Theme.UiFontSmall, subRect, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        // 关闭按钮
        Rectangle closeRect = new(row.Right - Theme.Sx(28),
            row.Top + row.Height / 2 - Theme.Sy(11), Theme.Sx(22), Theme.Sy(22));
        if (hover)
        {
            Theme.FillRounded(g, closeRect, Theme.RadiusSmall, Theme.Surface);
        }
        TextRenderer.DrawText(g, "\u00D7", Theme.IconFontSmall, closeRect,
            hover ? Theme.Text : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    private void DrawFooter(Graphics g)
    {
        int top = Height - FooterHeight;
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, 0, top, Width, top);
        }

        int pad = Theme.Sx(14);
        string line1 = $"渲染 {_hotCount} / 休眠 {_coldCount} · 上限 {_maxLive}";
        TextRenderer.DrawText(g, line1, Theme.UiFontSmall,
            new Rectangle(pad, top + Theme.Sy(8), Width - pad * 2, Theme.UiFontSmall.Height + 2),
            Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, UrlUtils.Ellipsis(_memoryText, 46), Theme.UiFontSmall,
            new Rectangle(pad, top + Theme.Sy(8) + Theme.UiFontSmall.Height + Theme.Sy(3),
                Width - pad * 2, Theme.UiFontSmall.Height + 2),
            Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private static string LifeText(TabLife life) => life switch
    {
        TabLife.Live => "渲染中",
        TabLife.Suspended => "已挂起",
        _ => "已休眠",
    };

    // ---------------------------------------------------------------- 交互

    private int HitTest(Point p)
    {
        if (_newTabRect.Contains(p))
        {
            return -1;
        }
        if (_closeAllRect.Contains(p))
        {
            return -2;
        }
        foreach (RowHit row in _rows)
        {
            if (row.Bounds.Contains(p))
            {
                return row.Index;
            }
        }
        return -3;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hoverRow)
        {
            _hoverRow = hit;
            Invalidate();
        }
        // 悬停在关闭按钮上时给个手型光标
        bool onClose = false;
        foreach (RowHit row in _rows)
        {
            if (row.Bounds.Contains(e.Location) &&
                e.X > row.Bounds.Right - Theme.Sx(32))
            {
                onClose = true;
                break;
            }
        }
        Cursor = onClose || hit is -1 or -2 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverRow = -3;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_newTabRect.Contains(e.Location))
        {
            NewTab?.Invoke();
            return;
        }
        if (_closeAllRect.Contains(e.Location))
        {
            CloseAll?.Invoke();
            return;
        }

        foreach (RowHit row in _rows)
        {
            if (!row.Bounds.Contains(e.Location))
            {
                continue;
            }
            // 点在行右侧的关闭区域就关标签，否则切过去
            if (e.X > row.Bounds.Right - Theme.Sx(32))
            {
                CloseTab?.Invoke(row.Index);
            }
            else
            {
                ActivateTab?.Invoke(row.Index);
            }
            return;
        }

        base.OnMouseDown(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        ClampScroll();
        base.OnSizeChanged(e);
    }

    private readonly record struct RowHit(Rectangle Bounds, int Index);
}
