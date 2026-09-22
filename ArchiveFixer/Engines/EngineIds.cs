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

    /// <summary>
    /// 引擎层**跨引擎共用**的错误类型词表（设计.md §二十 的回退规则用的就是这一组名字）。
    ///
    /// 为什么单独一份：这些字符串是"引擎 → 调度层"的契约 ——
    /// <c>ExtractionCoordinator</c> 靠 <c>"WrongPassword"</c> 驱动密码候选循环、
    /// 靠 <c>"EncryptedHeaders"</c> 落到"文件名已加密"状态。每个引擎各自写一份字面量，
    /// 就会出现"7-Zip 写对了、第二个引擎拼错一个字母"这种**只在真样本上才暴露**的分歧。
    /// 所以：**值只在这里出现**，两个引擎的解析器都引用它。
    /// （解析逻辑本身仍然各待在自己的引擎目录里 —— AGENTS.md §3.1 禁止项②的边界不变。）
    /// </summary>
    public static class EngineErrorTypes
    {
        /// <summary>密码错误 / 缺正确密码。**解压与测试路径上它是密码候选循环的驱动信号**，不要改写它。</summary>
        public const string WrongPassword = "WrongPassword";

        /// <summary>需要密码但当前没给（内容可读、条目加密）。</summary>
        public const string NeedPassword = "NeedPassword";

        /// <summary>归档加密了**文件名**（RAR <c>-hp</c> / 7z <c>-mhe</c>）：连条目名都读不出来。</summary>
        public const string EncryptedHeaders = "EncryptedHeaders";

        /// <summary>归档损坏 / 校验和不符 / 数据不全。</summary>
        public const string CorruptedArchive = "CorruptedArchive";

        /// <summary>分卷缺失，且**已经确知缺了哪几卷**（不变量 7 要求报出来）。</summary>
        public const string VolumeMissing = "VolumeMissing";

        /// <summary>文件名像分卷、但首卷看不到（数不出缺号，只能告诉用户"先找齐第一卷"）。</summary>
        public const string MissingFirstVolume = "MissingFirstVolume";

        /// <summary>引擎不认识 / 不支持这个格式（可换引擎）。</summary>
        public const string UnsupportedFormat = "UnsupportedFormat";

        /// <summary>引擎不支持某个特性（可换引擎）。</summary>
        public const string UnsupportedFeature = "UnsupportedFeature";

        /// <summary>引擎当前不可用（外部程序没装 / 被删）。</summary>
        public const string EngineUnavailable = "EngineUnavailable";

        /// <summary>引擎解析器拒绝（输出对不上预期）。</summary>
        public const string ParserRejected = "ParserRejected";

        /// <summary>已知兼容性问题（可换引擎）。</summary>
        public const string KnownCompatibilityIssue = "KnownCompatibilityIssue";

        /// <summary>权限不足。</summary>
        public const string AccessDenied = "AccessDenied";

        /// <summary>输出路径冲突。</summary>
        public const string OutputConflict = "OutputConflict";

        /// <summary>路径过长。</summary>
        public const string PathTooLong = "PathTooLong";

        /// <summary>命令行参数错误。</summary>
        public const string CommandLineError = "CommandLineError";

        /// <summary>内存不足。</summary>
        public const string OutOfMemory = "OutOfMemory";

        /// <summary>退出码 1 一类：发生非致命错误，产物是**部分**的（绝不算成功，不变量 6）。</summary>
        public const string NonFatalError = "NonFatalError";

        /// <summary>发生致命错误但分不出更具体的原因。</summary>
        public const string FatalError = "FatalError";

        /// <summary>用户取消。</summary>
        public const string Cancelled = "Cancelled";

        /// <summary>超时。</summary>
        public const string TimedOut = "TimedOut";

        /// <summary>没有结论时的落点，**不编造原因**。</summary>
        public const string UnknownError = "UnknownError";
    }
}
