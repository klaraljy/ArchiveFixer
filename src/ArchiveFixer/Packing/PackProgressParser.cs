using System;
using System.Text.RegularExpressions;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 打包这条路上两个外部工具**进度行**的解析器。
    ///
    /// <para>
    /// ⛔ 这是"直接解析外部工具的输出文本"，与 <c>Engines/SevenZip/SevenZipProgressParser</c>
    /// 同一性质：命令行的参数模板与输出解析**只允许待在 <c>Packing/</c> 内**
    /// （docs/打包功能.md §5 末段、AGENTS.md §3.1 四条禁止项同一口径）。
    /// 对外只暴露归一化后的百分比，界面看不到工具的输出格式。
    /// </para>
    ///
    /// <para><b>实测形态</b>（本机 2026-09-23）：</para>
    /// <list type="bullet">
    /// <item><description>7-Zip：<c>\r</c> 分隔的 <c>  0%</c> / <c> 67% 8 + 名字</c> /
    /// <c>  0M Scan 路径</c>（压缩时命令是 <c>+</c>）；</description></item>
    /// <item><description>RAR：同一行里用退格回写 <c>正在添加 … 50%\b\b\b100% 确定</c>
    /// —— 按行读会退化成"完事才有进度"，所以上游用的是 <c>ProcessOutputPump</c>
    /// （按 <c>\r</c>/<c>\n</c>/<c>\b</c> 切片）。</description></item>
    /// </list>
    ///
    /// <para>两者共有的可靠特征是"数字 + 百分号"，所以这里只认这一个特征：
    /// 认不出来就返回 null，那一段照常进日志，绝不编一个百分比出来。</para>
    /// </summary>
    internal static class PackProgressParser
    {
        /// <summary>
        /// 一段输出里的第一个百分比（1–3 位；4 位以上不可能是百分比）。
        ///
        /// <c>(?&lt;!\d)</c> 是必需的：没有它，<c>"1000%"</c> 会在第 2 个字符处匹配到
        /// <c>"000%"</c> —— 把一个不可能是百分比的东西读成"0%"，进度条就会莫名其妙地跳回 0。
        /// </summary>
        private static readonly Regex PercentRegex = new(
            @"(?<!\d)(?<value>\d{1,3})\s?%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>试解析一段输出。返回 null = "这一段里没有百分比"。</summary>
        public static int? TryParse(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            Match match = PercentRegex.Match(segment);

            if (!match.Success)
            {
                return null;
            }

            if (!int.TryParse(match.Groups["value"].Value, out int percent))
            {
                return null;
            }

            // 超过 100 的不是进度（例如某个文件名里带 "1000%"），丢弃。
            return percent is >= 0 and <= 100 ? percent : null;
        }

        /// <summary>
        /// 从进度片段里挑出"当前在处理什么"（界面上的进度明细）。
        /// 只是展示用：取不到就返回空串，**不影响百分比**。
        /// </summary>
        public static string ExtractDetail(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return string.Empty;
            }

            string text = segment.Trim();

            int percentIndex = text.IndexOf('%');

            if (percentIndex >= 0 && percentIndex + 1 < text.Length)
            {
                // 去掉开头的" 67% 8 + "这一段：剩下的就是条目名。
                string tail = text[(percentIndex + 1)..].Trim();
                string[] parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length > 2)
                {
                    return string.Join(' ', parts, 2, parts.Length - 2);
                }
            }

            return string.Empty;
        }
    }
}
