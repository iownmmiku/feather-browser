using System.Drawing.Drawing2D;

namespace FeatherBrowser.UI;

/// <summary>菜单项的像素画法：只有文字 + 快捷键，没有图标对象，内存占用可以忽略。</summary>
internal sealed class MenuEntry
{
    public string Text { get; init; } = "";

    public string Shortcut { get; init; } = "";

    public string Value { get; init; } = "";

    public Action Action { get; init; }

    public bool DividerBefore { get; init; }

    public bool StartsGroup { get; init; }
}

/// <summary>
/// 下拉菜单。
///
/// <p>自己一个无边框窗体 + ListBox（OwnerDrawFixed）实现，而不是 ContextMenuStrip：
/// ContextMenuStrip 会为菜单、每个 ToolStripItem、工具提示条各建一整套对象；
/// 这里所有条目共用一个 ListBox，交给一个测量回调，条目多也不明显增长。
///
/// <p>窗口本身用圆角 Region 裁形，行高悬停时画圆角底色，整体观感与工具栏一致。
/// </summary>
internal sealed class PopupMenu : Form
{
    private readonly List<MenuEntry> _items;
    private readonly ListBox _list;
    private readonly Font _font;
    private readonly Font _smallFont;
    private readonly int _rowHeight;
    private readonly int _separatorHeight;

    /// <summary>鼠标悬停的条目下标。自绘高亮靠它，不再依赖 ListBox 的选中态。</summary>
    private int _hoverIndex = -1;

