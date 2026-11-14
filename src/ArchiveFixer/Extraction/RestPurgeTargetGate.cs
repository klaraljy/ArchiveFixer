using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 其余物删除的**扫描/规划**阶段：把「这一次要删的那一份其余物」从任务事实上解析出来，
/// 并把只读盘、不动盘的那几条闸门一次跑完 —— 结论只有两种：**放行**（给出目录与允许根）
/// 或**拦下的那一句话**（调用方原样写进结论，一个字节都不删）。
///
/// <para><b>它不承载全部门槛</b>：机器终态 / 可证完整 / 未取消 / 删除计划对账仍留在
/// <see cref="RestItemPurger"/> 的公开入口里（那几条读的是任务事实，两档各有一套）；这里只放
/// 两档**共用**的那几条 —— 路径事实（定稿时记下的那一条 / 形状必须是其余物 / 允许根 / 自己在输出范围之内）、
/// 半套分卷（<see cref="RestVolumeCompletenessGate"/>）、内容物保留关键词（<see cref="ContentKeepRules"/>）。</para>
///
/// <para>⛔ 判据放在执行体这一侧（不是各个调用点）：其余物的删除只有 <see cref="RestItemPurger"/>
/// 一个执行体，三处调用（链尾 / 任务收尾 / 链尾清扫）都要过它 —— 放调用点等于漏两处。</para>
///
/// <para>⚠ 全部只读盘、不写盘；真正的删除一律在 <see cref="RestPurgeDeleter"/>。</para>
/// </summary>
internal sealed class RestPurgeTargetGate
{
    /// <summary>
    /// 点名时最多写几个名字（多出来折成"还有 K 个"）—— 与批末诊断的"每组最多 3 个名字"同一口径。
    /// </summary>
    private const int MaxContentKeepNames = 3;

    private readonly ContentKeepRules _keepRules;

    internal RestPurgeTargetGate(ContentKeepRules keepRules)
    {
        _keepRules = keepRules;
    }

