using System;
using System.Buffers.Binary;

namespace ArchiveFixer.Detection;

/// <summary>
/// **RAR 内容卷号解码**（RAR5 与 RAR 1.5–4.x，WinRAR 里后者叫 RAR4）：从主头 / 卷尾归档结尾块里读出
/// "它是不是分卷组的一员、是第几卷"，以及"这一卷就是第 1 卷"的头标记。
///
/// <list type="bullet">
/// <item><description><b>RAR5</b>：主归档头（headerType=1）的 archiveFlags 带 <c>0x0001</c> = 这是分卷组的一员；
/// 带 <c>0x0002</c> = 后面还有卷号字段（**第 1 卷没有这个字段**），字段值 v → 卷号 = v + 1。</description></item>
/// <item><description><b>RAR 1.5–4.x</b>（签名 <c>Rar!\x1A\x07\x00</c> 与 RAR3 是**同一个**，靠它根本分不出 3 与 4，
/// 所以本层一律不断言"这一卷是 RAR3"）：卷号**不在**主头里，而在**卷尾归档结尾块**
/// （type <c>0x7B</c>）的 <c>EARC_VOLNUMBER</c> 字段（flags <c>0x0008</c>）。块里可选字段按
/// <c>EARC_DATACRC(4)</c> → <c>EARC_VOLNUMBER(2)</c> → 保留区(7) 的**顺序**排，各自看出没出那一位 flag，
/// 所以卷号位置 = <c>块起点 + 7 + (有 DATACRC ? 4 : 0)</c>。⚠ 基数（0 起还是 1 起）RAR 的文档没写
/// → 交给"整组必须连成 1..N"自洽判定（见 <see cref="VolumeNumberGroupResolver"/>）。</description></item>
/// </list>
///
/// <para>⛔ 拿不准一律不认：头截断、CRC 对不上、归档头被加密、老式编号族 —— 全部落成
/// <see cref="VolumeNumberFail"/> 的对应档，绝不猜（改错名字比不改更糟）。</para>
///
/// <para>生命周期：纯静态、无状态；只读文件头尾若干字节（经 <see cref="VolumeContentFormatProbe.ReadBytes"/>），
/// 不建目录、不改名字、不调引擎。</para>
/// </summary>
internal static class RarVolumeNumberDecoder
{
    /// <summary>RAR5 主归档头的 headerType。</summary>
    private const ulong Rar5MainHeaderType = 1;

    /// <summary>RAR5 归档加密头（这种归档的后续头都是密文，读不出卷号）。</summary>
    private const ulong Rar5EncryptionHeaderType = 4;

    /// <summary>RAR5 archiveFlags：这是分卷组的一员。</summary>
    private const ulong Rar5ArchiveVolume = 0x0001;

    /// <summary>RAR5 archiveFlags：后面还有卷号字段（第 1 卷没有）。</summary>
    private const ulong Rar5ArchiveVolumeNumber = 0x0002;

    /// <summary>RAR5 headerFlags：头里带可选扩展区（扩展区大小字段存在）。</summary>
    private const ulong Rar5HeaderExtraArea = 0x0001;

    /// <summary>RAR5 headerFlags：头里带可选数据区（数据区大小字段存在）。</summary>
    private const ulong Rar5HeaderDataArea = 0x0002;

    /// <summary>RAR 1.5–4.x 主头 TYPE（<c>MHD_HEAD</c>）。</summary>
    private const byte Rar4LegacyMainHeaderType = 0x73;

    /// <summary>RAR 1.5–4.x 归档结尾块 TYPE（<c>ENDARC_HEAD</c>）。</summary>
    private const byte Rar4LegacyEndBlockType = 0x7B;

    /// <summary>RAR 1.5–4.x 主头 flags：这是分卷组的一员（<c>MHD_VOLUME</c>）。</summary>
    private const ushort Rar4LegacyVolume = 0x0001;

    /// <summary>RAR 1.5–4.x 主头 flags：新式编号（<c>partN.rar</c>）；不设 = 老式（<c>.rar</c>/<c>.r00</c>）。</summary>
    private const ushort Rar4LegacyNewNumbering = 0x0010;

    /// <summary>RAR 1.5–4.x 主头 flags：这一卷就是第 1 卷（<c>MHD_FIRSTVOLUME</c>）。</summary>
    private const ushort Rar4LegacyFirstVolume = 0x0100;

