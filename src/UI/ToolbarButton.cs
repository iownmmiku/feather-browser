using System.Drawing.Drawing2D;

namespace FeatherBrowser.UI;

/// <summary>
/// 工具栏按钮：自绘的圆角图标按钮。
///
/// <p>不用 ToolStrip / Button：前者会为每个按钮创建一整套渲染器与工具提示对象，
/// 后者带有系统主题的直角边框与焦点框。这里一个 Control 只画一个字形，
/// 圆角半径取自 <see cref="Theme.Radius"/>，与地址栏保持一致。
///
/// <p>尺寸与字形大小都走 <see cref="Theme.Sx"/> / <see cref="Theme.Pt"/>，
/// 因此 DPI 缩放和用户调大的界面倍率都能生效。
/// </summary>
internal sealed class ToolbarButton : Control
{
    private bool _hover;
    private bool _pressed;
    private float _glyphPoints = 13f;
    private Font _derivedFont;

    public string Glyph { get; set; } = "\u2022";

    /// <summary>处于激活状态（例如当前面板已打开），底色会用强调色。</summary>
    public bool Active { get; set; }

    public bool ShowBackground { get; set; } = true;

    /// <summary>右上角的数字徽标，0 表示不显示（用于标签数量）。</summary>
    public int Badge { get; set; }

    /// <summary>悬停时是否加一层柔和的底色。</summary>
    public bool HoverHighlight { get; set; } = true;

    public EventHandler ClickAction { get; set; }

    public ToolbarButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(Theme.Sx(40), Theme.Sy(36));
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    /// <summary>工具栏上的图标按钮。</summary>
    public ToolbarButton AsToolbarIcon() => AsIcon(40, 36, 13f);

    /// <summary>底部功能条的图标按钮（更宽，便于点按）。</summary>
    public ToolbarButton AsBottomIcon() => AsIcon(52, 46, 14f);

    /// <summary>查找条上的小图标按钮。</summary>
    public ToolbarButton AsSmallIcon() => AsIcon(36, 32, 12f);

    private ToolbarButton AsIcon(float width, float height, float glyphPoints)
    {
        Size = new Size(Theme.Sx(width), Theme.Sy(height));
        _glyphPoints = glyphPoints;
        _derivedFont?.Dispose();
        _derivedFont = null;
        return this;
    }

    /// <summary>
    /// 取当前应使用的字形字体。基准字体从 <see cref="Theme"/> 拿
    /// （DPI 或倍率变化时 Theme 会重建它），只有确实需要更大字号时才派生一个并缓存。
    /// </summary>
    private Font CurrentFont()
    {
        Font basis = _glyphPoints >= 13.5f ? Theme.IconFont : Theme.IconFontSmall;
        float target = Theme.Pt(_glyphPoints);
        try
        {
            if (Math.Abs(basis.Size - target) < 0.6f)
            {
                return basis;
            }
            if (_derivedFont == null || Math.Abs(_derivedFont.Size - target) > 0.6f)
            {
                _derivedFont?.Dispose();
                _derivedFont = new Font(basis.FontFamily, target, FontStyle.Regular,
                    GraphicsUnit.Point);
            }
            return _derivedFont;
        }
        catch
        {
            return basis;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            ClickAction?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var back = new SolidBrush(Parent?.BackColor ?? Theme.Toolbar))
        {
            g.FillRectangle(back, ClientRectangle);
        }

        // 圆角底色：激活 > 按下 > 悬停
        if (ShowBackground && (Active || _pressed || (_hover && HoverHighlight)))
        {
            Color fill = Active
                ? Theme.Accent
                : _pressed
                    ? Theme.SurfaceHover
                    : Theme.AccentSoft;
            Rectangle rect = new(1, 1, Width - 3, Height - 3);
            Theme.FillRounded(g, rect, Theme.Radius, fill);
        }

        Color color;
        if (Active)
        {
            color = Color.White;
        }
        else if (!Enabled)
        {
            color = Theme.Border;
        }
        else
        {
            color = _hover ? Theme.Text : Theme.TextDim;
        }

        TextRenderer.DrawText(g, Glyph, CurrentFont(), ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);

        if (Badge > 0)
        {
            DrawBadge(g);
        }
    }

    /// <summary>右上角的圆角数字徽标。</summary>
    private void DrawBadge(Graphics g)
    {
        string text = Badge > 99 ? "99+" : Badge.ToString();
        Font font = Theme.IconFontSmall;
        Size textSize = TextRenderer.MeasureText(text, font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);

        int padX = Theme.Sx(5);
        int height = Math.Max(Theme.Sy(15), textSize.Height);
        int width = Math.Max(height, textSize.Width + padX * 2);

        Rectangle rect = new(Width - width - Theme.Sx(2), Theme.Sy(3), width, height);

        using (var brush = new SolidBrush(Theme.Accent))
        using (GraphicsPath path = Theme.RoundedRect(rect, Theme.RadiusSmall))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, text, font, rect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _derivedFont?.Dispose();
            _derivedFont = null;
        }
        base.Dispose(disposing);
    }
}
