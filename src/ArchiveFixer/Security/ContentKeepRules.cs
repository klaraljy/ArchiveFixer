using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;

namespace ArchiveFixer.Security
{
    /// <summary>
    /// **「内容物保留关键词」的唯一判据出口**（用户 2026-10-04 拍板的新功能
    /// 「内容物压缩文件不解压」，设置项 <see cref="AppSettings.ContentKeepKeywords"/>）。
    ///
    /// <para><b>用户原话</b>：「现在出现一个功能叫做"内容物压缩文件不解压"，这个功能同样要有记忆功能，
    /// 用户可以在里面输入像，<c>1_名字里面包含特定字符的压缩文件不解压</c>，你甚至不用去检测他是否是压缩文件，
    /// 只要文件名里面包含着这个字符就不能动，例如 <c>小明</c>，只要内容物里面有文件的名称包含了"小明"的
    /// 这些压缩文件碰都不要碰，例如 <c>小明.zip</c>、<c>小明.part1.rar</c>，而且不仅仅是相对于的，
    /// 包含的也同样是，例如 <c>小明和小红.7z</c> 这种包含的也不要碰」。</para>
    ///
    /// <para><b>规则（就这四条，⛔ 不许再加）</b>：</para>
    /// <list type="number">
    /// <item><description><b>包含即命中</b>（<see cref="StringComparison.OrdinalIgnoreCase"/>，大小写不敏感）；</description></item>
    /// <item><description>空关键词与空白行**忽略**（列表为空 = 这个功能不生效 = 行为与以前一字不差）；</description></item>
    /// <item><description>⛔ **不做通配 / 正则**（用户没要求，做了反而会让"文件名里真有 <c>*</c>"那种名字变得不可预期）；</description></item>
    /// <item><description>只吃**文件名**（不含路径）—— 传路径进来也只看最后一段，⛔ 绝不拿目录名去撞关键词。</description></item>
    /// </list>
    ///
    /// <para><b>作用范围只在"内容物"</b>（用户明确划的界）：用户**导入的源包**不套用这条 ——
    /// 真机里 <c>小明.zip</c> 是别人给的包时要照常处理（识别 / 改名 / 解压 / 按设置搬运删除）。
    /// 命中之后"碰都不碰"的含义：**不解开、不改名、不搬进其余物、不删**；
    /// 它作为内容物**正常落盘**，定稿归位到目标目录**不算碰**。</para>
    ///
    /// <para>纯函数、不碰盘、不读全局：判据只有这一个实现，三处消费点
    /// （递归层内层归档探测 / 链尾收内层包 / 其余物删除）与续解扫描全部转调它。</para>
    /// </summary>
    public sealed class ContentKeepRules
    {
        /// <summary>一条关键词都没有（= 今天的行为，什么都不拦）。</summary>
        public static readonly ContentKeepRules Empty = new(Array.Empty<string>());

        private readonly string[] _keywords;

        private ContentKeepRules(string[] keywords) => _keywords = keywords;

        /// <summary>归一化之后的关键词（顺序 = 用户填的顺序；日志点名用它）。</summary>
        public IReadOnlyList<string> Keywords => _keywords;

        /// <summary>没有关键词 ⇒ 调用方可以整段跳过检查（**空列表 = 与今天完全一致**）。</summary>
        public bool IsEmpty => _keywords.Length == 0;

        /// <summary>
        /// **设置 → 判据的唯一通道**（⛔ 别在消费点自己读 <c>AppSettings</c> 再拼一遍规则）。
        /// </summary>
        public static ContentKeepRules FromSettings(AppSettings? settings) =>
            settings == null ? Empty : FromKeywords(settings.ContentKeepKeywords);

        /// <summary>按一串关键词造判据（先归一化：Trim、丢空白行、去重）。</summary>
        public static ContentKeepRules FromKeywords(IEnumerable<string>? keywords)
        {
            List<string> normalized = NormalizeKeywords(keywords);

            return normalized.Count == 0 ? Empty : new ContentKeepRules(normalized.ToArray());
        }

        /// <summary>
        /// 归一化关键词清单（**唯一实现**，<c>AppSettings.Normalize</c> 与 <see cref="FromKeywords"/>
        /// 都转调它）：去掉首尾空白、丢掉空行 / 全空白行、按大小写不敏感去重，保持用户输入顺序。
        /// </summary>
        public static List<string> NormalizeKeywords(IEnumerable<string>? keywords)
        {
            var result = new List<string>();

            if (keywords == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string? keyword in keywords)
            {
                string trimmed = (keyword ?? string.Empty).Trim();

                if (trimmed.Length == 0 || !seen.Add(trimmed))
                {
                    continue;
                }

                result.Add(trimmed);
            }

            return result;
        }

        /// <summary>
        /// 命中的那个关键词（没命中 / 没关键词 ⇒ <c>null</c>）。日志点名"命中谁"读它，
        /// ⛔ 别在别处再写一遍包含判断。
        /// </summary>
        /// <param name="fileNameOrPath">
        /// 文件名（传路径进来也只看最后一段）。空 / 空白 ⇒ 没命中（判不出就不拦）。
        /// </param>
        public string? FindMatch(string? fileNameOrPath)
        {
            if (_keywords.Length == 0 || string.IsNullOrWhiteSpace(fileNameOrPath))
            {
                return null;
            }

            string name = GetFileNameSafe(fileNameOrPath!);

            if (name.Length == 0)
            {
                return null;
            }

            foreach (string keyword in _keywords)
            {
                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return keyword;
                }
            }

            return null;
        }

        /// <summary>要不要"碰都不碰"这一个文件（<see cref="FindMatch"/> 的布尔形态）。</summary>
        public bool ShouldKeep(string? fileNameOrPath) => FindMatch(fileNameOrPath) != null;

        /// <summary>
        /// 点名（日志 / 结论里"哪几个"的那半句，最多 <paramref name="max"/> 个名字，多出来折成"还有 K 个"）。
        /// 与批末诊断的"每组最多 3 个名字"同一口径：名字只写文件名（§8：不写个人路径）。
        /// </summary>
        public static string DescribeHitNames(IReadOnlyList<string>? paths, int max = 3)
        {
            if (paths == null || paths.Count == 0)
            {
                return string.Empty;
            }

            var names = new List<string>();

            for (int index = 0; index < paths.Count && index < Math.Max(1, max); index++)
            {
                names.Add(GetFileNameSafe(paths[index]));
            }

            string line = string.Join("、", names);

            return paths.Count > max
                ? $"{line}（还有 {paths.Count - max} 个）"
                : line;
        }

        /// <summary>
        /// 取文件名：⛔ 只保留最后一段（判据只认文件名这件事在这里收口，消费点不许各写一遍）。
        /// 非法路径 / 空 ⇒ 空串（调用方按"没命中"处理 —— 判不出就不拦）。
        /// </summary>
        private static string GetFileNameSafe(string path)
        {
            try
            {
                return Path.GetFileName(path) ?? string.Empty;
            }
            catch
            {
                // 非法字符的路径：判不出文件名 ⇒ 当没命中（绝不因为一个怪名字就拦下整批）。
                return string.Empty;
            }
        }
    }
}