    /// <summary>RAR 1.5–4.x 归档结尾块 flags：块里有 <c>EARC_VOLNUMBER</c> 卷号字段。</summary>
    private const ushort Rar4LegacyEndBlockVolumeNumber = 0x0008;

    /// <summary>RAR 1.5–4.x 归档结尾块 flags：块里有 <c>EARC_DATACRC</c>（4 字节，排在卷号**前面**）。</summary>
    private const ushort Rar4LegacyEndBlockDataCrc = 0x0002;

    /// <summary>RAR 1.5–4.x 归档结尾块 flags：块尾有 7 字节保留区（<c>EARC_REVSPACE</c>，排在最后）。</summary>
    private const ushort Rar4LegacyEndBlockReservedSpace = 0x0004;

    /// <summary>RAR 1.5–4.x 归档结尾块末尾的保留区（7 字节，<c>EARC_REVSPACE</c>）。</summary>
    private const int Rar4LegacyEndBlockReservedSize = 7;

    /// <summary>RAR5 头大小上限：格式自己规定"不许超过 3 字节 vint"，也就是 2 MB。</summary>
    private const int Rar5MaxHeaderSize = 2 * 1024 * 1024;

    /// <summary>一次读进内存的 RAR5 头字节上限（头本身的合法上限 + vint 最长 10 字节）。</summary>
    private const int Rar5ReadLimit = Rar5MaxHeaderSize + 16;

    /// <summary>读 RAR 签名（第 7 字节：<c>0x01 0x00</c> = RAR5；<c>0x00</c> = RAR 1.5–4.x/RAR4）并分派到对应解码。</summary>
    public static VolumeNumberReading Read(string filePath)
    {
        byte[] head = VolumeContentFormatProbe.ReadBytes(filePath, 0, 8);

        if (head.Length < 8)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        // 签名第 7 字节：0x01 0x00 = RAR5；0x00 = RAR 1.5–4.x（WinRAR 里叫 RAR4；两者签名相同）。
        return head[6] == 0x01 && head[7] == 0x00
            ? ReadRar5(filePath)
            : ReadRar4Legacy(filePath);
    }