    public PopupMenu(IEnumerable<MenuEntry> items, Font uiFont, Font smallFont)
    {
        _items = items.ToList();
        _font = uiFont ?? Theme.UiFont;
        _smallFont = smallFont ?? Theme.UiFontSmall;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Font = _font;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;

        _rowHeight = _font.Height + Theme.Sy(14);
        _separatorHeight = Theme.Sy(12);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawVariable,
            ItemHeight = _rowHeight,
            IntegralHeight = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = _font,

            // 超出屏幕高度时保留原生滚动能力，滚轮和键盘都能到达最后一项。
            ScrollAlwaysVisible = false,
            HorizontalScrollbar = false,
        };
        _list.MeasureItem += (_, e) => e.ItemHeight =
            _list.Items[e.Index] is MenuEntry { Text: "-" } ? _separatorHeight : _rowHeight;
        _list.DrawItem += OnDrawItem;
        _list.KeyDown += OnListKeyDown;
        _list.MouseMove += OnListMouseMove;
        _list.MouseLeave += (_, _) =>
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                _list.Invalidate();
            }
        };
        // 用 MouseUp + IndexFromPoint 判定点中了哪一项。
        // 之前用 Click + SelectedItem 有两个坑：点已选中的项不会再次触发，
        // 点到分隔线或空白处则什么都不发生，看起来就像「点了没反应」。
        _list.MouseUp += OnListMouseUp;
        Controls.Add(_list);

        foreach (MenuEntry item in _items)
        {
            if (item.StartsGroup && _list.Items.Count > 0)
            {
                // 用一个不可选的占位条目画成分隔线
                _list.Items.Add(new MenuEntry { Text = "-" });
            }
            _list.Items.Add(item);
        }

        // 显示时再按锚点所在屏幕的工作区限制尺寸。
        int width = Theme.Sx(268);
        foreach (MenuEntry item in _items)
        {
            string trailing = string.IsNullOrEmpty(item.Value) ? item.Shortcut : item.Value;
            int textWidth = TextRenderer.MeasureText(item.Text, _font).Width
                            + TextRenderer.MeasureText(trailing, _smallFont).Width;
            width = Math.Max(width, textWidth + Theme.Sx(72));
        }
        Width = Math.Min(width, Theme.Sx(400));
        Height = _list.Items.Cast<MenuEntry>().Sum(item =>
            item.Text == "-" ? _separatorHeight : _rowHeight) + Theme.Sy(12);

        Deactivate += (_, _) => Close();
    }

    /// <summary>在指定屏幕坐标弹出（用于标签栏等非控件锚点的场景）。</summary>
    public void ShowAtScreen(Point screenPoint, Form owner)
    {
        ShowPopup(screenPoint, screenPoint, owner);
    }

    /// <summary>在指定控件的下方或上方弹出。</summary>
    public void ShowAt(Control anchor)
    {
        Point below = anchor.PointToScreen(new Point(0, anchor.Height + Theme.Sy(4)));
        Point above = anchor.PointToScreen(new Point(0, -Theme.Sy(4)));
        ShowPopup(below, above, anchor.FindForm());
    }

    internal Rectangle CalculateBounds(Point below, Point above, Rectangle workingArea)
    {
        int margin = Math.Min(Theme.Sx(6), Math.Min(workingArea.Width, workingArea.Height) / 4);
        Rectangle available = Rectangle.Inflate(workingArea, -margin, -margin);
        int width = Math.Min(Width, available.Width);
        int height = Math.Min(Height, available.Height);
        int x = Math.Clamp(below.X, available.Left, available.Right - width);
        int y = below.Y + height <= available.Bottom ? below.Y : above.Y - height;
        y = Math.Clamp(y, available.Top, available.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private void ShowPopup(Point below, Point above, Form owner)
    {
        if (owner == null || owner.IsDisposed) { Dispose(); return; }
        Bounds = CalculateBounds(below, above, Screen.FromPoint(below).WorkingArea);
        Padding = new Padding(Theme.Sx(6), Theme.Sy(6), Theme.Sx(6), Theme.Sy(6));
        using (GraphicsPath path = Theme.RoundedRect(
                   new Rectangle(0, 0, Width, Height), Theme.Radius))
        {
            Region = new Region(path);
        }
        Show(owner);
        Activate();
        _list.Focus();
    }

    /// <summary>点中哪一项就执行哪一项；分隔线与空白处不响应。</summary>
    private void OnListMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        int index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _list.Items.Count)
        {
            return;
        }
        if (_list.Items[index] is not MenuEntry entry || entry.Text == "-")
        {
            return;   // 分隔线不响应
        }
        _list.SelectedIndex = index;
        Run(entry);
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        int index = _list.IndexFromPoint(e.Location);
        if (index >= _list.Items.Count || index < 0)
        {
            index = -1;
        }
        // 分隔线不参与高亮
        if (index >= 0 && _list.Items[index] is MenuEntry sep && sep.Text == "-")
        {
            index = -1;
        }
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            _list.Invalidate();
        }
    }

    private void Invoke()
    {
        if (_list.SelectedItem is MenuEntry entry && entry.Action != null)
        {
            Run(entry);
        }
    }

    /// <summary>关掉菜单再执行动作，避免动作里弹出的对话框被菜单抢焦点。</summary>
    private void Run(MenuEntry entry)
    {
        if (entry?.Action == null)
        {
            return;
        }
        Form owner = Owner;
        Close();
        if (owner is { IsDisposed: false, IsHandleCreated: true })
        {
            // Close 会销毁菜单的句柄，后续动作必须派发到仍存活的主窗口。
            owner.BeginInvoke(() =>
            {
                if (!owner.IsDisposed) entry.Action();
            });
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                Invoke();
                e.Handled = true;
                break;
            case Keys.Escape:
                Close();
                e.Handled = true;
                break;
            case Keys.Down:
                SelectNext(1);
                e.Handled = true;
                break;
            case Keys.Up:
                SelectNext(-1);
                e.Handled = true;
                break;
            case Keys.Home:
                _list.SelectedIndex = -1;
                SelectNext(1);
                e.Handled = true;
                break;
            case Keys.End:
                _list.SelectedIndex = -1;
                SelectNext(-1);
                e.Handled = true;
                break;
        }
    }

    private void SelectNext(int direction)
    {
        int next = _list.SelectedIndex < 0
            ? (direction > 0 ? 0 : _list.Items.Count - 1)
            : _list.SelectedIndex + direction;
        while (next >= 0 && next < _list.Items.Count)
        {
            if (_list.Items[next] is MenuEntry { Text: not "-" })
            {
                _list.SelectedIndex = next;
                return;
            }
            next += direction;
        }
    }

    private void OnDrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }
        if (_list.Items[e.Index] is not MenuEntry entry)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var back = new SolidBrush(Theme.Background))
        {
            g.FillRectangle(back, e.Bounds);
        }

        if (entry.Text == "-")
        {
            using var pen = new Pen(Theme.Border);
            int y = e.Bounds.Top + e.Bounds.Height / 2;
            g.DrawLine(pen, e.Bounds.Left + Theme.Sx(10), y,
                e.Bounds.Right - Theme.Sx(10), y);
            return;
        }

        // 悬停：整行圆角底色，而不是直角高亮。
        // 以鼠标位置为准（_hoverIndex），键盘操作时回退到 ListBox 的选中态。
        bool hover = e.Index == _hoverIndex ||
                     (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        // 悬停：整行圆角底色，而不是直角高亮
        if (hover)
        {
            Rectangle row = new(e.Bounds.Left + Theme.Sx(4), e.Bounds.Top + Theme.Sy(1),
                e.Bounds.Width - Theme.Sx(8), e.Bounds.Height - Theme.Sy(2));
            Theme.FillRounded(g, row, Theme.Radius, Theme.AccentSoft);
        }

        string trailing = string.IsNullOrEmpty(entry.Value) ? entry.Shortcut : entry.Value;
        int rightWidth = string.IsNullOrEmpty(trailing) ? 0 :
            Math.Min(e.Bounds.Width / 2, TextRenderer.MeasureText(trailing, _smallFont).Width + Theme.Sx(8));
        Rectangle textRect = new(e.Bounds.Left + Theme.Sx(16), e.Bounds.Top,
            Math.Max(1, e.Bounds.Width - rightWidth - Theme.Sx(40)), e.Bounds.Height);
        TextRenderer.DrawText(g, entry.Text, _font, textRect,
            hover ? Theme.Text : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrEmpty(trailing))
        {
            Rectangle trailingRect = new(e.Bounds.Right - rightWidth - Theme.Sx(16), e.Bounds.Top,
                rightWidth, e.Bounds.Height);
            TextRenderer.DrawText(g, trailing, _smallFont, trailingRect,
                string.IsNullOrEmpty(entry.Value) ? Theme.TextDim : Theme.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawRounded(e.Graphics,
            new Rectangle(0, 0, Width, Height), Theme.Radius, Theme.Border);
    }
}