    /// <summary>
    /// 任务在结论里的称呼：文件名，没有就用当前路径的最后一段（两档原先各写一遍，这里收成唯一实现）。
    /// </summary>
    internal static string ResolveName(ArchiveTask? task)
    {
        if (task == null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(task.FileName)
            ? Path.GetFileName(task.CurrentPath) ?? string.Empty
            : task.FileName;
    }

    /// <summary>
    /// 解析并校验「这一次要删的其余物」，随后过两条读取型闸门（顺序与原先逐字相同）：
    /// 路径事实 → 半套分卷 → 内容物保留关键词。
    /// </summary>
    /// <param name="task">目标任务（非 null）。</param>
    /// <param name="allowedRootEvidence">
    /// **"本任务输出范围"这一道门槛的锚点**（默认 null = 用本任务自己的 `CollectedPath` / `OutputPath`）。
    ///
    /// <para>过路层那一单被收场时 `OutputPath` 已经被**显式清空**
    /// （它是跟班卷，没有自己的成品目录）⇒ 老写法会拒绝删除（"不在本任务的输出目录（）之内"）。
    /// 调用方用**唯一落点出口**现算一份它那条链的落点目录传进来即可
    /// —— 其余物正是建在那一层底下。</para>
    /// </param>
    /// <param name="directory">其余物目录的完整路径（被拦下时是已解析到的那个值）。</param>
    /// <param name="allowedRoot">删除安全前置用的允许根（被拦下时可能为空）。</param>
    /// <returns>null = 两条闸门全过；否则是**拦下的那句话**（已经带上任务名，调用方原样写进结论）。</returns>
    internal string? DescribeBlocker(
        ArchiveTask task,
        string? allowedRootEvidence,
        out string directory,
        out string allowedRoot)
    {
        string? targetError = ValidateTarget(task, allowedRootEvidence, out directory, out allowedRoot);

        if (targetError != null)
        {
            return targetError;
        }

        string name = ResolveName(task);

        /*
         * ===== 第六道门槛：其余物里的分卷**不许是"半套"**（用户 2026-09-30 真机，25 GB 被误删）=====
         *
         * 现场：内层包是一组 6 片跨盘 ZIP，末片当时没被认出来（ZIP64 收尾那两条闸门，见
         * `docs/真机事故复盘.md` §44.2）⇒ 末片被当**内容物**留在成品目录，同组 5 卷被当**过程物**
         * 收进其余物；上面五道门槛全都过得去，于是其余物被整份彻底删除 ——
         * 一组包被拆开、末片 1.62 GB 留下、另外 25 GB 永久消失，谁都再也解不开。
         *
         * 判据本体是公开纯函数 <see cref="RestVolumeCompletenessGate"/>（单独可测）；
         * ⛔ 放在**这里**而不是各个调用点：其余物的删除只有这一个执行体，
         * 三处调用（链尾 / 任务收尾 / 链尾清扫）都要过它，放调用点等于漏两处。
         */
        string? splitGroupBlocker = RestVolumeCompletenessGate.DescribeBlocker(directory);

        if (splitGroupBlocker != null)
        {
            return $"{name}：{splitGroupBlocker}";
        }

        /*
         * ===== 「内容物保留关键词」：其余物里有命中项 ⇒ 整份不删（用户 2026-10-04 的新功能）=====
         *
         * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
         * 其余物里躺着的是**过程物 / 内容物**（内层包、抠出来的内嵌归档副本）—— 命中关键词的那些
         * 一个字节都不许删；而这一档删的是**整个目录**，两件事冲突 ⇒ 兜底落在"什么都不做"：
         * **整份不删 + 一行点名**（用户要求"碰都不碰"）。
         *
         * ⚠ 唯一豁免：**这一单自己的源包**（<see cref="ArchiveTask.CurrentPath"/> /
         * <see cref="ArchiveTask.VolumePaths"/>）。用户明确划的界是"只管内容物" ——
         * 别人给的 `小明.zip` 作为源包被搬进其余物时照常按设置处理。
         * 判据读的是任务上的**事实路径**（搬完之后管线会把它们改成其余物里的新位置），⛔ 不猜名字。
         */
        string? contentKeepBlocker = DescribeContentKeepBlocker(
            directory,
            EnumerateTaskSourcePaths(task));

        return contentKeepBlocker == null ? null : $"{name}：{contentKeepBlocker}";
    }

    /// <summary>
    /// 半份清理（部分完成那一档）**要删哪些项**：按 <paramref name="keepPaths"/> 排除之后，
    /// 其余物顶层的每一项都是过程物。
    ///
    /// <para><b>门槛（判据另立两条）</b>：</para>
    /// <list type="number">
    /// <item><description>**要保留的东西里至少有一个此刻真的在盘上** —— 调用方传进来的判据是
    /// "这条链的最外层源包"。它不在 ⇒ 判不出 ⇒ **一个字节都不删**（源包是唯一能重建整条链的东西，
    /// 它都没了还清过程物，等于把最后一份可恢复路径也删掉）；</description></item>
    /// <item><description>要保留的那一项**在其余物里面**时按项保留（不会被当成"别的项"顺手删掉）。</description></item>
    /// </list>
    /// </summary>
    /// <param name="name">任务称呼（日志点名用）。</param>
    /// <param name="directory">已由 <see cref="DescribeBlocker"/> 放行的其余物目录。</param>
    /// <param name="keepPaths">要保留的路径（调用方给的"这条链的最外层源包"）。</param>
    /// <param name="keep">盘上确实存在、处理后要保留的那些项（完整路径、已去掉尾部斜杠）。</param>
    /// <param name="targets">排除保留项之后，其余物顶层要删的那些项。</param>
    /// <returns>null = 有东西可删；否则是拦下的那句话。</returns>
    internal static string? DescribeKeptItemsBlocker(
        string name,
        string directory,
        IReadOnlyList<string>? keepPaths,
        out List<string> keep,
        out List<string> targets)
    {
        keep = new List<string>();
        targets = new List<string>();

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
            return $"{name}：要保留的东西（这条链的最外层源包）一个都不在盘上 —— 判不出 ⇒ 其余物一个字节都不删";
        }

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
            AppDiagnostics.Problem("其余物", "删除或处理", ex);
            return $"{name}：读不了其余物目录（{ex.Message}），一个字节都不删";
        }

        if (targets.Count == 0)
        {
            return $"{name}：其余物里除了要保留的以外没有别的项，没有可删除的内容";
        }

