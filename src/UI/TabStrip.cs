using System.Drawing.Drawing2D;
using FeatherBrowser.Core;
using FeatherBrowser.Services;

namespace FeatherBrowser.UI;

/// <summary>
/// 窗口顶部的横向标签栏（像桌面浏览器那样）。
///
/// <para>与 <see cref="TabsSidebar"/> 的分工：这个是主界面的一部分，横排在窗口顶部，
/// 用来快速切换与拖动排序；侧边栏是完整的标签列表，带内存档位信息，按 Ctrl+Shift+E 打开。</para>
///
/// <para>命中区在 <see cref="Update"/> 里统一算好，**不在绘制里算**。
/// 侧边栏早期就是在绘制里填命中区，绘制被跳过时点击全部落空，
/// 表现为「点了完全没反应」，排查了很久。这里不再重复那个错误。</para>
/// </summary>
internal sealed class TabStrip : Control
{
    private const int TabMaxWidth = 220;
    private const int TabMinWidth = 92;
    private const int TabHeight = 34;
    private const int CloseSize = 18;

    /// <summary>每个标签的命中信息。</summary>
    private readonly List<TabHit> _hits = new();

    private List<BrowserTab> _tabs = new();
    private int _activeIndex = -1;
    private int _scrollOffset;
    private int _contentWidth;
    private int _hoverIndex = -1;
    private bool _hoverClose;
    private bool _hoverNew;
    private int _hoverNewTab = -1;

    /// <summary>拖动排序状态：按下后移动超过阈值才进入拖动，避免和单击冲突。</summary>
    private int _dragIndex = -1;
    private Point _dragOrigin;
    private bool _dragging;
    private int _dragTargetIndex = -1;

    public Action<int> ActivateTab { get; set; }

    public Action<int> CloseTab { get; set; }

    public Action NewTab { get; set; }

    /// <summary>请求标签右键菜单（参数：标签下标, 屏幕坐标）。</summary>
    public Action<int, Point> ShowTabMenu { get; set; }

