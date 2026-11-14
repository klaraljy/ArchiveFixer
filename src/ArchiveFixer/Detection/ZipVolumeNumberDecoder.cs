using System;
using System.Buffers.Binary;

namespace ArchiveFixer.Detection;

/// <summary>
/// **跨盘 / 分片 ZIP 的内容卷号解码**：从文件**尾部 EOCD**（<c>PK\x05\x06</c>）读盘号与总片数，
/// 从**开头签名**认"这一片像不像 zip 流"，并把两条后续定序要用的内容事实留在读数上
/// （<see cref="VolumeNumberReading.HasZipEndRecord"/> / <see cref="VolumeNumberReading.HasZipLocalHeader"/>）。
///
/// <list type="bullet">
/// <item><description><b>跨盘 ZIP</b>：EOCD 偏移 4 = 本盘号、偏移 6 = 中央目录起始盘号；
/// 两个都 0 = 单盘。EOCD 只在**末片**且必须在文件最末（注释长度要自洽）→ 本盘号 + 1 = 总片数。</description></item>
/// <item><description><b>非末片</b>内容里没有盘号，只能靠开头是不是 PK 签名认"它是 zip 流"
/// （真 PKZIP 跨盘的第 1 片以跨盘标记 <c>PK\x07\x08</c> 开头，不是本地文件头 —— 用户 2026-09-29 真样本），
/// 位置则由末片的盘号 + 片数 + 尺寸规律消去法定（见 <see cref="VolumeNumberGroupResolver"/>）。</description></item>
/// </list>
///
/// <para>⛔ 拿不准一律不认：EOCD 注释长度不自洽、盘号自相矛盾、片数 ≥ 3 时中间几片没有盘号 ——
/// 全部落成"没认出来"，由调用方原样不动。</para>
///
/// <para>生命周期：纯静态、无状态；只读文件头尾若干字节（经 <see cref="VolumeContentFormatProbe.ReadBytes"/>），
/// 不建目录、不改名字、不调引擎。</para>
/// </summary>
internal static class ZipVolumeNumberDecoder
{
    /// <summary>EOCD 定长部分（签名 4 + 18）。</summary>
    private const int EndOfCentralDirectorySize = 22;

    /// <summary>zip 注释最长 65535 —— EOCD 最多往回找这么多字节。</summary>
    private const int ZipCommentMaxLength = 0xFFFF;

    /// <summary>读一卷（先找尾部 EOCD；找不到再看开头是不是 PK 签名）。</summary>
    public static VolumeNumberReading Read(string filePath)
    {
        long length = VolumeFileFacts.SizeOf(filePath);

        if (length < EndOfCentralDirectorySize)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
        }

        int tailLength = (int)Math.Min(length, EndOfCentralDirectorySize + ZipCommentMaxLength);
        byte[] tail = VolumeContentFormatProbe.ReadBytes(filePath, length - tailLength, tailLength);

