using System;
using System.Collections.Generic;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// RAR 分卷的真字节夹具（RAR5 / RAR 1.5–4.x）。
    ///
    /// <para>为什么要它：2026-10-17 起「归组先问内容卡」（`FileNature.Inspect`），
    /// 只写一个签名、后面全是零的假文件会被判成<b>可证不是分卷成员</b>
    /// （RAR 主头类型读不出来 ⇒ `NotVolumeMember`），于是按名字归组这一档根本轮不到
    /// ⇒ 夹具与"每一卷都是真分卷"这个前提对不上。要钉"名字归组"的能力，
    /// 夹具就必须是<b>真卷头</b>：RAR5 = 主头里 <c>MHFL_VOLUME</c> 开位，
    /// RAR4 = 主头 <c>MHD_FIRSTVOLUME</c> + 归档结尾块里的 <c>EARC_VOLNUMBER</c>。</para>
    ///
    /// <para>字节布局与 <c>VolumeNumberContentTests</c> / <c>VolumeEntryPointTests</c>
    /// 里那两个构造器同一口径（真样本实测：第 1 卷卷号字段 0、第 2 卷 1）。</para>
    /// </summary>
    internal static class RarVolumeFixtures
    {
        /// <summary>RAR 1.5–4.x 的一卷：签名 + 主头（13 字节）+ 归档结尾块（卷号在里面）。</summary>
        internal static byte[] Rar4(ushort volumeField, bool firstVolume, bool nextVolume)
        {
            const ushort mainFlags = 0x0001; // MHFL_VOLUME：是分卷组的一员

            ushort flags = (ushort)(mainFlags | 0x0010); // MHD_NEWNUMBERING

            if (firstVolume)
            {
                flags |= 0x0100; // MHD_FIRSTVOLUME
            }

            var bytes = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 };

            // 主头：TYPE(1)=0x73 FLAGS(2) SIZE(2)=13 RESERVED1(2) RESERVED2(4)；CRC16 覆盖这 11 字节。
            var main = new byte[] { 0x73, (byte)(flags & 0xFF), (byte)(flags >> 8), 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            bytes.AddRange(BitConverter.GetBytes(Crc16(main)));
            bytes.AddRange(main);

            ushort endFlags = 0x0002 | 0x0004 | 0x0008;

            if (nextVolume)
            {
                endFlags |= 0x0001; // EARC_NEXT_VOLUME：后面还有一卷
            }

            const ushort endSize = 7 + 4 + 2 + 7;

            var end = new List<byte> { 0x7B, (byte)(endFlags & 0xFF), (byte)(endFlags >> 8), (byte)(endSize & 0xFF), (byte)(endSize >> 8) };

            end.AddRange(new byte[] { 0x11, 0x22, 0x33, 0x44 }); // EARC_DATACRC
            end.AddRange(BitConverter.GetBytes(volumeField));      // EARC_VOLNUMBER
            end.AddRange(new byte[7]);                             // EARC_REVSPACE

            bytes.AddRange(BitConverter.GetBytes(Crc16(end.ToArray())));
            bytes.AddRange(end);

            return bytes.ToArray();
        }

        /// <summary>RAR5 的一卷：签名 + CRC32 + 主归档头（卷号在头里，第 1 卷没有那个字段）。</summary>
        internal static byte[] Rar5(uint? volumeField)
        {
            var body = new List<byte>();
            body.AddRange(new byte[] { 0x01 });                                    // Header type = 主归档头
            body.AddRange(new byte[] { 0x00 });                                    // Header flags
            body.AddRange(new byte[] { 0x03 });                                    // Archive flags = Volume | 有卷号字段

            if (volumeField.HasValue)
            {
                body.Add((byte)volumeField.Value);
            }

            var sizeField = new List<byte> { (byte)body.Count };

            var covered = new List<byte>();
            covered.AddRange(sizeField);
            covered.AddRange(body);

            var header = new List<byte> { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00 };

            header.AddRange(BitConverter.GetBytes(Crc32(covered)));
            header.AddRange(sizeField);
            header.AddRange(body);

            return header.ToArray();
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

        private static uint Crc32(IReadOnlyList<byte> data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte current in data)
            {
                crc = (crc >> 8) ^ CrcTable[(crc ^ current) & 0xFF];
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static ushort Crc16(IReadOnlyList<byte> data) => (ushort)(Crc32(data) & 0xFFFF);
    }
}
