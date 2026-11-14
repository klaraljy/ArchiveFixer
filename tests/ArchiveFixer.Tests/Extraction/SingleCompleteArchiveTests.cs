using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **单一完整压缩包的专属算法**（2026-10-22，用户口径：第一大步只处理单一完整压缩包）。
    ///
    /// <para>三组判据各钉一条：</para>
    /// <list type="number">
    /// <item><description><b>正常单卷</b>：RAR5 / RAR4 主头读成功且卷位 = 0 ⇒ <see cref="ArchiveBodyKind.SingleComplete"/>；
    /// 分卷首片/片 ⇒ <see cref="ArchiveBodyKind.VolumeFirstPiece"/>/<see cref="ArchiveBodyKind.VolumePiece"/>；
    /// 结构不对（主头 CRC 不过）⇒ <see cref="ArchiveBodyKind.Unknown"/>（⛔ 不许当单卷）。</description></item>
    /// <item><description><b>7z 单卷</b>：起始头自述总字节 = 本文件 ⇒ SingleComplete。</description></item>
    /// <item><description><b>媒体前缀</b>：前面垫数据、归档从偏移开始 ⇒ 7z 靠 StartHeaderCRC + NextHeaderCRC 自洽、
    /// ZIP 走既有 EOCD 自洽 ⇒ SingleComplete + <c>EmbeddedOffset &gt; 0</c>；自述字节超出文件 ⇒ 判不出。</description></item>
    /// </list>
    /// </summary>
    public sealed class SingleCompleteArchiveTests : IDisposable
    {
        private readonly string _root;

        public SingleCompleteArchiveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSingleComplete", Guid.NewGuid().ToString("N"));
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

        // ── ① RAR 映射 ──

        [Fact]
        public void RAR5_普通单卷_卷位为0_判成单一完整()
        {
            string path = Write("rar5-single.bin", Rar5MainHeader(0u, null));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.Rar, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.False(card.ParticipatesInVolumeHandling);
        }

        [Fact]
        public void RAR5_首卷_判成第一片_不是单一完整()
        {
            string path = Write("rar5-first.bin", Rar5MainHeader(0x0001u, null));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(ArchiveBodyKind.VolumeFirstPiece, card.BodyKind);
            Assert.True(card.ParticipatesInVolumeHandling);
        }

        [Fact]
        public void RAR4_普通单卷_MHD_VOLUME为0_判成单一完整()
        {
            // mainFlags = 0 ⇒ MHD_VOLUME = 0（builder 会补"新编号"那一位，不影响卷位）
            string path = Write("rar4-single.bin", Rar4Archive(0x0000, 0, firstVolume: false));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.Rar, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
        }

        [Fact]
        public void RAR4_主头读不出_不许当成单一完整()
        {
            // 签名 + 13 字节主头，但 CRC 故意写错 ⇒ 结构不对那一档
            var bytes = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 };
            bytes.AddRange(new byte[] { 0xAA, 0xAA, 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            string path = Write("rar4-broken.bin", bytes.ToArray());

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(ArchiveBodyKind.Unknown, card.BodyKind);
        }

        // ── ② 7z 正常单卷 ──

        [Fact]
        public void SevenZip_普通单卷_自述等于本文件_判成单一完整()
        {
            byte[] archive = SevenZipArchive(nextHeader: new byte[] { 0x01, 0x02, 0x03, 0x04 });
            string path = Write("7z-single.bin", archive);

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.SevenZip, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.Equal(0, card.EmbeddedOffset);
        }

        // ── ③ 媒体前缀 ──

        [Fact]
        public void 媒体前缀_尾部7z单卷_判成单一完整_并记下前缀偏移()
        {
            byte[] prefix = MediaPrefix(192);
            byte[] archive = SevenZipArchive(nextHeader: new byte[] { 0x0A, 0x0B, 0x0C });
            string path = Write("media-7z.mp4", Concat(prefix, archive));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.SevenZip, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.Equal(prefix.Length, card.EmbeddedOffset);
            Assert.Equal(archive.Length, card.EmbeddedLength);
            Assert.False(card.ParticipatesInVolumeHandling);
        }

        [Fact]
        public void 媒体前缀_尾部7z自述超过文件长度_判不出()
        {
            byte[] prefix = MediaPrefix(64);
            byte[] archive = SevenZipArchive(nextHeader: new byte[] { 0x01, 0x02 });

            // 砍掉尾部 8 字节：起始头自述的整包字节数 > 文件剩余 ⇒ 不是完整单卷
            byte[] truncated = new byte[archive.Length - 8];
            Array.Copy(archive, truncated, truncated.Length);

            string path = Write("media-7z-cut.mp4", Concat(prefix, truncated));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(ArchiveBodyKind.Unknown, card.BodyKind);
            Assert.Equal(0, card.EmbeddedOffset);
        }

        [Fact]
        public void 媒体前缀_尾部7z的NextHeaderCRC对不上_判不出()
        {
            byte[] prefix = MediaPrefix(64);
            byte[] archive = SevenZipArchive(nextHeader: new byte[] { 0x01, 0x02, 0x03 });

            // 篡改下一个头的最后一个字节 ⇒ NextHeaderCRC 校验失败
            archive[archive.Length - 1] ^= 0xFF;

            string path = Write("media-7z-badcrc.mp4", Concat(prefix, archive));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(ArchiveBodyKind.Unknown, card.BodyKind);
        }

        [Fact]
        public void 媒体前缀_大7z归档_快扫漏掉_深扫要能找到()
        {
            // 前缀 6 MiB + 归档 8 MiB：归档起点离文件尾 8 MiB，超出快扫窗口（4 MiB）⇒ 只能靠深扫（≤512 MiB）
            byte[] prefix = MediaPrefix(6 << 20);
            byte[] archive = SevenZipArchiveWithPadding(nextHeader: new byte[] { 0x01, 0x02, 0x03 }, padding: 8 << 20);
            string path = Write("media-7z-big.mp4", Concat(prefix, archive));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.SevenZip, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.Equal(prefix.Length, card.EmbeddedOffset);
        }

        [Fact]
        public void 媒体前缀_大RAR4归档_快扫漏掉_深扫要能找到()
        {
            byte[] prefix = MediaPrefix(6 << 20);
            byte[] archive = Rar4Archive(0x0000, 0, firstVolume: false);
            var padded = new byte[archive.Length + (8 << 20)];
            Array.Copy(archive, padded, archive.Length);

            string path = Write("media-rar4-big.mp4", Concat(prefix, padded));

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.Rar, card.Format);
            Assert.Equal(ArchiveBodyKind.SingleComplete, card.BodyKind);
            Assert.Equal(prefix.Length, card.EmbeddedOffset);
        }
        // ── 构造器 ──

        private static byte[] MediaPrefix(int size)
        {
            var bytes = new byte[size];
            bytes[0] = 0x89;
            bytes[1] = 0x50;
            bytes[2] = 0x4E;
            bytes[3] = 0x47;

            for (int index = 4; index < size; index++)
            {
                bytes[index] = (byte)(index & 0x7F);
            }

            return bytes;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var bytes = new byte[first.Length + second.Length];
            Array.Copy(first, bytes, first.Length);
            Array.Copy(second, 0, bytes, first.Length, second.Length);

            return bytes;
        }

        /// <summary>合法的 7z：32 字节起始头（StartHeaderCRC 正确）+ next header（NextHeaderCRC 正确）。</summary>
        private static byte[] SevenZipArchive(byte[] nextHeader)
        {
            var bytes = new List<byte> { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 };

            ulong nextHeaderOffset = 0;
            ulong nextHeaderSize = (ulong)nextHeader.Length;
            uint nextHeaderCrc = TestCrc32(nextHeader);

            var startBody = new List<byte>();
            startBody.AddRange(BitConverter.GetBytes(nextHeaderOffset));
            startBody.AddRange(BitConverter.GetBytes(nextHeaderSize));
            startBody.AddRange(BitConverter.GetBytes(nextHeaderCrc));

            bytes.AddRange(BitConverter.GetBytes(TestCrc32(startBody.ToArray())));
            bytes.AddRange(startBody);
            bytes.AddRange(nextHeader);

            return bytes.ToArray();
        }

        /// <summary>合法的 7z，中间用 padding 个 0 字节把"打包流"撑大（模拟大归档）。</summary>
        private static byte[] SevenZipArchiveWithPadding(byte[] nextHeader, int padding)
        {
            var bytes = new List<byte> { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 };

            ulong nextHeaderOffset = (ulong)padding;
            ulong nextHeaderSize = (ulong)nextHeader.Length;
            uint nextHeaderCrc = TestCrc32(nextHeader);

            var startBody = new List<byte>();
            startBody.AddRange(BitConverter.GetBytes(nextHeaderOffset));
            startBody.AddRange(BitConverter.GetBytes(nextHeaderSize));
            startBody.AddRange(BitConverter.GetBytes(nextHeaderCrc));

            bytes.AddRange(BitConverter.GetBytes(TestCrc32(startBody.ToArray())));
            bytes.AddRange(startBody);
            bytes.AddRange(new byte[padding]);
            bytes.AddRange(nextHeader);

            return bytes.ToArray();
        }
        private static byte[] Rar5MainHeader(uint archiveFlags, uint? volumeField)
        {
            var body = new List<byte>();
            body.AddRange(Vint(1));          // header type = 主归档头
            body.AddRange(Vint(0));          // header flags
            body.AddRange(Vint(archiveFlags));

            if (volumeField.HasValue)
            {
                body.AddRange(Vint(volumeField.Value));
            }

            byte[] sizeField = Vint((ulong)body.Count).ToArray();

            var header = new List<byte> { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
            var covered = new List<byte>();
            covered.AddRange(sizeField);
            covered.AddRange(body);

            header.AddRange(BitConverter.GetBytes(TestCrc32(covered.ToArray())));
            header.AddRange(sizeField);
            header.AddRange(body);

            return header.ToArray();
        }

        private static byte[] Rar4Archive(ushort mainFlags, ushort volumeField, bool firstVolume)
        {
            ushort flags = mainFlags;

            flags |= 0x0010; // 新式编号族

            if (firstVolume)
            {
                flags |= 0x0100;
            }

            var bytes = new List<byte> { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
            var main = new List<byte> { 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            main[1] = (byte)(flags & 0xFF);
            main[2] = (byte)(flags >> 8);

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(main.ToArray())));
            bytes.AddRange(main);

            var end = new List<byte> { 0x7B, 0x00, 0x00 };
            ushort endFlags = 0x0002 | 0x0004 | 0x0008;
            ushort endSize = 20;
            end[1] = (byte)(endFlags & 0xFF);
            end[2] = (byte)(endFlags >> 8);
            end.Add((byte)(endSize & 0xFF));
            end.Add((byte)(endSize >> 8));
            end.AddRange(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            end.AddRange(BitConverter.GetBytes(volumeField));
            end.AddRange(new byte[7]);

            bytes.AddRange(BitConverter.GetBytes(TestCrc16(end.ToArray())));
            bytes.AddRange(end);

            return bytes.ToArray();
        }

        private static byte[] Vint(ulong value)
        {
            var bytes = new List<byte>();

            while (true)
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;

                if (value != 0)
                {
                    bytes.Add((byte)(current | 0x80));
                    continue;
                }

                bytes.Add(current);
                break;
            }

            return bytes.ToArray();
        }

        /// <summary>标准 CRC32（反射、终值取补）——7z 起始头/下一个头用的就是这一档。</summary>
        private static uint TestCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc ^= current;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>RAR 1.5–4.x 的 16 位头 CRC（真样本约定：CRC32 的低 16 位）。</summary>
        private static ushort TestCrc16(byte[] data) => (ushort)(TestCrc32Raw(data) & 0xFFFF);

        private static uint TestCrc32Raw(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc = (crc >> 8) ^ CrcTable[(crc ^ current) & 0xFF];
            }

            return crc;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (uint index = 0; index < 256; index++)
            {
                uint value = index;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        private string Write(string fileName, byte[] bytes)
        {
            string path = Path.Combine(_root, fileName);
            File.WriteAllBytes(path, bytes);

            return path;
        }
    }
}
