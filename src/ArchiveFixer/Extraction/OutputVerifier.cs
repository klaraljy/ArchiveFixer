using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 产物校验的**机器可判**结论（AGENTS.md §7：统计与判定不得依赖中文文案比较）。
    ///
    /// <para>
    /// 为什么一定要有它：清理源包 / 危险模式删其余物 / 续解下一层 / 汇总分桶全都以"这一单到底算出东西了吗"
    /// 为前提，而这个前提以前只能靠 <c>task.Status</c> 的中文字符串去猜。用户 2026-09-24 的真机日志里
    /// 恰好出现了最坏那种情形：**校验已经判否，状态却仍是「解压成功」** ——
    /// 于是"删源 / 搬源 / 续解"三条路会一起误判（那一次源包侥幸没被删，是因为另有一条碰巧的守卫）。
    /// 现在这些裁决一律读这个枚举，读的是**校验那一刻的事实**，与后来谁改了状态字符串无关。
    /// </para>
    /// </summary>
    public enum OutputVerificationOutcome
    {
        /// <summary>还没做过校验（没解压 / 越界这类在定稿之前就结束的情形）。</summary>
        NotAttempted = 0,

        /// <summary>校验通过：产物非空，且与引擎声明的条目数 / 总字节数对得上。</summary>
        Passed = 1,

        /// <summary>校验**判否**：产物为空、或少于预期。这一档永远不许被当成成功（不变量 6）。</summary>
        Failed = 2
    }

    /// <summary>
    /// 解压产物校验结果（M3「一批包一键搞定」）。
    /// </summary>
    public sealed class OutputVerificationResult
    {
        public bool Verified { get; init; }

        /// <summary>机器可判的结论（<see cref="Verified"/> 的枚举形态；二者恒一致）。</summary>
        public OutputVerificationOutcome Outcome =>
            Verified ? OutputVerificationOutcome.Passed : OutputVerificationOutcome.Failed;

        /// <summary>
        /// 用户可读的失败结论（**只在判否时用**）：一句话说清"产物校验未通过 + 预期与实际"。
        /// 任务状态与失败清单都写它 —— 用户 2026-09-24 要求错误信息里必须带这三个数字。
        /// </summary>
        public string FailureMessage =>
            Message.StartsWith("校验未通过", StringComparison.Ordinal)
                ? "产物" + Message
                : "产物校验未通过：" + Message;

        public int ExpectedFileCount { get; init; }

        public int ActualFileCount { get; init; }

        public long ExpectedTotalSize { get; init; }

        public long ActualTotalSize { get; init; }

        /// <summary>
        /// 归档里**仅大小写不同**的同名条目组数（2026-09-27 真机：`rar-android-722.132.apk`）。
        ///
        /// <para>Windows 的盘不区分大小写，`res\9N.9.png` 与 `res\9n.9.png` 物理上只能是**同一个文件**，
        /// 所以这类条目**注定落不全** —— 预期数必须先按"仅大小写去重"折算，否则每一个这种包都会被判成
        /// "产物不完整"（真机后果：把 10 个密码候选白试一遍，最后还报「密码错误」）。</para>
        /// </summary>
        public int CaseOnlyDuplicateGroups { get; init; }

        /// <summary>被大小写折叠**吃掉**的条目数（预期折算时扣掉的份数）。</summary>
        public int CaseOnlyDroppedEntries { get; init; }

        /// <summary>
        /// 这一次校验**到底有没有拿可信清单逐条核对过**（L4 的判据，用户 2026-09-30 的检验等级）。
        ///
        /// <para>为什么必须有这个字段：<see cref="Verified"/> 为 true 有**两种完全不同的含义** ——
        /// ① "拿清单核对过了，产物对得上"；② "没拿到清单，只做了非空 + 非全 0 字节的底线校验"。
        /// 老口径下两者在数据上长得一模一样，于是"删源包"这道不可逆的闸门分不开它们
        /// （见 <see cref="ResultCompletenessClassifier"/>）。这里把事实如实记下来，判定交给那一个出口。</para>
        /// </summary>
        public bool ManifestCrossChecked { get; init; }

        /// <summary>给人看的一句话（直接进汇总报告与日志，不要在这里拼密码等敏感内容）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 解压结果校验。
    ///
    /// 定位：它是**清理源包（不可逆操作）的唯一前置门槛**，所以刻意做得很保守 ——
    /// 只比"文件数 + 总字节数"，且一律用 &gt;= 比较；宁可判定不通过（源包留着），
    /// 也不要把没解压全的包当成成功、进而删掉用户的源文件。
    ///
    /// <para>
    /// <b>产物为空（0 个文件 / 全是 0 字节 / 总字节为 0）一律判否</b>（用户 2026-09-24 铁证）：
    /// "预期 0 个文件 / 0 字节、实际也 0 字节"不是"校验通过"，而是"什么都没解出来"。
    /// </para>
    /// </summary>
    public static class OutputVerifier
    {
        /// <summary>递归统计目录下的文件数与总字节数。目录不存在返回 (0,0)，不抛。</summary>
        public static (int FileCount, long TotalSize) Measure(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return (0, 0);
            }

            if (!SafeDirectoryExists(directory))
            {
                return (0, 0);
            }

            /*
             * 输出目录自身就是符号链接 / 联接点时直接按"没有产物"处理。
             * 理由：跟着链接统计到的字节数完全不能代表本次解压的结果（可能落在别的盘、别的目录），
             * 而这种误判的后果是"校验通过 → 删源包"，属于不可逆错误，必须往保守一侧倒。
             */
            if (!TryGetAttributes(directory, out FileAttributes rootAttributes) ||
                (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return (0, 0);
            }

            int fileCount = 0;
            long totalSize = 0;

            // 用显式栈而不是递归：解压产物可能有很深的目录层级，递归写法有栈溢出风险。
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 权限不足、目录刚好被删掉等等：跳过这一层继续统计。
                    // 单个目录读不了不能把整次校验变成"一个文件都没有"。
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    /*
                     * 不跟随符号链接 / 目录联接点：
                     * 跟随可能绕圈（链接指回祖先目录 → 死循环），
                     * 也可能把链接目标位置的无关文件算进本次产物。
                     */
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    fileCount++;

                    try
                    {
                        totalSize += new FileInfo(entry).Length;
                    }
                    catch
                    {
                        // 大小读不出来时仍然算作一个文件、字节按 0 记：
                        // 校验用 >= 比较，少算字节只会更保守，不会把"缺文件"误判成通过。
                    }
                }
            }

            return (fileCount, totalSize);
        }

        /// <summary>
        /// 解压后校验。expected 为 null 或 Success=false 时：只做“输出目录存在且非空”的底线校验。
        /// </summary>
        public static OutputVerificationResult Verify(string? outputDirectory, ArchiveListResult? expected)
        {
            /*
             * ===== 预期数必须按"仅大小写去重"折算（2026-09-27 真机铁证）=====
             *
             * 现场：`rar-android-722.132.apk`（6.8 MiB，1271 个条目）。候选 1 = **空密码**就解出 1238 个文件，
             * 我们却说"校验未通过：预期 1271 / 实际 1238"，于是把 10 个候选密码全试了一遍（5 分钟），
             * 最后报「密码错误」——而它**根本不需要密码**。
             *
             * 复现结论（内置 7z 实测）：这个包里有 **32 组仅大小写不同的同名条目**（65 条，例
             * `res\9N.9.png` 与 `res\9n.9.png`）。Windows 不区分大小写 → 只能落地一份 → 65 − 32 = **33 个**
             * 缺口，与日志里差的那 33 个逐字吻合。谁赢？实测是**后写的那一条**：按"取每组最后一条"折算，
             * 总字节正好等于盘上的 13,543,946（另两种折算分别是 13,547,285 / 13,562,445，都不对）。
             *
             * 所以：预期 = 按 `Path.ToLowerInvariant()` 分组、每组取**最后一条**的字节数。
             * ⛔ 这不是放宽判据：真正缺文件的包照样判不过（下面仍然是 >= 比较），
             * 只是不再把"Windows 物理上放不下"算成"解压不完整"。
             */
            var (expectedFileCount, expectedTotalSize, caseGroups, caseDropped) = CollapseCaseOnlyDuplicates(expected);
            string caseNote = caseGroups > 0
                ? $"（归档里有 {caseGroups} 组仅大小写不同的同名条目、共 {caseDropped} 条：Windows 不区分大小写，"
                  + "只能落地一份，预期已按\"后写覆盖先写\"折算）"
                : string.Empty;

            bool directoryExists = SafeDirectoryExists(outputDirectory);
            (int actualFileCount, long actualTotalSize) = Measure(outputDirectory);

            /*
             * 规则 1：输出目录不存在 / 是空目录 —— 直接不通过。
             * 必须放在最前面：后面两条规则都以"确实有产物"为前提，
             * 一个文件都没有时比较预期数量毫无意义。
             */
            if (actualFileCount <= 0)
            {
                return new OutputVerificationResult
                {
                    Verified = false,
                    ExpectedFileCount = expectedFileCount,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = expectedTotalSize,
                    ActualTotalSize = actualTotalSize,
                    Message = directoryExists
                        ? "校验未通过：输出目录是空目录，没有产物"
                        : "校验未通过：输出目录不存在，没有产物"
                };
            }

            /*
             * 规则 2：**产物总字节为 0** —— 一律判否（用户 2026-09-24 真机铁证，本条是新增的）。
             *
             * 现场：外层 ZIP 里有加密条目，7z 半成功，写出两个 **0 字节**的 `*.7z.001/.002`；
             * 老口径下"文件数够、字节数也够（拿 0 比 0）"居然判了**通过**，于是
             * ① 任务落「解压成功」② 0 字节的 `*.7z.001` 被当成内层包继续解 ③ 那 2 项还进了 `其余物`。
             * 一条 0 字节的记录根本不是产物：它既不能看、也不能再解，把它算成"解出来了"就是骗人。
             *
             * 位置刻意放在"没有可信预期"那条**之前**：预期拿不到（列目录失败 / `-mhe` 读不出清单）时
             * 老口径只做"目录非空"的底线校验 —— 一堆 0 字节垃圾照样能把那个底线混过去。
             */
            if (actualTotalSize <= 0)
            {
                return new OutputVerificationResult
                {
                    Verified = false,
                    ExpectedFileCount = expectedFileCount,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = expectedTotalSize,
                    ActualTotalSize = actualTotalSize,
                    Message = expected == null || !expected.Success
                        ? $"校验未通过：产物为空 —— 输出目录里有 {actualFileCount} 个文件，但全是 0 字节（没有解出任何内容）"
                        : $"校验未通过：产物为空 —— 预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，" +
                          $"实际 {actualFileCount} 个 / {actualTotalSize} 字节（解出来的全是 0 字节文件）"
                };
            }

            /*
             * 规则 3：没有可信的预期条目（没列过目录，或列目录失败）时，
             * 只能做"输出目录非空"的底线校验。
             * Message 必须写明这一点，否则用户会以为做过完整校验、进而相信后面的删源包。
             *
             * ⚠ 2026-10-02：**「清单成功但一个条目都没有」与「没有清单」是同一件事** ——
             * 空清单能核对的东西是零，而下面的 `<c>actual &gt;= expected</c>`（规则 4）在两个 0 面前
             * **天然成立** ⇒ 老口径会落 `ManifestCrossChecked = true` ⇒ 报「可证完整（拿归档清单逐条核对过，
             * 文件数与总字节都对得上）」并放行"搬走 + 永久删除源包"。真机现场（`（3399）…mp4` 那一族）：
             * 中文界面的 UnRAR 6.11 把 lt 的键名本地化了，一份 5 个文件的包被读成"清单 0 个文件 / 0 字节"，
             * 于是日志里写着「0 比 5 对得上」。⇒ 空清单一律按这一档办：底线校验 + 不算核对过。
             * （第一道修在 `UnRarEngine.InterpretListing`：那种输出根本不该被当成清单；这一条是兜底。）
             */
            bool expectedIsEmpty = expected is { Success: true } && expectedFileCount == 0 && expectedTotalSize == 0;

            if (expected == null || !expected.Success || expectedIsEmpty)
            {
                /*
                 * 清单为什么没拿到，调用方如果说得出原因，就**必须**写出来
                 * （用户 2026-09-30：日志不许再让人猜）。唯一来源是
                 * `ChainManifestResolver` 给的那份空预期的 Message —— 它点名了是哪一层、为什么。
                 */
                string why = expected == null || string.IsNullOrWhiteSpace(expected.Message)
                    ? string.Empty
                    : $"（{expected.Message}）";

                return new OutputVerificationResult
                {
                    Verified = true,

                    /*
                     * ⛔ 这一档"通过"的是**底线校验**，不是清单核对：如实记下来。
                     * 下游（L4 分类器）据此把它判成"判不出完整性" ⇒ 源包一个字节都不删。
                     */
                    ManifestCrossChecked = false,
                    ExpectedFileCount = 0,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = 0,
                    ActualTotalSize = actualTotalSize,
                    Message = expectedIsEmpty
                        ? $"引擎给的清单里一个条目都没有（0 个文件 / 0 字节），核对不了产物，"
                          + $"只做了非空校验：输出目录里有 {actualFileCount} 个文件 / {actualTotalSize} 字节"
                        : $"未取得预期条目数{why}，只做了非空校验：输出目录里有 {actualFileCount} 个文件 / {actualTotalSize} 字节"
                };
            }

            /*
             * 规则 4：用 >= 而不是 ==。
             * ① 用户可能把产物解压进一个本来就有东西的目录；
             * ② 归档里可能有重复条目（同名同内容的多份），展开后的数量与条目数未必一一对应。
             * 用 == 会把这两种正常情况判成"解压不完整"，白白拦下清理源包。
             *
             * 2026-10-05 真机补一句**归因**（用户报："判通过却没说多出来的 4 个是什么"）：
             * 多层递归时**叶子层的清单只描述它自己那一层**，而产物树里还留着别的层解出来的内容物
             * （真机：预期 851 / 实际 855，多出来的 4 个是上一层包里那几个说明 txt）。
             * 判据一个字没放宽（仍然是 >=），只是把"多的是谁"如实点名前几个 ——
             * 用户看到"实际多于预期也算通过"才不会以为程序没核对。
             */
            bool verified = actualFileCount >= expectedFileCount && actualTotalSize >= expectedTotalSize;

            string surplusNote = verified
                && (actualFileCount > expectedFileCount || actualTotalSize > expectedTotalSize)
                    ? DescribeSurplus(outputDirectory, expected, actualFileCount - expectedFileCount)
                    : string.Empty;

            return new OutputVerificationResult
            {
                Verified = verified,

                // 走到这一行就说明：手上确实有可信清单，而且**真的逐条比对过**（判据 = 上面那次比较）。
                ManifestCrossChecked = true,
                ExpectedFileCount = expectedFileCount,
                ActualFileCount = actualFileCount,
                ExpectedTotalSize = expectedTotalSize,
                ActualTotalSize = actualTotalSize,
                CaseOnlyDuplicateGroups = caseGroups,
                CaseOnlyDroppedEntries = caseDropped,
                Message = (verified
                    ? $"校验通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节"
                    : $"校验未通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节")
                    + surplusNote
                    + caseNote
            };
        }

        /// <summary>
        /// 「实际比预期多」时多出来的那些文件是谁（用户 2026-10-05 真机：判了通过却没说多出来的是什么）。
        ///
        /// <para>判据只读盘上事实：把产物树里的文件逐个跟**这一层的清单**对（路径按平台分隔符归一、
        /// 忽略大小写），对不上的就是"多出来的"。⛔ 只说事实与最可能的解释（别的层留下来的内容物 /
        /// 目录里本来就有的东西），⛔ 不替用户下结论说"这就是 XX 层留下的"。</para>
        ///
        /// <para>只在"确实多出来"这一档才走这一遍目录：正常情况下一个字节都不多读。</para>
        /// </summary>
        private static string DescribeSurplus(string? outputDirectory, ArchiveListResult? expected, int extraFileCount)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveEntry? entry in expected?.Entries ?? (IReadOnlyList<ArchiveEntry>)Array.Empty<ArchiveEntry>())
            {
                if (entry == null || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                known.Add(NormalizeRelativePath(entry.Path));
            }

            var names = new List<string>();

            foreach (string file in EnumerateFilesSafe(outputDirectory))
            {
                string relative = NormalizeRelativePath(Path.GetRelativePath(outputDirectory!, file));

                if (known.Contains(relative))
                {
                    continue;
                }

                names.Add(relative);

                if (names.Count >= 3)
                {
                    break;
                }
            }

            if (names.Count == 0)
            {
                // 名字一个都不多（只是字节更多）：如实说"多的是字节"，不编文件名。
                return "（实际比预期多出来的只有字节数 —— 产物里没有清单之外的额外文件）";
            }

            string listed = string.Join("、", names);
            string more = extraFileCount > names.Count ? $"，还有 {extraFileCount - names.Count} 个" : string.Empty;

            return $"（实际比预期多 {extraFileCount} 个文件 —— 多出来的这些不在这一层的清单里，"
                + "多半是别的层解出来、仍留在产物树里的内容物（也可能目录里本来就有）："
                + listed + more + "；判据是「实际多于预期也算通过」，所以照旧通过）";
        }

        /// <summary>路径归一：反斜杠统一成正斜杠、去掉开头的分隔符，比较时再忽略大小写。</summary>
        private static string NormalizeRelativePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        /// <summary>产物树里的文件（不跟随符号链接 / 联接点；读不了的目录跳过 —— 与 <see cref="Measure"/> 同一口径）。</summary>
        private static IEnumerable<string> EnumerateFilesSafe(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !SafeDirectoryExists(directory))
            {
                yield break;
            }

            var pending = new Stack<string>();
            pending.Push(directory!);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes)
                        || (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    yield return entry;
                }
            }
        }

        /// <summary>
        /// 把"归档声明的条目"折算成**在这台机器上真能落地的**条目数 / 字节数：
        /// 按 <c>Path.ToLowerInvariant()</c> 分组，每组只算**最后一条**（Windows 不区分大小写，后写的覆盖先写的）。
        ///
        /// <para>为什么要单独一个函数：这段口径要能被测试直接调（真机证据见 <see cref="Verify"/> 里的长注释），
        /// 也要在 <c>Entries</c> 拿不到时安全退回引擎给的两个总数。</para>
        /// </summary>
        internal static (int FileCount, long TotalSize, int CaseGroups, int DroppedEntries) CollapseCaseOnlyDuplicates(
            ArchiveListResult? expected)
        {
            if (expected == null || !expected.Success || expected.Entries == null || expected.Entries.Count == 0)
            {
                return (expected?.FileCount ?? 0, expected?.TotalUncompressedSize ?? 0, 0, 0);
            }

            // 每组取**最后一条**：字典的赋值天然就是"后来的覆盖先前的"，正合 7z 顺序解压的实际行为。
            var lastSizeByLowerPath = new Dictionary<string, long>(StringComparer.Ordinal);
            var entriesPerLowerPath = new Dictionary<string, int>(StringComparer.Ordinal);
            int totalEntries = 0;

            foreach (ArchiveEntry entry in expected.Entries)
            {
                if (entry == null || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                totalEntries++;

                string key = entry.Path.ToLowerInvariant();
                lastSizeByLowerPath[key] = entry.Size;
                entriesPerLowerPath[key] = entriesPerLowerPath.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            long totalSize = 0;

            foreach (long value in lastSizeByLowerPath.Values)
            {
                totalSize += value;
            }

            int caseGroups = 0;

            foreach (int count in entriesPerLowerPath.Values)
            {
                if (count > 1)
                {
                    caseGroups++;
                }
            }

            /*
             * ⛔ **没有大小写冲突时，一个字都不改**：原样返回引擎给的两个总数。
             *
             * 为什么必须分这一档（2026-09-27 自查逮到的回归）：`Entries` 里每条的大小是**解析出来的**，
             * 而 `TotalUncompressedSize` 是引擎声明的总量 —— 正常情况下两者相等，但"条目大小不可信"
             * 的场合确实存在（测试夹具、头部声明与条目不符的畸形包）。只有**真的存在冲突**时，
             * 才需要（也才允许）用"条目求和"替换引擎的总量。
             */
            if (caseGroups == 0)
            {
                return (expected.FileCount, expected.TotalUncompressedSize, 0, 0);
            }

            return (lastSizeByLowerPath.Count, totalSize, caseGroups, totalEntries - lastSizeByLowerPath.Count);
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}
