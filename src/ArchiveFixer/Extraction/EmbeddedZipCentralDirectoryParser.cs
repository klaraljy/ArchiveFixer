using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **流解析**阶段：把已经通过结构自洽校验的中央目录逐条翻成 <see cref="EmbeddedZipEntry"/> 清单
/// （ZIP64 占位符换真值、AES 扩展字段、条目名解码、数据起点复核），再交给落点规划与路径预检。
///
/// <para>两条刻意的规矩：① <b>尺寸一律以中央目录为准</b>（本地头里的尺寸在有数据描述符时是 0，
/// 9 成以上的工具都会写成 0）；② 本地头长度 = 30 + 名字长 + 扩展区长，**不是**固定 30。</para>
///
/// <para>⛔ 这里的任何"不支持"都发生在写盘之前：所以调用方看到回落信号时可以放心地原地回落到抠取。
/// 判据一项都不放松：ZIP64 占位符必须换成真值、加密只认 AES、方法只认 stored / deflate、
/// 数据区不越过中央目录起点、条目名走 <see cref="ArchivePathGuard"/> 同一处预检。</para>
/// </summary>
internal static class EmbeddedZipCentralDirectoryParser
{
    /// <summary>
    /// 逐条读中央目录（<c>PK\x01\x02</c>），再去本地头（<c>PK\x03\x04</c>）确认数据起点。
    /// </summary>
    internal static EmbeddedZipCandidateEvaluation BuildEntries(
        FileStream stream,
        byte[] centralDirectory,
        long offset,
        long zipLength,
        long cdRelStart,
        long declaredEntryCount,
        string? targetDirectory,
        IReadOnlyList<string>? passwords,
        CancellationToken cancellationToken,
        bool confirmResolvedPassword)
    {
        var entries = new List<EmbeddedZipEntry>();
        var listEntries = new List<ArchiveEntry>();
        int index = 0;
        long totalBytes = 0;
        bool hasAesEntry = false;

        while (index < centralDirectory.Length)
        {
            if (index + EmbeddedZipStreamFormat.CentralDirectoryHeaderLength > centralDirectory.Length ||
                !EmbeddedZipByteReader.Matches(
                    centralDirectory,
                    index,
                    EmbeddedZipStreamFormat.CentralDirectorySignature))
            {
                return EmbeddedZipCandidateEvaluation.NotACandidate;
            }

            int nameLength = BitConverter.ToUInt16(centralDirectory, index + 28);
            int extraLength = BitConverter.ToUInt16(centralDirectory, index + 30);
            int entryCommentLength = BitConverter.ToUInt16(centralDirectory, index + 32);

            if (index + EmbeddedZipStreamFormat.CentralDirectoryHeaderLength + nameLength + extraLength + entryCommentLength >
                centralDirectory.Length)
            {
                return EmbeddedZipCandidateEvaluation.NotACandidate;
            }

            int flags = BitConverter.ToUInt16(centralDirectory, index + 8);
            int method = BitConverter.ToUInt16(centralDirectory, index + 10);
            uint crc32 = BitConverter.ToUInt32(centralDirectory, index + 16);
            long compressedSize = BitConverter.ToUInt32(centralDirectory, index + 20);
            long uncompressedSize = BitConverter.ToUInt32(centralDirectory, index + 24);
            long diskStart = BitConverter.ToUInt16(centralDirectory, index + 34);
            long localOffset = BitConverter.ToUInt32(centralDirectory, index + 42);

            int nameIndex = index + EmbeddedZipStreamFormat.CentralDirectoryHeaderLength;
            int extraIndex = nameIndex + nameLength;
            string name = DecodeName(centralDirectory, nameIndex, nameLength, flags);

            // ZIP64 扩展字段 0x0001：只按"哪些字段是占位符"的顺序出现（APPNOTE 4.5.3）。
            Zip64Extras extras = ReadZip64Extras(centralDirectory, extraIndex, extraLength, uncompressedSize, compressedSize, localOffset, diskStart);

            uncompressedSize = extras.UncompressedSize;
            compressedSize = extras.CompressedSize;
            localOffset = extras.LocalOffset;
            diskStart = extras.DiskStart;

            /*
             * 占位符必须已经被 ZIP64 扩展字段里的真值换掉。
             *
             * ⚠ 0xFFFFFFFF / 0xFFFF 是**正整数**，光判 "< 0" 是漏的：漏掉的后果是拿 4 GB 当尺寸去读，
             * 读出一堆垃圾还可能把盘写满 —— 所以这里逐个字段显式比占位符。
             */
            if (uncompressedSize == uint.MaxValue ||
                compressedSize == uint.MaxValue ||
                localOffset == uint.MaxValue ||
                diskStart == ushort.MaxValue ||
                uncompressedSize < 0 ||
                compressedSize < 0 ||
                localOffset < 0)
            {
                return EmbeddedZipCandidateEvaluation.Unsupported(
                    $"条目 {name} 的 ZIP64 尺寸/偏移字段缺失，直读不支持");
            }

            if (diskStart != 0)
            {
                return EmbeddedZipCandidateEvaluation.Unsupported(
                    $"条目 {name} 声明在别的盘上（分卷 ZIP），直读不支持");
            }

            // 通用位标志 bit0 = 加密。
            bool isEncrypted = (flags & ZipAesCrypto.EncryptedFlag) != 0;
            int aesStrength = 0;

            if (isEncrypted)
            {
                /*
                 * 加密条目分两种，能不能直读差别很大（2026-09-25 第 29 条）：
                 *
                 * · **AES（method = 99）**：加密方式写在扩展字段 0x9901 里，我们按规范自己解
                 *   （<see cref="ZipAesCrypto"/>）。这条路必须自己走 —— 7-Zip 对 AES 包的密码
                 *   只按 ANSI 字节派生，百度网盘那种"UTF-8 打包"的中文密码在它那里永远报密码错误。
                 * · **老式 ZipCrypto**：本读取器不做，仍是"不支持"（回落到抠取 + 7z 是对的，7z 解它没问题）。
                 *
                 * ⚠ 这里必须**立刻返回**，不能"记一笔、continue 到循环末尾再统一报"：
                 * 中央目录的游标 `index += …` 在循环体末尾，`continue` 会把它跳过 → 死循环空转
                 * （2026-09-25 实测：全量测试卡在这条路上二十多分钟，进程 0.79 核空转、不碰磁盘）。
                 */
                if (method != ZipAesCrypto.AesMethod)
                {
                    return EmbeddedZipCandidateEvaluation.Unsupported($"归档里有加密条目（{name}），直读不支持");
                }

                ReadAesExtra(centralDirectory, extraIndex, extraLength, out aesStrength, out int actualMethod);

                if (ZipAesCrypto.KeyLengthForStrength(aesStrength) == 0)
                {
                    return EmbeddedZipCandidateEvaluation.Unsupported(
                        $"条目 {name} 声明了认不出的 AES 强度（{aesStrength}），直读不支持");
                }

                if (actualMethod != 0 && actualMethod != 8)
                {
                    return EmbeddedZipCandidateEvaluation.Unsupported(
                        $"条目 {name} 的 AES 内层压缩方法 {actualMethod} 直读不支持（只认 stored / deflate）");
                }

                if (compressedSize < ZipAesCrypto.OverheadLength + 1)
                {
                    return EmbeddedZipCandidateEvaluation.Unsupported(
                        $"条目 {name} 的压缩后大小 {compressedSize} 放不下 AES 的盐与认证码，直读不支持");
                }

                // method 换成**真方法**：解密之后要按它解压（下游只看 Method，不关心它原来写着 99）。
                method = actualMethod;
                hasAesEntry = true;
            }

            if (method != 0 && method != 8)
            {
                return EmbeddedZipCandidateEvaluation.Unsupported(
                    $"条目 {name} 用了直读不支持的压缩方法（{method}）");
            }

            bool isDirectory = name.EndsWith("/", StringComparison.Ordinal) ||
                               name.EndsWith("\\", StringComparison.Ordinal);

            if (!isDirectory)
            {
                // 数据必须落在中央目录之前：越过去就说明偏移体系不对（防"看着成功、内容错位"）。
                if (localOffset + EmbeddedZipStreamFormat.LocalFileHeaderLength > cdRelStart ||
                    localOffset + compressedSize > cdRelStart)
                {
                    return EmbeddedZipCandidateEvaluation.NotACandidate;
                }

                if (!EmbeddedZipByteReader.Matches(
                        EmbeddedZipByteReader.ReadAt(
                            stream,
                            offset + localOffset,
                            EmbeddedZipStreamFormat.LocalFileHeaderLength),
                        0,
                        EmbeddedZipStreamFormat.LocalFileHeaderSignature))
                {
                    return EmbeddedZipCandidateEvaluation.NotACandidate;
                }

                int localNameLength = BitConverter.ToUInt16(
                    EmbeddedZipByteReader.ReadAt(stream, offset + localOffset + 26, 2),
                    0);
                int localExtraLength = BitConverter.ToUInt16(
                    EmbeddedZipByteReader.ReadAt(stream, offset + localOffset + 28, 2),
                    0);
                long dataRel = localOffset + EmbeddedZipStreamFormat.LocalFileHeaderLength + localNameLength + localExtraLength;

                if (dataRel < 0 || dataRel + compressedSize > cdRelStart)
                {
                    return EmbeddedZipCandidateEvaluation.NotACandidate;
                }

                totalBytes = EmbeddedZipByteReader.SaturatingSum(totalBytes, uncompressedSize);
                listEntries.Add(new ArchiveEntry { Path = name, Size = uncompressedSize, IsDirectory = false });
            }
            else
            {
                listEntries.Add(new ArchiveEntry { Path = name, Size = 0, IsDirectory = true });
            }

            entries.Add(new EmbeddedZipEntry
            {
                Name = name,
                Size = isDirectory ? 0 : uncompressedSize,
                CompressedSize = compressedSize,
                IsDirectory = isDirectory,
                Method = method,
                LocalHeaderOffset = localOffset,
                Crc32 = crc32,
                AesStrength = isDirectory ? 0 : aesStrength,
                IsEncrypted = isEncrypted
            });

            index += EmbeddedZipStreamFormat.CentralDirectoryHeaderLength + nameLength + extraLength + entryCommentLength;
        }

        if (index != centralDirectory.Length ||
            (declaredEntryCount > 0 && entries.Count != declaredEntryCount))
        {
            return EmbeddedZipCandidateEvaluation.NotACandidate;
        }

        /*
         * 加密包没有候选密码：仍然返回"不支持"这个**回落信号**（外加"只差一个密码"这个标记）。
         * 识别阶段（ArchiveDetectService）就是这条路径 —— 那里拿不到密码，也不该在识别时猜密码；
         * 但空间核算要知道"只要给对密码就能直读"，否则会为一笔不会发生的抠取副本预留空间。
         */
        if (hasAesEntry && (passwords == null || passwords.Count == 0))
        {
            return EmbeddedZipCandidateEvaluation.NeedsPassword("归档里有加密条目（AES），直读需要密码");
        }

        int resolvedPasswordIndex = 0;
        ZipAesPasswordEncoding resolvedEncoding = ZipAesPasswordEncoding.Utf8;

        if (hasAesEntry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!EmbeddedZipAesAccess.TryResolvePassword(
                    stream,
                    offset,
                    entries,
                    passwords!,
                    cancellationToken,
                    confirmResolvedPassword,
                    out resolvedPasswordIndex,
                    out resolvedEncoding,
                    out string passwordReason))
            {
                /*
                 * 一个候选都没通过 = **硬失败**，绝不回落抠取：
                 * 抠取 + 7-Zip 这条路对 UTF-8 打包的 AES 中文密码**必然**再报一次"密码错误"，
                 * 只会白拷一份等大的副本（用户 2026-07-… 那种 980 MB 级副本 + 失败残留的现场）。
                 */
                return EmbeddedZipCandidateEvaluation.PasswordRejected(
                    passwordReason,
                    entries,
                    listEntries,
                    totalBytes,
                    isEncrypted: true);
            }
        }

