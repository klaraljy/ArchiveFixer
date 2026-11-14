using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Services;

/// <summary>
/// **加密判据的唯一出口**（用户 2026-09-30 从"只认 RAR"扩到 **RAR / ZIP / 7z**）。
///
/// <para>三个格式各自用**只读头尾**的判据器解析（<see cref="RarEncryptionReader"/> /
/// <see cref="ZipEncryptionReader"/> / <see cref="SevenZipEncryptionReader"/>，
/// ⛔ 都不调引擎、不引依赖、都有读入量硬上限），读出"有加密证据"才置
/// <see cref="DetectResult.IsProbablyEncrypted"/>；读不出来（截断 / 布局不符 / 结构藏在压缩流里）
/// 一律**不报加密**（⛔ 宁可漏报，也不误报 —— 见 <see cref="ArchiveEncryptionState.Unknown"/>）。</para>
///
/// <para><b>为什么必须是"一个方法"</b>：识别、"尾部内嵌归档"、多卷卷组三条路都要给同一个结论，
/// 三处各写一份判据必然漂移，而漂移的后果是批首那句"本批有 N 个包没有可用密码"与实际跑起来的结果对不上
/// （AGENTS.md §9.5）。拆分大文件时这一条**原样保留**：三个调用方（<see cref="ArchiveContentIdentifier"/>、
/// <see cref="ArchiveAdmissionEvaluator"/>）拿到的仍是本方法，⛔ 谁都不许另写一套。</para>
/// </summary>
internal static class ArchiveEncryptionVerdict
{
    /// <summary>
    /// 给识别结论补上加密判读（判据见类注释；<paramref name="result"/> 原样返回，只是可能被加上注脚）。
    ///
    /// <para><paramref name="offset"/>：这一段归档在文件里的起点（内嵌归档用；<c>0</c> = 文件开头就是它）。</para>
    /// <para><paramref name="volumePaths"/>：分卷组的全部卷（**只有 7z 用得上** —— 它的元数据在最后一卷）。
    /// 不给（或只有一卷）时按单文件判读，多卷 7z 会如实落到"不知道"。</para>
    ///
    /// <para>⚠ 这一层**不缓存**（缓存里存的是它之前的那份）：它的判据要读头链 / 中央目录 / 头尾，
    /// 读到的位置可以越过 34 KB 文件头，缓存键那四样证据盖不住它 —— 每次现算，代价只有几 KB 的读。</para>
    /// </summary>
    internal static DetectResult Apply(
        DetectResult result,
        string filePath,
        long offset = 0,
        IReadOnlyList<string>? volumePaths = null)
    {
        if (result == null)
        {
            // result 为 null 时**原样**把它交回调用方（保持"进来什么、出去什么"），用的是可空抑制。
            return result!;
        }

        ArchiveEncryptionReading? reading;

        if (result.Format == "RAR4" || result.Format == "RAR5")
        {
            reading = RarEncryptionReader.Read(filePath, offset);
        }
        else if (ArchiveFormatProbe.IsZipFamily(result.Format))
        {
            reading = ZipEncryptionReader.Read(filePath, offset);
        }
        else if (result.Format == "7Z")
        {
            reading = SevenZipEncryptionReader.Read(filePath, offset, volumePaths);
        }
        else
        {
            // 别的格式这一轮不判（GZIP / BZIP2 / XZ / TAR / Unknown…），照旧"如实不报"。
            return result;
        }

        if (reading == null || !reading.IsEncrypted)
        {
            return result;
        }

        string note = ResolveNote(result.Format, reading.State);

        /*
         * 注脚只追加一次：同一个 result 可能被判两次（识别那一遍 + 拿到卷组后再走一遍，
         * 见 ArchiveAdmissionEvaluator.ApplyAsync），⛔ 不许出现两个注脚叠在一起。
         */
        if (!result.Message.Contains(note, StringComparison.Ordinal))
        {
            result.Message += note;
        }

        result.IsProbablyEncrypted = true;

        return result;
    }

    /// <summary>
    /// 加密结论对应的**用户可见注脚**（照 RAR 那两条的写法：一句中文，强调用「」）。
    ///
    /// <para>ZIP 只有一条：我们的判据是"至少一个条目置了 bit0"，分不出"头也加密"这一档
    /// （ZIP 的中央目录本身不加密，ZipCrypto / AES 都只盖条目数据）。</para>
    /// </summary>
    private static string ResolveNote(string format, ArchiveEncryptionState state)
    {
        if (ArchiveFormatProbe.IsZipFamily(format))
        {
            return StatusText.DetectZipEncryptedNote;
        }

        return state == ArchiveEncryptionState.HeadersEncrypted
            ? StatusText.DetectSevenZipHeadersEncryptedNote
            : StatusText.DetectSevenZipDataEncryptedNote;
    }
}
