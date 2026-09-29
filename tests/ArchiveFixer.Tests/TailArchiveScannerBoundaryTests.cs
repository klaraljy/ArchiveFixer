using ArchiveFixer.Detection;
using System;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <see cref="TailArchiveScanner"/> 那一遍顺序扫的**分块边界**守门用例。
    ///
    /// <para>为什么要单独钉：2026-09-29 量化发现这一遍是 **CPU 密集**的（512 MiB 要 3.5 秒，
    /// 而同一块盘纯读只要 0.34 秒），于是把"逐字节比一比"改成了"先用 SIMD 的
    /// <c>IndexOfAny</c> 跳到下一个可能是签名首字节的位置"。改动的风险全在**块与块的交界**上
    /// （每轮只保留 7 个字节到下一轮，签名可能正好跨在两块之间）——
    /// 这几条用例就是拿"签名恰好骑在 4 MiB 边界上"来钉住它。</para>
    ///
    /// <para><b>红检</b>：把 <c>FindNextCandidate</c> 换回逐字节循环之外的做法（例如漏掉
    /// <c>carried</c> 重叠区、或把窗口长度算成 <c>total - start</c>）→
    /// <see cref="签名骑在4MiB分块边界上_照样命中"/> 当场变红。</para>
    /// </summary>
    public class TailArchiveScannerBoundaryTests : IDisposable
    {
        private const int ChunkBytes = 4 * 1024 * 1024;

        private static readonly byte[] Rar4Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };

        private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        private static readonly byte[] ZipEocd = { 0x50, 0x4B, 0x05, 0x06 };

        private static readonly byte[] ZipLocalHeader = { 0x50, 0x4B, 0x03, 0x04 };

        private readonly string _root;

        public TailArchiveScannerBoundaryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af_tail_scan_" + Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        /// <summary>
        /// 签名**骑在 4 MiB 分块边界上**（最后一个字节落在下一块的第一字节）：
        /// 上一轮只剩 7 个字节带进下一轮，这正是最容易被改坏的地方。
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void 签名骑在4MiB分块边界上_照样命中(int deltaFromBoundary)
        {
            long offset = ChunkBytes + deltaFromBoundary;

            string file = BuildFile("boundary.rar", offset, Rar4Magic);
            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.NotNull(result.Archive);
            Assert.Equal(offset, result.Archive!.Offset);
            Assert.Equal("RAR4", result.Archive.Format);
        }

        /// <summary>7z 魔数同样要能跨边界命中（它的首字节是 <c>0x37</c>）。</summary>
        [Fact]
        public void 七z魔数骑在分块边界上_照样命中()
        {
            long offset = ChunkBytes - 3;

            string file = BuildFile("boundary-7z.bin", offset, SevenZipMagic);
            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.NotNull(result.Archive);
            Assert.Equal(offset, result.Archive!.Offset);
            Assert.Equal("7Z", result.Archive.Format);
        }

        /// <summary>ZIP 的 EOCD 候选同样按签名找，骑在边界上也要被记下来。</summary>
        [Fact]
        public void EOCD候选骑在分块边界上_照样被记下来()
        {
            long offset = ChunkBytes - 1;

            string file = BuildFile("boundary-eocd.bin", offset, ZipEocd);
            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.Contains(offset, result.ZipEocdOffsets);
        }

        /// <summary>
        /// 偏移 0 上的签名**不算**"尾部内嵌"（那就是文件本身的头）。
        /// 同时钉住"第一命中即结论"：后面的第二个魔数不该顶掉它。
        /// </summary>
        [Fact]
        public void 偏移0的签名不算内嵌_而且取第一次命中()
        {
            string file = Path.Combine(_root, "two-hits.bin");

            var bytes = new byte[64 * 1024];

            Rar4Magic.CopyTo(bytes, 0);
            SevenZipMagic.CopyTo(bytes, 32 * 1024);

            File.WriteAllBytes(file, bytes);

            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.NotNull(result.Archive);
            Assert.Equal(32 * 1024, result.Archive!.Offset);
            Assert.Equal("7Z", result.Archive.Format);
        }

        /// <summary>
        /// "先见过 PK\x03\x04"那条路：命中 RAR 魔数之后**不许立刻收工**，
        /// 要把后面的 EOCD 候选一起收下来（"整体被顶偏的 ZIP 里装着一个 RAR"就是这种形状）。
        /// </summary>
        [Fact]
        public void 见过PK局部头之后_魔数命中也不立刻收工_继续收EOCD候选()
        {
            string file = Path.Combine(_root, "zip-then-rar.bin");

            var bytes = new byte[512 * 1024];

            ZipLocalHeader.CopyTo(bytes, 1024);
            Rar4Magic.CopyTo(bytes, 200 * 1024);
            ZipEocd.CopyTo(bytes, 400 * 1024);

            File.WriteAllBytes(file, bytes);

            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.True(result.SawZipLocalHeader);
            Assert.NotNull(result.Archive);
            Assert.Equal("RAR4", result.Archive!.Format);
            Assert.Contains(400L * 1024, result.ZipEocdOffsets);
        }

        /// <summary>反过来：没见过 PK 局部头时，第一个魔数命中就收工 —— 数一数候选，证明它真的没往后读。</summary>
        [Fact]
        public void 没见过PK局部头时_第一个魔数命中就收工()
        {
            string file = Path.Combine(_root, "rar-then-eocd.bin");

            var bytes = new byte[512 * 1024];

            Rar4Magic.CopyTo(bytes, 200 * 1024);
            ZipEocd.CopyTo(bytes, 400 * 1024);

            File.WriteAllBytes(file, bytes);

            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.False(result.SawZipLocalHeader);
            Assert.NotNull(result.Archive);
            Assert.Empty(result.ZipEocdOffsets);
        }

        /// <summary>什么都不含的大文件：扫完不命中，而且不许抛（结果为空是合法答案）。</summary>
        [Fact]
        public void 干干净净的大文件_扫完不命中()
        {
            string file = Path.Combine(_root, "clean.bin");

            using (FileStream stream = File.Create(file))
            {
                stream.SetLength(ChunkBytes + 123);
            }

            TailArchiveScanResult result = TailArchiveScanner.Scan(file);

            Assert.Null(result.Archive);
            Assert.Empty(result.ZipEocdOffsets);
            Assert.False(result.SawZipLocalHeader);
        }

        private string BuildFile(string name, long signatureOffset, byte[] signature)
        {
            string path = Path.Combine(_root, name);
            long length = signatureOffset + signature.Length + 4096;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            // 用递增花纹填充（不是全零）：全零文件里 0x50/0x52/0x37 一个都不出现，
            // 那样就测不出"候选筛选有没有漏掉东西"。
            var buffer = new byte[64 * 1024];

            for (int index = 0; index < buffer.Length; index++)
            {
                buffer[index] = (byte)((index % 200) + 1);
            }

            long written = 0;

            while (written < length)
            {
                long remaining = length - written;

                if (written <= signatureOffset && signatureOffset < written + buffer.Length)
                {
                    int gap = (int)(signatureOffset - written);

                    if (gap > 0)
                    {
                        stream.Write(buffer, 0, gap);
                        written += gap;
                    }

                    stream.Write(signature, 0, signature.Length);
                    written += signature.Length;

                    continue;
                }

                int take = (int)Math.Min(buffer.Length, remaining);
                stream.Write(buffer, 0, take);
                written += take;
            }

            stream.SetLength(length);

            return path;
        }
    }
}