        // 路径预检：与正常解压**同一处实现**（AGENTS.md §6 第 4 条），不另写一份。
        PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(listEntries);

        if (!pathReport.IsSafe)
        {
            return EmbeddedZipCandidateEvaluation.PathRejected(
                "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary,
                entries,
                listEntries,
                totalBytes);
        }

        if (!EmbeddedZipEntryPlanner.TryBuildOutputPaths(entries, targetDirectory, out string pathReason2))
        {
            return EmbeddedZipCandidateEvaluation.PathRejected(
                "归档里的条目落点不安全，已拒绝解压：" + pathReason2,
                entries,
                listEntries,
                totalBytes);
        }

        return EmbeddedZipCandidateEvaluation.Ok(
            entries,
            new ArchiveListResult
            {
                Success = true,
                Entries = listEntries,
                TotalUncompressedSize = totalBytes,
                FileCount = listEntries.Count(e => !e.IsDirectory),
                DirectoryCount = listEntries.Count(e => e.IsDirectory),
                IsEncrypted = hasAesEntry,
                IsMultiVolume = false,
                EngineId = "embedded-zip-direct",
                EngineVersion = EmbeddedZipStreamFormat.ReaderVersion,
                Message = "内置 ZIP 直读解析出的条目清单"
            },
            totalBytes,
            offset,
            offset + zipLength,
            resolvedPasswordIndex,
            resolvedEncoding,
            pathReport.NamingSummary);
    }

    /// <summary>
    /// 读 AES 扩展字段（<c>0x9901</c>，7 字节）：版本(2) + 厂商标识 "AE"(2) + 强度(1) + **真压缩方法**(2)。
    ///
    /// <para>⚠ 真方法必须从这里读：中央目录与方法字段写的是 99（= "是 AES"），
    /// 数据其实还是先按 stored/deflate 处理再加密的。少了这一步会把密文当 deflate 解，得到垃圾或异常。</para>
    /// </summary>
    private static void ReadAesExtra(
        byte[] extra,
        int extraIndex,
        int extraLength,
        out int strength,
        out int actualMethod)
    {
        strength = 0;
        actualMethod = -1;

        int end = extraIndex + extraLength;
        int cursor = extraIndex;

        while (cursor + 4 <= end)
        {
            int id = BitConverter.ToUInt16(extra, cursor);
            int size = BitConverter.ToUInt16(extra, cursor + 2);
            int body = cursor + 4;

            if (body + size > end)
            {
                return;
            }

            if (id == 0x9901 && size >= 7)
            {
                strength = extra[body + 4];
                actualMethod = BitConverter.ToUInt16(extra, body + 5);
                return;
            }

            cursor = body + size;
        }
    }

    /// <summary>
    /// 解条目名。通用位标志 bit 11（0x0800）表示"名字是 UTF-8"；没有这一位时先严格试 UTF-8，
    /// 失败再用 GBK（中文 Windows 上打包工具最常用的默认编码），最后才退到 Latin-1（不会抛）。
    ///
    /// <para>不这么做的话，中文名在"没设 UTF-8 位"的包里会变成乱码目录名 ——
    /// 那种产物用户根本认不出来是哪来的。</para>
    /// </summary>
    private static string DecodeName(byte[] buffer, int index, int length, int flags)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if ((flags & 0x0800) != 0)
        {
            return Encoding.UTF8.GetString(buffer, index, length);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, index, length);
        }
        catch
        {
            // 不是合法 UTF-8：按中文 ZIP 的惯例试 GBK。
        }

        CodePageEncodingBootstrap.EnsureRegistered();

        try
        {
            return Encoding.GetEncoding(936).GetString(buffer, index, length);
        }
        catch
        {
            return Encoding.Latin1.GetString(buffer, index, length);
        }
    }

    /// <summary>ZIP64 扩展字段（0x0001）解出来的四个值；不是占位符的字段保持原样。</summary>
    private readonly struct Zip64Extras
    {
        public Zip64Extras(long uncompressedSize, long compressedSize, long localOffset, long diskStart)
        {
            UncompressedSize = uncompressedSize;
            CompressedSize = compressedSize;
            LocalOffset = localOffset;
            DiskStart = diskStart;
        }

        public long UncompressedSize { get; }

        public long CompressedSize { get; }

        public long LocalOffset { get; }

        public long DiskStart { get; }
    }

    /// <summary>
    /// 读 ZIP64 扩展字段：<b>只按"哪些字段是占位符"的顺序</b>出现
    /// （APPNOTE 4.5.3：原始大小 → 压缩后大小 → 本地头偏移 → 盘号）。顺序读错会把偏移读成尺寸，
    /// 那是"看着成功、内容全错"的典型来源，所以一个字段一个字段地按位点推进。
    /// </summary>
    private static Zip64Extras ReadZip64Extras(
        byte[] centralDirectory,
        int extraIndex,
        int extraLength,
        long uncompressedSize,
        long compressedSize,
        long localOffset,
        long diskStart)
    {
        int end = extraIndex + extraLength;
        int cursor = extraIndex;

        while (cursor + 4 <= end)
        {
            int headerId = BitConverter.ToUInt16(centralDirectory, cursor);
            int dataSize = BitConverter.ToUInt16(centralDirectory, cursor + 2);
            int dataIndex = cursor + 4;

            if (dataIndex + dataSize > end)
            {
                break;
            }

            if (headerId == 0x0001)
            {
                int at = dataIndex;

                if (uncompressedSize == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                {
                    uncompressedSize = EmbeddedZipByteReader.ReadUInt64AsLong(centralDirectory, at);
                    at += 8;
                }

                if (compressedSize == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                {
                    compressedSize = EmbeddedZipByteReader.ReadUInt64AsLong(centralDirectory, at);
                    at += 8;
                }

                if (localOffset == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                {
                    localOffset = EmbeddedZipByteReader.ReadUInt64AsLong(centralDirectory, at);
                    at += 8;
                }

                if (diskStart == ushort.MaxValue && at + 4 <= dataIndex + dataSize)
                {
                    diskStart = BitConverter.ToUInt32(centralDirectory, at);
                }

                break;
            }

            cursor = dataIndex + dataSize;
        }

        return new Zip64Extras(uncompressedSize, compressedSize, localOffset, diskStart);
    }
}
