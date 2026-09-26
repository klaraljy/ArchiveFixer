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
}
