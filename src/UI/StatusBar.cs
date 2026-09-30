using System.Drawing.Drawing2D;
using FeatherBrowser.Core;

namespace FeatherBrowser.UI;

/// <summary>
/// 底部状态栏：左侧显示页面状态，右侧显示内存与拦截统计。
///
/// <p>整条栏是一个自定义 Control，只做两次 TextRenderer 调用，
/// 相比 StatusStrip + 若干 ToolStripStatusLabel 少了整套渲染器与布局对象。
/// 高度按当前字体自动计算，DPI 缩放后不会把文字压扁。
/// </summary>
internal sealed class StatusBar : Control
{
    private string _left = "就绪";
    private string _right = "";

    public StatusBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Bottom;
        BackColor = Theme.Toolbar;
    }

    /// <summary>按当前字体决定合适的高度。字体变化（DPI 或倍率）后由外部再调一次。</summary>
    public void FitToFont()
    {
        Height = Theme.LineHeight(Font ?? Theme.UiFontSmall, 10f);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitToFont();
    }

    public void SetLeft(string text)
    {
        if (_left == text)
        {
            return;
        }
        _left = text ?? "";
        Invalidate();
    }

    public void SetRight(string text)
    {
        if (_right == text)
        {
            return;
        }
        _right = text ?? "";
        Invalidate();
    }

    /// <summary>吃掉背景擦除，避免自绘时闪烁。</summary>
    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // 全部在 OnPaint 里一次画完
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        using (var brush = new SolidBrush(Theme.Toolbar))
        {
            g.FillRectangle(brush, ClientRectangle);
        }
        using (var pen = new Pen(Theme.Border))
        {
            g.DrawLine(pen, 0, 0, Width, 0);
        }

        Font font = Font ?? Theme.UiFontSmall;
        int padX = Theme.Sx(14);

        Rectangle leftRect = new(padX, 0, Math.Max(10, Width - Theme.Sx(480)), Height);
        TextRenderer.DrawText(g, Core.UrlUtils.Ellipsis(_left, 80), font, leftRect, Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrEmpty(_right))
        {
            Rectangle rightRect = new(Math.Max(0, Width - Theme.Sx(470)), 0,
                Theme.Sx(456), Height);
            TextRenderer.DrawText(g, _right, font, rightRect, Theme.TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}
