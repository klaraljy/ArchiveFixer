using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 内嵌 ZIP 的**扫描**阶段：在 <c>[Offset, ArchiveEnd)</c> 尾部找 EOCD 候选，逐个校验"结构自洽"，
/// 命中的那个再交给 <see cref="EmbeddedZipCentralDirectoryParser"/> 翻成条目清单。
///
/// <para><b>逐个试而不是只看最后一个</b>：真实现场里 EOCD 之后还有十几 KB 正常数据，
/// 那些数据里完全可能再出现一次同样的字节序列（与 <c>EmbeddedArchiveDetector</c> 同一取法）。</para>
///
/// <para><b>自洽的核心一步</b>：声明的中央目录偏移必须正好等于"从 EOCD 往前数"得到的实际起点。
/// 不放松成"允许一个正 delta"：ZIP 里所有偏移都是相对 ZIP 自己起点的，一旦差了一截，
/// 说明"这个 ZIP 真正的起点"并不是调用方给的 Offset —— 那时按 offset + localOffset 去取数据
/// 会取到错位的字节，产出看着像成功、内容却是坏的。这种形态交给"抠取 + 7z"才对。</para>
///
/// <para>⛔ 只读：一个字节都不写，也不改源文件。</para>
/// </summary>
internal static class EmbeddedZipCentralDirectoryScanner
{
    /// <summary>
    /// 在归档区间尾部找 EOCD 的窗口：256 KiB。取法与 <c>EmbeddedArchiveDetector.DefaultTailBytes</c> 一致 ——
    /// 既要放得下 ZIP64 收尾（76 字节）与注释上限（65535），也要覆盖"EOCD 之后还有十几 KB 正常数据"
    /// 那种真实现场（实测 14,350–17,424 字节）。
    /// </summary>
    private const int TailBytes = 262144;

    /// <summary>
    /// 中央目录字节数上限（解析用的护栏，不是资源预算）：64 MiB 足够放 20 万条目，
    /// 再大就不是"正常归档"了，直接回落让 7z 去处理。
    /// </summary>
    private const long MaxCentralDirectoryBytes = 64L * 1024 * 1024;

    /// <summary>条目数上限（同上，解析护栏）。真正的上限判据在 <c>ResourceBudget</c> 里。</summary>
    private const int MaxEntryCount = 1_000_000;

    /// <summary>
    /// 解析虚拟 ZIP：从后往前逐个试 EOCD 候选，第一个**结构自洽**的候选就是结论。
    /// 逐个试而不是"只看最后一个"的理由见类注释。
    /// </summary>
    internal static EmbeddedZipProbeResult Parse(
        FileStream stream,
        long offset,
        long end,
        string? targetDirectory,
        IReadOnlyList<string>? passwords,
        CancellationToken cancellationToken,
        bool confirmResolvedPassword)
    {
        long zipLength = end - offset;
        int windowLength = (int)Math.Min(zipLength, TailBytes);
        long windowStartRel = zipLength - windowLength;
        byte[] window = EmbeddedZipByteReader.ReadAt(stream, offset + windowStartRel, windowLength);

        if (window.Length < EmbeddedZipStreamFormat.EndOfCentralDirectoryLength)
        {
            return EmbeddedZipOutcomes.Unsupported("内嵌归档区间太短，连一个 ZIP 收尾都放不下");
        }

        string structuralReason = "内嵌归档里找不到自洽的 ZIP 收尾（EOCD 与中央目录位置对不上）";
        int eocdIndex = EmbeddedZipByteReader.LastIndexOfSignature(
            window,
            EmbeddedZipStreamFormat.EndOfCentralDirectorySignature,
            window.Length - EmbeddedZipStreamFormat.EndOfCentralDirectoryLength);

        while (eocdIndex >= 0)
        {
            EmbeddedZipCandidateEvaluation evaluation = EvaluateCandidate(
                stream,
                window,
                windowStartRel,
                eocdIndex,
                offset,
                zipLength,
                targetDirectory,
                passwords,
                cancellationToken,
                confirmResolvedPassword,
                out string? reason);

            if (reason != null)
            {
                structuralReason = reason;
            }

            if (evaluation.IsCandidate)
            {
                return evaluation.Result!;
            }

            eocdIndex = EmbeddedZipByteReader.LastIndexOfSignature(
                window,
                EmbeddedZipStreamFormat.EndOfCentralDirectorySignature,
                eocdIndex - 1);
        }

        return EmbeddedZipOutcomes.Unsupported(structuralReason);
    }

