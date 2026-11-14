using ArchiveFixer.Detection;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// **普通媒体内容物事实**（2026-11-11 用户口径：媒体前缀只服务"单一完整压缩包"，
/// 分卷不在这一版范围内；普通 PNG/JPG/MP4 不能进第二大步分卷候选）。
///
/// <para>判据只认内容：媒体结构按自己的格式走完，且结束位置正好等于文件长度；
/// 媒体结构后面还有数据 ⇒ 判不出（可能是损坏/未识别的尾部归档）；
/// 裸切续卷伪装成 .png ⇒ 媒体签名本身不成立，同样判不出。</para>
/// </summary>
public sealed class MediaContentFactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "af-media-content-" + Guid.NewGuid().ToString("N"));

    public MediaContentFactTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [Fact]
    public void 普通PNG_媒体结构占满整个文件_记成NormalMedia()
    {
        string path = Create("336.png", BuildPng());

        FileNatureCard card = FileNature.Inspect(path);

        Assert.Equal(FileContentKind.NormalMedia, card.ContentKind);
        Assert.True(card.IsKnownNormalMedia);
        Assert.Equal(ArchiveBodyKind.Unknown, card.BodyKind);
    }

    [Fact]
    public void 普通JPEG_媒体结构占满整个文件_记成NormalMedia()
    {
        string path = Create("封面.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        Assert.Equal(FileContentKind.NormalMedia, FileNature.Inspect(path).ContentKind);
    }

    [Fact]
    public void 普通MP4_媒体结构占满整个文件_记成NormalMedia()
    {
        string path = Create(
            "视频.mp4",
            new byte[] { 0x00, 0x00, 0x00, 0x08, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });

        Assert.Equal(FileContentKind.NormalMedia, FileNature.Inspect(path).ContentKind);
    }

    [Fact]
    public void 伪装成png的WebP_媒体结构占满整个文件_记成NormalMedia()
    {
        string path = Create("336.png", BuildWebp());

        FileNatureCard card = FileNature.Inspect(path);

        Assert.Equal(FileContentKind.NormalMedia, card.ContentKind);
        Assert.True(card.IsKnownNormalMedia);
    }

    [Fact]
    public void 裸切续卷伪装成png_没有媒体魔数_不算内容物()
    {
        var bytes = new byte[128];
        new Random(20261111).NextBytes(bytes);

        string path = Create("336.png", bytes);

        Assert.Equal(FileContentKind.Unknown, FileNature.Inspect(path).ContentKind);
    }

    [Fact]
    public void 媒体结构后面还有数据_判不出_不算普通内容物()
    {
        byte[] png = BuildPng();
        var withTail = new byte[png.Length + 32];
        png.CopyTo(withTail, 0);
        new Random(7).NextBytes(withTail.AsSpan(png.Length));

        string path = Create("带尾巴.png", withTail);

        Assert.Equal(FileContentKind.Unknown, FileNature.Inspect(path).ContentKind);
    }

    [Fact]
    public void 媒体前缀的完整ZIP_仍按单一完整包处理_内容物事实不得覆盖它()
    {
        byte[] png = BuildPng();
        byte[] zip = BuildZip();

        var bytes = new byte[png.Length + zip.Length];
        png.CopyTo(bytes, 0);
        zip.CopyTo(bytes, png.Length);

        string path = Create("图片.png", bytes);

        FileNatureCard card = FileNature.Inspect(path);

        Assert.Equal(VolumeContentFormat.Zip, card.Format);
        Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
        Assert.True(card.EmbeddedOffset > 0);
        Assert.Equal(FileContentKind.Unknown, card.ContentKind);
    }

    [Fact]
    public void 普通媒体即使带名字槽_也不是第二大步候选()
    {
        var ledger = new FirstStepFileLedger();

        ledger.AddOrReplace(new FirstStepFileLedgerEntry
        {
            Path = Path.Combine(_root, "336.png"),
            BaseName = "336",
            Size = 45,
            Format = VolumeContentFormat.Unknown,
            ContentKind = FileContentKind.NormalMedia,
            NameSlot = 2,
            SourceLayer = _root,
            IsGatePassed = true
        });

        Assert.Empty(ledger.SecondStepCandidates("336"));
        Assert.Empty(ledger.AllSecondStepCandidates());
    }

    private string Create(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>PNG：8 字节签名 + 一个 IHDR 块 + 一个 IEND 块（长度/块类型结构合法即可）。</summary>
    private static byte[] BuildPng()
    {
        var bytes = new byte[45];

        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        signature.CopyTo(bytes, 0);

        int position = 8;

        WriteBigEndian(bytes, position, 13);
        bytes[position + 4] = (byte)'I';
        bytes[position + 5] = (byte)'H';
        bytes[position + 6] = (byte)'D';
        bytes[position + 7] = (byte)'R';
        position += 12 + 13;

        WriteBigEndian(bytes, position, 0);
        bytes[position + 4] = (byte)'I';
        bytes[position + 5] = (byte)'E';
        bytes[position + 6] = (byte)'N';
        bytes[position + 7] = (byte)'D';

        return bytes;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>最小 WebP：RIFF 头 + 一个 `VP8 ` 块（大小字段按 RFC 9649：从偏移 8 起算）。</summary>
    private static byte[] BuildWebp()
    {
        var bytes = new byte[24];

        byte[] signature = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
        signature.CopyTo(bytes, 0);

        // 整个文件 24 字节，RIFF File Size = 24 - 8 = 16。
        bytes[4] = 16;
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';
        bytes[12] = (byte)'V';
        bytes[13] = (byte)'P';
        bytes[14] = (byte)'8';
        bytes[15] = (byte)' ';
        bytes[16] = 4;

        return bytes;
    }

    private static byte[] BuildZip()
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("payload.txt");
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write("media-prefix payload");
        }

        return stream.ToArray();
    }
}
