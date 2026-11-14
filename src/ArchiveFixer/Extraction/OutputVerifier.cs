using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

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
        OutputTreeSnapshot snapshot = OutputTreeSnapshot.Capture(directory);
        return (snapshot.FileCount, snapshot.TotalSize);
    }

    /// <summary>
    /// 解压后校验。expected 为 null 或 Success=false 时：只做“输出目录存在且非空”的底线校验。
    /// </summary>
    public static OutputVerificationResult Verify(
        string? outputDirectory,
        ArchiveListResult? expected,
        string? expectedRootPath = null,
        OutputTreeSnapshot? snapshot = null)
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
        snapshot ??= OutputTreeSnapshot.Capture(outputDirectory);
        int actualFileCount = snapshot.FileCount;
        long actualTotalSize = snapshot.TotalSize;

        bool hasExpectedEntries =
            expected is { Success: true } &&
            expected.Entries is { Count: > 0 };

        string? expectedRootRelative = ResolveExpectedRoot(outputDirectory, expectedRootPath, out bool expectedRootKnown);

        (List<ArchiveEntry> missingEntries, int sizeMismatchCount, string? effectiveRoot, bool suffixFallback) =
            hasExpectedEntries
                ? CompareExpectedEntries(snapshot, expected!, expectedRootRelative)
                : (new List<ArchiveEntry>(), 0, expectedRootRelative, false);

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
         * 叶子层清单只描述它自己那一层；最终产物树里可能还套着包名文件夹。
         * 预期根映射不出来时，不能把“路径对不上”当成“缺文件”，也不能冒充“清单核对过”：
         * 只做非空底线校验，ManifestCrossChecked=false，源包不动。
         */
        if (hasExpectedEntries && !expectedRootKnown && !suffixFallback)
        {
            return new OutputVerificationResult
            {
                Verified = true,
                Known = false,
                ManifestCrossChecked = false,
                ExpectedFileCount = expectedFileCount,
                ActualFileCount = actualFileCount,
                ExpectedTotalSize = expectedTotalSize,
                ActualTotalSize = actualTotalSize,
                Message = $"预期清单对应的产物根无法映射到本次产物目录（{expectedRootPath}），"
                          + $"只做了非空校验：输出目录里有 {actualFileCount} 个文件 / {actualTotalSize} 字节"
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
        bool verified = actualFileCount >= expectedFileCount
            && actualTotalSize >= expectedTotalSize
            && missingEntries.Count == 0
            && sizeMismatchCount == 0;

        /*
         * 只缺 0 字节条目：
         * · 引擎清单里有、盘上没有，但缺的每一条都是 0 字节；
         * · 没有“存在但大小不对”的条目；
         * · 产物总字节不少于清单。
         * 这一档内容字节是完整的（真机 24 就是 12/11、6,937,356,265 对 6,937,356,265），
         * 但逐条完整性仍不是 Complete：不删源包，只允许发布已核过的内容。
         */
        bool onlyZeroByteEntriesMissing =
            !verified
            && hasExpectedEntries
            && sizeMismatchCount == 0
            && missingEntries.Count > 0
            && missingEntries.All(entry => entry.Size == 0)
            && actualTotalSize >= expectedTotalSize;

        if (onlyZeroByteEntriesMissing)
        {
            verified = true;
        }

        string missingNote = missingEntries.Count == 0
            ? string.Empty
            : "；缺 " + missingEntries.Count + " 个条目："
              + string.Join(
                  "、",
                  missingEntries
                      .Take(5)
                      .Select(entry => $"{entry.Path}（{entry.Size} 字节）"))
              + (missingEntries.Count > 5 ? " 等" : string.Empty);

        string suffixNote = suffixFallback
            ? $"；清单路径未在预期根下逐条命中，按实际产物的唯一路径后缀匹配到公共根 {(string.IsNullOrWhiteSpace(effectiveRoot) ? "（空）" : effectiveRoot)}，"
              + "清单条目与字节逐条对上"
            : string.Empty;

        string surplusNote = verified
            && !onlyZeroByteEntriesMissing
            && (actualFileCount > expectedFileCount || actualTotalSize > expectedTotalSize)
                ? DescribeSurplus(snapshot, expected, actualFileCount - expectedFileCount, effectiveRoot)
                : string.Empty;

        return new OutputVerificationResult
        {
            Verified = verified,

            /*
             * 正常清单核对通过 ⇒ true。
             * 只缺 0 字节条目 ⇒ false：内容可以发布，但不是“可证完整”，
             * 因此 L4 落 Undeterminable、源包不删（用户 2026-11-06 拍板的口径）。
             */
            ManifestCrossChecked = verified && !onlyZeroByteEntriesMissing,
            ExpectedFileCount = expectedFileCount,
            ActualFileCount = actualFileCount,
            ExpectedTotalSize = expectedTotalSize,
            ActualTotalSize = actualTotalSize,
            CaseOnlyDuplicateGroups = caseGroups,
            CaseOnlyDroppedEntries = caseDropped,
            OnlyZeroByteEntriesMissing = onlyZeroByteEntriesMissing,
            MissingEntries = missingEntries,
            Message = onlyZeroByteEntriesMissing
                ? $"内容字节完整，但清单核对缺 {missingEntries.Count} 个 0 字节条目"
                  + $"（预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，"
                  + $"实际 {actualFileCount} 个 / {actualTotalSize} 字节）{missingNote}"
                : (verified
                    ? $"校验通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节"
                    : $"校验未通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节")
                  + missingNote
                  + suffixNote
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
    private static string DescribeSurplus(
        OutputTreeSnapshot snapshot,
        ArchiveListResult? expected,
        int extraFileCount,
        string? expectedRootRelative = null)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveEntry? entry in expected?.Entries ?? (IReadOnlyList<ArchiveEntry>)Array.Empty<ArchiveEntry>())
        {
            if (entry == null || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            known.Add(NormalizeExpectedPath(entry.Path, expectedRootRelative));
        }

        var names = new List<string>();

        foreach (string relative in snapshot.FilesByRelativePath.Keys)
        {
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

    /// <summary>
    /// 逐条对账：返回清单里有、盘上**根本不存在**的条目，以及“存在但大小不对”的条目数。
    ///
    /// <para>只做事实比较；大小写折叠与“后写覆盖先写”沿用 <see cref="CollapseCaseOnlyDuplicates"/>
    /// 的同一口径（每组最后一条）。目录已在清单侧滤掉。</para>
    /// </summary>
    private static (List<ArchiveEntry> Missing, int SizeMismatchCount, string? EffectiveRoot, bool SuffixFallback)
        CompareExpectedEntries(
            OutputTreeSnapshot snapshot,
            ArchiveListResult expected,
            string? expectedRootRelative)
    {
        IReadOnlyDictionary<string, long> actualByPath = snapshot.FilesByRelativePath;

        var lastExpectedByPath = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveEntry entry in expected.Entries ?? Array.Empty<ArchiveEntry>())
        {
            if (entry == null || entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            lastExpectedByPath[NormalizeExpectedPath(entry.Path, expectedRootRelative)] = entry;
        }

        var missing = new List<ArchiveEntry>();
        int sizeMismatchCount = 0;

        foreach ((string path, ArchiveEntry entry) in lastExpectedByPath)
        {
            if (!actualByPath.TryGetValue(path, out long actualSize))
            {
                missing.Add(entry);
            }
            else if (actualSize != entry.Size)
            {
                sizeMismatchCount++;
            }
        }

        if (missing.Count == 0 && sizeMismatchCount == 0)
        {
            return (missing, sizeMismatchCount, expectedRootRelative, false);
        }

        /*
         * 叶子层清单可能被发布到“包名层 / 归档自带文件夹”之下，而 work 目录里的
         * layer.OutputPath 并不是最终产物根。此时按完整相对路径精确比对必然报缺，
         * 但文件确实在产物树里。这里只在“每个清单条目在整个产物树里唯一命中、大小一致、
         * 且公共前缀一致”时采用该前缀；任何重复/缺失/大小不符都不放宽。
         */
        string? suffixRoot = TryFindUniqueSuffixRoot(actualByPath, lastExpectedByPath);

        if (suffixRoot != null)
        {
            var fallbackMissing = new List<ArchiveEntry>();
            int fallbackMismatch = 0;

            foreach (ArchiveEntry entry in lastExpectedByPath.Values)
            {
                string path = NormalizeExpectedPath(entry.Path, suffixRoot);

                if (!actualByPath.TryGetValue(path, out long actualSize))
                {
                    fallbackMissing.Add(entry);
                }
                else if (actualSize != entry.Size)
                {
                    fallbackMismatch++;
                }
            }

            if (fallbackMissing.Count == 0 && fallbackMismatch == 0)
            {
                return (fallbackMissing, 0, suffixRoot, true);
            }
        }

        return (missing, sizeMismatchCount, expectedRootRelative, false);
    }

    /// <summary>
    /// 为清单找“唯一的实际产物公共前缀”：每个预期相对路径在产物树里只能有一个
    /// 以该路径结尾的实际文件，大小必须一致，且所有命中文件剥掉预期路径后的目录前缀相同。
    /// 任一条件不成立返回 null。
    /// </summary>
    private static string? TryFindUniqueSuffixRoot(
        IReadOnlyDictionary<string, long> actualByPath,
        IReadOnlyDictionary<string, ArchiveEntry> expectedByPath)
    {
        string? root = null;

        foreach (ArchiveEntry entry in expectedByPath.Values)
        {
            string expectedPath = NormalizeRelativePath(entry.Path);
            string suffix = "/" + expectedPath;

            var matches = actualByPath
                .Where(kv => string.Equals(kv.Key, expectedPath, StringComparison.OrdinalIgnoreCase)
                             || kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length != 1 || matches[0].Value != entry.Size)
            {
                return null;
            }

            string matched = matches[0].Key;
            string candidate = matched.Length == expectedPath.Length
                ? string.Empty
                : NormalizeRelativePath(matched.Substring(0, matched.Length - expectedPath.Length));

            if (root == null)
            {
                root = candidate;
            }
            else if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return root ?? string.Empty;
    }

    /// <summary>
    /// 把清单对应的产物根折算成**相对最终产物目录的前缀**。
    /// 空根 = 前缀为空；根在产物目录之外/取不到 = known=false。
    /// </summary>
    private static string? ResolveExpectedRoot(
        string? outputDirectory,
        string? expectedRootPath,
        out bool known)
    {
        known = true;

        if (string.IsNullOrWhiteSpace(expectedRootPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            known = false;
            return null;
        }

        try
        {
            string outputFull = Path.GetFullPath(outputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootFull = Path.GetFullPath(expectedRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(outputFull, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string relative = Path.GetRelativePath(outputFull, rootFull);

            if (string.Equals(relative, ".", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                if (!string.Equals(relative, ".", StringComparison.Ordinal))
                {
                    known = false;
                }

                return null;
            }

            return NormalizeRelativePath(relative);
        }
        catch
        {
            known = false;
            return null;
        }
    }

    private static string NormalizeExpectedPath(string path, string? expectedRootRelative)
    {
        string normalized = NormalizeRelativePath(path);

        return string.IsNullOrWhiteSpace(expectedRootRelative)
            ? normalized
            : NormalizeRelativePath(expectedRootRelative + "/" + normalized);
    }

    /// <summary>路径归一：反斜杠统一成正斜杠、去掉开头的分隔符，比较时再忽略大小写。</summary>
    private static string NormalizeRelativePath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim('/');
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
