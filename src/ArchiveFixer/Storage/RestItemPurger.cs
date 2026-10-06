using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;

namespace ArchiveFixer.Storage
{
    /// <summary>危险模式下一次"彻底删其余物"的结论。</summary>
    public sealed class RestPurgeOutcome
    {
        /// <summary>是否真的执行过删除（被门槛挡下 / 没有可删的 → false，此时一个字节都没动）。</summary>
        public bool Attempted { get; init; }

        /// <summary>是否**确实释放了空间**（危险模式的全部意义就在这里：净占用基本不变）。</summary>
        public bool Succeeded { get; init; }

        /// <summary>删掉的其余物目录（没删时为空）。</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>彻底删除真正释放的字节数。</summary>
        public long FreedBytes { get; init; }

        /// <summary>顶层条目数。</summary>
        public int EntryCount { get; init; }

        /// <summary>
        /// **这一档是"空壳就地删"**（用户 2026-10-03）：其余物里递归地一个文件都没有 ⇒ 直接
        /// <c>Directory.Delete(recursive: true)</c> 掉，**一个字节都没进回收站**。
        ///
        /// <para>为什么要这个事实位、而不是让调用方看 <c>EntryCount == 0 &amp;&amp; FreedBytes == 0</c>：
        /// 那两个数**证明不了**"没进回收站"（别的原因也能是 0），而调用方要据此选文案 ——
        /// 选了「移入回收站」档时照旧写"已移入回收站"就是一句**假话**
        /// （AGENTS.md §9.5：要删 / 要写盘的动作判据只准读事实，⛔ 不许比数字猜）。</para>
        /// </summary>
        public bool RemovedAsEmptyShell { get; init; }

        /// <summary>一句话结论（进日志与任务详情）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>删除日志（路径 + 理由 + 条目数 + 总大小），调用方原样写进界面/文件日志。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 「删除操作」那一档的**唯一执行体**：按用户选的方式处理一个任务自己的 `其余物`
    /// （<see cref="DeleteMode.RecycleBin"/> = 移入回收站，可还原；<see cref="DeleteMode.Permanent"/> = 彻底删除）。
    ///
    /// <para>⚠ 2026-09-25 第 32 条起它不再只服务"危险模式"（那套已整块退役）：③页的「删除操作」
    /// 三档里后两档都走这里，<b>五道门槛一个字不改</b>。</para>
    ///
    /// <para><b>门槛（五条，缺一不可 —— 这是那条红线"失败不删任何东西"的落点）</b>：</para>
    /// <list type="number">
    /// <item><description>任务的**机器终态**是「完成」（<see cref="TaskOutcome.Succeeded"/>，也就是产物已定稿 +
    /// 校验通过 + 源包处理没出问题）——「部分完成」「解压失败」「已取消」「已跳过」一律不动。
    /// ⚠ 判据是**枚举**，不是 <c>Status</c> 那个中文文案（用户 2026-09-24 要求：删除的裁决只准看事实）；</description></item>
    /// <item><description>输出校验通过（<see cref="ArchiveTask.OutputVerification"/> ==
    /// <see cref="OutputVerificationOutcome.Passed"/>，同样读事实而不是文案）；</description></item>
    /// <item><description>没被取消（调用方传进来的 <c>cancelled</c>）；</description></item>
    /// <item><description>`其余物` 目录**是本次真的记下来的那一个**（<see cref="ArchiveTask.RestDirectoryPath"/>）——
    /// 现场重算会在三处算错（内容物那层也叫「其余物」时的 `其余物(1)`、共享根下按包名分的那层、归集把目录整体搬走之后的落点），
    /// 而算错的后果是把**别人的**其余物删掉；</description></item>
    /// <item><description>它落在本任务自己的输出根之内（<see cref="ArchivePathGuard.IsInsideRoot"/>），
    /// 而且路径形状确实是"其余物"（自己就是，或上一级是 —— 共用输出根模式是 `其余物\包名`）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么不用 <c>MaintenanceCleanupService.CleanProcessArtifacts</c></b>：它按任务**现场**解析作用域
    /// （<c>ResolveArtifactScope</c>），而危险模式动作的时刻是"源包刚被搬进其余物之后"——
    /// 那时 <see cref="ArchiveTask.CurrentPath"/> 已经指向其余物里面的新位置，
    /// 现场解析在共享输出根模式下会退化成"整个 `其余物\`"，一删就是同目录里所有包的过程物。
    /// 所以这里只认定稿那一刻记下来的那一条路径（门槛 4），并用"必须在自己的输出根之内 + 形状必须是其余物"
    /// 两道校验兜住（门槛 5），两条都过了才动手。</para>
    ///
    /// <para><b>线程</b>：全是磁盘活，调用方必须放后台线程。</para>
    /// </summary>
    public sealed class RestItemPurger
    {
        /// <summary>
        /// 点名时最多写几个名字（多出来折成"还有 K 个"）—— 与批末诊断的"每组最多 3 个名字"同一口径。
        /// </summary>
        private const int MaxContentKeepNames = 3;

