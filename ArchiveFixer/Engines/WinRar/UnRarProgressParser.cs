using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Engines.WinRar
{
    /// <summary>
    /// RARLAB <c>UnRAR.exe</c> **进度输出**的解析器。
    ///
    /// <para>
    /// ⛔ 与 7-Zip 那一侧同一个边界（AGENTS.md §3.1 禁止项②）：
    /// 解析只允许待在 <c>Engines/WinRar/</c> 内，对外只给归一化后的 <see cref="ArchiveProgress"/>。
    /// </para>
    ///
    /// <para>
    /// <b>实测形态</b>（本机 UnRAR 7.23 x64，2026-09-22 用 250MB 的包抓的原始字节；
    /// <c>&lt;BS&gt;</c> = 退格 0x08）：
    /// </para>
    /// <code>
    /// Extracting  E:\out\payload 01 数据.bin     &lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  1%&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  3%&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  4%…&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  OK &lt;CR&gt;&lt;LF&gt;
    /// Testing     payload 02 数据.bin            &lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  9%&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt; 11%…&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;&lt;BS&gt;  OK &lt;CR&gt;&lt;LF&gt;
    /// Creating    E:\out  OK
    /// </code>
    ///
    /// <para>
    /// 两点与 7-Zip 的关键差别：
    /// </para>
    /// <list type="number">
    /// <item><description>百分比是**同一个文件内部**用退格重写的，而且它是**整包累计**百分比
    /// （实测：第一个文件 1%→8%，第二个接着 9%→16%）—— 正好可以直接当整体进度用。</description></item>
    /// <item><description>条目名出现在**行的开头**（<c>Extracting  &lt;路径&gt;</c>），
    /// 而且给的是**落盘后的完整路径**；展示时取最后一段（文件名）更贴近用户认知。</description></item>
    /// </list>
    ///
    /// <para>
    /// ⚠ 这些片段是靠 <c>ProcessOutputPump</c> 按退格切出来的 —— 按行读的话，
    /// 一个 5GB 的单文件在解完之前一条进度都拿不到。
    /// </para>
    /// </summary>
    internal static class UnRarProgressParser
    {
        /// <summary>百分比片段：UnRAR 把数字右对齐到 3 列再跟一个 <c>%</c>（<c>  1%</c>、<c> 11%</c>、<c>100%</c>）。</summary>
        private static readonly Regex PercentRegex = new(
            @"^[ \t]*(?<value>\d{1,3})%[ \t]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 条目行：<c>Extracting  &lt;目标路径&gt;</c> / <c>Testing     &lt;条目名&gt;</c> /
        /// <c>Creating    &lt;目录&gt;</c> / <c>Updating    …</c>。
        ///
        /// 关键字用完整词表而不是"任意单词"：UnRAR 的普通日志行（<c>UNRAR 7.23 x64 freeware</c>、
        /// <c>Cannot find volume …</c>）都不能被吞成进度，否则错误分类会缺输入。
        ///
        /// ⚠ <c>Extracting from</c> 必须排在 <c>Extracting</c> **前面**：正则的多选分支是**先匹配先算**，
        /// 反过来的话 <c>Extracting from C:\x\a.rar</c> 会被当成"条目名 = from C:\x\a.rar"，
        /// 于是每次运行都会先冒出一条假的条目进度（实测踩到）。
        /// </summary>
        private static readonly Regex EntryRegex = new(
            @"^(?<verb>Extracting from|Extracting|Testing|Creating|Updating|Repairing)[ \t]+(?<target>.+?)[ \t]*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 试解析一段输出。返回 null = "这不是进度片段"。
        ///
        /// 只报两种东西：**累计百分比**与**当前条目名**。
        /// UnRAR 不报字节数，"OK" 之类的收尾词也刻意不当进度（它们由退出码与既有解析器负责）。
        /// </summary>
        public static ArchiveProgress? TryParse(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            Match percentMatch = PercentRegex.Match(segment);

            if (percentMatch.Success &&
                int.TryParse(percentMatch.Groups["value"].Value, out int percent) &&
                percent <= 100)
            {
                return new ArchiveProgress
                {
                    Percent = percent,
                    ProcessedBytes = ArchiveProgress.UnknownBytes,
                    TotalBytes = ArchiveProgress.UnknownBytes,
                    ProcessedEntries = ArchiveProgress.UnknownCount
                };
            }

            Match entryMatch = EntryRegex.Match(segment);

            if (entryMatch.Success)
            {
                string verb = entryMatch.Groups["verb"].Value;
                string target = entryMatch.Groups["target"].Value.Trim();

                // "Extracting from <包路径>" 是开跑前的一行说明，不是条目。
                if (string.Equals(verb, "Extracting from", StringComparison.Ordinal))
                {
                    return null;
                }

                return new ArchiveProgress
                {
                    Percent = ArchiveProgress.UnknownPercent,
                    CurrentEntry = DescribeEntry(target),
                    ProcessedBytes = ArchiveProgress.UnknownBytes,
                    TotalBytes = ArchiveProgress.UnknownBytes,
                    ProcessedEntries = ArchiveProgress.UnknownCount
                };
            }

            return null;
        }

        /// <summary>
        /// 把 UnRAR 给的路径压成用户认得的那一段。
        ///
        /// <c>Extracting</c> 给的是落盘后的完整路径，<c>Testing</c> 给的是包内条目名 ——
        /// 两种都取最后一段；取不到（比如路径以分隔符结尾）就原样返回，绝不返回空白。
        /// </summary>
        private static string DescribeEntry(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return string.Empty;
            }

            string trimmed = target.TrimEnd('\\', '/');

            if (trimmed.Length == 0)
            {
                return target;
            }

            try
            {
                string name = Path.GetFileName(trimmed);

                return string.IsNullOrWhiteSpace(name) ? trimmed : name;
            }
            catch
            {
                // 含非法字符的路径：原样给出，至少让用户看得出在处理哪一条。
                return trimmed;
            }
        }
    }
}
