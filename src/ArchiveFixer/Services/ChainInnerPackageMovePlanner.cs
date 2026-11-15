using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 链尾「把哪些内层包收进其余物、收到哪个名字」的**纯计划**（生命周期：归集之前；失败模型：纯函数，永不失败）。
///
/// <para>计划只读盘（存在性 / 分卷组清单 / 名字线索），<b>一个字节都不动</b>：搬运在
/// <see cref="ChainRestArtifactCollector"/>，删在 <see cref="RestPurgeStep"/>。抽成独立类的理由与用例可钉：
/// 真机 2026-10-01 那一类缺陷全在"名字怎么算"上，而算名字这一步以前埋在匿名体里谁都测不到。</para>
///
/// <para>⛔ 三条红线只在这里判一次：① 分卷组**整组一起搬**（搬一半等于拆包）；② 命中
/// 「内容物保留关键词」/ 默认内容容器 ⇒ 碰都不碰；③ 目标撞名自动让位（连本计划里前面那几条一起算），绝不覆盖。</para>
/// </summary>
internal static class ChainInnerPackageMovePlanner
{
    /// <summary>
    /// 一条续解链里"该收进其余物的内层包"的搬运计划（**纯计划：一个字节都不动**）。
    ///
    /// <para>抽成静态方法是为了能被用例直接钉住（真机 2026-10-01 那一类缺陷全在"名字怎么算"上，
    /// 而算名字这一步以前埋在 <c>Task.Run</c> 的匿名体里，谁都测不到）。</para>
    ///
    /// <para><b>⛔ 撞名让位必须把"本计划里前面那几条"也算进去</b>（<c>reserved</c>）：
    /// 只问文件系统的话，**两条同名内层包会算出同一个目标** —— 第一条搬成功、第二条撞上
    /// <c>already exists</c> 而**留在原地**。用户 2026-10-01 真机 BBBB / CCCC 两批的现场就是它：
    /// <c>…\111\111\111\111.part1.rar</c> 与 <c>…\222\222\222\222.zip</c> 卡在内容物里没被收走，
    /// 日志写着「内层包没能移入其余物：… The file '…\其余物\111.part1(1).rar' already exists.（它留在原地）」。
    /// 口径与源包搬运（<c>SourcePackageMover.Plan</c>）**逐字相同**：reserved + <c>MakeUniqueTarget</c>。</para>
    /// </summary>
    /// <param name="rootTask">这一条链的根任务（其余物挂在它身上）。</param>
    /// <param name="chainTasks">整条链的任务（含根任务自己，会被跳过）。</param>
    /// <param name="restDirectory">其余物目录（定稿时算好的那一个，⛔ 不现场重算）。</param>
    /// <param name="outputRoot">根任务的输出根（只搬这个范围里的内层包）。</param>
    /// <param name="keepRules">
    /// 「内容物保留关键词」判据（用户 2026-10-04 的新功能；唯一出口 <see cref="ContentKeepRules"/>）。
    /// 命中它的内层包**不搬进其余物**（"碰都不碰"），整组一起留；空判据 ⇒ 一个都不拦（老行为）。
    /// 传 <c>null</c> 等价于 <see cref="ContentKeepRules.Empty"/>。
    /// </param>
    internal static List<(string From, string To)> Plan(
        ArchiveTask? rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        string? restDirectory,
        string? outputRoot,
        ContentKeepRules? keepRules,
        out List<string> warnings)
    {
        var planned = new List<(string, string)>();
        warnings = new List<string>();

        ContentKeepRules rules = keepRules ?? ContentKeepRules.Empty;

        if (rootTask == null ||
            chainTasks == null ||
            chainTasks.Count == 0 ||
            string.IsNullOrWhiteSpace(restDirectory))
        {
            return planned;
        }

        string artifactRoot = restDirectory!;

        // 本计划**已经排出去的名字**（见方法注释：不问它就必然撞名）。
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /*
         * ⛔ 谁的"过程物"要收（**2026-11-01 用户新口径**）：
         *
         * <para>「未解开的内层包，放入其余物里面吧，它就相当于失败的时候的最后一层过程物，
         * 放在其余物里面看看用户的选择删或者是不删」。</para>
         *
         * <para>⇒ 链上**所有**续解内层包（解开过的 / 没解开过的 / 失败的）都收进其余物；
         * 解开过的成功链由逐层回收直接删（不会留到这里），留在盘上的通常是失败/部分完成/未展开，
         * 由链尾「删除操作」按用户选择处理。⛔ 唯一例外仍是「内容物保留关键词」：碰都不碰。</para>
         */
        foreach (ArchiveTask? candidate in chainTasks)
        {
            if (candidate == null ||
                ReferenceEquals(candidate, rootTask) ||
                !candidate.IsContinuationTask)
            {
                continue;
            }

            string path = candidate.CurrentPath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            /*
             * ⛔ 分卷组必须**整组一起搬**（2026-09-28 真机：内层包是 4 卷分卷组时只搬走了
             * `amb.7z.001` 并把它彻底删掉，`.002/.003/.004` 留在成品目录里 —— 用户原话
             * "同样犯了 winrar 会犯的问题：只删除 .001 为首的分卷头文件，其他分卷还残留着"）。
             * 搬一半等于把一套完整的包拆成废件，比不搬糟得多。
             *
             * 判据与"源包整组一起移"**同一套**：分卷组取 VolumePaths 全卷，单文件取它自己。
             */
            var groupPaths = new List<string>();

            if (candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0)
            {
                foreach (string volumePath in candidate.VolumePaths)
                {
                    if (!string.IsNullOrWhiteSpace(volumePath) && File.Exists(volumePath))
                    {
                        groupPaths.Add(volumePath);
                    }
                }
            }

            if (groupPaths.Count == 0)
            {
                groupPaths.Add(path);
            }

            /*
             * 兜底（**宁可不动，也不搬一半**）：分组信息没拿到（IsVolumeGroup=false），
             * 但名字明显是分卷、同目录里还躺着同组的别的卷 —— 这一卷不搬，写清为什么。
             * 留下的是一套完整的包，用户还能自己接着处理。
             */
            if (!candidate.IsVolumeGroup && VolumeSiblingProbe.HasSiblingVolumeBeside(path))
            {
                // 日志由调用方写（纯计划里不写盘、不记日志 —— 这样它才测得动）。
                warnings.Add(
                    $"{rootTask.LogName}：{Path.GetFileName(path)} 看着是分卷组的一卷，"
                    + "但没拿到整组清单 —— 这次不搬它（搬一半会把一套包拆成废件）。");
                continue;
            }

            /*
             * ===== 「内容物保留关键词」：命中就**整组都不搬**（用户 2026-10-04 的新功能）=====
             *
             * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
             * 链尾把内层包收进其余物，是为了让「删除操作」那一档把它们清掉；命中关键词的既然
             * "碰都不碰"，那就**一步都不动**：不搬、不改名、后面的删除也删不到它（它不在其余物里）。
             *
             * ⛔ 判据唯一出口 <see cref="ContentKeepRules"/>（关键词 + 默认内容容器保护）。
             * ⛔ 分卷组**整组一起判**：一卷命中就整组留着，绝不只留一半。
             * 空关键词列表仍要拦默认内容容器（.docx / .jar 等）。
             */
            string? keepKeyword = groupPaths
                .Select(groupPath => rules.FindMatch(groupPath))
                .FirstOrDefault(keyword => keyword != null);
            string? protectedContainer = groupPaths
                .FirstOrDefault(ArchiveFixer.Security.ContentContainerProtection.ShouldProtect);

            if (keepKeyword != null || protectedContainer != null)
            {
                string reason = keepKeyword != null
                    ? $"命中「内容物保留关键词」（{keepKeyword}）"
                    : $"是内容容器（{ArchiveFixer.Security.ContentContainerProtection.Describe(protectedContainer)}）";

                warnings.Add(
                    $"{rootTask.LogName}：{Path.GetFileName(path)} {reason}"
                    + " —— 默认当内容物保留，这次不搬进其余物（它留在原处，也不参与后面的删除）；"
                    + "要拆请单独选中它用「只解压」。");
                continue;
            }

            foreach (string groupPath in groupPaths)
            {
                // 已经在其余物里了（重复调用 / 上一轮搬过）：跳过。
                if (SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(groupPath) ?? string.Empty)
                        .EndsWith(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(outputRoot) &&
                    !ArchivePathGuard.IsInsideRoot(outputRoot, groupPath, out _))
                {
                    // 不在根任务的输出范围内（理论上不该发生）：不动它。
                    continue;
                }

                /*
                 * 撞名（同一条链里两个同名内层包）自动让位，绝不覆盖 ——
                 * **本计划里已经排出去的名字也算撞名**（reserved，见方法注释）。
                 */
                string target = Path.Combine(
                    artifactRoot,
                    FileNameHelper.SanitizeFileName(Path.GetFileName(groupPath)));

                if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
                {
                    target = ProcessArtifactLayout.MakeUniqueTarget(
                        target,
                        isDirectory: false,
                        reserved,
                        FileSystemArtifactTargetProbe.Instance);
                }

                reserved.Add(target);
                planned.Add((groupPath, target));
            }
        }

        return planned;
    }
}
