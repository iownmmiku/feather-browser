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

    public PopupMenu(IEnumerable<MenuEntry> items, Font uiFont, Font smallFont)
    {
        _items = items.ToList();
        _font = uiFont ?? Theme.UiFont;
        _smallFont = smallFont ?? Theme.UiFontSmall;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Font = _font;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        KeyPreview = true;
        DoubleBuffered = true;

        _rowHeight = _font.Height + Theme.Sy(14);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = _rowHeight,
            IntegralHeight = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = _font,
        };
        _list.DrawItem += OnDrawItem;
        _list.Click += (_, _) => Invoke();
        _list.KeyDown += OnListKeyDown;
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

        // 宽度按最长文字估算，高度按条目数，都限制在屏幕内
        int width = Theme.Sx(268);
        foreach (MenuEntry item in _items)
        {
            int textWidth = TextRenderer.MeasureText(item.Text, _font).Width
                            + TextRenderer.MeasureText(item.Shortcut, _smallFont).Width;
            width = Math.Max(width, textWidth + Theme.Sx(72));
        }
        Width = Math.Min(width, Theme.Sx(400));
        Height = Math.Min(_list.Items.Count, 22) * _rowHeight + Theme.Sy(12);

        Deactivate += (_, _) => Close();
    }

    /// <summary>在指定控件的下方或上方弹出。</summary>
    public void ShowAt(Control anchor)
    {
        // 圆角外观：窗口整体裁成一个圆角矩形
        using (GraphicsPath path = Theme.RoundedRect(
                   new Rectangle(0, 0, Width, Height), Theme.Radius))
        {
            Region = new Region(path);
        }
        Padding = new Padding(Theme.Sx(6), Theme.Sy(6), Theme.Sx(6), Theme.Sy(6));

        Point screen = anchor.PointToScreen(new Point(0, anchor.Height));
        Rectangle wa = Screen.FromControl(anchor).WorkingArea;

        int x = Math.Min(screen.X, wa.Right - Width - Theme.Sx(6));
        int y = screen.Y + Theme.Sy(4);
        if (y + Height > wa.Bottom)
        {
            y = anchor.PointToScreen(Point.Empty).Y - Height - Theme.Sy(4);
        }
        x = Math.Max(wa.Left + Theme.Sx(6), x);
        y = Math.Max(wa.Top + Theme.Sx(6), y);

        Location = new Point(x, y);
        Show();
        Activate();
        _list.Focus();
    }

    private void Invoke()
    {
        if (_list.SelectedItem is MenuEntry entry && entry.Action != null)
        {
            Close();
            // 延后执行，保证菜单已经关掉，动作里弹出的对话框不会被抢焦点
            BeginInvoke(entry.Action);
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
                if (_list.SelectedIndex < _list.Items.Count - 1)
                {
                    _list.SelectedIndex++;
                    SkipSeparator(1);
                }
                e.Handled = true;
                break;
            case Keys.Up:
                if (_list.SelectedIndex > 0)
                {
                    _list.SelectedIndex--;
                    SkipSeparator(-1);
                }
                e.Handled = true;
                break;
        }
    }

    private void SkipSeparator(int direction)
    {
        for (int guard = 0; guard < 4; guard++)
        {
            if (_list.SelectedItem is MenuEntry entry && entry.Text == "-")
            {
                int next = _list.SelectedIndex + direction;
                if (next < 0 || next >= _list.Items.Count)
                {
                    _list.SelectedIndex -= direction;
                    return;
                }
                _list.SelectedIndex = next;
                continue;
            }
            return;
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

        bool hover = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        // 悬停：整行圆角底色，而不是直角高亮
        if (hover)
        {
            Rectangle row = new(e.Bounds.Left + Theme.Sx(4), e.Bounds.Top + Theme.Sy(1),
                e.Bounds.Width - Theme.Sx(8), e.Bounds.Height - Theme.Sy(2));
            Theme.FillRounded(g, row, Theme.Radius, Theme.AccentSoft);
        }

        Rectangle textRect = new(e.Bounds.Left + Theme.Sx(16), e.Bounds.Top,
            e.Bounds.Width - Theme.Sx(126), e.Bounds.Height);
        TextRenderer.DrawText(g, entry.Text, _font, textRect,
            hover ? Theme.Text : Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (!string.IsNullOrEmpty(entry.Value))
        {
            Rectangle valueRect = new(e.Bounds.Right - Theme.Sx(110), e.Bounds.Top,
                Theme.Sx(94), e.Bounds.Height);
            TextRenderer.DrawText(g, entry.Value, _smallFont, valueRect, Theme.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (!string.IsNullOrEmpty(entry.Shortcut))
        {
            Rectangle shortcutRect = new(e.Bounds.Right - Theme.Sx(124), e.Bounds.Top,
                Theme.Sx(108), e.Bounds.Height);
            TextRenderer.DrawText(g, entry.Shortcut, _smallFont, shortcutRect, Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
