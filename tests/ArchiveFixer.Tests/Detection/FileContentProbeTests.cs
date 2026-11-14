using ArchiveFixer.Detection;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **非压缩文件识别**的守门用例：魔数优先；ZIP 家族看条目；
    /// 后缀只是弱提示（`.apk`/`.docx` 里是裸字节时不许当内容物）。
    /// </summary>
    public sealed class FileContentProbeTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-content-probe-" + Guid.NewGuid().ToString("N"));

        public FileContentProbeTests()
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
            }
        }

        [Fact]
        public void 普通产物不探测_只有准入范围才探测()
        {
            string path = Write("普通.png", BuildWebp());

            /*
             * FileNature 只负责底层格式/分卷事实，不再替所有调用点做内容识别。
             */
            Assert.Equal(FileContentClass.Unknown, FileNature.Inspect(path).ContentClass);
            Assert.Equal(
                FileContentClass.Unknown,
                ContentAdmission.Probe(path, ContentAdmissionScope.None, deep: true).Class);
            Assert.Equal(
                FileContentClass.KnownContent,
                ContentAdmission.Probe(path, ContentAdmissionScope.UserSource, deep: true).Class);
        }

        [Fact]
        public void FileNature同路径同版本复用同一张卡()
        {
            string path = Write("缓存.png", BuildWebp());

            FileNatureCard first = FileNature.Inspect(path);
            FileNatureCard second = FileNature.Inspect(path);

            Assert.Same(first, second);
        }

        [Fact]
        public void 普通WebP_识别为内容物()
        {
            string path = Write("336.png", BuildWebp());

            FileContentProbeResult result = FileContentProbe.Probe(path, deep: true);

            Assert.Equal(FileContentClass.KnownContent, result.Class);
            Assert.Equal("WebP", result.Format);
        }

        [Fact]
        public void MP4和MP3和FLAC_识别为内容物()
        {
            string mp4 = Write(
                "视频.mp4",
                new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' });
            string mp3 = Write("音频.mp3", Encoding.ASCII.GetBytes("ID3\u0004\u0000\u0000"));
            string flac = Write("音频.flac", Encoding.ASCII.GetBytes("fLaC"));

            Assert.Equal("MP4/MOV", FileContentProbe.Probe(mp4, true).Format);
            Assert.Equal("MP3", FileContentProbe.Probe(mp3, true).Format);
            Assert.Equal("FLAC", FileContentProbe.Probe(flac, true).Format);
        }

        [Fact]
        public void PDF和TXT_识别为内容物()
        {
            string pdf = Write("书.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n"));
            string txt = Write("小说.txt", Encoding.UTF8.GetBytes("第一章\n正文"));

            Assert.Equal(FileContentClass.KnownContent, FileContentProbe.Probe(pdf, true).Class);
            Assert.Equal(FileContentClass.KnownContent, FileContentProbe.Probe(txt, true).Class);
        }

        [Fact]
        public void OOXML和APK按ZIP条目识别_普通ZIP仍是归档()
        {
            string docx = Path.Combine(_root, "说明.docx");
            WriteZip(docx, "[Content_Types].xml", "word/document.xml");

            string apk = Path.Combine(_root, "安装包.zip");
            WriteZip(apk, "AndroidManifest.xml", "classes.dex");

            string plain = Path.Combine(_root, "普通.zip");
            WriteZip(plain, "readme.txt");

            Assert.Equal(FileContentClass.KnownContent, FileContentProbe.Probe(docx, true).Class);
            Assert.Equal("OOXML", FileContentProbe.Probe(docx, true).Format);

            Assert.Equal(FileContentClass.KnownContent, FileContentProbe.Probe(apk, true).Class);
            Assert.Equal("APK", FileContentProbe.Probe(apk, true).Format);

            Assert.Equal(FileContentClass.Archive, FileContentProbe.Probe(plain, true).Class);
        }

        [Fact]
        public void 后缀像阿帕克但内容是裸字节_不许当内容物()
        {
            string path = Write("被改名的尾卷.apk", new byte[256]);
            string docx = Write("被改名的尾卷.docx", new byte[256]);

            Assert.Equal(FileContentClass.Unknown, FileContentProbe.Probe(path, deep: true).Class);
            Assert.Equal(FileContentClass.Unknown, FileContentProbe.Probe(docx, deep: true).Class);
        }

        [Fact]
        public void 媒体前缀的完整ZIP_仍是归档而不是内容物()
        {
            byte[] webp = BuildWebp();
            byte[] zip = BuildZip("payload.txt");

            var bytes = new byte[webp.Length + zip.Length];
            webp.CopyTo(bytes, 0);
            zip.CopyTo(bytes, webp.Length);

            string path = Write("视频.png", bytes);
            FileNatureCard card = FileNature.Inspect(path);

            Assert.True(card.EmbeddedOffset > 0);
            Assert.Equal(FileContentClass.Archive, card.ContentClass);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
        }

        private string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void WriteZip(string path, params string[] entries)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            foreach (string entry in entries)
            {
                ZipArchiveEntry item = zip.CreateEntry(entry);
                using StreamWriter writer = new(item.Open(), new UTF8Encoding(false));
                writer.Write("content");
            }
        }

        private static byte[] BuildZip(string entry)
        {
            using var stream = new MemoryStream();

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry item = zip.CreateEntry(entry);
                using StreamWriter writer = new(item.Open(), new UTF8Encoding(false));
                writer.Write("payload");
            }

            return stream.ToArray();
        }

        private static byte[] BuildWebp()
        {
            var bytes = new byte[24];

            byte[] signature = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
            signature.CopyTo(bytes, 0);

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
    }
}
