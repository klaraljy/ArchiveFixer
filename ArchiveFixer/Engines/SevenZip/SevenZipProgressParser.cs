using System;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Engines.SevenZip
{
    /// <summary>
    /// 7-Zip **进度行**的解析器。
    ///
    /// <para>
    /// ⛔ 这是"直接解析 7-Zip 文本输出"（AGENTS.md §3.1 禁止项②），
    /// 所以它**只允许待在 <c>Engines/SevenZip/</c> 内**，对外只暴露归一化后的
    /// <see cref="ArchiveProgress"/>。上层永远看不到 7-Zip 的格式。
    /// </para>
    ///
    /// <para>
    /// <b>实测形态</b>（本机 7-Zip 26.03 x64，2026-09-22 用 250MB 的包抓的原始字节）：
    /// </para>
    /// <code>
    ///   0%                                                                   ← 刚开始
    ///  67% 8 - payload 08 数据.bin                                          ← 解压：百分比 条目序号 命令(-) 条目名
    ///   2% + payload 01 数据.bin                                            ← 压缩：命令是 +
    ///   0M Scan E:\...\src\                                                ← 总大小未知时的"扫描"阶段
    /// </code>
    ///
    /// <para>
    /// 格式来源与字段顺序对得上 7-Zip 自己的 <c>CPercentPrinter::Print</c>：
    /// 百分比（宽度 4，<c>%</c> 或 <c>M</c>）→ 条目序号（<c>Files != 0</c> 时才有）→ 命令 → 条目名。
    /// 三段都是可选的，所以正则里每一段都必须允许缺省。
    /// </para>
    ///
    /// <para>
    /// <b>为什么只认"片段/整行开头就是百分号"这一种形态</b>：
    /// 7-Zip 的普通日志行（<c>Path = …</c>、<c>Type = 7z</c>、<c>Files: 15</c>）都不会以
    /// "数字 + %"开头，所以这个正则不会误吞诊断信息。
    /// 解析失败时返回 null，调用方照常把它当普通输出收进日志。
    /// </para>
    /// </summary>
    internal static class SevenZipProgressParser
    {
        /// <summary>
        /// 进度行的形状：<c>  0%</c> / <c> 67% 8 - 名字</c> / <c>  0M Scan 路径</c>。
        ///
        /// 说明：
        /// <list type="bullet">
        /// <item><description><c>^</c> 后允许空白（7-Zip 把百分比右对齐到 4 列）；</description></item>
        /// <item><description>数字限制在 1–3 位：4 位以上不可能是百分比，避免把奇怪的行吞掉；</description></item>
        /// <item><description>单位只认 <c>%</c> 与 <c>M</c>（<c>M</c> = 总大小未知时的兆字节口径）。</description></item>
        /// </list>
        /// </summary>
        private static readonly Regex ProgressLineRegex = new(
            @"^[ \t]*(?<value>\d{1,3})(?<unit>[%M])(?:[ \t]+(?<files>\d+))?(?:[ \t]+(?<command>[-+]))?(?:[ \t]+(?<name>.+))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 试解析一段输出。返回 null = "这不是进度行"。
        ///
        /// <c>M</c>（扫描阶段）返回 <see cref="ArchiveProgress.IsIndeterminate"/> 为 true 的进度：
        /// 我们知道"引擎在动"，但**不知道到哪了** —— 不编一个百分比出来。
        /// </summary>
        public static ArchiveProgress? TryParse(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            Match match = ProgressLineRegex.Match(segment);

            if (!match.Success)
            {
                return null;
            }

            string unit = match.Groups["unit"].Value;
            string valueText = match.Groups["value"].Value;

            if (!int.TryParse(valueText, out int value))
            {
                return null;
            }

            string name = match.Groups["name"].Success ? match.Groups["name"].Value.Trim() : string.Empty;

            if (unit == "M")
            {
                /*
                 * "  0M Scan E:\…\src\" —— 扫描/分析阶段，总量未知。
                 * 只报"在处理这个路径"，不报百分比；CurrentEntry 记成 Scan 后面的路径，
                 * 因为那正是用户此刻能对上号的信息。
                 */
                return new ArchiveProgress
                {
                    Percent = ArchiveProgress.UnknownPercent,
                    CurrentEntry = name,
                    ProcessedBytes = value * 1024L * 1024L,
                    TotalBytes = ArchiveProgress.UnknownBytes,
                    ProcessedEntries = ArchiveProgress.UnknownCount
                };
            }

            if (value > 100)
            {
                // 超过 100 的"百分比"不是进度行（普通输出里数字后面跟 % 的情况宁可放过）。
                return null;
            }

            int entries = ArchiveProgress.UnknownCount;

            if (match.Groups["files"].Success &&
                int.TryParse(match.Groups["files"].Value, out int parsedEntries))
            {
                entries = parsedEntries;
            }

            return new ArchiveProgress
            {
                Percent = value,
                CurrentEntry = name,
                ProcessedBytes = ArchiveProgress.UnknownBytes,
                TotalBytes = ArchiveProgress.UnknownBytes,
                ProcessedEntries = entries
            };
        }
    }
}