        private readonly IDeleteExecutor _executor;
        private readonly IDeleteFileSystemProbe _probe;
        private readonly ContentKeepRules _keepRules;

        /// <param name="executor">删除执行体（默认真实文件系统 / 回收站）。</param>
        /// <param name="probe">文件系统探测器（默认 Windows 实现）。</param>
        /// <param name="keepRules">
        /// 「内容物保留关键词」判据（用户 2026-10-04 的新功能；唯一出口 <see cref="ContentKeepRules"/>）。
        ///
        /// <para>⛔ 判据放在**执行体里**（不是各个调用点）：其余物的删除只有这一个类，
        /// 三处调用（链尾 / 任务收尾 / 链尾补搬）都要过它 —— 放调用点等于漏两处。
        /// 生产路径三处构造点都从设置里取（<c>ContentKeepRules.FromSettings</c>）；
        /// 传 <c>null</c> / 空列表 ⇒ **一个都不拦**（行为与加这条功能之前逐字相同）。</para>
        ///
        /// <para>命中之后的口径：**整份不删 + 一行点名**（用户要求"碰都不碰"，而"整目录删"与
        /// "里面有命中项"冲突时，兜底永远落在"什么都不做"那一档）。</para>
        /// </param>
        public RestItemPurger(
            IDeleteExecutor? executor = null,
            IDeleteFileSystemProbe? probe = null,
            ContentKeepRules? keepRules = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
            _keepRules = keepRules ?? ContentKeepRules.Empty;
        }

        /// <summary>彻底删除那一档的理由（写进删除日志）。必须写清"为什么可以删"与"进了哪里"。</summary>
        public const string AutoPurgeReason =
            "删除操作=彻底删除：内容物已定稿并按落点策略排好、输出校验通过、未取消 —— 彻底删除本任务的其余物（源包 + 过程物，不进回收站）";

        /// <summary>移入回收站那一档的理由。</summary>
        public const string AutoRecycleReason =
            "删除操作=移入回收站：内容物已定稿并按落点策略排好、输出校验通过、未取消 —— 把本任务的其余物（源包 + 过程物）移入回收站（可还原；空间要等清空回收站才释放）";

        /// <summary>
        /// 部分完成收尾那一档的理由（用户 2026-10-02：「我建议源包 + 已解出的内容物留着，其他的都删掉」）。
        /// </summary>
        public const string PartialPurgeReason =
            "部分完成收尾：已解出的内容物按「部分完成」发布进了目标目录，这条链的最外层源包仍在盘上（它是唯一能重建整条链的东西）"
            + " —— 所以其余物里「除它以外」的过程物（内层包、抠出来的内嵌归档副本）都删掉，盘上只留「源包 + 内容物」两份";

        /// <summary>
        /// **部分完成之后的"半份清理"**：其余物里除 <paramref name="keepPaths"/> 以外全删。
        ///
        /// <para><b>为什么是同一个执行体</b>：其余物的删除只有这一个类（AGENTS.md：「其余物的删除只有这一个执行体，
        /// 三处调用都要过它」）—— 新口径要删的只是"其余的项"，但那仍然是其余物，走别处等于又开了一个删删除体。</para>
        ///
        /// <para><b>门槛（与 <see cref="Purge"/> 同一套容器内校验，判据另立两条）</b>：</para>
        /// <list type="number">
        /// <item><description>`其余物` 目录**是本次真的记下来的那一个**（<see cref="ArchiveTask.RestDirectoryPath"/>），
        /// 形状必须是其余物、必须在自己输出根之内 —— 与 <see cref="Purge"/> 逐字相同；</description></item>
        /// <item><description>第六道门槛（分卷半套，<see cref="Extraction.RestVolumeCompletenessGate"/>）照过；</description></item>
        /// <item><description>**要保留的东西里至少有一个此刻真的在盘上** —— 调用方传进来的判据是
        /// "这条链的最外层源包"。它不在 ⇒ 判不出 ⇒ **一个字节都不删**（源包是唯一能重建整条链的东西，
        /// 它都没了还清过程物，等于把最后一份可恢复路径也删掉）；</description></item>
        /// <item><description>要保留的那一项**在其余物里面**时按项保留（不会被当成"别的项"顺手删掉）。</description></item>
        /// </list>
        /// </summary>
        public RestPurgeOutcome PurgeExcept(
            ArchiveTask? task,
            IReadOnlyList<string>? keepPaths,
            DeleteMode mode = DeleteMode.Permanent)
        {
            // 这一档（部分完成发布）没有"换证据来源"的需求：输出范围按本任务自己的算。
            string? allowedRootEvidence = null;

            if (task == null)
            {
                return Skip("没有任务，未删除任何东西");
            }

            string name = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return Skip($"{name}：没记下本次定稿实际用的其余物目录，不敢猜路径，一个字节都不删");
            }

