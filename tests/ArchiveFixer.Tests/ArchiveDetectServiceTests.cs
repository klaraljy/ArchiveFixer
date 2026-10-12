using System.IO;
using ArchiveFixer.Services;

namespace ArchiveFixer.Tests;

public class ArchiveDetectServiceTests
{
    private static string WriteTempFile(byte[] content)
    {
        string file = Path.Combine(Path.GetTempPath(), "af_detect_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(file, content);
        return file;
    }

    [Fact]
    public async Task Detect_Zip_Magic()
    {
        string file = WriteTempFile(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 });
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("ZIP", result.Format);
            Assert.Equal(".zip", result.SuggestedExtension);
            Assert.True(result.IsArchive);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_ZipEncrypted_ByGeneralPurposeFlag()
    {
        // general purpose bit flag offset 6-7 = 0x0001（加密位）
        string file = WriteTempFile(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x01, 0x00 });
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.True(result.IsProbablyEncrypted);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_SevenZip_Magic()
    {
        string file = WriteTempFile(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("7Z", result.Format);
            Assert.Equal(".7z", result.SuggestedExtension);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "RAR4")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "RAR5")]
    public async Task Detect_Rar_Magic(byte[] header, string expectedFormat)
    {
        string file = WriteTempFile(header);
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal(expectedFormat, result.Format);
            Assert.Equal(".rar", result.SuggestedExtension);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B }, "GZIP", ".gz")]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68 }, "BZIP2", ".bz2")]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, "XZ", ".xz")]
    [InlineData(new byte[] { 0x4D, 0x53, 0x43, 0x46 }, "CAB", ".cab")]
    [InlineData(new byte[] { 0x60, 0xEA }, "ARJ", ".arj")]
    [InlineData(new byte[] { 0x2D, 0x6C, 0x68 }, "LZH", ".lzh")]
    [InlineData(new byte[] { 0x1F, 0x9D }, "Z", ".z")]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "ZSTD", ".zst")]
    [InlineData(new byte[] { 0x04, 0x22, 0x4D, 0x18 }, "LZ4", ".lz4")]
    [InlineData(new byte[] { 0xED, 0xAB, 0xEE, 0xDB }, "RPM", ".rpm")]
    public async Task Detect_SingleStream_Magic(byte[] header, string expectedFormat, string expectedExtension)
    {
        string file = WriteTempFile(header);
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal(expectedFormat, result.Format);
            Assert.Equal(expectedExtension, result.SuggestedExtension);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_Tar_ByUstarMarker()
    {
        var header = new byte[1024];
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("ustar"), 0, header, 257, 5);

        string file = WriteTempFile(header);
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("TAR", result.Format);
            Assert.Equal(".tar", result.SuggestedExtension);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_Iso9660_ByVolumeDescriptor()
    {
        var header = new byte[0x8001 + 5];
        Array.Copy(System.Text.Encoding.ASCII.GetBytes("CD001"), 0, header, 0x8001, 5);

        string file = WriteTempFile(header);
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("ISO", result.Format);
            Assert.Equal(".iso", result.SuggestedExtension);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_UnknownContent_ReturnsUnknown()
    {
        string file = WriteTempFile(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("Unknown", result.Format);
            Assert.False(result.IsArchive);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Detect_EmptyFile_ReturnsUnknown()
    {
        string file = WriteTempFile(Array.Empty<byte>());
        try
        {
            var result = await new ArchiveDetectService().DetectAsync(file);
            Assert.Equal("Unknown", result.Format);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("a.zip", ".zip", "ZIP", "后缀正常")]
    [InlineData("a", "无", "ZIP", "后缀缺失")]
    [InlineData("a.jpg", ".jpg", "ZIP", "后缀不匹配")]
    [InlineData("a.7z.jpg", ".jpg", "7Z", "多重后缀疑似伪装")]
    public void GetExtensionStatus_VariousCases(string fileName, string currentExtension, string format, string expected)
    {
        var service = new ArchiveDetectService();

        string status = service.GetExtensionStatus(
            fileName,
            currentExtension,
            ArchiveFixer.Models.DetectResult.Archive(format, service.GetSuggestedExtension(format)));

        Assert.Equal(expected, status);
    }

    /// <summary>
    /// **名字自报"我是某一组分卷的一片" ⇒ 不许判成内嵌归档（双面文件）**。
    ///
    /// <para><b>现场</b>（2026-10-12 大测试困难批，样本 `22222222` 逐字）：顶层三片里
    /// `资源222-L2.zip.--p002`（7z 切的 zip 第 2 片）的内容里**恰好含有下一层 RAR 卷的魔数**
    /// —— 因为那一层切的就是下一层的 RAR 卷 —— 于是顺序扫扫到那个魔数、判成"双面文件"，
    /// 逐字 `检测到内嵌归档：已从偏移 162 处取出 26214238 字节（区间 162–26214400）`
    /// ⇒ 这一片的**原始字节再没被当成这一组的一片** ⇒ 整组凑不齐 ⇒ ①「内容物解出来」红。</para>
    ///
    /// <para>判据只读名字（唯一出口 <c>FileNameHelper.IsVolumePartFileName</c>）：一片分卷的名字就是它的身份，
    /// "尾部有归档魔数"在它身上是假证据（切片里本来可能出现任何字节）。</para>
    ///
    /// <para><b>红检</b>：把 <c>ArchiveDetectService</c> 里那道 <c>namedVolumePart</c> 闸门去掉 ⇒
    /// 本条当场变红（`EmbeddedArchiveOffset` 会被填成 162）。</para>
    /// </summary>
    [Fact]
    public async Task Detect_片名带卷标记_内容里恰有归档魔数_不许判成内嵌归档()
    {
        // 造一个**真** 7z（顺序扫要能找到它的魔数；用项目内置的 7z.exe，绝不依赖用户机器上的工具）
        string tempRoot = Path.Combine(Path.GetTempPath(), "af_detect_volpiece_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string payloadDir = Path.Combine(tempRoot, "payload");
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(Path.Combine(payloadDir, "inner.txt"), "内容\n");

            string innerArchive = Path.Combine(tempRoot, "inner.7z");
            string sevenZip = ArchiveFixer.Engines.ToolLocator.Default.SevenZipExePath;

            Assert.False(string.IsNullOrWhiteSpace(sevenZip), "本机没有内置 7z.exe，这条用例失去意义");

            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = sevenZip,
                ArgumentList = { "a", "-t7z", "-mx0", innerArchive, Path.Combine(payloadDir, "inner.txt") },
                WorkingDirectory = tempRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            })!)
            {
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }

            byte[] inner = File.ReadAllBytes(innerArchive);

            /*
             * 现场形状：`[162 字节别的东西][一整个归档]`，名字带卷标记（第 2 片）。
             * 162 就是真机日志里那个偏移（逐字：`已从偏移 162 处取出`）。
             */
            var piece = new byte[162 + inner.Length];
            new Random(20261012).NextBytes(piece.AsSpan(0, 162));
            Buffer.BlockCopy(inner, 0, piece, 162, inner.Length);

            string pieceDir = Path.Combine(tempRoot, "piece");
            Directory.CreateDirectory(pieceDir);
            string piecePath = Path.Combine(pieceDir, "set.zip.002");
            File.WriteAllBytes(piecePath, piece);

            var service = new ArchiveDetectService();
            ArchiveFixer.Models.DetectResult result = await service.DetectAsync(piecePath);

            Assert.Equal(0, result.EmbeddedArchiveOffset);

            // 后缀状态照旧按"这是分卷标记"报（⛔ 不是"内嵌归档"）：
            // 那条死路（改成 .zip 也打不开）在这一档根本不该出现。
            Assert.Equal(
                ArchiveFixer.Models.StatusText.ExtensionVolume,
                service.GetExtensionStatus("set.zip.002", ".002", result));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}
