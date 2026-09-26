using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArchiveFixer.IconGen;

/// <summary>
/// ArchiveFixer 的应用图标生成器（开发工具，⛔ 不参与主程序构建）。
///
/// <para><b>画的是什么</b>：蓝底上一个"箱子"（归档） —— 金色箱盖 + 白色箱体 + 深蓝对勾
/// （修好了 / 校验通过）。用户 2026-09-26 说旧图标丑，这一版按"一个箱子 + 一个对勾"重画，
/// 只有三块色、没有细线，缩到 16px 还看得清。</para>
///
/// <para><b>怎么保证小尺寸不糊</b>：每个尺寸都**单独画**（按 4 倍超采样后高质量缩到目标尺寸），
/// ⛔ 不是把 256 那一张缩下来 —— 大图里那些细节缩到 16px 只会变成一团灰。</para>
///
/// <para><b>ICO 编码</b>：≤128 用经典 DIB（32bpp + 全零 AND 掩码），256 用 PNG ——
/// 这是 Vista 以来最稳的组合（小的走老格式，任何 shell 路径都认得；大的走 PNG 省体积）。</para>
/// </summary>
internal static class Program
{
    /// <summary>要生成的每一档尺寸；顺序即 ICO 里的顺序（由小到大）。</summary>
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    /// <summary>超采样倍数：先按 4 倍画，再缩到目标尺寸（抗锯齿之外再要一层边缘平滑）。</summary>
    private const int Supersample = 4;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法：icon-gen <输出 .ico 路径> [预览图 .png 路径]");
            return 2;
        }

        string icoPath = Path.GetFullPath(args[0]);
        string previewPath = args.Length > 1 ? Path.GetFullPath(args[1]) : string.Empty;

        var frames = new List<Frame>(Sizes.Length);

        foreach (int size in Sizes)
        {
            using Bitmap bitmap = Render(size);
            frames.Add(new Frame(size, EncodePng(bitmap), EncodeDib(bitmap)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        File.WriteAllBytes(icoPath, BuildIco(frames));

        Console.WriteLine($"已写出 {icoPath}（{new FileInfo(icoPath).Length} 字节，" +
                          $"{Sizes.Length} 档：{string.Join(" / ", Sizes)}）");

        if (previewPath.Length > 0)
        {
            WritePreview(frames, previewPath);
            Console.WriteLine($"预览图 {previewPath}");
        }

        return 0;
    }

    // ================================================================ 画面

    /// <summary>
    /// 画一档尺寸。全部坐标按"1.0 = 整张画布"归一化，这样同一段代码能画 16px 也能画 1024px。
    /// </summary>
    private static Bitmap Render(int size)
    {
        int big = size * Supersample;

        using var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Paint(g, big, size);
        }

        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, size, size), 0, 0, big, big, GraphicsUnit.Pixel);
        }

        return result;
    }

    private static void Paint(Graphics g, float u, int targetSize)
    {
        // ---- 底：圆角方块 + 深蓝渐变（Windows 任务栏里成一个"蓝方块"，一眼能找到）
        var background = new RectangleF(0, 0, u, u);

        using (var path = RoundedRect(background, 0.215f * u))
        using (var brush = new LinearGradientBrush(
                   background,
                   Color.FromArgb(255, 0x5B, 0x9B, 0xF9),
                   Color.FromArgb(255, 0x17, 0x38, 0x9B),
                   55f))
        {
            g.FillPath(brush, path);
        }

        // ---- 箱盖：金色，比箱体宽一点（这一道"台阶"就是它读起来像一个箱子的原因）
        var lid = new RectangleF(0.150f * u, 0.238f * u, 0.700f * u, 0.140f * u);

        using (var path = RoundedRect(lid, 0.030f * u))
        using (var brush = new LinearGradientBrush(
                   lid,
                   Color.FromArgb(255, 0xFD, 0xD8, 0x6A),
                   Color.FromArgb(255, 0xEF, 0x9A, 0x0B),
                   90f))
        {
            g.FillPath(brush, path);
        }

        // ---- 箱体：白色（微微带一点冷调渐变，避免死白）
        var body = new RectangleF(0.222f * u, 0.362f * u, 0.556f * u, 0.412f * u);

        using (var path = RoundedRect(body, 0.042f * u))
        using (var brush = new LinearGradientBrush(
                   body,
                   Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
                   Color.FromArgb(255, 0xE0, 0xEA, 0xFA),
                   90f))
        {
            g.FillPath(brush, path);
        }

        // ---- 对勾：深蓝，压在箱体正中（"修好了 / 校验通过"）。
        // 小尺寸把笔画加粗一档，否则 16px 上这根线只有 1.2 像素、缩完就看不见了。
        float stroke = (targetSize <= 20 ? 0.104f : 0.076f) * u;

        using var pen = new Pen(Color.FromArgb(255, 0x17, 0x33, 0x86), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        g.DrawLines(
            pen,
            new[]
            {
                new PointF(0.382f * u, 0.556f * u),
                new PointF(0.468f * u, 0.648f * u),
                new PointF(0.630f * u, 0.468f * u),
            });
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    // ================================================================ 编码

    private readonly record struct Frame(int Size, byte[] Png, byte[] Dib);

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);

        return stream.ToArray();
    }

    /// <summary>
    /// 经典 ICO 里的那种位图：BITMAPINFOHEADER（高度写两倍）+ 自底向上的 32bpp BGRA + 1bpp AND 掩码。
    /// 掩码全 0（= 全部不透明），真正的不透明信息在 alpha 通道里。
    /// </summary>
    private static byte[] EncodeDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 4;
        int maskRowBytes = ((width + 31) / 32) * 4;

        var xor = new byte[rowBytes * height];

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            var raw = new byte[stride * height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            for (int y = 0; y < height; y++)
            {
                // data.Stride 为正 = 自顶向下；DIB 一律自底向上。
                int sourceRow = data.Stride > 0 ? y : height - 1 - y;
                int targetRow = height - 1 - y;
                Buffer.BlockCopy(raw, sourceRow * stride, xor, targetRow * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var mask = new byte[maskRowBytes * height];

        var header = new byte[40];
        WriteI32(header, 0, 40);                  // biSize
        WriteI32(header, 4, width);               // biWidth
        WriteI32(header, 8, height * 2);          // biHeight = XOR + AND 两块
        WriteI16(header, 12, 1);                  // biPlanes
        WriteI16(header, 14, 32);                 // biBitCount
        WriteI32(header, 20, xor.Length + mask.Length);

        var result = new byte[header.Length + xor.Length + mask.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(xor, 0, result, header.Length, xor.Length);
        Buffer.BlockCopy(mask, 0, result, header.Length + xor.Length, mask.Length);

        return result;
    }

    private static byte[] BuildIco(List<Frame> frames)
    {
        var blobs = new List<(Frame Frame, byte[] Data)>(frames.Count);

        foreach (Frame frame in frames)
        {
            // 256 那一档用 PNG（省体积）；更小的走 DIB，兼容性最好。
            blobs.Add((frame, frame.Size >= 256 ? frame.Png : frame.Dib));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                  // reserved
        writer.Write((ushort)1);                  // type = icon
        writer.Write((ushort)blobs.Count);

        int offset = 6 + (blobs.Count * 16);

        foreach ((Frame frame, byte[] data) in blobs)
        {
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));   // 256 在这里写 0
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
            writer.Write((byte)0);                // 调色板数（真彩 = 0）
            writer.Write((byte)0);                // reserved
            writer.Write((ushort)1);              // planes
            writer.Write((ushort)32);             // bit count
            writer.Write(data.Length);
            writer.Write(offset);

            offset += data.Length;
        }

        foreach ((Frame _, byte[] data) in blobs)
        {
            writer.Write(data);
        }

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// 预览图：上排按**真实像素**摆一遍，下排把 16 / 32 放大（最近邻）看小尺寸糊没糊。
    /// 只为"生成完自己能看一眼"，⛔ 不进仓库、不影响图标本身。
    /// </summary>
    private static void WritePreview(List<Frame> frames, string path)
    {
        const int Gap = 16;
        const int Padding = 16;

        int stripWidth = frames.Sum(f => f.Size) + (Gap * (frames.Count - 1));
        int zoomWidth = 128 + Gap + 128;
        int width = Padding + Math.Max(stripWidth, zoomWidth) + Padding;
        int height = Padding + 256 + Gap + 128 + Padding;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(255, 0xF2, 0xF4, 0xF8));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int x = Padding;

            foreach (Frame frame in frames)
            {
                using var bitmap = new Bitmap(new MemoryStream(frame.Png));
                g.DrawImage(bitmap, new Rectangle(x, Padding + (256 - frame.Size), frame.Size, frame.Size));
                x += frame.Size + Gap;
            }

            int zoomY = Padding + 256 + Gap;
            DrawZoom(g, frames, 16, 8, Padding, zoomY);
            DrawZoom(g, frames, 32, 4, Padding + 128 + Gap, zoomY);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sheet.Save(path, ImageFormat.Png);
    }

    private static void DrawZoom(Graphics g, List<Frame> frames, int size, int factor, int x, int y)
    {
        Frame frame = frames.First(f => f.Size == size);

        using var bitmap = new Bitmap(new MemoryStream(frame.Png));
        g.DrawImage(bitmap, new Rectangle(x, y, size * factor, size * factor));
    }

    private static void WriteI16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteI32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
