using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace FeatherBrowser.UI;

/// <summary>界面主题模式。</summary>
internal enum ThemeMode
{
    /// <summary>跟随 Windows 的应用主题设置。</summary>
    System = 0,

    Light = 1,

    Dark = 2,
}

/// <summary>
/// 界面配色、字体与尺寸。
///
/// <p>尺寸全部走 <see cref="Sx"/> / <see cref="Sy"/>，它们把逻辑像素换算成当前显示器的
/// 物理像素：DPI 125% 的屏幕上按物理像素写死的 32px 按钮会显得偏小。
/// </p>
///
/// <p>主题支持浅色 / 深色 / 跟随系统三种模式。切换时通过 <see cref="Changed"/> 通知，
/// 由 <see cref="ApplyTo"/> 递归把新配色套到所有已打开的窗体上。
/// </summary>
internal static class Theme
{
    // ---------------------------------------------------------------- 主题模式

    /// <summary>用户选择的主题模式。</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>当前实际是否使用深色。跟随系统时由 Windows 的设置决定。</summary>
    public static bool Dark { get; private set; }

    /// <summary>主题切换后触发，界面据此重绘。</summary>
    public static event Action Changed;

    /// <summary>系统主题发生变化时触发（仅在跟随系统模式下有意义）。</summary>
    public static event Action SystemThemeChanged;

    private static bool _systemDark;