        return null;
    }

    /// <summary>
    /// 删除计划是不是**本任务那一份其余物**：目标恰好一个、任务记下了其余物、两条路径是同一份。
    /// 计划不能被别的任务借用 —— 对不上就一个字节都不删。
    /// </summary>
    internal static bool PlanTargetsRestDirectory(ArchiveTask task, DeletionPlan plan)
    {
        return plan.Targets.Count == 1
               && !string.IsNullOrWhiteSpace(task.RestDirectoryPath)
               && string.Equals(
                   SafePathHelper.GetFullPathSafe(plan.Targets[0]),
                   SafePathHelper.GetFullPathSafe(task.RestDirectoryPath),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 「内容物保留关键词」这一档的**唯一判据**（用户 2026-10-04 的新功能，执行体里判）：
    /// 其余物这棵树里**有没有**名字命中关键词的文件 —— 有 ⇒ 返回一句话（点名哪几个 + 命中哪个词），
    /// 调用方**整份不删**；没有 ⇒ <c>null</c>（照旧按档删）。
    ///
    /// <para><b>三条口径</b>：① 空关键词列表也要扫默认内容容器（`.docx` / `.jar` 等）；
    /// ② <paramref name="exemptPaths"/> 里的是**这一单自己的源包**（用户划的界：
    /// 只管内容物，源包照常处理）；③ **扫不动（权限 / 目录读不了）⇒ 当作"有命中项"拦下**
    /// —— 兜底永远落在"什么都不做"那一档（AGENTS.md §9.5）。</para>
    /// </summary>
    private string? DescribeContentKeepBlocker(string directory, IReadOnlyList<string> exemptPaths)
    {
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
        string? hitContainer = null;

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
                bool protectedContainer = ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(file);

                if (keyword == null && !protectedContainer)
                {
                    continue;
                }

                hitKeyword ??= keyword;
                hitContainer ??= protectedContainer
                    ? ArchiveFixer.Security.ContentContainerProtection.Describe(file)
                    : null;
                hits.Add(file);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("其余物", "删除或处理", ex);
            return $"其余物里有「内容物保留关键词」这一档，但这棵树扫不动（{ex.Message}）"
                   + " —— 判不出里面有没有命中项，所以一个字节都不删（宁可不动，也不误删）";
        }

        if (hits.Count == 0)
        {
            return null;
        }

        string reason = hitKeyword != null
            ? $"「内容物保留关键词」（{hitKeyword}）"
            : $"内容容器保护（{hitContainer}）";

        return $"其余物里有 {hits.Count} 个文件命中{reason}："
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
    /// 解析并校验「要删的其余物目录」。只判路径事实，不判断该不该删。
    /// </summary>
    private static string? ValidateTarget(
        ArchiveTask task,
        string? allowedRootEvidence,
        out string directory,
        out string allowedRoot)
    {
        directory = string.Empty;
        allowedRoot = string.Empty;

        string name = ResolveName(task);

        if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            return $"{name}：没记下本次定稿实际用的其余物目录，不敢猜路径，一个字节都不删";
        }

        directory = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return $"{name}：其余物目录不存在（{task.RestDirectoryPath}），没有可删除的内容";
        }

        if (!TryResolveAllowedRoot(task, directory, allowedRootEvidence, out allowedRoot, out string why))
        {
            return $"{name}：{why}，已拒绝删除";
        }

        if (!LooksLikeArtifactDirectory(directory))
        {
            return $"{name}：{directory} 的形状不像「其余物」目录（既不是 {ProcessArtifactLayout.ArtifactDirectoryName}，"
                + "上一级也不是），已拒绝删除";
        }

        if (!ArchivePathGuard.IsInsideRoot(allowedRoot, directory, out string outsideReason))
        {
            return $"{name}：其余物目录不在本任务的输出范围之内，已拒绝删除 —— {outsideReason}";
        }

        return null;
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
         * 调用方给的锚点（见 `RestItemPurger.Purge` 的 `allowedRootEvidence`）：只在过路层那一格用得上
         * —— 那一单被收场时 `OutputPath` 已被显式清空，自己的两个候选都是空的。
         */
        if (!string.IsNullOrWhiteSpace(allowedRootEvidence))
        {
            candidates.Add(SafePathHelper.GetFullPathSafe(allowedRootEvidence));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(task.CollectedPath))
            {
                AddAllowedRootCandidate(candidates, SafePathHelper.GetFullPathSafe(task.CollectedPath));
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                AddAllowedRootCandidate(candidates, SafePathHelper.GetFullPathSafe(task.OutputPath));
            }
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
    /// 允许根本身 + 它的**父目录**各算一次：其余物现在和内容物文件夹同级，
    /// 落在输出目录的父目录下（`out\包名` 的其余物在 `out\其余物`），
    /// 只拿输出目录自己当允许根会把合法目标判成越界。
    /// </summary>
    private static void AddAllowedRootCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        candidates.Add(path);

        string? parent = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidates.Add(parent);
        }
    }

    /// <summary>
    /// 路径形状校验：最后一段是「其余物 / 过程物」，**或者**上一级是
    /// （共用输出根模式是 <c>&lt;根&gt;\其余物\&lt;包基名&gt;</c>，最后一段是包名）。
    ///
    /// <para>入口保留在 <see cref="RestItemPurger.LooksLikeArtifactDirectory"/>（薄转发），
    /// 判据本体只此一处。</para>
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
}
