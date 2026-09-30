using System.Drawing.Drawing2D;

namespace FeatherBrowser.UI;

/// <summary>
/// 支持深色主题与圆角外观的单行输入框。
///
/// <p>原生 TextBox 的 FixedSingle 边框在深色下是一圈亮色，很突兀；这里把边框设为无，
/// 由容器自己画圆角底与描边，与地址栏的处理方式保持一致。
/// </summary>
internal sealed class ThemedTextBox : Panel
{
    public TextBox Inner { get; }

    public ThemedTextBox()
    {
        BackColor = Theme.Surface;
        Padding = new Padding(Theme.Sx(10), 0, Theme.Sx(10), 0);
        Height = Theme.Sy(34);

        Inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
        };
        // 垂直居中：用 Dock 撑满再把高度收窄
        Inner.Dock = DockStyle.Fill;
        Inner.AutoSize = false;
        Controls.Add(Inner);
    }

    /// <summary>兼容 TextBox 的常用属性，调用方不必关心内部结构。</summary>
    public string TextValue
    {
        get => Inner.Text;
        set => Inner.Text = value;
    }

    public string PlaceholderText
    {
        get => Inner.PlaceholderText;
        set => Inner.PlaceholderText = value;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        // 让内层文本框在自己高度里垂直居中
        int height = Inner.Font.Height + Theme.Sy(6);
        int top = Math.Max(0, (ClientSize.Height - height) / 2);
        Inner.SetBounds(Padding.Left, top,
            Math.Max(10, ClientSize.Width - Padding.Horizontal), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRounded(e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusSmall, Theme.Surface);
        Theme.DrawRounded(e.Graphics,
            new Rectangle(0, 0, Width, Height), Theme.RadiusSmall, Theme.Border);
        base.OnPaint(e);
    }
}

/// <summary>
/// 支持深色主题的下拉框。
///
/// <p>原生 ComboBox 在深色下始终是白底：它的下拉列表由系统绘制，设 BackColor 无效。
/// 这里改成 OwnerDrawFixed，自己画选中项与列表项，并给下拉列表一个主题色的边框。
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ItemHeight = Theme.Sy(24);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        // 编辑区（下拉框收起时显示的那一格）用输入框底色，列表项用面板底色
        bool isEditPortion = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
        Color back = isEditPortion
            ? Theme.Surface
            : selected ? Theme.AccentSoft : Theme.Background;

        using (var brush = new SolidBrush(back))
        {
            g.FillRectangle(brush, e.Bounds);
        }

        if (e.Index >= 0 && e.Index < Items.Count)
        {
            string text = Items[e.Index]?.ToString() ?? "";
            Rectangle textRect = new(e.Bounds.Left + Theme.Sx(8), e.Bounds.Top,
                e.Bounds.Width - Theme.Sx(12), e.Bounds.Height);
            TextRenderer.DrawText(g, text, Font ?? Theme.UiFont, textRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 补一圈主题色描边，替代系统默认的白色边框
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawRounded(e.Graphics,
            new Rectangle(0, 0, Width, Height), Theme.RadiusSmall, Theme.Border);
    }
}

/// <summary>
/// 支持深色主题的数字输入框。
///
/// <p>NumericUpDown 由「一个 TextBox + 两个按钮」组合而成，直接设 BackColor 只会影响
/// 边框区域；必须把内嵌的 TextBox 一起改色，否则深色下中间还是白的。
/// </summary>
internal sealed class ThemedNumericUpDown : NumericUpDown
{
    public ThemedNumericUpDown()
    {
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyToInnerTextBox();
    }

    /// <summary>主题切换后重新上色。</summary>
    public void RefreshTheme()
    {
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ApplyToInnerTextBox();
        Invalidate(true);
    }

    private void ApplyToInnerTextBox()
    {
        foreach (Control child in Controls)
        {
            if (child is TextBox inner)
            {
                inner.BackColor = Theme.Surface;
                inner.ForeColor = Theme.Text;
                inner.BorderStyle = BorderStyle.None;
            }
        }
    }
}