    /// <summary>拖动排序完成后通知（原位置, 新位置）。</summary>
    public Action<int, int> MoveTab { get; set; }

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Font = Theme.UiFont;
    }

    /// <summary>鼠标捕获被系统收走时（例如弹出别的窗口）取消拖动，避免状态卡住。</summary>
    protected override void OnLostFocus(EventArgs e)
    {
        EndDrag(commit: false);
        base.OnLostFocus(e);
    }

    private int TabHeightPx => Theme.Sy(TabHeight);

    private int TabMaxWidthPx => Theme.Sx(TabMaxWidth);

    private int TabMinWidthPx => Theme.Sx(TabMinWidth);

    /// <summary>「新建标签」按钮占的宽度。</summary>
    private int NewTabWidth => Theme.Sx(30);

    /// <summary>把标签状态灌进来并重算命中区。</summary>
    public void Update(IReadOnlyList<BrowserTab> tabs, int activeIndex)
    {
        _tabs = tabs != null ? new List<BrowserTab>(tabs) : new List<BrowserTab>();
        _activeIndex = activeIndex;
        RebuildLayout();
        Invalidate();
    }

    /// <summary>
    /// 计算每个标签的矩形与滚动范围。
    ///
    /// <para>宽度策略：先按上限排，放不下就整体压缩到下限，再放不下才启用滚动。
    /// 这样标签少时好看、标签多时不至于每个都窄到看不清字。</para>
    /// </summary>
    private void RebuildLayout()
    {
        _hits.Clear();

        int count = _tabs.Count;
        if (count == 0)
        {
            _contentWidth = 0;
            return;
        }

        int available = Math.Max(0, Width - NewTabWidth - Theme.Sx(6));
        int width = TabMaxWidthPx;
        if (count * width > available)
        {
            width = Math.Max(TabMinWidthPx, available / count);
        }
        _contentWidth = count * width + NewTabWidth + Theme.Sx(6);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _contentWidth - Width));

        int x = -_scrollOffset + Theme.Sx(3);
        int y = Math.Max(0, (Height - TabHeightPx) / 2);
        for (int i = 0; i < count; i++)
        {
            var bounds = new Rectangle(x, y, width - Theme.Sx(4), TabHeightPx);
            var close = new Rectangle(
                bounds.Right - Theme.Sx(CloseSize) - Theme.Sx(8),
                bounds.Top + (bounds.Height - Theme.Sx(CloseSize)) / 2,
                Theme.Sx(CloseSize), Theme.Sx(CloseSize));
            _hits.Add(new TabHit(bounds, close, i));
            x += width;
        }
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // 全部在 OnPaint 里画
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var back = new SolidBrush(Theme.Background))
        {
            g.FillRectangle(back, new Rectangle(0, 0, Width, Height));
        }

        foreach (TabHit hit in _hits)
        {
            if (hit.Bounds.Right < 0 || hit.Bounds.Left > Width)
            {
                continue;   // 滚出可视区域
            }
            if (hit.Index < _tabs.Count)
            {
                DrawTab(g, hit, _tabs[hit.Index], hit.Index == _activeIndex);
            }
        }

        DrawNewTabButton(g);

        // 拖动排序时画一条指示线，告诉用户会插到哪里
        if (_dragging && _dragTargetIndex >= 0)
        {
            int x = InsertionX(_dragTargetIndex);
            using var pen = new Pen(Theme.Accent, Math.Max(2f, Theme.Sx(2)));
            g.DrawLine(pen, x, Theme.Sy(4), x, Height - Theme.Sy(4));
        }

        if (_contentWidth > Width)
        {
            DrawOverflowHints(g);
        }
    }

    private void DrawTab(Graphics g, TabHit hit, BrowserTab tab, bool active)
    {
        bool hover = hit.Index == _hoverIndex && !_dragging;

        // 当前标签用面板底色（视觉上和下方内容连成一片），其余用略暗的底色
        Color fill = active
            ? Theme.Surface
            : hover ? Theme.SurfaceHover : Theme.Background;

        Theme.FillRounded(g, hit.Bounds, Theme.Radius, fill);

        // 当前标签加一条底部强调线，比整块高亮更接近桌面浏览器的观感
        if (active)
        {
            int inset = Theme.Sx(10);
            var line = new Rectangle(hit.Bounds.Left + inset,
                hit.Bounds.Bottom - Theme.Sy(3),
                Math.Max(1, hit.Bounds.Width - inset * 2), Math.Max(1, Theme.Sy(2)));
            Theme.FillRounded(g, line, Theme.Sx(1), Theme.Accent);
        }

        int left = hit.Bounds.Left + Theme.Sx(10);
        int rightLimit = hit.Close.Left - Theme.Sx(4);

        // 档位色点：绿=渲染中，黄=已挂起，灰=已休眠。
        Color dot = tab.Life switch
        {
            TabLife.Live => Color.FromArgb(76, 187, 118),
            TabLife.Suspended => Color.FromArgb(231, 176, 60),
            _ => Theme.Dark ? Color.FromArgb(120, 126, 138) : Color.FromArgb(178, 183, 192),
        };
        int dotSize = Theme.Sx(7);
        g.FillEllipse(new SolidBrush(dot), left - Theme.Sx(2),
            hit.Bounds.Top + hit.Bounds.Height / 2 - dotSize / 2, dotSize, dotSize);

        left += Theme.Sx(10);
        var textRect = new Rectangle(left, hit.Bounds.Top, Math.Max(1, rightLimit - left),
            hit.Bounds.Height);
        TextRenderer.DrawText(g, UrlUtils.Ellipsis(tab.DisplayTitle, 60),
            active ? Theme.UiFontBold : Theme.UiFont, textRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        // 关闭按钮：当前标签或鼠标悬停时显示
        if (active || hover)
        {
            if (_hoverClose && hover)
            {
                Theme.FillRounded(g, hit.Close, Theme.RadiusSmall, Theme.SurfaceHover);
            }
            TextRenderer.DrawText(g, "\u00D7", Theme.IconFontSmall, hit.Close,
                hover ? Theme.Text : Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);
        }
    }

    private void DrawNewTabButton(Graphics g)
    {
        int y = Math.Max(0, (Height - TabHeightPx) / 2);
        int x = _hits.Count > 0
            ? _hits[^1].Bounds.Right + Theme.Sx(6)
            : Theme.Sx(4);
        if (x > Width - NewTabWidth)
        {
            return;   // 放不下就不画（标签占满了）
        }

        var rect = new Rectangle(x, y + (TabHeightPx - Theme.Sy(26)) / 2,
            Theme.Sy(26), Theme.Sy(26));
        if (_hoverNew)
        {
            Theme.FillRounded(g, rect, Theme.RadiusSmall, Theme.SurfaceHover);
        }
        TextRenderer.DrawText(g, "\uFF0B", Theme.IconFontSmall, rect,
            _hoverNew ? Theme.Text : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
        _newTabRect = rect;
    }

    private Rectangle _newTabRect;

    /// <summary>内容超宽时，在两端画一点渐变提示还能滚动。</summary>
    private void DrawOverflowHints(Graphics g)
    {
        int w = Theme.Sx(18);
        if (_scrollOffset > 0)
        {
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, w, Height), Theme.Background, Color.Transparent,
                LinearGradientMode.Horizontal);
            g.FillRectangle(brush, 0, 0, w, Height);
        }
        if (_scrollOffset < _contentWidth - Width)
        {
            using var brush = new LinearGradientBrush(
                new Rectangle(Width - w, 0, w, Height), Color.Transparent, Theme.Background,
                LinearGradientMode.Horizontal);
            g.FillRectangle(brush, Width - w, 0, w, Height);
        }
    }

    /// <summary>拖动时插入位置对应的 x 坐标。</summary>
    private int InsertionX(int index)
    {
        if (_hits.Count == 0)
        {
            return Theme.Sx(4);
        }
        index = Math.Clamp(index, 0, _hits.Count);
        if (index >= _hits.Count)
        {
            return _hits[^1].Bounds.Right + Theme.Sx(2);
        }
        return _hits[index].Bounds.Left + Theme.Sx(2);
    }

    // ---------------------------------------------------------------- 交互

    /// <summary>按屏幕/客户坐标判定点中了什么。返回的 index 为 -1 表示新建按钮，-2 表示空白。</summary>
    private (int Index, bool OnClose) HitTest(Point p)
    {
        if (_newTabRect.Contains(p))
        {
            return (-1, false);
        }
        foreach (TabHit hit in _hits)
        {
            if (hit.Close.Contains(p))
            {
                return (hit.Index, true);
            }
            if (hit.Bounds.Contains(p))
            {
                return (hit.Index, false);
            }
        }
        return (-2, false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        // 中键关闭标签（桌面浏览器惯例）
        if (e.Button == MouseButtons.Middle)
        {
            (int index, _) = HitTest(e.Location);
            if (index >= 0)
            {
                CloseTab?.Invoke(index);
            }
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            (int index, _) = HitTest(e.Location);
            if (index >= 0)
            {
                ShowTabMenu?.Invoke(index, PointToScreen(e.Location));
            }
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        (int hitIndex, bool onClose) = HitTest(e.Location);
        if (hitIndex == -1)
        {
            NewTab?.Invoke();
            return;
        }
        if (hitIndex >= 0)
        {
            if (onClose)
            {
                CloseTab?.Invoke(hitIndex);
                return;
            }

            // 先选中，再准备可能的拖动排序
            ActivateTab?.Invoke(hitIndex);
            _dragIndex = hitIndex;
            _dragOrigin = e.Location;
            _dragging = false;
            Capture = true;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragIndex >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging &&
                Math.Abs(e.X - _dragOrigin.X) > Theme.Sx(6))
            {
                _dragging = true;
            }
            if (_dragging)
            {
                _dragTargetIndex = TargetIndexFor(e.Location);
                Invalidate();
                return;
            }
        }

        (int index, bool onClose) = HitTest(e.Location);
        bool newHover = _newTabRect.Contains(e.Location);
        if (index != _hoverIndex || onClose != _hoverClose || newHover != _hoverNew)
        {
            _hoverIndex = newHover ? -1 : index;
            _hoverClose = onClose;
            _hoverNew = newHover;
            Cursor = onClose || index >= 0 || newHover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    /// <summary>鼠标位置对应应该插入到第几个位置。</summary>
    private int TargetIndexFor(Point p)
    {
        for (int i = 0; i < _hits.Count; i++)
        {
            Rectangle b = _hits[i].Bounds;
            if (p.X < b.Left + b.Width / 2)
            {
                return i;
            }
        }
        return _hits.Count;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragIndex >= 0)
        {
            int from = _dragIndex;
            int to = _dragTargetIndex;
            EndDrag(commit: _dragging && to >= 0);

            if (_dragging && to >= 0)
            {
                // 插入位置是「第 to 个之前」，移动后目标下标要相应调整
                int target = to > from ? to - 1 : to;
                if (target != from)
                {
                    MoveTab?.Invoke(from, target);
                }
            }
        }
        base.OnMouseUp(e);
    }

    private void EndDrag(bool commit)
    {
        _dragIndex = -1;
        _dragging = false;
        _dragTargetIndex = -1;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = -1;
        _hoverClose = false;
        _hoverNew = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_contentWidth <= Width)
        {
            return;
        }
        _scrollOffset -= e.Delta / 2;
        RebuildLayout();
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        RebuildLayout();
        base.OnSizeChanged(e);
    }

    /// <summary>把某个标签滚进可视区域（新建标签后调用，用户能立刻看到它）。</summary>
    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _hits.Count)
        {
            return;
        }
        Rectangle b = _hits[index].Bounds;
        if (b.Left < 0)
        {
            _scrollOffset -= -b.Left + Theme.Sx(8);
        }
        else if (b.Right > Width)
        {
            _scrollOffset += b.Right - Width + Theme.Sx(8);
        }
        RebuildLayout();
        Invalidate();
    }

    /// <summary>自检用：当前命中区描述。</summary>
    public string DescribeHit(Point p)
    {
        (int index, bool onClose) = HitTest(p);
        return index switch
        {
            -1 => $"({p.X},{p.Y}) -> 新建标签按钮",
            -2 => $"({p.X},{p.Y}) -> 空白（标签数={_hits.Count}）",
            _ => $"({p.X},{p.Y}) -> 第 {index} 个标签{(onClose ? " 的关闭按钮" : "")} {_hits[index].Bounds}",
        };
    }

    /// <summary>自检用：标签栏状态。</summary>
    public string DescribeState() =>
        $"Bounds={Bounds} Visible={Visible} 标签数={_hits.Count} " +
        $"内容宽={_contentWidth} 滚动={_scrollOffset}";

    private readonly record struct TabHit(Rectangle Bounds, Rectangle Close, int Index);
}
