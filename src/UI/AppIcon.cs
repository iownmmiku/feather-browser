using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FeatherBrowser.Services;

namespace FeatherBrowser.UI;

/// <summary>
/// 程序图标。
///
/// <para>两种来源：
/// <list type="bullet">
///   <item>如果 <c>assets\icon.png</c> 存在（用 AI 生成的正式图标），
///         就在启动时把它转成多尺寸 Icon 用；</item>
///   <item>没有图源时用代码画一枚兜底图标，保证任何情况下窗口都有图标。</item>
/// </list>
/// </para>
///
/// <para><see cref="WriteIco"/> 会把 PNG 转成多尺寸 .ico 落盘，
/// 由 <c>tools\make-icon.ps1</c> 调用后作为 exe 的 ApplicationIcon 嵌进去，
/// 这样任务管理器、快捷方式、安装程序也都有图标。
/// </summary>
internal static class AppIcon
{
    /// <summary>把图中的这些尺寸都塞进 .ico，Windows 按场合自己挑。</summary>
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static Icon _cached;

    /// <summary>正式图标的路径（AI 生成图放这里）。</summary>
    public static string SourcePngPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icon.png"));

    /// <summary>程序数据目录里生成好的 .ico。</summary>
    public static string GeneratedIcoPath => Path.Combine(AppPaths.Root, "feather.ico");

    public static Icon Create()
    {
        if (_cached != null)
        {
            return _cached;
        }

        _cached = TryLoadGeneratedIco() ?? TryLoadSourcePng() ?? DrawFallback();
        return _cached;
    }

    private static Icon TryLoadGeneratedIco()
    {
        try
        {
            if (File.Exists(GeneratedIcoPath))
            {
                return new Icon(GeneratedIcoPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取生成的图标失败: " + ex.Message);
        }
        return null;
    }

    private static Icon TryLoadSourcePng()
    {
        try
        {
            string[] candidates =
            {
                SourcePngPath,
                Path.Combine(AppContext.BaseDirectory, "icon.png"),
                Path.Combine(AppPaths.Root, "icon.png"),
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                using var bitmap = new Bitmap(path);
                return FromBitmap(bitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取图标 PNG 失败: " + ex.Message);
        }
        return null;
    }

    /// <summary>兜底图标：蓝底 + 白色羽毛，纯代码绘制。</summary>
    private static Icon DrawFallback()
    {
        using var bitmap = new Bitmap(256, 256);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var path = Theme.RoundedRect(new Rectangle(8, 8, 240, 240), 56))
            using (var brush = new LinearGradientBrush(
                       new Rectangle(0, 0, 256, 256),
                       Color.FromArgb(90, 140, 255),
                       Color.FromArgb(58, 100, 224),
                       LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            using var spine = new Pen(Color.White, 14f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawBezier(spine,
                new PointF(80, 184), new PointF(120, 160),
                new PointF(168, 112), new PointF(188, 68));

            using var barb = new Pen(Color.FromArgb(235, 255, 255, 255), 9f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawBezier(barb, new PointF(96, 164), new PointF(80, 128), new PointF(84, 100), new PointF(104, 72));
            g.DrawBezier(barb, new PointF(120, 140), new PointF(112, 108), new PointF(124, 84), new PointF(144, 60));
            g.DrawBezier(barb, new PointF(140, 120), new PointF(148, 96), new PointF(164, 80), new PointF(180, 64));
        }

        Log.Info("未找到图标图源，使用代码生成的兜底图标");
        return FromBitmap(bitmap);
    }

    /// <summary>把位图转成 Icon（走 HICON 复制，避免句柄随位图释放而失效）。</summary>
    public static Icon FromBitmap(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    // ---------------------------------------------------------------- 生成 .ico

    /// <summary>
    /// 把一张 PNG 转成多尺寸 .ico。
    /// 由 <c>tools\make-icon.ps1</c> 调用；也可以用来做自定义图标。
    /// 每帧都存成 PNG 格式（Vista 之后都支持），能保留透明通道。
    /// </summary>
    public static void WriteIco(string pngPath, string outputPath)
    {
        using var source = new Bitmap(pngPath);

        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in IconSizes)
        {
            frames.Add((size, RenderPngBytes(source, size)));
        }

        AppPaths.EnsureCreated();
        string folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);              // 保留
        writer.Write((ushort)1);              // 类型 = 图标
        writer.Write((ushort)frames.Count);   // 图像数量

        int offset = 6 + 16 * frames.Count;
        foreach ((int size, byte[] png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // 宽（256 记 0）
            writer.Write((byte)(size >= 256 ? 0 : size));   // 高
            writer.Write((byte)0);                          // 调色板数
            writer.Write((byte)0);                          // 保留
            writer.Write((ushort)1);                        // 色彩平面
            writer.Write((ushort)32);                       // 位深
            writer.Write(png.Length);                       // 数据长度
            writer.Write(offset);                           // 数据偏移
            offset += png.Length;
        }

        foreach ((int _, byte[] png) in frames)
        {
            writer.Write(png);
        }
    }

    /// <summary>等比缩放渲染成 PNG 字节（保留透明通道）。</summary>
    private static byte[] RenderPngBytes(Bitmap source, int size)
    {
        using var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
