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
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteFileSystemProbe _probe;

        public RestItemPurger(IDeleteExecutor? executor = null, IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
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

            if (!TryResolveAllowedRoot(task, directory, out string allowedRoot, out string why))
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
        public RestPurgeOutcome Purge(ArchiveTask? task, bool cancelled, DeleteMode mode = DeleteMode.Permanent)
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
            ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(task);

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

            if (!TryResolveAllowedRoot(task, directory, out string allowedRoot, out string why))
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
            out string allowedRoot,
            out string why)
        {
            allowedRoot = string.Empty;
            why = string.Empty;

            var candidates = new List<string>();

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
