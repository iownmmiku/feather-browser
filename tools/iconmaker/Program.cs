using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace IconMaker;

/// <summary>
/// 独立的图标转换器：把一张 PNG 转成多尺寸 .ico。
///
/// <para>刻意不引用主工程的任何代码 —— 这是个纯工具，不该因为主工程改了
/// Theme / AppPaths 就编译不过。需要的能力只有 System.Drawing。</para>
///
/// <para>用法：IconMaker &lt;源PNG&gt; &lt;输出ICO&gt; [--no-normalize]</para>
///
/// <para>默认会做「去圆角底」处理：很多 AI 生成的图标是「图标本体 + 白色圆角方块」
/// 的构图，圆角外的白底在做 Windows 图标时会在深色任务栏上出现白边，
/// 16×16 下尤其明显。这里会自动识别圆角并把它之外的部分变成透明。</para>
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        bool normalize = !args.Any(a =>
            a.Equals("--no-normalize", StringComparison.OrdinalIgnoreCase));

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("用法: IconMaker <源PNG> <输出ICO> [--no-normalize]");
            return 2;
        }

        string sourcePath = Path.GetFullPath(positional[0]);
        string outputPath = Path.GetFullPath(positional[1]);

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine("找不到图源: " + sourcePath);
            return 3;
        }

        try
        {
            using var loaded = new Bitmap(sourcePath);
            using Bitmap source = normalize ? NormalizeRoundedCorners(loaded) : loaded;

            var frames = new List<(int Size, byte[] Png)>();
            foreach (int size in Sizes)
            {
                frames.Add((size, RenderPngBytes(source, size)));
            }

            string folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                // ICONDIR：保留字 + 类型(1=图标) + 图像数量
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);

                // 每帧 16 字节的目录项，数据紧随其后
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

            var info = new FileInfo(outputPath);
            Console.WriteLine($"OK {info.FullName} {info.Length / 1024} KB ({frames.Count} 个尺寸)" +
                              (normalize ? " [已去圆角底]" : ""));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("转换失败: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 判断这张图是不是「圆角图标 + 浅色方底」的构图，如果是就把圆角之外的浅色底置为透明。
    ///
    /// <para>判断依据不是扫描转折点（很多图在垂直中线上并不会经过圆角，扫不到），
    /// 而是直接采样**四角**的颜色：四角都是浅色 → 说明有方底要处理；
    /// 四角本来就透明 → 图已经是好的，原样返回。</para>
    ///
    /// <para>遮罩只作用于四个角上的方形区域，不会碰到中间的本体，
    /// 所以即使本体有部分伸向角落也不会被误裁。</para>
    /// </summary>
    private static Bitmap NormalizeRoundedCorners(Bitmap source)
    {
        int w = source.Width;
        int h = source.Height;
        int probe = Math.Max(2, Math.Min(w, h) / 50);

        // 四角各自的平均色
        bool CornerLight(int x0, int y0)
        {
            long r = 0, g = 0, b = 0, a = 0;
            int n = 0;
            for (int y = y0; y < y0 + probe && y < h; y++)
            {
                for (int x = x0; x < x0 + probe && x < w; x++)
                {
                    Color c = source.GetPixel(x, y);
                    r += c.R; g += c.G; b += c.B; a += c.A;
                    n++;
                }
            }
            if (n == 0) return false;
            return a / n > 200 && r / n > 200 && g / n > 200 && b / n > 200;
        }

        bool topLeft = CornerLight(0, 0);
        bool topRight = CornerLight(w - probe, 0);
        bool bottomLeft = CornerLight(0, h - probe);
        bool bottomRight = CornerLight(w - probe, h - probe);

        if (!(topLeft && topRight && bottomLeft && bottomRight))
        {
            Console.WriteLine("    四角不是浅色底（可能已是透明），保持原图");
            return source;
        }

        // 从左上角沿对角线往内走，找到离开浅色底的位置，用来估算圆角半径。
        // 对角线一定会穿过圆角，所以这个探测比沿中轴扫描可靠。
        int limit = Math.Min(w, h) / 2;
        int firstSolid = -1;
        for (int d = 1; d < limit; d++)
        {
            Color c = source.GetPixel(d, d);
            if (!(c.A > 200 && c.R > 200 && c.G > 200 && c.B > 200))
            {
                firstSolid = d;
                break;
            }
        }
        if (firstSolid < 0)
        {
            Console.WriteLine("    无法估出圆角半径，保持原图");
            return source;
        }

        // 对角线交点距顶点 d 与半径 r 的关系：d = r(1 - 1/√2) => r ≈ d * 3.414
        int radius = (int)Math.Round(firstSolid * 3.414);
        radius = Math.Clamp(radius, 4, Math.Min(w, h) / 2);

        Console.WriteLine($"    识别到浅色圆角底，半径约 {radius}px（原图 {w}x{h}），圆角外将置为透明");

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        {
            g.DrawImage(source, 0, 0, w, h);
        }

        // 直接在结果上按圆角几何处理四个角
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double inside = InsideRoundedRect(x + 0.5, y + 0.5, w, h, radius);
                if (inside >= 0.999)
                {
                    continue;
                }
                Color c = result.GetPixel(x, y);
                int alpha = (int)Math.Round(c.A * Math.Clamp(inside, 0, 1));
                result.SetPixel(x, y, Color.FromArgb(alpha, c.R, c.G, c.B));
            }
        }

        return result;
    }

    /// <summary>
    /// 点 (x,y) 在圆角矩形内的覆盖率，1 = 完全在内，0 = 完全在外。
    /// 用「到圆心的距离」做一次线性过渡，得到抗锯齿边缘。
    /// </summary>
    private static double InsideRoundedRect(double x, double y, int w, int h, int radius)
    {
        // 找出最近的圆角圆心（如果点在四个角的区域内）
        double cx = x, cy = y;
        bool inCorner = false;

        if (x < radius && y < radius) { cx = radius; cy = radius; inCorner = true; }
        else if (x > w - radius && y < radius) { cx = w - radius; cy = radius; inCorner = true; }
        else if (x < radius && y > h - radius) { cx = radius; cy = h - radius; inCorner = true; }
        else if (x > w - radius && y > h - radius) { cx = w - radius; cy = h - radius; inCorner = true; }

        if (!inCorner)
        {
            return 1.0;
        }

        double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        // 在半径附近做 1px 宽的过渡带
        return Math.Clamp(radius - dist + 0.5, 0, 1);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

    /// <summary>等比缩放渲染成 PNG 字节（保留透明通道）。</summary>
    private static byte[] RenderPngBytes(Bitmap source, int size)
    {
        using var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }
}
