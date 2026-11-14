using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎标识的**唯一词表**（写进设置、写进报告、用于机器判定的那一份）。
    ///
    /// 为什么要有它：引擎 id 同时出现在三个地方 —— 落盘的 <c>AppSettings.EnginePriority</c>、
    /// 引擎实现自己的 <c>Id</c>、以及界面上的优先级列表。以前 7-Zip 的 id 是
    /// <c>SevenZipEngine</c> 里的一个 <c>private const</c>，别处只能再写一遍字面量；
    /// 一旦有人把 <c>"sevenzip"</c> 拼错成 <c>"7zip"</c>，优先级设置会**静默失效**
    /// （列表里的 id 对不上任何引擎，排序时被当成"未知引擎"排到最后）——
    /// 这类"改了没反应"的缺陷最难查，所以词表只留一份。
    /// </summary>
    public static class EngineIds
    {
        /// <summary>RAR 侧的解压引擎：RARLAB 的免费件 <c>UnRAR.exe</c>（<c>Engines/WinRar/UnRarEngine</c>）。</summary>
        public const string WinRar = "winrar";

        /// <summary>通用引擎：7-Zip 命令行（<c>Engines/SevenZip/SevenZipEngine</c>）。</summary>
        public const string SevenZip = "sevenzip";

        /// <summary>
        /// **换引擎兜底**那一档用的机器标识（<c>Engines/WinRar/WinRarProcessRunner</c>）：
        /// 主引擎把这一包的所有密码候选都试完仍失败、而失败是密码类时，用**用户自己装的**
        /// <c>WinRAR.exe</c> 把同一批候选再试一遍（用户 2026-10-05 拍板"补"）。
        ///
        /// <para>⛔ 它**不在** <see cref="DefaultPriority"/> 里、也不进任何引擎优先级表：
        /// 平时 Zip / 7z 照旧由 7-Zip 解（它才有可解析的进度输出），这一份只在兜底那一档上场。
        /// 它存在的唯一理由是**溯源**（不变量 14）：报告里那一行"引擎：…"必须写清
        /// "这个包到底是谁解开的"，⛔ 不能把 WinRAR 干的活记在 7-Zip 头上。</para>
        /// </summary>
        public const string WinRarFallback = "winrar-fallback";

        /// <summary>
        /// 默认优先级（用户 2026-09-22 指示："先是 winrar、7z、然后就是后面的引擎"）。
        ///
        /// ⚠ 它只是**顺序**，不是"只能用第一个"：选择规则是"先按能力筛，再用优先级做 tiebreaker"，
        /// 不可用的引擎直接跳过 —— 不许因为"排第一但没装"就打不开包（AGENTS.md §3.1）。
        /// </summary>
        public static IReadOnlyList<string> DefaultPriority { get; } = new[] { WinRar, SevenZip };

        /// <summary>
        /// 归一化一个优先级列表（设置层与运行时共用这一份口径）：
        /// 去掉空白项、按不区分大小写去重、保留大小写为词表里的规范写法、
        /// 最后把**词表里还没出现过的引擎**按 <see cref="DefaultPriority"/> 的顺序补到末尾。
        ///
        /// 为什么要把漏掉的引擎补回来（而不是"列表里没有就不用"）：
        /// ① 旧配置里根本没有这个字段 → 反序列化后是空列表，补回来就等于默认值；
        /// ② 以后加第三、第四个引擎时，老配置不会把它们永久排除在外
        ///    （用户只是没排过序，不是"不要这个引擎"）；
        /// ③ 界面上只提供"上移/下移"，没有"删掉某个引擎"，所以补回来不会覆盖用户的真实意图。
        /// </summary>
        public static List<string> Normalize(IEnumerable<string>? priority)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (priority != null)
            {
                foreach (string? raw in priority)
                {
                    string id = Canonicalize(raw);

                    if (id.Length == 0 || !seen.Add(id))
                    {
                        continue;
                    }

                    result.Add(id);
                }
            }

            foreach (string id in DefaultPriority)
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        /// <summary>把用户手改的写法映射回词表里的规范写法（认大小写差异，认不出就原样返回去掉空白后的值）。</summary>
        public static string Canonicalize(string? id)
        {
            string trimmed = id?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (string.Equals(trimmed, WinRar, StringComparison.OrdinalIgnoreCase))
            {
                return WinRar;
            }

            if (string.Equals(trimmed, SevenZip, StringComparison.OrdinalIgnoreCase))
            {
                return SevenZip;
            }

            return trimmed;
        }

        /// <summary>这个 id 在优先级列表里的位置；没列出时返回一个很大的值（排在已知引擎之后，但**不排除**它）。</summary>
        public static int PriorityIndexOf(IReadOnlyList<string>? priority, string? engineId)
        {
            if (priority == null || string.IsNullOrWhiteSpace(engineId))
            {
                return int.MaxValue;
            }

            for (int i = 0; i < priority.Count; i++)
            {
                if (string.Equals(priority[i], engineId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }
}
