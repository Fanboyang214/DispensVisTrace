using System.Diagnostics;

namespace Acquisition;

/// <summary>
/// 把相机出来的 8bit 灰度像素写成 BMP。
/// </summary>
/// <remarks>
/// 用 BMP 而不是 PNG：BMP 头部固定 1078 字节 + 原始像素，零依赖、零编码耗时，
/// 而 PNG 在 net8.0 上要么引入 System.Drawing.Common（Windows 专用包）要么引入 ImageSharp，
/// 高节拍下还多一份编码延迟。模板图片是静态小图，继续用 PNG 不受影响。
/// Halcon 的 HOperatorSet.ReadImage 直接能读 BMP，下游不用转换。
/// </remarks>
internal static class ImageFileWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteSize = 256 * 4;
    private const int PixelDataOffset = FileHeaderSize + InfoHeaderSize + PaletteSize;

    /// <summary>
    /// 写一张 8bit 灰度 BMP。先写同目录的 .tmp 再改名，
    /// 保证下游扫描共享目录时永远读不到写了一半的图。
    /// </summary>
    public static void WriteMono8Bmp(string path, byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"图像尺寸非法：{width}x{height}");

        // 相机 SDK 给出的缓冲区长度不对是现场会真实发生的故障，宁可报错也不要写出错位的图
        if (pixels.Length < (long)width * height)
            throw new ArgumentException(
                $"像素缓冲区长度 {pixels.Length} 不足，期望 {width}x{height} = {(long)width * height}", nameof(pixels));

        var tempPath = path + ".tmp";
        WriteBmp(tempPath, pixels, width, height);

        // 同目录改名，同卷上是原子操作
        File.Move(tempPath, path, overwrite: true);
    }

    private static void WriteBmp(string tempPath, byte[] pixels, int width, int height)
    {
        int stride = (width + 3) & ~3;          // BMP 每行按 4 字节对齐
        int imageSize = stride * height;

        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(PixelDataOffset + imageSize);
        writer.Write(0);                        // 保留
        writer.Write(PixelDataOffset);

        // BITMAPINFOHEADER
        writer.Write(InfoHeaderSize);
        writer.Write(width);
        writer.Write(height);                   // 正数 = 自下而上
        writer.Write((short)1);                 // 平面数
        writer.Write((short)8);                 // 每像素位数
        writer.Write(0);                        // BI_RGB 不压缩
        writer.Write(imageSize);
        writer.Write(2835);                     // 72 DPI
        writer.Write(2835);
        writer.Write(256);                      // 调色板项数
        writer.Write(256);

        // 灰度调色板
        for (int i = 0; i < 256; i++)
        {
            writer.Write((byte)i);
            writer.Write((byte)i);
            writer.Write((byte)i);
            writer.Write((byte)0);
        }

        // 像素：BMP 默认自下而上，所以从最后一行开始写
        int padding = stride - width;
        for (int y = height - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * width, width);
            for (int i = 0; i < padding; i++)
                writer.Write((byte)0);
        }
    }

    /// <summary>
    /// BMP 头是手写二进制，错了只会表现为"图能打开但内容错位/上下颠倒"，所以留一个自检钉住格式。
    /// </summary>
#if DEBUG
    private static bool _verified;

    [Conditional("DEBUG")]
    public static void VerifyMono8Bmp()
    {
        if (_verified) return;
        _verified = true;

        const int width = 3;                    // 故意选非 4 的倍数，覆盖行对齐补齐
        const int height = 2;
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i + 1);

        var path = Path.Combine(Path.GetTempPath(), $"bmpcheck-{Guid.NewGuid():N}.bmp");
        try
        {
            WriteMono8Bmp(path, pixels, width, height);
            var bytes = File.ReadAllBytes(path);

            Debug.Assert(bytes[0] == 'B' && bytes[1] == 'M', "BMP 魔数不对");
            Debug.Assert(BitConverter.ToInt32(bytes, 2) == bytes.Length, "BMP 文件长度字段与实际不符");
            Debug.Assert(BitConverter.ToInt32(bytes, 10) == PixelDataOffset, "像素起始偏移不对");
            Debug.Assert(BitConverter.ToInt32(bytes, 18) == width, "宽不对");
            Debug.Assert(BitConverter.ToInt32(bytes, 22) == height, "高不对");
            Debug.Assert(BitConverter.ToInt16(bytes, 28) == 8, "位深不是 8");
            Debug.Assert(BitConverter.ToInt32(bytes, 46) == 256, "调色板项数不对");

            // 按 BMP 约定把像素解回来比对：文件里的第一行是图像的最后一行。
            // 整块往返比对，避免自己手算索引把断言写反。
            const int stride = 4;               // width=3 补齐到 4
            for (int fileRow = 0; fileRow < height; fileRow++)
            {
                int imageRow = height - 1 - fileRow;
                for (int x = 0; x < width; x++)
                {
                    byte decoded = bytes[PixelDataOffset + fileRow * stride + x];
                    Debug.Assert(decoded == pixels[imageRow * width + x],
                        $"BMP 像素往返不一致：文件第 {fileRow} 行第 {x} 列，期望 {pixels[imageRow * width + x]} 实际 {decoded}");
                }
            }

            Debug.Assert(!File.Exists(path + ".tmp"), "临时文件没有清理干净");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
#endif
}
