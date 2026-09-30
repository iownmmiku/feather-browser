using System.Drawing.Drawing2D;

namespace FeatherBrowser.UI;

/// <summary>
/// 运行时生成的程序图标：一枚蓝底白色羽毛。
///
/// <p>不内嵌 .ico 资源是有意的 —— 图标只在窗口创建时用一次，
/// 用代码画出来既省一个资源文件，也避免不同 DPI 下图标模糊。
/// </summary>
internal static class AppIcon
{
    private static Icon _cached;

    public static Icon Create()
    {
        if (_cached != null)
        {
            return _cached;
        }

        using var bitmap = new Bitmap(64, 64);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角蓝底
            using (var path = Theme.RoundedRect(new Rectangle(2, 2, 60, 60), 14))
            using (var brush = new LinearGradientBrush(
                       new Rectangle(0, 0, 64, 64),
                       Color.FromArgb(88, 140, 255),
                       Color.FromArgb(58, 100, 224),
                       LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            // 羽毛：一条主脉 + 三片羽枝
            using var pen = new Pen(Color.White, 3.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawBezier(pen,
                new PointF(20, 46), new PointF(30, 40),
                new PointF(42, 28), new PointF(47, 17));

            using var thin = new Pen(Color.FromArgb(235, 255, 255, 255), 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawBezier(thin, new PointF(24, 41), new PointF(20, 32), new PointF(21, 25), new PointF(26, 18));
            g.DrawBezier(thin, new PointF(30, 35), new PointF(28, 27), new PointF(31, 21), new PointF(36, 15));
            g.DrawBezier(thin, new PointF(35, 30), new PointF(37, 24), new PointF(41, 20), new PointF(45, 16));
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            _cached = (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
        return _cached;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