    /// <summary>RAR5：解析主归档头（字段布局取自 RAR 5.0 官方格式说明，见类注释里的链接口径）。</summary>
    private static VolumeNumberReading ReadRar5(string filePath)
    {
        byte[] raw = VolumeContentFormatProbe.ReadBytes(filePath, 0, 12 + Rar5ReadLimit);

        if (raw.Length < 12 + 4)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));

        int offset = 12;

        if (!VolumeHeaderPrimitives.TryReadVint(raw, ref offset, out ulong headerSize)
            || headerSize == 0
            || headerSize > Rar5MaxHeaderSize)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        // HEAD_SIZE 从 Header type 字段算起，而 offset 此刻正指着 Header type 字段。
        int headerEnd = offset + (int)headerSize;

        if (headerEnd <= offset || headerEnd > raw.Length)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        ReadOnlySpan<byte> header = raw.AsSpan(0, headerEnd);

        /*
         * 头部 CRC32 覆盖 [Header size 字段, 头末尾]（官方说明：CRC32 of header data starting from
         * Header size field and up to and including the optional extra area）。它是"这真是 RAR5 头"的硬证据。
         *
         * ⚠ 取不取补两种约定都收：文档只写"CRC32"，而参考实现里这个值取不取补在历史上并不统一，
         * 本机又造不出 RAR 样本可验。收宽一点才不会把真样本挡在门外 —— 真正的闸门是
         * "签名 + 字段自洽 + 整组卷号连成 1..N"，CRC 只用来挡随机字节。
         */
        if (!VolumeHeaderPrimitives.CrcMatches(storedCrc, header[12..]))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        int p = offset;

        if (!VolumeHeaderPrimitives.TryReadVint(header, ref p, out ulong headerType))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if (headerType == Rar5EncryptionHeaderType)
        {
            // 头被加密：卷号在密文里，读不出来（如实说不认，绝不猜）。
            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Rar,
                Size = VolumeFileFacts.SizeOf(filePath),
                RarVariant = RarVariant.Rar5HeadersEncrypted,
                Fail = VolumeNumberFail.HeadersUnreadable
            };
        }

        if (headerType != Rar5MainHeaderType)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
        }

        if (!VolumeHeaderPrimitives.TryReadVint(header, ref p, out ulong flags))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if ((flags & Rar5HeaderExtraArea) != 0 && !VolumeHeaderPrimitives.TryReadVint(header, ref p, out _))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if ((flags & Rar5HeaderDataArea) != 0 && !VolumeHeaderPrimitives.TryReadVint(header, ref p, out _))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if (!VolumeHeaderPrimitives.TryReadVint(header, ref p, out ulong archiveFlags))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if ((archiveFlags & Rar5ArchiveVolume) == 0)
        {
            // 主归档头读出来了、Volume 位 = 0 ⇒ 这是一份完整单卷（⛔ 不是"结构不对"那一档）。
            return VolumeContentEvidence.NotAVolume(
                filePath,
                VolumeContentFormat.Rar,
                VolumeNumberFail.NotVolumeMember,
                mainHeaderRead: true);
        }

        ulong rawField = 0;
        int number = 1;

        if ((archiveFlags & Rar5ArchiveVolumeNumber) != 0)
        {
            if (!VolumeHeaderPrimitives.TryReadVint(header, ref p, out rawField) || rawField >= int.MaxValue - 1)
            {
                return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
            }

            // 官方说明：字段值 1 = 第 2 卷、2 = 第 3 卷……（第 1 卷没有这个字段）。
            number = (int)rawField + 1;
        }

        return new VolumeNumberReading
        {
            Path = filePath,
            Format = VolumeContentFormat.Rar,
            Size = VolumeFileFacts.SizeOf(filePath),
            IsVolumeMember = true,
            Number = number,
            RawField = (int)rawField,
            /*
             * "第 1 卷"标记同样落在卡上（RAR5 就是"没有卷号字段"那一份）——
             * 组定序那一档要用它与内容卷号互相印证，两族同一把尺子。
             */
            FirstVolumeFlag = number == 1,
            RarNumbering = RarNumberingFamily.New,
            RarVariant = RarVariant.Rar5,
            Fail = VolumeNumberFail.None
        };
    }

    /// <summary>
    /// **媒体前缀里的 RAR5 主头**（2026-11-11）：只验证“从 <paramref name="fileOffset"/> 起确实是
    /// RAR5 主归档头”，供 <see cref="EmbeddedArchiveLocator"/> 的媒体前缀锚点使用。
    /// ⛔ 不判卷号/不建组；卷号仍走正常 RAR5 那条。
    /// </summary>
    internal static bool TryValidateRar5At(
        string filePath,
        long fileOffset,
        out bool singleComplete)
    {
        singleComplete = false;

        byte[] raw = VolumeContentFormatProbe.ReadBytes(filePath, fileOffset, 12 + Rar5ReadLimit);

        if (raw.Length < 12 + 4
            || raw[0] != 0x52 || raw[1] != 0x61 || raw[2] != 0x72 || raw[3] != 0x21
            || raw[4] != 0x1A || raw[5] != 0x07 || raw[6] != 0x01 || raw[7] != 0x00)
        {
            return false;
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
        int offset = 12;

        if (!VolumeHeaderPrimitives.TryReadVint(raw, ref offset, out ulong headerSize)
            || headerSize == 0
            || headerSize > Rar5MaxHeaderSize)
        {
            return false;
        }

        int headerEnd = offset + (int)headerSize;

        if (headerEnd <= offset || headerEnd > raw.Length)
        {
            return false;
        }

        if (!VolumeHeaderPrimitives.CrcMatches(storedCrc, raw.AsSpan(12, headerEnd - 12)))
        {
            return false;
        }

        int p = offset;

        if (!VolumeHeaderPrimitives.TryReadVint(raw, ref p, out ulong headerType)
            || headerType != Rar5MainHeaderType
            || !VolumeHeaderPrimitives.TryReadVint(raw, ref p, out ulong flags))
        {
            return false;
        }

        if ((flags & Rar5HeaderExtraArea) != 0 && !VolumeHeaderPrimitives.TryReadVint(raw, ref p, out _))
        {
            return false;
        }

        if ((flags & Rar5HeaderDataArea) != 0 && !VolumeHeaderPrimitives.TryReadVint(raw, ref p, out _))
        {
            return false;
        }

        if (!VolumeHeaderPrimitives.TryReadVint(raw, ref p, out ulong archiveFlags))
        {
            return false;
        }

        singleComplete = (archiveFlags & Rar5ArchiveVolume) == 0;

        return true;
    }

    /// <summary>
    /// RAR 1.5–4.x（WinRAR 里叫 RAR4）：主头读编号族与"第 1 卷"标记，卷号从**卷尾归档结尾块**里取。
    /// </summary>
    private static VolumeNumberReading ReadRar4Legacy(string filePath)
    {
        long length = VolumeFileFacts.SizeOf(filePath);

        if (length < 7 + 13 + 7)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        byte[] main = VolumeContentFormatProbe.ReadBytes(filePath, 7, 13);

        if (main.Length < 13 || main[2] != Rar4LegacyMainHeaderType)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.NotVolumeMember);
        }

        ushort mainCrc = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(0, 2));
        ushort mainFlags = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(3, 2));
        ushort mainSize = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(5, 2));

        /*
         * RAR 1.5–4.x 的 HEAD_SIZE 把 HEAD_CRC 一起算进去（主头就是 13 字节），块的范围是
         * [起点, 起点 + HEAD_SIZE)，校验范围是 [HEAD_TYPE, 块末尾)。
         */
        if (mainSize < 13 || 7 + mainSize > length)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        byte[] mainFull = VolumeContentFormatProbe.ReadBytes(filePath, 7, mainSize);

        if (mainFull.Length < mainSize || !VolumeHeaderPrimitives.CrcMatches16(mainCrc, mainFull.AsSpan(2)))
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, VolumeNumberFail.HeadersUnreadable);
        }

        if ((mainFlags & Rar4LegacyVolume) == 0)
        {
            // 主头 CRC 已通过、MHD_VOLUME = 0 ⇒ 完整单卷。
            return VolumeContentEvidence.NotAVolume(
                filePath,
                VolumeContentFormat.Rar,
                VolumeNumberFail.NotVolumeMember,
                mainHeaderRead: true);
        }

        bool newNumbering = (mainFlags & Rar4LegacyNewNumbering) != 0;
        bool firstVolume = (mainFlags & Rar4LegacyFirstVolume) != 0;

        if (!newNumbering)
        {
            /*
             * 老式编号族（第一卷 .rar，之后 .r00/.r01……）。本程序只留**一个**名字出口
             * （partN.rar，用户 2026-09-29 要求），所以这一族一律不动 —— 猜错族就是把整组改成解不开的。
             */
            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Rar,
                Size = length,
                IsVolumeMember = true,
                RawField = null,
                FirstVolumeFlag = firstVolume,
                RarNumbering = RarNumberingFamily.Old,
                RarVariant = RarVariant.Rar4Old,
                Fail = VolumeNumberFail.RarOldNumbering
            };
        }

        int? volumeField = TryReadRar4LegacyEndBlockVolumeNumber(filePath, length, out VolumeNumberFail fail);

        if (volumeField == null)
        {
            return VolumeContentEvidence.NotAVolume(filePath, VolumeContentFormat.Rar, fail);
        }

        /*
         * ===== RAR4 新式分卷：**卷号 = 归档结尾块里的字段 + 1**（该字段 0 起）=====
         *
         * <para>2026-10-22，调试器 + 30 份真机批次 `7777` 定案：这一支原来只写 `RawField`、
         * **漏了 `Number`** ⇒ 卡上永远「卷号=判不出」⇒ 内容这条路证不出"1..N 齐了" ⇒ 组只能靠
         * 名字 + 试开；名字被伪装（`bbb-L2.AAAAAAAApart2`）或试开要密码时整组建不出来。
         * 实测三片 raw = 0 / 1 / 2 ⇒ 卷号 1 / 2 / 3，且首卷那片与 `FirstVolumeFlag` 互相印证。</para>
         *
         * <para>换算与 RAR5 那一支**逐字一致**（`number = (int)rawField + 1`）：同一件事只允许一种算法。
         * ⛔ 兜底：字段值超出合理范围（&gt; 9999）⇒ 按"卷号判不出"处理，⛔ 不猜。</para>
         */
        if (volumeField.Value > 9999)
        {
            return new VolumeNumberReading
            {
                Path = filePath,
                Format = VolumeContentFormat.Rar,
                Size = length,
                IsVolumeMember = true,
                RawField = volumeField,
                FirstVolumeFlag = firstVolume,
                RarNumbering = RarNumberingFamily.New,
                RarVariant = RarVariant.Rar4New,
                Fail = VolumeNumberFail.HeadersUnreadable
            };
        }

        return new VolumeNumberReading
        {
            Path = filePath,
            Format = VolumeContentFormat.Rar,
            Size = length,
            IsVolumeMember = true,
            Number = volumeField.Value + 1,
            RawField = volumeField,
            FirstVolumeFlag = firstVolume,
            RarNumbering = RarNumberingFamily.New,
            RarVariant = RarVariant.Rar4New,
            Fail = VolumeNumberFail.None
        };
    }

    /// <summary>
    /// 找卷尾的归档结尾块并取出卷号字段（原始值，基数待定）。
    ///
    /// <para><b>字段位置按官方布局**顺序**算，不写死偏移</b>：结尾块里可选字段按
    /// <c>EARC_DATACRC(4)</c> → <c>EARC_VOLNUMBER(2)</c> → 保留区(7) 的顺序排，各自看出没出那一位 flag。
    /// 所以卷号的偏移 = <c>7 + (有 DATACRC ? 4 : 0)</c>（7 = HEAD_CRC(2) + TYPE(1) + FLAGS(2) + SIZE(2)）。</para>
    ///
    /// <para><b>⚠ 老写法在这里是真错的（2026-09-29 真样本逮到）</b>：它把卷号当成 4 字节、按
    /// <c>块起点 + HEAD_SIZE − 11</c> 读 —— 真样本两卷因此读出 <c>0xCF7C</c> / <c>0x01EF02</c>
    /// （DATACRC 的尾巴混了进来），整组连不成 1..N，于是"RAR 分卷一律不认"。
    /// 真样本的字节是硬证据：两卷的 <c>HEAD_SIZE = 20 = 7 + 4 + 2 + 7</c>，卷号是紧跟 DATACRC 的
    /// **2 字节**（第 1 卷 0、第 2 卷 1），其后再 7 字节保留区；而且只有第 1 卷带
    /// <c>EARC_NEXT_VOLUME</c>（0x0001）—— 与"它是第 1 卷"的结论互相印证。</para>
    ///
    /// <para>块本身按"TYPE=0x7B、HEAD_SIZE 正好收在文件末尾、头部 CRC 对得上、各可选字段的总长与
    /// HEAD_SIZE 相等"四条一起认；布局与官方说明不符时**不认**（宁可不动，也不拿一个错偏移去凑卷号）。</para>
    /// </summary>
    private static int? TryReadRar4LegacyEndBlockVolumeNumber(
        string filePath,
        long length,
        out VolumeNumberFail fail)
    {
        fail = VolumeNumberFail.HeadersUnreadable;

        int tailLength = (int)Math.Min(length, 512);
        byte[] tail = VolumeContentFormatProbe.ReadBytes(filePath, length - tailLength, tailLength);

        if (tail.Length < tailLength)
        {
            return null;
        }

        for (int start = tailLength - 14; start >= 0; start--)
        {
            if (tail[start + 2] != Rar4LegacyEndBlockType)
            {
                continue;
            }

            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + 5, 2));

            // 块必须正好收在文件末尾：末尾之后 RAR 不再读任何东西。
            if (start + size != tailLength || size < 7 + Rar4LegacyEndBlockReservedSize)
            {
                continue;
            }

            ushort storedCrc = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start, 2));

            if (!VolumeHeaderPrimitives.CrcMatches16(storedCrc, tail.AsSpan(start + 2, size - 2).ToArray()))
            {
                continue;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + 3, 2));

            if ((flags & Rar4LegacyEndBlockVolumeNumber) == 0)
            {
                // 结尾块里没有卷号字段：这一卷的内容里就没有卷号（如实说不认）。
                fail = VolumeNumberFail.HeadersUnreadable;
                return null;
            }

            int fieldOffset = 7 + ((flags & Rar4LegacyEndBlockDataCrc) != 0 ? 4 : 0);
            int expectedSize = fieldOffset + 2
                + ((flags & Rar4LegacyEndBlockReservedSpace) != 0 ? Rar4LegacyEndBlockReservedSize : 0);

            if (size != expectedSize)
            {
                /*
                 * 各可选字段按 flag 算出来的总长与 HEAD_SIZE 对不上 —— 说明这一块的布局不是官方说明的那一套，
                 * 谁也不敢保证那个 2 字节字段就是卷号。⛔ 不认（改错名字比不改更糟）。
                 */
                fail = VolumeNumberFail.HeadersUnreadable;
                return null;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(start + fieldOffset, 2));
        }

        return null;
    }
}
