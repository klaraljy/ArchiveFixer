using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Security
{
    /// <summary>
    /// 归档条目名的风险类别。
    ///
    /// ⚠ 这里的声明顺序**不是**危险程度顺序（枚举值一旦重排就会影响比较与持久化），
    /// 真正的优先级只写在 <see cref="ArchivePathGuard"/> 的 Severity 里，一处定义、一处可查。
    /// </summary>
    public enum PathRiskKind
    {
        /// <summary>没有发现问题。</summary>
        None,

        /// <summary>含 <c>..</c> 片段，试图跳出目标目录（Zip Slip 的经典形式）。</summary>
        ParentTraversal,

        /// <summary>绝对路径（<c>/etc/passwd</c> 或 <c>C:\Windows\x</c>）。</summary>
        AbsolutePath,

        /// <summary>盘符相对路径（<c>C:foo</c>，盘符后面没有斜杠），落点取决于该盘的"当前目录"。</summary>
        DriveRelative,

        /// <summary>网络共享路径（<c>\\server\share</c>）。</summary>
        UncPath,

        /// <summary>扩展 / 设备前缀（<c>\\?\</c> 或 <c>\\.\</c>），会绕过常规路径解析。</summary>
        ExtendedPrefix,

        /// <summary>Windows 保留设备名（<c>CON</c> / <c>NUL</c> / <c>COM1</c> …，含带扩展名的形式）。</summary>
        DeviceName,

        /// <summary>名称以点或空格结尾（<c>name.</c> / <c>name␠</c>），Windows 会静默改名，实际落点与预期不符。</summary>
        TrailingDotOrSpace,

        /// <summary>名称含冒号，可能是 NTFS 备用数据流（内容会写到主文件流之外）。</summary>
        AlternateDataStream,

        /// <summary>路径里含不可见控制字符。</summary>
        ControlCharacter,

        /// <summary>条目路径为空。</summary>
        EmptyEntry
    }

    /// <summary>一条不安全条目。<see cref="Reason"/> 是中文说明，可以直接展示给用户。</summary>
    public sealed class UnsafeArchiveEntry
    {
        /// <summary>归档内的原始条目路径（未做任何改写，方便用户回原始包里去核对）。</summary>
        public string EntryPath { get; init; } = string.Empty;

        public PathRiskKind Kind { get; init; }

        /// <summary>为什么判成危险。中文，直接给用户看。</summary>
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>一次批量预检的结果。</summary>
    public sealed class PathSafetyReport
    {
        /// <summary>没有任何危险条目。<b>只代表"条目名"这一层没问题</b>，不代表解压落点安全。</summary>
        public bool IsSafe { get; init; }

        public IReadOnlyList<UnsafeArchiveEntry> UnsafeEntries { get; init; } = Array.Empty<UnsafeArchiveEntry>();

        /// <summary>实际检查过的条目数（含目录条目）。</summary>
        public int CheckedEntryCount { get; init; }

        /// <summary>一句话结论，直接进提示框 / 日志。</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// 解压前的条目路径预检 —— 路径安全的第一道（设计.md §二十五、AGENTS.md §6 第 4 条）。
    ///
    /// ⚠ <b>能力边界，先说清楚，免得被当成"检查过就安全了"</b>：
    /// 真正写盘的是外部进程 7z.exe，<b>进程外无法阻止 Zip Slip</b>。
    /// 本类只做一件事：把那些"一旦落盘就必然越界、或者落点不可预期"的条目名挑出来，
    /// 交给调用方去决定是拒绝整包、排除这些条目，还是提示用户。
    /// 它<b>不</b>保证解压安全 —— 第二道是解压后用 <see cref="IsInsideRoot"/> 校验真实落点，
    /// 而第二道只有在文件已经写出来之后才起作用（能发现越界，不能阻止那一次写入）。
    ///
    /// 还有两个本类<b>看不到</b>的地方，调用方别以为这里盖住了：
    /// ① 符号链接条目 —— <see cref="ArchiveEntry"/> 里没有链接目标信息，
    ///    一个指向目标目录之外的链接解出来照样越界，而它的名字可能干干净净；
    /// ② 引擎自己拼出来的落点 —— 条目名干净不等于引擎的输出路径干净。
    /// </summary>
    public static class ArchivePathGuard
    {
        /// <summary>Summary 里最多列几条样例（列太多会把提示框刷屏，用户反而不看）。</summary>
        private const int MaxSampleCountInSummary = 3;

        private const string NoEntrySummary = "没有可检查的条目";

        /// <summary>
        /// 检查单个归档内条目路径。返回 null 表示"条目名层面没发现问题"。
        ///
        /// 本方法是全函数（对任何输入都不抛异常）：它跑在"一个包几万条条目"的循环里，
        /// 抛一次就会把整批预检带崩，而预检崩溃的后果往往是"干脆不检查了"。
        /// </summary>
        public static UnsafeArchiveEntry? CheckEntry(string? entryPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                return new UnsafeArchiveEntry
                {
                    EntryPath = string.Empty,
                    Kind = PathRiskKind.EmptyEntry,
                    Reason = "条目路径为空，无法确定解压落点"
                };
            }

            // 统一斜杠再分析：归档里（尤其 tar / zip）两种斜杠混用很常见，
            // 只按其中一种切段就会漏判 —— "..\\..\\evil.txt" 正是靠这个绕过只认 "/" 的检查的。
            string normalized = entryPath.Replace('\\', '/');

            /*
             * 整条路径级别的前缀判定，按优先级从高到低短路。
             * \\?\ 必须排在 UNC 之前：它同样以 \\ 开头，但危险等级更高（会绕过常规路径解析，
             * 让 .. 和保留名检查在引擎侧全部失效）。
             */
            if (normalized.StartsWith("//?/", StringComparison.Ordinal) ||
                normalized.StartsWith("//./", StringComparison.Ordinal))
            {
                return new UnsafeArchiveEntry
                {
                    EntryPath = entryPath,
                    Kind = PathRiskKind.ExtendedPrefix,
                    Reason = @"带 \\?\ 或 \\.\ 前缀（扩展 / 设备路径），会绕过常规路径解析，落点完全不可预期"
                };
            }

            if (normalized.StartsWith("//", StringComparison.Ordinal))
            {
                return new UnsafeArchiveEntry
                {
                    EntryPath = entryPath,
                    Kind = PathRiskKind.UncPath,
                    Reason = @"是网络共享路径（UNC），会写到目标目录之外的另一台机器 / 另一个共享上"
                };
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal) || HasDriveWithSeparator(normalized))
            {
                return new UnsafeArchiveEntry
                {
                    EntryPath = entryPath,
                    Kind = PathRiskKind.AbsolutePath,
                    Reason = "是绝对路径，解压会直接写到目标目录之外"
                };
            }

            if (IsDriveRelative(normalized))
            {
                return new UnsafeArchiveEntry
                {
                    EntryPath = entryPath,
                    Kind = PathRiskKind.DriveRelative,
                    Reason = "是盘符相对路径（盘符后面没有斜杠，如 C:x），落点取决于该盘的当前目录，会写到目标目录之外"
                };
            }

            /*
             * 逐段判定。段级规则里 ParentTraversal 最严重，其余并列时取 Severity 更靠前的那条 ——
             * 一条路径只报一个问题（用户一次只需要知道最该先处理的那个），
             * 但整条路径里只要有任何一段命中，结论就是不安全。
             */
            PathRiskKind? worstKind = null;
            string worstReason = string.Empty;

            foreach (string segment in normalized.Split('/'))
            {
                // "a//b" 里的空段没有名字，既不是 .. 也不是设备名，跳过即可。
                if (segment.Length == 0)
                {
                    continue;
                }

                if (!TryGetSegmentRisk(segment, out PathRiskKind kind, out string reason))
                {
                    continue;
                }

                if (worstKind == null || Severity(kind) < Severity(worstKind.Value))
                {
                    worstKind = kind;
                    worstReason = reason;
                }
            }

            if (worstKind == null)
            {
                return null;
            }

            return new UnsafeArchiveEntry
            {
                EntryPath = entryPath,
                Kind = worstKind.Value,
                Reason = worstReason
            };
        }

        /// <summary>
        /// 批量检查。<paramref name="entries"/> 为 null（引擎给不出清单）时返回"没有可检查的条目"，
        /// 而不是假装安全 —— 调用方看到 <see cref="PathSafetyReport.CheckedEntryCount"/> 为 0
        /// 就应该明白"这次根本没做预检"。
        /// </summary>
        public static PathSafetyReport CheckEntries(IEnumerable<ArchiveEntry>? entries)
        {
            if (entries == null)
            {
                return new PathSafetyReport
                {
                    IsSafe = true,
                    UnsafeEntries = Array.Empty<UnsafeArchiveEntry>(),
                    CheckedEntryCount = 0,
                    Summary = NoEntrySummary
                };
            }

            var unsafeEntries = new List<UnsafeArchiveEntry>();
            int checkedCount = 0;

            foreach (ArchiveEntry? entry in entries)
            {
                checkedCount++;

                /*
                 * 目录条目同样要查：目录名决定的是它后面所有文件的落点，
                 * 漏掉目录就等于"目录先跳出去、文件再跟着落进去"，只查文件条目挡不住。
                 * entry 为 null 是调用方数据异常，按空路径处理（判 EmptyEntry），不当成"没问题"。
                 */
                UnsafeArchiveEntry? risk = CheckEntry(entry?.Path);

                if (risk != null)
                {
                    unsafeEntries.Add(risk);
                }
            }

            if (checkedCount == 0)
            {
                return new PathSafetyReport
                {
                    IsSafe = true,
                    UnsafeEntries = Array.Empty<UnsafeArchiveEntry>(),
                    CheckedEntryCount = 0,
                    Summary = NoEntrySummary
                };
            }

            return new PathSafetyReport
            {
                IsSafe = unsafeEntries.Count == 0,
                UnsafeEntries = unsafeEntries,
                CheckedEntryCount = checkedCount,
                Summary = BuildSummary(checkedCount, unsafeEntries)
            };
        }

        /// <summary>
        /// 解压后校验落点：<paramref name="producedPath"/> 必须落在 <paramref name="outputRoot"/> 之内。
        ///
        /// ⚠ 这是第二道防护，<b>只有在文件已经写出来之后</b>才能执行：
        /// 它能发现越界，但阻止不了那一次写入。所以它不能替代第一道 <see cref="CheckEntry"/>。
        ///
        /// 比较用的是规范化后的完整路径（<see cref="SafePathHelper.GetFullPathSafe"/> /
        /// <see cref="SafePathHelper.PathEquals"/>），不是字符串直接比 ——
        /// 比字符串会被 <c>root\..\x</c>、大小写、冗余分隔符绕过去。
        /// </summary>
        public static bool IsInsideRoot(string? outputRoot, string? producedPath, out string reason)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                reason = "目标根目录为空，无法校验落点";
                return false;
            }

            if (string.IsNullOrWhiteSpace(producedPath))
            {
                reason = "产物路径为空，无法校验落点";
                return false;
            }

            if (!TryGetComparableFullPath(outputRoot, out string rootFull) ||
                !TryGetComparableFullPath(producedPath, out string targetFull))
            {
                // 规范化失败说明路径本身有问题（非法字符、超长、带 \\?\ 前缀时也可能落到这里）。
                // 这种情况下"无法确认在目标目录内"就是结论，不能猜成安全。
                reason = "路径无法规范化，无法确认落点是否在目标目录内";
                return false;
            }

            // 落点就是目标根目录本身（例如条目名是 "./"）：没有越界，不算不安全。
            if (SafePathHelper.PathEquals(rootFull, targetFull))
            {
                reason = "落点就是目标根目录本身";
                return true;
            }

            /*
             * 用"带分隔符的前缀"比较，而不是 target.StartsWith(root)：
             * 后者会把 C:\t\out2 误判成在 C:\t\out 里面 —— 前缀相同，但根本不是同一个目录。
             * 路径到这里已经被 GetFullPath 统一成分隔符规范的形式，所以前缀比较是可靠的。
             */
            string prefix = Path.EndsInDirectorySeparator(rootFull)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (targetFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = "落点在目标目录内";
                return true;
            }

            reason = $"落点不在目标根目录内：{targetFull} 不在 {rootFull} 之下";
            return false;
        }

        /// <summary>
        /// 规范成"可以用来比较"的完整路径：展开相对路径、消掉 <c>..</c> 与冗余分隔符，并去掉结尾分隔符
        /// （否则 <c>C:\t\out</c> 与 <c>C:\t\out\</c> 会被当成两个目录）。
        /// </summary>
        private static bool TryGetComparableFullPath(string path, out string fullPath)
        {
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return true;
            }
            catch
            {
                fullPath = string.Empty;
                return false;
            }
        }

        /// <summary>盘符 + 分隔符形式（<c>C:\x</c> / <c>C:/x</c>），即真正的绝对路径。</summary>
        private static bool HasDriveWithSeparator(string normalizedPath)
        {
            return normalizedPath.Length >= 3 &&
                   char.IsAsciiLetter(normalizedPath[0]) &&
                   normalizedPath[1] == ':' &&
                   normalizedPath[2] == '/';
        }

        /// <summary>盘符后面没有分隔符（<c>C:x</c> / <c>C:</c>），落点取决于该盘的当前目录。</summary>
        private static bool IsDriveRelative(string normalizedPath)
        {
            return normalizedPath.Length >= 2 &&
                   char.IsAsciiLetter(normalizedPath[0]) &&
                   normalizedPath[1] == ':';
        }

        /// <summary>判断单个路径段的风险；返回 false 表示这一段没问题。</summary>
        private static bool TryGetSegmentRisk(string segment, out PathRiskKind kind, out string reason)
        {
            /*
             * 先剥掉结尾的空格再判定".."：Windows 解析路径时会静默丢掉每段结尾的空格与点，
             * 于是 ".. " 的实际含义和 ".." 一模一样（剥完就是父目录）。
             * 只按逐字符相等判 ".."，会被一个尾随空格绕过去。
             */
            string withoutTrailingSpaces = segment.TrimEnd(' ');

            // 规则 1：任何一段是 ".." 都危险 —— 不只看开头。"a/../../b" 开头看着人畜无害，
            // 一样能跳出去（这是"只检查前两个字符"的实现最常漏的形态）。
            // 优先级高过设备名 / 冒号 / 控制字符：它是最直接、最该先说的那条。
            if (string.Equals(withoutTrailingSpaces, "..", StringComparison.Ordinal))
            {
                kind = PathRiskKind.ParentTraversal;
                reason = "包含 .. 片段，试图跳出目标目录";
                return true;
            }

            // 规则 2：Windows 保留设备名。
            if (IsReservedDeviceSegment(segment))
            {
                kind = PathRiskKind.DeviceName;
                reason = $"名称 \"{segment}\" 是 Windows 保留设备名（CON / NUL / COM1 …），落盘行为不可预期";
                return true;
            }

            // 规则 3：冒号。盘符形式已在整条路径级别判过，段内部再出现冒号另有含义。
            int colonIndex = segment.IndexOf(':');

            if (colonIndex >= 0)
            {
                if (segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':')
                {
                    // 路径中间夹了一个盘符段（如 a/C:/b）：它同样是盘符相对，报 DriveRelative 更准确。
                    kind = PathRiskKind.DriveRelative;
                    reason = "路径中间出现盘符片段（如 a/C:/b），落点取决于该盘的当前目录";
                    return true;
                }

                kind = PathRiskKind.AlternateDataStream;
                reason = "名称含冒号，可能是 NTFS 备用数据流，内容会写到主文件流之外（也可能被引擎拒绝）";
                return true;
            }

            // 规则 4：不可见控制字符。显示出来和实际落点可能不是一回事，
            // 用户看着"没问题"的名字里可能藏着 \u0001 之类的字符。
            for (int i = 0; i < segment.Length; i++)
            {
                if (char.IsControl(segment[i]))
                {
                    kind = PathRiskKind.ControlCharacter;
                    reason = "路径含不可见控制字符，界面显示与实际落点可能不一致";
                    return true;
                }
            }

            /*
             * 规则 5：". " / "." 单独成段（"当前目录"）不报。
             * 依据：危险条目定义在 ".." / 绝对路径 / 盘符 / UNC / 保留名 / 备用数据流这些形态上，
             * "." 自己逃不出目标目录；而 tar 用 "./" 前缀存条目极其常见，把它报成危险
             * 会把一整个正常的 tar 判成"不安全"、进而被整包拒绝。
             * 代价说清楚："./a/b.txt" 的实际落点（a\b.txt）与朴素拼接的结果不一致，
             * 所以解压后的落点校验（IsInsideRoot）不能因为这里放行就省掉。
             */
            if (string.Equals(withoutTrailingSpaces, ".", StringComparison.Ordinal))
            {
                kind = PathRiskKind.None;
                reason = string.Empty;
                return false;
            }

            // 规则 6：结尾的点或空格。Windows 解析时会静默丢弃 / 改名，
            // 于是"预期落点"和"实际落点"分叉 —— 落点校验也会跟着对不上。
            // 整段只有点或空格的写法（"..." / "   "）一并落在这里，问题同源。
            char last = segment[segment.Length - 1];

            if (last == '.' || last == ' ')
            {
                kind = PathRiskKind.TrailingDotOrSpace;
                reason = "名称以点或空格结尾，Windows 会静默改写（丢掉结尾字符或按当前目录解析），实际落点与预期不符";
                return true;
            }

            kind = PathRiskKind.None;
            reason = string.Empty;
            return false;
        }

        /// <summary>
        /// 单个路径段是不是 Windows 保留设备名。
        ///
        /// Windows 做设备名判定时，会先丢掉每段结尾的点与空格，再只看<b>第一个点之前</b>的部分：
        /// <c>NUL</c> / <c>NUL.txt</c> / <c>NUL.foo.bar</c> / <c>NUL.</c> 全都是设备 NUL。
        /// <see cref="FileNameHelper.IsReservedDeviceName"/> 走的是 Path.GetFileNameWithoutExtension
        /// （只丢最后一个扩展名），对 <c>NUL.txt</c> 有效，但漏掉 <c>NUL.foo.bar</c> 这种多扩展名形式，
        /// 所以这里把"第一个点之前"的形式再判一次。
        /// 设备名清单本身仍然只有 FileNameHelper 一处，本方法不另抄一份名单（避免两处名单漂移）。
        /// </summary>
        private static bool IsReservedDeviceSegment(string segment)
        {
            string trimmed = segment.TrimEnd(' ', '.');

            if (trimmed.Length == 0)
            {
                return false;
            }

            if (FileNameHelper.IsReservedDeviceName(trimmed))
            {
                return true;
            }

            int firstDot = trimmed.IndexOf('.');

            return firstDot > 0 && FileNameHelper.IsReservedDeviceName(trimmed[..firstDot]);
        }

        /// <summary>
        /// 危险程度排序（数字越小越严重）。规则优先级只在这里定义一次，
        /// 枚举本身按可读性排列，不承担优先级含义。
        /// </summary>
        private static int Severity(PathRiskKind kind)
        {
            return kind switch
            {
                PathRiskKind.EmptyEntry => 1,
                PathRiskKind.ExtendedPrefix => 2,
                PathRiskKind.UncPath => 3,
                PathRiskKind.AbsolutePath => 4,
                PathRiskKind.DriveRelative => 5,
                PathRiskKind.ParentTraversal => 6,
                PathRiskKind.DeviceName => 7,
                PathRiskKind.AlternateDataStream => 8,
                PathRiskKind.ControlCharacter => 9,
                PathRiskKind.TrailingDotOrSpace => 10,
                _ => int.MaxValue
            };
        }

        /// <summary>拼一句话结论：带总数、危险条数和最多 3 条样例。</summary>
        private static string BuildSummary(int checkedCount, List<UnsafeArchiveEntry> unsafeEntries)
        {
            if (unsafeEntries.Count == 0)
            {
                /*
                 * 安全的措辞只敢说到"名字这一层"。
                 * 写盘的是 7z.exe，进程外挡不住它；这句话让用户在界面上看到的结论
                 * 和代码真正做到的检查范围一致，不至于以为"预检通过 = 随便解压都安全"。
                 */
                return $"{checkedCount} 个条目名通过预检（仅条目名这一层，实际落点仍需解压后校验）";
            }

            var builder = new StringBuilder();
            builder.Append(checkedCount)
                   .Append(" 个条目中有 ")
                   .Append(unsafeEntries.Count)
                   .Append(" 个不安全：");

            int sampleCount = Math.Min(MaxSampleCountInSummary, unsafeEntries.Count);

            for (int i = 0; i < sampleCount; i++)
            {
                if (i > 0)
                {
                    builder.Append('；');
                }

                builder.Append(unsafeEntries[i].EntryPath)
                       .Append('（')
                       .Append(unsafeEntries[i].Reason)
                       .Append('）');
            }

            if (unsafeEntries.Count > sampleCount)
            {
                builder.Append("；另有 ")
                       .Append(unsafeEntries.Count - sampleCount)
                       .Append(" 条未列出");
            }

            return builder.ToString();
        }
    }
}