            string directory = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Skip($"{name}：其余物目录不存在（{task.RestDirectoryPath}），没有可删除的内容");
            }

            if (!TryResolveAllowedRoot(task, directory, allowedRootEvidence, out string allowedRoot, out string why))
            {
                return Skip($"{name}：{why}，已拒绝删除");
            }

            if (!LooksLikeArtifactDirectory(directory))
            {
                return Skip(
                    $"{name}：{directory} 的形状不像「其余物」目录（既不是 {ProcessArtifactLayout.ArtifactDirectoryName}，"
                    + "上一级也不是），已拒绝删除");
            }

            if (!ArchivePathGuard.IsInsideRoot(allowedRoot, directory, out string outsideReason))
            {
                return Skip($"{name}：其余物目录不在本任务的输出范围之内，已拒绝删除 —— {outsideReason}");
            }

            string? splitGroupBlocker = Extraction.RestVolumeCompletenessGate.DescribeBlocker(directory);

            if (splitGroupBlocker != null)
            {
                return Skip($"{name}：{splitGroupBlocker}");
            }

            /*
             * ===== 「内容物保留关键词」：其余物里有命中项 ⇒ 整份不删（用户 2026-10-04 的新功能）=====
             *
             * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
             * 其余物里躺着的是**过程物 / 内容物**（内层包、抠出来的内嵌归档副本）—— 命中关键词的那些
             * 一个字节都不许删；而这一档删的是**整个目录**，两件事冲突 ⇒ 兜底落在"什么都不做"：
             * **整份不删 + 一行点名**。
             *
             * ⚠ 唯一豁免：**这一单自己的源包**（<see cref="ArchiveTask.CurrentPath"/> /
             * <see cref="ArchiveTask.VolumePaths"/>）。用户明确划的界是"只管内容物" ——
             * 别人给的 `小明.zip` 作为源包被搬进其余物时照常按设置处理。
             * 判据读的是任务上的**事实路径**（搬完之后管线会把它们改成其余物里的新位置），⛔ 不猜名字。
             */
            string? contentKeepBlocker = DescribeContentKeepBlocker(
                directory,
                EnumerateTaskSourcePaths(task));

            if (contentKeepBlocker != null)
            {
                return Skip($"{name}：{contentKeepBlocker}");
            }

            var keep = new List<string>();

            foreach (string path in keepPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(path).TrimEnd('\\', '/');

                if (full.Length > 0 && (File.Exists(full) || Directory.Exists(full)))
                {
                    keep.Add(full);
                }
            }

            if (keep.Count == 0)
            {
                return Skip(
                    $"{name}：要保留的东西（这条链的最外层源包）一个都不在盘上 —— 判不出 ⇒ 其余物一个字节都不删");
            }

            var targets = new List<string>();

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    string full = entry.TrimEnd('\\', '/');

                    // 要保留的那一项（或它的祖先 / 后代）跳过 —— 其余的一律是过程物。
                    if (keep.Any(item => IsSameOrUnder(full, item) || IsSameOrUnder(item, full)))
                    {
                        continue;
                    }