    /// <summary>读取 Windows 的「应用模式」设置（0 = 深色，1 = 浅色）。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object value = key?.GetValue("AppsUseLightTheme");
            if (value is int light)
            {
                return light == 0;
            }
        }
        catch
        {
            // 读不到注册表就当作浅色
        }
        return false;
    }

    /// <summary>设置主题模式并立即生效。</summary>
    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        _systemDark = IsSystemDark();
        bool dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => _systemDark,
        };
        if (dark == Dark && _initialized)
        {
            return;
        }
        Dark = dark;
        _initialized = true;
        RebuildFonts();
        Changed?.Invoke();
    }

    private static bool _initialized;

    /// <summary>
    /// 订阅系统主题变化。Windows 会广播 UserPreferenceChanged，
    /// 跟随系统模式下收到通知就立刻重新判断深浅。
    /// </summary>
    public static void WatchSystemTheme()
    {
        if (_watching)
        {
            return;
        }
        _watching = true;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General &&
                    e.Category != UserPreferenceCategory.VisualStyle)
                {
                    return;
                }
                bool nowDark = IsSystemDark();
                if (nowDark == _systemDark)
                {
                    return;
                }
                _systemDark = nowDark;
                SystemThemeChanged?.Invoke();
                if (Mode == ThemeMode.System)
                {
                    SetMode(ThemeMode.System);
                }
            };
        }
        catch
        {
            // 订阅失败不影响使用，只是不再跟随系统实时切换
        }
    }

    private static bool _watching;

    // ---------------------------------------------------------------- 尺寸

    /// <summary>设备像素比（= 系统 DPI / 96）。窗口创建时采集一次。</summary>
    public static float DpiScale { get; private set; } = 1f;

    /// <summary>用户可调的整体界面倍率。</summary>
    public static float Scale { get; set; } = 1.15f;

    /// <summary>把逻辑像素换算成物理像素（水平方向）。</summary>
    public static int Sx(float value) => (int)Math.Round(value * DpiScale * Scale);

    /// <summary>把逻辑像素换算成物理像素（垂直方向）。</summary>
    public static int Sy(float value) => (int)Math.Round(value * DpiScale * Scale);

    /// <summary>字号换算：返回一个按 DPI 与倍率放大后的磅值。</summary>
    public static float Pt(float points) => points * Scale;

    /// <summary>统一圆角半径。整界面只用这一个函数取半径，保证各处弧度一致。</summary>
    public static int Radius => Math.Max(4, Sx(10));

    /// <summary>小圆角（标签徽标、色点等）。</summary>
    public static int RadiusSmall => Math.Max(3, Sx(6));

    public static void CaptureDpi(Control control)
    {
        try
        {
            float dpi = control.DeviceDpi > 0 ? control.DeviceDpi : 96f;
            DpiScale = Math.Clamp(dpi / 96f, 1f, 3f);
        }
        catch
        {
            DpiScale = 1f;
        }
        RebuildFonts();
    }

    // ---------------------------------------------------------------- 配色

    // 浅色：白 + 冷灰，强调色蓝
    private static readonly Color LightBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color LightToolbar = Color.FromArgb(248, 249, 251);
    private static readonly Color LightSurface = Color.FromArgb(240, 242, 246);
    private static readonly Color LightSurfaceHover = Color.FromArgb(230, 234, 242);
    private static readonly Color LightText = Color.FromArgb(28, 32, 38);
    private static readonly Color LightTextDim = Color.FromArgb(120, 127, 140);
    private static readonly Color LightBorder = Color.FromArgb(226, 230, 237);

    // 深色：中性偏冷，避免纯黑造成的边界丢失
    private static readonly Color DarkBackground = Color.FromArgb(22, 23, 26);
    private static readonly Color DarkToolbar = Color.FromArgb(31, 32, 36);
    private static readonly Color DarkSurface = Color.FromArgb(43, 45, 50);
    private static readonly Color DarkSurfaceHover = Color.FromArgb(58, 61, 68);
    private static readonly Color DarkText = Color.FromArgb(232, 234, 238);
    private static readonly Color DarkTextDim = Color.FromArgb(150, 155, 165);
    private static readonly Color DarkBorder = Color.FromArgb(52, 55, 61);

    public static Color Background => Dark ? DarkBackground : LightBackground;

    public static Color Toolbar => Dark ? DarkToolbar : LightToolbar;

    public static Color Surface => Dark ? DarkSurface : LightSurface;

    public static Color SurfaceHover => Dark ? DarkSurfaceHover : LightSurfaceHover;

    public static Color Text => Dark ? DarkText : LightText;

    public static Color TextDim => Dark ? DarkTextDim : LightTextDim;

    public static Color Accent => Dark ? Color.FromArgb(96, 142, 255) : Color.FromArgb(74, 124, 247);

    /// <summary>强调色的柔和版本，用于标签徽标、选中底色这类大面积填充。</summary>
    public static Color AccentSoft => Dark
        ? Color.FromArgb(52, 72, 122)
        : Color.FromArgb(226, 235, 255);

    public static Color Border => Dark ? DarkBorder : LightBorder;

    public static Color Incognito => Dark ? Color.FromArgb(46, 44, 62) : Color.FromArgb(236, 236, 246);

    /// <summary>网页区域的底色。深色模式下要让新标签/加载中不闪白。</summary>
    public static Color PageBackground => Dark ? Color.FromArgb(18, 19, 22) : Color.FromArgb(255, 255, 255);

    // ---------------------------------------------------------------- 字体
    // 字体对象带 GDI 句柄，全部缓存成字段；DPI 或倍率变化时统一重建。

    public static Font UiFont { get; private set; }

    public static Font UiFontSmall { get; private set; }

    public static Font UiFontBold { get; private set; }

    public static Font UiFontLarge { get; private set; }

    public static Font UiFontTitle { get; private set; }

    public static Font IconFont { get; private set; }

    public static Font IconFontSmall { get; private set; }

    static Theme()
    {
        _systemDark = IsSystemDark();
        Dark = _systemDark;
        _initialized = true;
        RebuildFonts();
    }

    private static void RebuildFonts()
    {
        Font old = UiFont;
        // 字号整体比最初版本上调一档：9pt → 10pt，小字 8pt → 9pt。
        UiFont = CreateFont(Pt(10f), FontStyle.Regular);
        UiFontSmall = CreateFont(Pt(9f), FontStyle.Regular);
        UiFontBold = CreateFont(Pt(10f), FontStyle.Bold);
        UiFontLarge = CreateFont(Pt(12.5f), FontStyle.Bold);
        UiFontTitle = CreateFont(Pt(15f), FontStyle.Bold);
        IconFont = CreateFont(Pt(13f), FontStyle.Regular);
        IconFontSmall = CreateFont(Pt(11f), FontStyle.Regular);
        old?.Dispose();
    }

    private static Font CreateFont(float size, FontStyle style)
    {
        try
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
        }
    }

    /// <summary>按字体实际高度算出一个「一行文字刚好放得下」的高度。</summary>
    public static int LineHeight(Font font, float padding = 8f) =>
        (font ?? UiFont).Height + Sy(padding);

    // ---------------------------------------------------------------- 应用与传播

    /// <summary>
    /// 把当前配色递归套到窗体及其所有子控件上。
    ///
    /// <p>主题切换和新建窗口都走这一个入口，避免各处零散地设一遍 BackColor 导致漏改。
    /// </summary>
    public static void ApplyTo(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        foreach (Control child in form.Controls)
        {
            ApplyToControl(child);
        }
        form.Invalidate(true);
    }

    private static void ApplyToControl(Control control)
    {
        switch (control)
        {
            case StatusBar status:
                status.BackColor = Toolbar;
                status.ForeColor = TextDim;
                break;
            case ToolbarButton icon:
                icon.BackColor = icon.Parent?.BackColor ?? Toolbar;
                icon.Invalidate();
                break;
            case ThemedTextBox themed:
                themed.BackColor = Surface;
                themed.Inner.BackColor = Surface;
                themed.Inner.ForeColor = Text;
                themed.Invalidate(true);
                break;
            case ThemedNumericUpDown numeric:
                numeric.RefreshTheme();
                break;
            case ThemedComboBox combo:
                combo.BackColor = Surface;
                combo.ForeColor = Text;
                combo.Invalidate();
                break;
            case Panel panel:
                // 面板由窗口自己决定用哪层底色，这里只兜默认值
                panel.ForeColor = Text;
                break;
            case Label label:
                label.ForeColor = label.ForeColor == Color.Empty ? Text : label.ForeColor;
                break;
            case TextBox text:
                text.BackColor = Surface;
                text.ForeColor = Text;
                break;
            case ListBox list:
                list.BackColor = Background;
                list.ForeColor = Text;
                break;
            case CheckBox check:
                check.ForeColor = Text;
                break;
            case RadioButton radio:
                radio.ForeColor = Text;
                break;
            case GroupBox group:
                group.ForeColor = Text;
                break;
        }

        if (control.HasChildren)
        {
            foreach (Control child in control.Controls)
            {
                ApplyToControl(child);
            }
        }
    }

    /// <summary>显示器的 DPI 变了，或用户改了界面倍率，重新生成字体。</summary>
    public static void RefreshFonts()
    {
        // 先把新字体分发给所有已存在的窗口及其子控件，再重建 Theme 里的字体对象。
        // 顺序很重要：控件不能还握着即将被释放的旧字体；TextBox 这类原生控件也不会
        // 自动从父容器继承，必须显式递归设置。
        var forms = new List<Form>();
        foreach (Form form in Application.OpenForms)
        {
            forms.Add(form);
        }
        foreach (Form form in forms)
        {
            try
            {
                form.Font = UiFont;
                ApplyFontRecursive(form);
                form.PerformLayout();
            }
            catch
            {
                // 忽略
            }
        }

        RebuildFonts();

        foreach (Form form in forms)
        {
            try
            {
                form.Invalidate(true);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>把窗口字体递归套到所有子控件上，并让自绘控件重新测量高度。</summary>
    public static void ApplyFontRecursive(Control root, Font font = null)
    {
        Font target = font ?? UiFont;
        foreach (Control child in root.Controls)
        {
            try
            {
                child.Font = target;
                if (child is StatusBar status)
                {
                    status.FitToFont();
                }
                if (child.HasChildren)
                {
                    ApplyFontRecursive(child, target);
                }
            }
            catch
            {
                // 个别控件可能在遍历过程中被释放，忽略即可
            }
        }
    }

    /// <summary>把所有已打开窗口重新上色（主题切换时调用）。</summary>
    public static void ApplyToAllForms()
    {
        var forms = new List<Form>();
        foreach (Form form in Application.OpenForms)
        {
            forms.Add(form);
        }
        foreach (Form form in forms)
        {
            try
            {
                ApplyTo(form);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>画圆角矩形路径。</summary>
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2) * 2);
        var path = new GraphicsPath();
        if (d >= bounds.Width || d >= bounds.Height)
        {
            path.AddEllipse(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>填充一个圆角矩形（内部自己管理资源）。</summary>
    public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color color)
    {
        using GraphicsPath path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>描边一个圆角矩形。</summary>
    public static void DrawRounded(Graphics g, Rectangle bounds, int radius, Color color,
        float width = 1f)
    {
        using GraphicsPath path = RoundedRect(
            new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }
}
