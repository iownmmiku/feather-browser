using System.Drawing.Drawing2D;
using FeatherBrowser.Core;

namespace FeatherBrowser.UI;

/// <summary>
/// 标签列表弹出面板。
///
/// <p>用 OwnerDrawFixed 的 ListBox 承载全部标签：无论开 3 个还是 30 个标签，
/// 都只有一块绘制区域、一组字体对象，列表项本身不创建任何控件。
/// 每项左侧用色点标出该标签所处的档位（渲染中 / 已挂起 / 已休眠），
/// 让「省内存」这件事对用户是可见的。
///
/// <p>窗口用圆角 Region 裁形，当前标签与悬停项画圆角卡片底色。
/// </summary>
internal sealed class TabListPopup : Form
{
    private readonly ListBox _list;
    private readonly Func<IReadOnlyList<BrowserTab>> _provider;
    private readonly Func<int> _activeProvider;
    private readonly Action<int> _onActivate;
    private readonly Action<int> _onClose;
    private readonly Font _font;
    private readonly Font _boldFont;
    private readonly Font _smallFont;
    private readonly int _rowHeight;

    public TabListPopup(Func<IReadOnlyList<BrowserTab>> provider,
        Func<int> activeProvider,
        Action<int> onActivate,
        Action<int> onClose,
        Font uiFont,
        Font boldFont,
        Font smallFont)
    {
        _provider = provider;
        _activeProvider = activeProvider;
        _onActivate = onActivate;
        _onClose = onClose;
        _font = uiFont ?? Theme.UiFont;
        _boldFont = boldFont ?? Theme.UiFontBold;
        _smallFont = smallFont ?? Theme.UiFontSmall;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = _font;
        KeyPreview = true;
        DoubleBuffered = true;

        _rowHeight = _font.Height + _smallFont.Height + Theme.Sy(18);

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
        _list.MouseUp += OnMouseUp;
        Controls.Add(_list);

        Deactivate += (_, _) => Close();
    }

    public void RefreshItems()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (BrowserTab tab in _provider())
        {
            _list.Items.Add(tab);
        }
        _list.SelectedIndex = _activeProvider();
        _list.EndUpdate();

        // 高度按标签数自适应，最多 8 行，超出滚动
        int rows = Math.Min(Math.Max(_list.Items.Count, 1), 8);
        Height = rows * _rowHeight + Theme.Sy(14);
        ApplyShape();
    }

    /// <summary>把窗口裁成圆角矩形。</summary>
    private void ApplyShape()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }
        using GraphicsPath path = Theme.RoundedRect(
            new Rectangle(0, 0, Width, Height), Theme.Radius);
        Region = new Region(path);
    }

    public void ShowAt(Control anchor)
    {
        Width = Theme.Sx(420);
        RefreshItems();
        Padding = new Padding(Theme.Sx(6), Theme.Sy(6), Theme.Sx(6), Theme.Sy(6));
        ApplyShape();

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

    private void OnMouseUp(object sender, MouseEventArgs e)
    {
        int index = _list.IndexFromPoint(e.Location);
        if (index < 0 || index >= _list.Items.Count)
        {
            return;
        }

        if (e.Button == MouseButtons.Middle)
        {
            _onClose(index);
            RefreshItems();
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // 点在右侧的关闭区域就关闭，否则切换过去
        Rectangle bounds = _list.GetItemRectangle(index);
        if (e.X > bounds.Right - Theme.Sx(42))
        {
            _onClose(index);
            if (_list.Items.Count == 0)
            {
                Close();
            }
            else
            {
                RefreshItems();
            }
            return;
        }

        Close();
        _onActivate(index);
    }

    private void OnDrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }
        if (_list.Items[e.Index] is not BrowserTab tab)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var back = new SolidBrush(Theme.Background))
        {
            g.FillRectangle(back, e.Bounds);
        }

        bool hover = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        int active = _activeProvider();
        bool isActive = e.Index == active;

        // 当前标签 / 悬停：整块圆角卡片，而不是直角高亮
        Rectangle card = new(e.Bounds.Left + Theme.Sx(4), e.Bounds.Top + Theme.Sy(2),
            e.Bounds.Width - Theme.Sx(10), e.Bounds.Height - Theme.Sy(4));
        if (isActive)
        {
            Theme.FillRounded(g, card, Theme.Radius, Theme.AccentSoft);
        }
        else if (hover)
        {
            Theme.FillRounded(g, card, Theme.Radius, Theme.SurfaceHover);
        }

        // 档位色点：绿=渲染中，黄=已挂起，灰=已休眠
        Color dot = tab.Life switch
        {
            TabLife.Live => Color.FromArgb(76, 187, 118),
            TabLife.Suspended => Color.FromArgb(231, 176, 60),
            _ => Theme.Dark ? Color.FromArgb(120, 126, 138) : Color.FromArgb(178, 183, 192),
        };
        int dotSize = Theme.Sx(8);
        using (var dotBrush = new SolidBrush(dot))
        {
            g.FillEllipse(dotBrush, card.Left + Theme.Sx(12),
                card.Top + card.Height / 2 - dotSize / 2, dotSize, dotSize);
        }

        int left = card.Left + Theme.Sx(30);
        int textWidth = card.Width - Theme.Sx(72);

        Rectangle titleRect = new(left, card.Top + Theme.Sy(7), textWidth, _font.Height + 2);
        TextRenderer.DrawText(g, Core.UrlUtils.Ellipsis(tab.DisplayTitle, 40),
            isActive ? _boldFont : _font,
            titleRect, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        string sub = tab.IsHomePage
            ? LifeText(tab.Life) + " · 内置首页"
            : LifeText(tab.Life) + " · " + Core.UrlUtils.Ellipsis(tab.Url, 48);
        Rectangle subRect = new(left, titleRect.Bottom + Theme.Sy(2), textWidth,
            _smallFont.Height + 2);
        TextRenderer.DrawText(g, sub, _smallFont, subRect, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        // 关闭按钮位置画一个 ×
        Rectangle closeRect = new(card.Right - Theme.Sx(34), card.Top,
            Theme.Sx(30), card.Height);
        TextRenderer.DrawText(g, "\u00D7", Theme.IconFont, closeRect,
            hover ? Theme.Text : Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    private static string LifeText(TabLife life) => life switch
    {
        TabLife.Live => "渲染中",
        TabLife.Suspended => "已挂起",
        _ => "已休眠",
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawRounded(e.Graphics,
            new Rectangle(0, 0, Width, Height), Theme.Radius, Theme.Border);
    }
}