        if (tail.Length < tailLength)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
        }

        // EOCD 必须在文件最末：起点 + 22 + 注释长度 == 文件长度（注释长度自洽）。
        for (int index = tailLength - EndOfCentralDirectorySize; index >= 0; index--)
        {
            if (tail[index] != 0x50 || tail[index + 1] != 0x4B
                || tail[index + 2] != 0x05 || tail[index + 3] != 0x06)
            {
                continue;
            }

            ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2));

            if (index + EndOfCentralDirectorySize + commentLength != tailLength)
            {
                continue;
            }

            return FromEndOfCentralDirectory(filePath, tail.AsSpan(index));
        }

        /*
         * 不是末片：**只要开头是任意一个 PK 签名**就算这一组的成员（用户 2026-09-29 真样本）。
         *
         * ⚠ 老写法要求"开头是本地文件头 PK\x03\x04 **且** 结尾是跨盘标记 PK\x07\x08"，真样本两条都不满足：
         * 真 PKZIP / 网盘那种跨盘 zip 的第 1 片**以跨盘标记 PK\x07\x08 开头**（7-Zip 管这 4 个字节叫
         * Embedded Stub），而切点落在数据中间，文件结尾根本不是标记。于是"第一片认不出来" →
         * 整组凑不齐 → 一个字节都不敢动。
         *
         * ⛔ 放松的只有"这一片像不像 zip 流"这一条判据；"哪一片是第几片"仍然只由 EOCD 的盘号 + 片数 +
         * 尺寸规律决定（见 ResolveZipGroup），拿不准一律不认。
         */
        byte[] head = VolumeContentFormatProbe.ReadBytes(filePath, 0, 64);

        if (IsZipSignature(head))
        {
            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Zip,
                Size = VolumeFileFacts.SizeOf(filePath),
                IsVolumeMember = true,
                /*
                 * 「开头是不是**本地文件头** `PK\x03\x04`」＝ 通用分片族里"这是第 1 片"的内容证据
                 * （设计文档 §6.6 A / 工单 #10）。中间片是从数据中间切开的，开头是别的 PK 签名。
                 *
                 * ⚠ 2026-10-24 真机 940 批 `16161616`：第 1 片开头是**跨盘标记** `PK\x07\x08`
                 * （4 字节 Embedded Stub），本地文件头在它**后面**。只看前 4 字节会把第一片判成
                 * “没有本地文件头” ⇒ 组里丢第 1 槽 ⇒ 整组 Unknown。所以在头部前 64 字节里找
                 * 本地文件头，而不是只认 offset 0。
                 */
                HasZipLocalHeader = ContainsZipLocalHeader(head),
                Number = null, // 非末片内容里没有盘号：位置只能靠消去法
                Fail = VolumeNumberFail.None
            };
        }

        return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.NotVolumeMember);
    }

    /// <summary>
    /// 这 4 个字节是不是 zip 流的开头签名：本地文件头 <c>PK\x03\x04</c>、中央目录 <c>PK\x01\x02</c>、
    /// EOCD <c>PK\x05\x06</c>、数据描述符/跨盘标记 <c>PK\x07\x08</c>。
    ///
    /// <para>四个都收的理由：跨盘 zip 的每一片都是从**字节流中间**切出来的，切点落在哪儿都不奇怪 ——
    /// 真正的闸门不是"开头像不像本地文件头"，而是"整组能不能连成 1..N"（片数、盘号、尺寸）。</para>
    /// </summary>
    private static bool IsZipSignature(ReadOnlySpan<byte> head) =>
        head.Length >= 4
        && head[0] == 0x50
        && head[1] == 0x4B
        && ((head[2] == 0x03 && head[3] == 0x04)
            || (head[2] == 0x01 && head[3] == 0x02)
            || (head[2] == 0x05 && head[3] == 0x06)
            || (head[2] == 0x07 && head[3] == 0x08));

    /// <summary>
    /// 头部前 64 字节里有没有**结构合理**的 ZIP 本地文件头 <c>PK\x03\x04</c>。
    /// 跨盘 ZIP 第 1 片允许先出现 4 字节跨盘标记（<c>PK\x07\x08</c>），所以不能只认 offset 0。
    /// ⛔ 只认 4 字节签名会把“魔数 + 全零”的坏文件也当第一片；必须再过版本/文件名长度两条。
    /// </summary>
    private static bool ContainsZipLocalHeader(ReadOnlySpan<byte> head)
    {
        for (int offset = 0; offset + 30 <= head.Length; offset++)
        {
            if (head[offset] != 0x50
                || head[offset + 1] != 0x4B
                || head[offset + 2] != 0x03
                || head[offset + 3] != 0x04)
            {
                continue;
            }

            ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(offset + 4, 2));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(offset + 26, 2));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(offset + 28, 2));

            if (versionNeeded >= 10 && nameLength > 0 && extraLength <= 4096)
            {
                return true;
            }
        }

        return false;
    }

    private static VolumeNumberReading FromEndOfCentralDirectory(string filePath, ReadOnlySpan<byte> eocd)
    {
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(4, 2));
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(6, 2));

        if (diskNumber == 0 && centralDirectoryDisk == 0)
        {
            /*
             * ⚠ 2026-10-18（运行期取证补上这条判据的另一半）：**盘号 0/0 不等于"单卷"** ——
             * 7-Zip `-v` 切出来的末片 EOCD 同样写盘号 0，它与单卷 zip 的唯一区别就是上面那条判据的
             * 前半句：**中央目录偏移 ≥ 本文件长度**（中央目录起在更早的片里）。
             * 真机 6666 逐字：`打包111-L2.zip.000003`（10 490 952 字节）的 EOCD 写 cdOffset=62 919 611。
             * 原来这里直接按 `ZipSingleDisk` 早退 ⇒ 卡上 `单卷/分卷=SingleComplete`、`参与分卷那一套=False`
             * ⇒ 末片被排除在收拢之外，整组永远拼不起来（① ② 双红）。
             *
             * 返回的 `Fail=None` 是刻意的：**"判不出它是第几片"不等于"可证它不是分卷成员"**
             * （后者会让 `ClassifyMembership` 判成 ProvablyNotVolumeMember，又把这一片排除掉）。
             */
            long splitFileLength = VolumeFileFacts.SizeOf(filePath);

            if (eocd.Length >= 20 && splitFileLength > 0)
            {
                long declaredCentralDirectoryOffset =
                    BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(16, 4));

                if (declaredCentralDirectoryOffset >= splitFileLength)
                {
                    return new VolumeNumberReading
                    {
                        Path = filePath,
                        Format = VolumeContentFormat.Zip,
                        Size = splitFileLength,
                        IsVolumeMember = false,
                        HasZipEndRecord = true,
                        ZipEndRecordMeansSplit = true,
                        Fail = VolumeNumberFail.None
                    };
                }
            }

            /*
             * 两个盘号都是 0 = 单盘 zip（7z 的 -v 切出来的"裸分片 zip"末片也是这个样子）。
             * ⚠ **但"尾部有 EOCD"这条内容事实本身仍然是证据**（设计文档 §6.6 A / 工单 #10）：
             * `X.zip.001..00N` 那一族里，**只有最后一片的尾部有 EOCD**，其余是裸切块。
             * ⇒ 这里把 `HasZipEndRecord` 置真再返回，让定序能用它认末片（⛔ 不再只靠序号）。
             */
            VolumeNumberReading singleDisk = VolumeContentEvidence.NotAVolume(
                filePath, VolumeContentFormat.Zip, VolumeNumberFail.ZipSingleDisk);

            /*
             * 单卷 zip ⇒ 中央目录就在自己身上 ⇒ `ZipEndRecordMeansSplit` 为假。
             * （只有"中央目录偏移 ≥ 本文件长度"那种 EOCD 才说明它是**分片的最后一片**。）
             */
            return new VolumeNumberReading
            {
                Path = singleDisk.Path,
                Format = singleDisk.Format,
                Size = singleDisk.Size,
                IsVolumeMember = singleDisk.IsVolumeMember,
                Number = singleDisk.Number,
                Total = singleDisk.Total,
                Fail = singleDisk.Fail,
                HasZipEndRecord = true,
                ZipEndRecordMeansSplit = false
            };
        }

        if (diskNumber == 0 || centralDirectoryDisk > diskNumber)
        {
            // 自相矛盾（中央目录不可能起在比本盘更靠后的盘上）。
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Zip, VolumeNumberFail.HeadersUnreadable);
        }

        return new VolumeNumberReading
        {
            Path = filePath,
            Format = VolumeContentFormat.Zip,
            Size = VolumeFileFacts.SizeOf(filePath),
            IsVolumeMember = true,
            Number = diskNumber + 1, // 盘号 0 起
            Total = diskNumber + 1,  // 末片：本盘号 + 1 = 总片数
            HasZipEndRecord = true,
            ZipEndRecordMeansSplit = true,   // 盘号 > 0 ⇒ 这是真跨盘末片
            Fail = VolumeNumberFail.None
        };
    }
}
