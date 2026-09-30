using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// **引擎原话**的挑行与排序（7-Zip 与 UnRAR 共用一处）。
    ///
    /// <para><b>为什么要有这个类</b>（用户 2026-09-27 真机：`giu.7z.001`）：
    /// 「用户不看日志，只想看错在哪」—— 而 7-Zip / UnRAR 那句真正说明原因的原话
    /// （<c>Cannot open encrypted archive. Wrong password?</c>、<c>CRC Failed in …</c>）本来
    /// 就在我们手上的输出里，却**一个字都没进日志**；结论里也只留了一行。</para>
    ///
    /// <para><b>为什么放在 <c>Engines/</c> 而不是某个引擎目录里</b>：§3 的四条禁止项禁止的是
    /// "核心模块解析某个引擎的输出" —— 这里解析的是**两个引擎共有的那份挑行规则**
    /// （关键字表本身分两份，见 <see cref="SevenZipKeywords"/> / <see cref="UnRarKeywords"/>），
    /// 与 <see cref="EngineIds"/>、<see cref="EngineErrorTypes"/> 同一性质：
    /// 换引擎时谁也不认识谁，只能靠这一层共用。</para>
    ///
    /// <para>规则（三档，与用户要求逐条对应）：</para>
    /// <list type="number">
    /// <item><description><c>ERROR</c> 开头的行（引擎自己标的错）最要紧；</description></item>
    /// <item><description>说出**原因**的行（密码 / 校验 / 数据 / 缺卷 / 打不开）次之；</description></item>
    /// <item><description>其它被识别的行（权限 / 路径 / 取消 …）再次。</description></item>
    /// </list>
    /// <para>每档内按**出现顺序**取，去重后最多三条 —— ⛔ 绝不把整段 stdout 倾泻给用户
    /// （那是噪声，而且可能带用户名路径）。</para>
    /// </summary>
    public static class EngineOutputKeywords
    {
        /// <summary>挑出来的原话；空 = 这一路输出里没有任何可识别的行。</summary>
        public static IReadOnlyList<string> PickImportantLines(
            string? text,
            int maxLines,
            IReadOnlyList<string[]>? keywordBuckets)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLines <= 0 || keywordBuckets == null)
            {
                return Array.Empty<string>();
            }

            string[] lines = SplitLines(text);

            if (lines.Length == 0)
            {
                return Array.Empty<string>();
            }

            var picked = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string[] bucket in keywordBuckets)
            {
                if (bucket == null || bucket.Length == 0)
                {
                    continue;
                }

                foreach (string line in lines)
                {
                    if (picked.Count >= maxLines)
                    {
                        return picked;
                    }

                    if (MatchesAny(line, bucket) && seen.Add(line))
                    {
                        picked.Add(line);
                    }
                }

                if (picked.Count >= maxLines)
                {
                    return picked;
                }
            }

            return picked;
        }

        /// <summary>挑行（三档桶直接给全的写法）。</summary>
        public static IReadOnlyList<string> PickImportantLines(
            string? text,
            int maxLines,
            params string[][] keywordBuckets)
            => PickImportantLines(text, maxLines, (IReadOnlyList<string[]>)keywordBuckets);

        /// <summary>
        /// **同一句原话**、按同一次挑行结果拼出来（结论行与日志行都不许自己再挑一遍）。
        /// </summary>
        public static string RankedMessage(
            string? text,
            string separator,
            int maxLines,
            IReadOnlyList<string[]>? keywordBuckets)
        {
            IReadOnlyList<string> lines = PickImportantLines(text, maxLines, keywordBuckets);

            return string.Join(separator, lines);
        }

        /// <summary>
        /// 挑不出任何关键字行时的可选兜底。
        /// <paramref name="preferFirst"/> = true 取第一行（7-Zip 侧的新口径），
        /// false 取最后一行（UnRAR 的结论常在末尾 —— 老口径，别动）。
        /// </summary>
        public static string FallbackLine(string? text, bool preferFirst)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string[] lines = SplitLines(text);

            if (lines.Length == 0)
            {
                return string.Empty;
            }

            return preferFirst ? lines[0] : lines[^1];
        }

        private static string[] SplitLines(string text) => text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        private static bool MatchesAny(string line, string[] keywords)
        {
            foreach (string keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>7-Zip 的原话关键字（三档；顺序 = 重要度，见 <see cref="EngineOutputKeywords"/>）。</summary>
    public static class SevenZipKeywords
    {
        /// <summary>引擎自己标出来的错行。</summary>
        public static readonly string[] ErrorLines = { "ERROR" };

        /// <summary>说出**原因**的行 —— 用户真正要看的就是这一档。</summary>
        public static readonly string[] ReasonLines =
        {
            "Wrong password",
            "Password is incorrect",
            "Enter password",
            "Can not get password",
            "Cannot get password",
            "CRC Failed",
            "CRC error",
            "Data Error",
            "Data error",
            "Headers Error",
            "Header Error",
            "Unexpected end",
            "Missing volume",
            "Can not open encrypted archive",
            "Cannot open encrypted archive",
            "Can not open",
            "Cannot open",
            "Is not archive",
            "is not archive",
            "Unsupported Method",
            "Unsupported method"
        };

        /// <summary>其它被识别的行（含警告、权限、取消、超时）。</summary>
        public static readonly string[] OtherLines =
        {
            "WARNING",
            "Warning",
            "Can not create",
            "Cannot create",
            "Access is denied",
            "Permission denied",
            "Command Line Error",
            "Incorrect command line",
            "Can not read",
            "Cannot read",
            "Break signaled",
            "User break",
            "User stopped",
            "Operation canceled",
            "Operation cancelled",
            "timed out",
            "timeout"
        };

        /// <summary>三档一次给全（挑行与排序的唯一顺序）。</summary>
        public static string[][] Buckets => new[] { ErrorLines, ReasonLines, OtherLines };
    }

    /// <summary>UnRAR 的原话关键字（三档，与 7-Zip 侧同一份规则）。</summary>
    public static class UnRarKeywords
    {
        public static readonly string[] ErrorLines = { "ERROR" };

        public static readonly string[] ReasonLines =
        {
            "Incorrect password",
            "Wrong password",
            "Enter password",
            "Cannot find volume",
            "Can not find volume",
            "Missing volume",
            "checksum error",
            "Checksum error",
            "CRC error",
            "CRC failed",
            "Unexpected end",
            "Cannot open",
            "Can not open"
        };

        public static readonly string[] OtherLines =
        {
            "WARNING",
            "Warning",
            "Cannot create",
            "Can not create",
            "Access is denied",
            "Permission denied",
            "is not RAR archive",
            "Total errors",
            "Unknown option",
            "timed out",
            "timeout"
        };

        public static string[][] Buckets => new[] { ErrorLines, ReasonLines, OtherLines };
    }
}
