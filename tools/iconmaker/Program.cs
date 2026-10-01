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
/// <para>用法：IconMaker &lt;源PNG&gt; &lt;输出ICO&gt;</para>
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: IconMaker <源PNG> <输出ICO>");
            return 2;
        }

        string sourcePath = Path.GetFullPath(args[0]);
        string outputPath = Path.GetFullPath(args[1]);

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine("找不到图源: " + sourcePath);
            return 3;
        }

        try
        {
            using var source = new Bitmap(sourcePath);

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

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

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

            var info = new FileInfo(outputPath);
            Console.WriteLine($"OK {info.FullName} {info.Length / 1024} KB ({frames.Count} 个尺寸)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("转换失败: " + ex.Message);
            return 1;
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
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }
}