                    targets.Add(full);
                }
            }
            catch (Exception ex)
            {
                return Skip($"{name}：读不了其余物目录（{ex.Message}），一个字节都不删");
            }

            if (targets.Count == 0)
            {
                return Skip($"{name}：其余物里除了要保留的以外没有别的项，没有可删除的内容");
            }

            /*
             * ===== 空壳就地删掉，不进回收站（用户 2026-10-03 真机）=====
             *
             * 与 <see cref="Purge"/> 那一档**同一份判据、同一条路**：按 keepPaths 排除之后剩下的
             * 如果**全是**空目录（递归一个文件都没有），就一个个就地删掉，⛔ 不再逐个条目送回收站
             * —— 那就是回收站里那堆 1 KB 同名「其余物」文件夹的另一半来源。
             *
             * ⛔ keepPaths 的语义一个字不改：要保留的那一份（这条链的最外层源包）怎么算、怎么排除，
             * 全在上面，这里只决定"剩下这些怎么删"。目标里只要有一个是真文件，
             * <see cref="IsFileFreeDirectoryTree"/> 就对它返回 false ⇒ 整个 targets 照旧走回收站。
             */
            if (targets.All(target => IsFileFreeDirectoryTree(target)))
            {
                int removedCount = 0;

                foreach (string target in targets)
                {
                    try
                    {
                        // 判据已证明这一个递归无文件 ⇒ 只剩空目录结构，就地删掉（不进回收站）。
                        Directory.Delete(target, recursive: true);
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        return Skip(
                            $"{name}：其余物里剩下的全是空壳，但就地删空目录失败（{target}：{ex.Message}）"
                            + $" —— 已就地删掉 {removedCount} / {targets.Count} 个空目录，没有送进回收站，"
                            + "没删掉的项仍在原处");
                    }
                }

                string shellLine =
                    $"{name}：其余物里除要保留的以外只剩下空壳（0 个文件），已就地删掉 {removedCount} 个空目录，"
                    + "没有送进回收站";

                return new RestPurgeOutcome
                {
                    Attempted = true,
                    Succeeded = true,
                    Directory = directory,
                    FreedBytes = 0,
                    EntryCount = 0,
                    RemovedAsEmptyShell = true,
                    Message = shellLine + $"，保留的那一份仍在盘上：{string.Join("、", keep)}",
                    LogLines = new[] { shellLine }
                };
            }

            var service = new RecycleBinService(_executor, null, _probe);

            var requests = targets
                .Select(path => new DeleteRequest(path, PartialPurgeReason))
                .ToList();

            DeleteResult result;

            try
            {
                result = service.Delete(
                    requests,
                    new DeleteOptions
                    {
                        AllowedRoot = allowedRoot,
                        UserConfirmed = true,
                        Mode = mode,
                        Reason = PartialPurgeReason
                    });
            }
            catch (Exception ex)
            {
                return Skip($"{name}：清理其余物里的过程物时出现意外错误（{ex.Message}），一个字节都没删");
            }

            var logLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList();
            int entryCount = result.Outcomes.Sum(outcome => outcome.EntryCount);

            if (result.SuccessCount <= 0)
            {
                string failure = result.FailureReasons.Count > 0
                    ? string.Join("；", result.FailureReasons)
                    : result.Message;

                return new RestPurgeOutcome
                {
                    Attempted = true,
                    Succeeded = false,
                    Directory = directory,
                    Message = $"{name}：其余物里的 {targets.Count} 个过程物没能删掉（{failure}）—— "
                              + "内容物与源包都不受影响",
                    LogLines = logLines
                };
            }

            return new RestPurgeOutcome
            {
                Attempted = true,
                Succeeded = true,
                Directory = directory,
                FreedBytes = result.FreedBytes,
                EntryCount = entryCount,
                Message = $"{name}：其余物里除要保留的以外已删掉 {result.SuccessCount} / {targets.Count} 个项"
                          + $"（{entryCount} 个条目 / 释放 {TaskSpaceEstimate.FormatSize(result.FreedBytes)}，不进回收站）"
                          + $"，保留的那一份仍在盘上：{string.Join("、", keep)}",
                LogLines = logLines
            };
        }

        /// <summary>
        /// 「内容物保留关键词」这一档的**唯一判据**（用户 2026-10-04 的新功能，执行体里判）：
        /// 其余物这棵树里**有没有**名字命中关键词的文件 —— 有 ⇒ 返回一句话（点名哪几个 + 命中哪个词），
        /// 调用方**整份不删**；没有 ⇒ <c>null</c>（照旧按档删）。
        ///
        /// <para><b>三条口径</b>：① 空关键词列表 ⇒ 直接 <c>null</c>（连扫都不扫，行为与加这条功能之前
        /// 逐字相同）；② <paramref name="exemptPaths"/> 里的是**这一单自己的源包**（用户划的界：
        /// 只管内容物，源包照常处理）；③ **扫不动（权限 / 目录读不了）⇒ 当作"有命中项"拦下**
        /// —— 兜底永远落在"什么都不做"那一档（AGENTS.md §9.5）。</para>
        /// </summary>
        private string? DescribeContentKeepBlocker(string directory, IReadOnlyList<string> exemptPaths)
        {
            if (_keepRules.IsEmpty)
            {
                return null;
            }

            var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in exemptPaths)
            {
                string full = SafePathHelper.GetFullPathSafe(path);

                if (full.Length > 0)
                {
                    exempt.Add(full);
                }
            }

            var hits = new List<string>();
            string? hitKeyword = null;

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    if (exempt.Contains(SafePathHelper.GetFullPathSafe(file)))
                    {
                        // 这一单自己的源包：不套用关键词（见方法注释第 ② 条）。
                        continue;
                    }

                    string? keyword = _keepRules.FindMatch(file);

                    if (keyword == null)
                    {
                        continue;
                    }

                    hitKeyword ??= keyword;
                    hits.Add(file);
                }
            }
            catch (Exception ex)
            {
                return $"其余物里有「内容物保留关键词」这一档，但这棵树扫不动（{ex.Message}）"
                       + " —— 判不出里面有没有命中项，所以一个字节都不删（宁可不动，也不误删）";
            }

            if (hits.Count == 0)
            {
                return null;
            }

            return $"其余物里有 {hits.Count} 个文件命中「内容物保留关键词」（{hitKeyword}）："
                   + $"{ContentKeepRules.DescribeHitNames(hits, MaxContentKeepNames)}"
                   + " —— 按设置这些文件碰都不碰，所以这一份其余物整份不删（一个字节都没动）";
        }

        /// <summary>
        /// 这一单**自己的源包路径**（搬进其余物之后 <see cref="ArchiveTask.CurrentPath"/> 与
        /// <see cref="ArchiveTask.VolumePaths"/> 已经是新位置）—— 它们是关键词这一档的**豁免项**：
        /// 用户划的界是"只管内层包这类内容物"，别人给的 `小明.zip` 当源包时照常按设置处理。
        /// </summary>
        private static List<string> EnumerateTaskSourcePaths(ArchiveTask? task)
        {
            var paths = new List<string>();

            if (task == null)
            {
                return paths;
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                paths.Add(task.CurrentPath);
            }

            foreach (string volumePath in task.VolumePaths)
            {
                if (!string.IsNullOrWhiteSpace(volumePath))
                {
                    paths.Add(volumePath);
                }
            }

            return paths;
        }

        /// <summary><paramref name="candidate"/> 就是 <paramref name="root"/>，或者在它之下。</summary>
        private static bool IsSameOrUnder(string candidate, string root)
        {
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 试着删除一个任务的其余物。**任何一条门槛不成立都返回"没动"**，并给出原因（不抛异常）。
        /// </summary>
        /// <param name="task">目标任务。</param>
        /// <param name="cancelled">这一刻是不是已经被取消（用户按了「取消当前」/「停止后续」）。</param>
        /// <param name="mode">怎么处理：<see cref="DeleteMode.RecycleBin"/> 或 <see cref="DeleteMode.Permanent"/>。</param>
        /// <param name="completenessEvidence">
        /// **完整性证据的来源**。默认（null）= 本任务自己那一份（<see cref="ResultCompletenessClassifier.Classify"/>）。
        ///
        /// <para>唯一需要换来源的形状（真机 CCCC 2026-10-06 18:13，用户两次追问的 48.35 MB
        /// `111\111\其余物\111.zip`）：某一单只出**过程物**（它解出来的入口包就是下一层的输入）、
        /// 自己因此停在中途 ⇒ **它自己的完整性永远判不出来**，而那一组后来由**另一个当场核过清单、
        /// 可证完整**的消费方解开 ⇒ 该按证据的是**消费方那一份**。</para>
        ///
        /// <para>⛔ 换的只是"证据从哪来"，**六道门槛一条都不绕过**；⛔ 这条证据只用于这一次删除，
        /// 不许写回任务自己的账（伪造记录是更坏的事）。</para>
        /// </param>
        /// <param name="allowedRootEvidence">
        /// **"本任务输出范围"这一道门槛的锚点**（默认 null = 用本任务自己的 `CollectedPath` / `OutputPath`）。
        ///
        /// <para>同上那一格：过路层那一单被收场时 `OutputPath` 已经被**显式清空**
        /// （它是跟班卷，没有自己的成品目录）⇒ 老写法会拒绝删除（"不在本任务的输出目录（）之内"）。
        /// 调用方用**唯一落点出口**现算一份它那条链的落点目录传进来即可
        /// —— 其余物正是建在那一层底下。</para>
        /// </param>
        public RestPurgeOutcome Purge(
            ArchiveTask? task,
            bool cancelled,
            DeleteMode mode = DeleteMode.Permanent,
            ResultCompletenessVerdict? completenessEvidence = null,
            string? allowedRootEvidence = null)
        {
            if (task == null)
            {
                return Skip("没有任务，未删除任何东西");
            }

            string name = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            if (cancelled)
            {
                return Skip($"{name}：已取消 —— 其余物一个字节都不删（失败 / 取消 / 校验不通过一律不动）");
            }

            if (task.Outcome != TaskOutcome.Succeeded)
            {
                return Skip(
                    $"{name}：任务的机器终态不是「完成」（当前：{task.Outcome}）—— " +
                    $"其余物一个字节都不删（部分完成 / 失败 / 取消 / 跳过一律不动）");
            }

            /*
             * ⚠ 判据只准看**事实**（用户 2026-09-24 要求）：这里读的是校验那一刻写下的枚举，
             * ⛔ 不再用 `task.Status == "解压成功"` 这种中文状态字符串 ——
             * 真机日志里出现过"状态写着解压成功、校验却已判否"的那一帧，
             * 万一那种任务走到这里，用户的源包就会被永久删掉。
             *
             * ⚠ 2026-09-30（检验等级 L4）：判据从"校验通过"**收紧**成"可证完整"——
             * 唯一出口 <see cref="ResultCompletenessClassifier"/>。老口径下"拿不到清单、
             * 只做了非空底线校验"也算通过（`Verified = true`），于是**没有任何证据**的这一档
             * 照样拿到了删源包的通行证；现在它落在"判不出"⇒ 一个字节都不删。
             */
            ResultCompletenessVerdict completeness = completenessEvidence
                ?? ResultCompletenessClassifier.Classify(task);

            if (!completeness.AllowsSourceRemoval)
            {
                return Skip(
                    $"{name}：{completeness.Message}（机器结论：{completeness.Evidence}），其余物一个字节都不删");
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return Skip($"{name}：没记下本次定稿实际用的其余物目录，不敢猜路径，一个字节都不删");
            }

            string directory = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Skip($"{name}：其余物目录不存在（{task.RestDirectoryPath}），没有可删除的内容");
            }

            if (!TryResolveAllowedRoot(task, directory, allowedRootEvidence, out string allowedRoot, out string why))
            {
                return Skip($"{name}：{why}，已拒绝删除");
            }

            if (!LooksLikeArtifactDirectory(directory))
            {
                return Skip(
                    $"{name}：{directory} 的形状不像「其余物」目录（既不是 {ProcessArtifactLayout.ArtifactDirectoryName}，"
                    + "上一级也不是），已拒绝删除");
            }

            if (!ArchivePathGuard.IsInsideRoot(allowedRoot, directory, out string outsideReason))
            {
                return Skip($"{name}：其余物目录不在本任务的输出范围之内，已拒绝删除 —— {outsideReason}");
            }

            /*
             * ===== 第六道门槛：其余物里的分卷**不许是"半套"**（用户 2026-09-30 真机，25 GB 被误删）=====
             *
             * 现场：内层包是一组 6 片跨盘 ZIP，末片当时没被认出来（ZIP64 收尾那两条闸门，见
             * `docs/真机事故复盘.md` §44.2）⇒ 末片被当**内容物**留在成品目录，同组 5 卷被当**过程物**
             * 收进其余物；上面五道门槛全都过得去，于是其余物被整份彻底删除 ——
             * 一组包被拆开、末片 1.62 GB 留下、另外 25 GB 永久消失，谁都再也解不开。
             *
             * 判据本体是公开纯函数 <see cref="Extraction.RestVolumeCompletenessGate"/>（单独可测）；
             * ⛔ 放在**这里**而不是各个调用点：其余物的删除只有这一个执行体，
             * 三处调用（链尾 / 任务收尾 / 链尾清扫）都要过它，放调用点等于漏两处。
             */
            string? splitGroupBlocker = Extraction.RestVolumeCompletenessGate.DescribeBlocker(directory);

            if (splitGroupBlocker != null)
            {
                return Skip($"{name}：{splitGroupBlocker}");
            }

            /*
             * ===== 「内容物保留关键词」：其余物里有命中项 ⇒ 整份不删（用户 2026-10-04 的新功能）=====
             *
             * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
             * 这一档删的是**整个其余物目录**（源包 + 过程物一起），而里面有命中项时两件事冲突
             * ⇒ 兜底落在"什么都不做"：**整份不删 + 一行点名**（用户要求"碰都不碰"）。
             *
             * ⚠ 唯一豁免：**这一单自己的源包**（<see cref="ArchiveTask.CurrentPath"/> /
             * <see cref="ArchiveTask.VolumePaths"/>）—— 用户划的界是"只管内容物"，
             * 别人给的 `小明.zip` 作为源包被搬进其余物时照常按设置处理。
             *
             * ⛔ 判据放在这里（执行体）而不是调用点：其余物的删除只有这一个类，三处调用都得过它。
             * ⛔ 空关键词列表 ⇒ 连扫都不扫（行为与加这条功能之前逐字相同）。
             */
            string? contentKeepBlocker = DescribeContentKeepBlocker(
                directory,
                EnumerateTaskSourcePaths(task));

            if (contentKeepBlocker != null)
            {
                return Skip($"{name}：{contentKeepBlocker}");
            }

            /*
             * ===== 空壳就地删掉，不进回收站（用户 2026-10-03 真机）=====
             *
             * 现场：用户用「删除操作 = 移入回收站」跑完一批之后，回收站里堆了非常多 1 KB 的同名
             * 「其余物」文件夹 —— 本方法把**整个 `其余物\` 目录当一个条目**送回收站，而每个任务
             * （递归时每层）各送一次，里面往往只剩空壳 / 极小中间件，**空目录也照送**。
             * 空目录没有"可还原"的价值，送回收站只是往用户的回收站里塞垃圾。
             *
             * 判据唯一出口 <see cref="IsFileFreeDirectoryTree"/>：**递归地一个文件都没有**
             * （只剩空目录结构，含嵌套空目录）。⛔ 排在上面六道门槛**全部通过之后** ——
             * 这一条只决定"怎么删"，绝不决定"能不能删"：越界 / 形状不像 / 半套分卷照旧 Skip、什么都不做。
             */
            if (IsFileFreeDirectoryTree(directory))
            {
                try
                {
                    // 判据已证明这一棵递归无文件，目录本身又过了六道门槛 ⇒ 这里只可能是空目录结构。
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    return Skip(
                        $"{name}：其余物里只剩下空壳（0 个文件），但就地删空目录失败（{ex.Message}）"
                        + " —— 没有送进回收站，盘上可能还剩一部分空目录");
                }

                string shellLine =
                    $"{name}：其余物目录里只剩下空壳（0 个文件），已就地删掉空目录，没有送进回收站";

                return new RestPurgeOutcome
                {
                    Attempted = true,
                    Succeeded = true,
                    Directory = directory,
                    FreedBytes = 0,
                    EntryCount = 0,
                    RemovedAsEmptyShell = true,
                    Message = shellLine,
                    LogLines = new[] { shellLine }
                };
            }

            // logSink 传 null：删除日志由调用方（协调器）原样写进界面与文件日志，
            // 不在这里另开一个落点（两处各写一份会让日志出现两个时间戳）。
            string reason = mode == DeleteMode.RecycleBin ? AutoRecycleReason : AutoPurgeReason;

            var service = new RecycleBinService(_executor, null, _probe);
            DeleteRequest request = new DeleteRequest(directory, reason);

            DeleteResult result;

            try
            {
                result = service.Delete(
                    new[] { request },
                    new DeleteOptions
                    {
                        AllowedRoot = allowedRoot,
                        UserConfirmed = true,
                        Mode = mode,
                        Reason = reason
                    });
            }
            catch (Exception ex)
            {
                return Skip($"{name}：删除其余物时出现意外错误（{ex.Message}），源包与过程物都还在");
            }

            var logLines = result.LogEntries.Select(entry => entry.ToDisplayText()).ToList();
            DeleteOutcome? outcome = result.Outcomes.FirstOrDefault();

            if (result.SuccessCount <= 0)
            {
                string failure = result.FailureReasons.Count > 0
                    ? string.Join("；", result.FailureReasons)
                    : result.Message;

                return new RestPurgeOutcome
                {
                    Attempted = true,
                    Succeeded = false,
                    Directory = directory,
                    Message = mode == DeleteMode.RecycleBin
                        ? $"{name}：其余物没能移入回收站（{failure}）—— 内容物不受影响，其余物仍在原处"
                        : $"{name}：其余物没能删掉（{failure}）—— 内容物不受影响，但空间没有回来",
                    LogLines = logLines
                };
            }

            return new RestPurgeOutcome
            {
                Attempted = true,
                Succeeded = true,
                Directory = directory,
                FreedBytes = result.FreedBytes,
                EntryCount = outcome?.EntryCount ?? 0,
                Message = mode == DeleteMode.RecycleBin
                    ? $"{name}：其余物已移入回收站 {directory}"
                      + $"（{outcome?.EntryCount ?? 0} 个条目 / {TaskSpaceEstimate.FormatSize(result.FreedBytes)}，可还原）"
                    : $"{name}：其余物已彻底删除 {directory}"
                      + $"（{outcome?.EntryCount ?? 0} 个条目 / 释放 {TaskSpaceEstimate.FormatSize(result.FreedBytes)}，不进回收站）",
                LogLines = logLines
            };
        }

        /// <summary>
        /// 允许删除的根：本任务的输出目录，或（开了结果归集时）归集之后的落点。
        /// **只认这两个**；都不包含目标目录就拒绝 —— 宁可少删，不可错删。
        /// </summary>
        private static bool TryResolveAllowedRoot(
            ArchiveTask task,
            string directory,
            string? allowedRootEvidence,
            out string allowedRoot,
            out string why)
        {
            allowedRoot = string.Empty;
            why = string.Empty;

            var candidates = new List<string>();

            /*
             * 调用方给的锚点（见 `Purge` 的 `allowedRootEvidence`）：只在过路层那一格用得上
             * —— 那一单被收场时 `OutputPath` 已被显式清空，自己的两个候选都是空的。
             */
            if (!string.IsNullOrWhiteSpace(allowedRootEvidence))
            {
                candidates.Add(SafePathHelper.GetFullPathSafe(allowedRootEvidence));
            }

            if (!string.IsNullOrWhiteSpace(task.CollectedPath))
            {
                candidates.Add(SafePathHelper.GetFullPathSafe(task.CollectedPath));
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                candidates.Add(SafePathHelper.GetFullPathSafe(task.OutputPath));
            }

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (ArchivePathGuard.IsInsideRoot(candidate, directory, out _))
                {
                    allowedRoot = candidate;
                    return true;
                }
            }

            why = $"其余物目录 {directory} 不在本任务的输出目录（{string.Join(" / ", candidates.Where(c => c.Length > 0))}）之内";
            return false;
        }

        /// <summary>
        /// 路径形状校验：最后一段是「其余物 / 过程物」，**或者**上一级是
        /// （共用输出根模式是 <c>&lt;根&gt;\其余物\&lt;包基名&gt;</c>，最后一段是包名）。
        /// </summary>
        internal static bool LooksLikeArtifactDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (ProcessArtifactLayout.IsArtifactDirectoryName(trimmed))
            {
                return true;
            }

            try
            {
                string? parent = Path.GetDirectoryName(trimmed);

                return !string.IsNullOrWhiteSpace(parent) &&
                       ProcessArtifactLayout.IsArtifactDirectoryName(parent);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// **空壳判据（唯一出口）**：这个目录**递归地一个文件都没有**（只剩空目录结构，含嵌套空目录）。
        ///
        /// <para><b>为什么要它</b>（用户 2026-10-03 真机）：删除操作 = 移入回收站跑完一批之后，回收站里堆了
        /// 非常多 1 KB 的同名「其余物」文件夹 —— <see cref="Purge"/> 把整个 `其余物\` 目录当一个条目送回收站、
        /// 每个任务（递归时每层）各送一次，而里面往往只剩空壳。空目录没有"可还原"的价值
        /// ⇒ 这一档改为**就地永久删掉、不进回收站**。</para>
        ///
        /// <para>两个执行体（<see cref="Purge"/> 与 <see cref="PurgeExcept"/>）都转调这一份，
        /// ⛔ 不许各写一遍（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。</para>
        ///
        /// <para><b>判据只读文件系统事实</b>：数"这一棵下面有没有文件"
        /// （⛔ 不比中文名、⛔ 不看时间戳、⛔ 不看大小）。见到第一个文件就返回 false，不必数到底。
        /// 目录不在 / 读不动（枚举中途抛异常）⇒ 一律 false = **判不出就当它不空** ——
        /// 于是 <c>Directory.Delete(recursive: true)</c> 只可能作用在**已被本判据证明递归无文件**的那个目录上。</para>
        ///
        /// <para>⚠ 目录联接点（junction）会被枚举跟着进去（实测 .NET 如此）：所以"链到别处一棵有文件的树"
        /// 会被正确判成不空；而链到一棵空树时本判据会说"是空壳"，随后的
        /// <c>Directory.Delete(recursive: true)</c> 会因为重解析点报"拒绝访问"抛出来
        /// —— 由调用点的 catch 兜住（实测：.NET 的递归删除**不跟着重解析点删**，链接目标不会被删）。</para>
        /// </summary>
        private static bool IsFileFreeDirectoryTree(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             SearchOption.AllDirectories))
                {
                    // 只认文件：`File.Exists` 对目录恒为 false（枚举里的目录一律放过）。
                    if (File.Exists(entry))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static RestPurgeOutcome Skip(string message)
        {
            return new RestPurgeOutcome
            {
                Attempted = false,
                Succeeded = false,
                Message = message
            };
        }
    }
}