    /// <summary>
    /// 校验一个 EOCD 候选。
    /// <c>IsCandidate = false</c> 表示"这不是那个归档"（继续往前找下一个候选）；
    /// <c>IsCandidate = true</c> 表示"就是它"，此时 <c>Result</c> 可能是"不支持"（加密 / 方法 / ZIP64 缺字段）。
    /// </summary>
    private static EmbeddedZipCandidateEvaluation EvaluateCandidate(
        FileStream stream,
        byte[] window,
        long windowStartRel,
        int eocdIndex,
        long offset,
        long zipLength,
        string? targetDirectory,
        IReadOnlyList<string>? passwords,
        CancellationToken cancellationToken,
        bool confirmResolvedPassword,
        out string? structuralReason)
    {
        structuralReason = null;

        long eocdRel = windowStartRel + eocdIndex;

        // EOCD 布局：偏移 10 = 条目总数(2)，12 = 中央目录大小(4)，16 = 中央目录偏移(4)，20 = 注释长度(2)。
        long entryCount16 = BitConverter.ToUInt16(window, eocdIndex + 10);
        long cdSize32 = BitConverter.ToUInt32(window, eocdIndex + 12);
        long cdOffset32 = BitConverter.ToUInt32(window, eocdIndex + 16);
        int commentLength = BitConverter.ToUInt16(window, eocdIndex + 20);

        /*
         * 归档区间必须容得下"EOCD + 注释"：注释长度是从文件里读出来的，一个坏值就能把它顶到区间外面。
         * 这一条同时保证后面按终点截断/读取时不会越界。
         */
        if (eocdRel + EmbeddedZipStreamFormat.EndOfCentralDirectoryLength + commentLength > zipLength)
        {
            return EmbeddedZipCandidateEvaluation.NotACandidate;
        }

        bool hasZip64 = eocdIndex >= EmbeddedZipStreamFormat.Zip64FooterLength
            && EmbeddedZipByteReader.Matches(
                window,
                eocdIndex - EmbeddedZipStreamFormat.Zip64LocatorLength,
                EmbeddedZipStreamFormat.Zip64EndOfCentralDirectoryLocatorSignature)
            && EmbeddedZipByteReader.Matches(
                window,
                eocdIndex - EmbeddedZipStreamFormat.Zip64FooterLength,
                EmbeddedZipStreamFormat.Zip64EndOfCentralDirectorySignature);

        long cdSize = cdSize32;
        long cdOffset = cdOffset32;
        long entryCount = entryCount16;

        if (hasZip64)
        {
            int recordIndex = eocdIndex - EmbeddedZipStreamFormat.Zip64FooterLength;

            // ZIP64 EOCD 记录：条目数在 +32，CD 大小在 +40，CD 偏移在 +48。
            long zip64EntryCount = EmbeddedZipByteReader.ReadUInt64AsLong(window, recordIndex + 32);
            long zip64CdSize = EmbeddedZipByteReader.ReadUInt64AsLong(window, recordIndex + 40);
            long zip64CdOffset = EmbeddedZipByteReader.ReadUInt64AsLong(window, recordIndex + 48);

            if (zip64CdSize >= 0 && zip64CdOffset >= 0)
            {
                cdSize = zip64CdSize;
                cdOffset = zip64CdOffset;

                if (zip64EntryCount >= 0)
                {
                    entryCount = zip64EntryCount;
                }
            }
            else if (cdSize32 == uint.MaxValue || cdOffset32 == uint.MaxValue || entryCount16 == ushort.MaxValue)
            {
                /*
                 * ZIP64 记录在、真值却拿不到，而 32 位字段又是占位符 —— 没法算，**不猜**。
                 * 这正是"ZIP64 尺寸/偏移字段缺失 → 不支持"的落点。
                 */
                return EmbeddedZipCandidateEvaluation.Unsupported(
                    "ZIP64 字段缺失或不可解析（EOCD 写的是占位符，ZIP64 记录里也没有真值）");
            }
        }
        else if (cdSize32 == uint.MaxValue || cdOffset32 == uint.MaxValue || entryCount16 == ushort.MaxValue)
        {
            return EmbeddedZipCandidateEvaluation.Unsupported("EOCD 用了 ZIP64 占位符，但归档里没有 ZIP64 收尾记录");
        }

        long footer = hasZip64 ? EmbeddedZipStreamFormat.Zip64FooterLength : 0;

        if (cdSize < 0 || cdSize > eocdRel - footer)
        {
            structuralReason = $"中央目录大小 {cdSize} 与归档区间对不上，不是内嵌归档的收尾";
            return EmbeddedZipCandidateEvaluation.NotACandidate;
        }

        long cdRelStart = eocdRel - footer - cdSize;

        /*
         * 自洽的核心一步：**声明的中央目录偏移必须正好等于"从 EOCD 往前数"得到的实际起点**。
         *
         * 为什么不放松成"允许一个正 delta"：ZIP 里所有偏移都是相对 ZIP 自己起点的，
         * 一旦声明的偏移与实际起点差了一截，说明"这个 ZIP 真正的起点"并不是调用方给的 Offset ——
         * 那时按 offset + localOffset 去取数据会取到错位的字节，产出看着像成功、内容却是坏的。
         * 这种形态交给"抠取 + 7z"（它自带按 EOCD 反推基准偏移的容忍逻辑）才是对的。
         */
        if (cdOffset != cdRelStart)
        {
            structuralReason =
                $"中央目录声明偏移 {cdOffset} 与从 EOCD 反推的实际起点 {cdRelStart} 不一致，直读不敢认";
            return EmbeddedZipCandidateEvaluation.NotACandidate;
        }

        if (cdSize > MaxCentralDirectoryBytes)
        {
            return EmbeddedZipCandidateEvaluation.Unsupported(
                $"中央目录 {cdSize} 字节，超过直读的解析上限（{MaxCentralDirectoryBytes} 字节）");
        }

        if (entryCount < 0 || entryCount > MaxEntryCount)
        {
            return EmbeddedZipCandidateEvaluation.Unsupported($"归档声明的条目数 {entryCount} 不合理，直读不支持");
        }

        byte[] centralDirectory = EmbeddedZipByteReader.ReadAt(stream, offset + cdRelStart, (int)cdSize);

        if (centralDirectory.Length != cdSize)
        {
            structuralReason = "中央目录读不满（文件可能被截断）";
            return EmbeddedZipCandidateEvaluation.NotACandidate;
        }

        return EmbeddedZipCentralDirectoryParser.BuildEntries(
            stream,
            centralDirectory,
            offset,
            zipLength,
            cdRelStart,
            entryCount,
            targetDirectory,
            passwords,
            cancellationToken,
            confirmResolvedPassword);
    }
}
