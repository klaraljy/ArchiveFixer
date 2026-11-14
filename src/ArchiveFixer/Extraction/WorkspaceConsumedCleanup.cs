using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **已消费过程物的清理**：把"已经被解开、位置让给同名目录"的那个内层包从产物里删掉；
/// 它要是分卷组的一卷，同一组一起判、一起删。
///
/// <para>这是不可逆操作，兜底一律落在"什么都不做"那一档：
/// ⛔ 只在它能被证明位于发布目标之下、而且确实是我们这一步搬进来的那一份时才删；
/// ⛔ 分卷组判不出"整组都在我们能证明的范围里"时，**连被消费的那一份都不删** ——
/// 只删一半就是半套卷，定稿侧的分卷完整性闸门会让整份计划作废。</para>
/// </summary>
internal static class WorkspaceConsumedCleanup
{
    /// <summary>
    /// 把**已经被解开**的那个包从产物里删掉（它的位置让给同名目录）；
    /// 它要是分卷组的一卷，**同一组的其余卷一起删**（见 <see cref="TryDeleteConsumedVolumeGroup"/>）。
    ///
    /// <para>
    /// 只在它确实位于发布目标之下时才删（越界一律不碰，只记一条）—— 这是不可逆操作，
    /// 兜底必须落在"什么都不做"那一档。删不掉也不影响已经落位的内容物，只记进 Message。
    /// </para>
    ///
    /// <para><b>2026-10-03 加的那道闸门</b>：删之前先按事实回答"**这一份确实是我们这一步搬进来的那一份**吗"
    /// （<paramref name="movedFromTo"/>：来源全路径 → 落点全路径）。
    /// 老写法只问 <c>File.Exists</c> —— 而落点是**算出来的**（父层落点 + 相对路径），
    /// 撞名时我们真搬的那一份会被改名成 `名字(1).ext`，算出来的那个位置上站的可能是
    /// **本来就在那里的同名残留**（上一次留下的、或别的工序放进去的）⇒ 老写法会把**不是我们的东西**删掉。
    /// 现在：算出来的那个位置上的同名残留**一个字节都不动**（只写一条 WARN 说清），
    /// 真正被消费的那一份按事实在**它自己的落点**上清掉。</para>
    ///
    /// <para>它要是分卷组的一卷，整组一起判、一起删（见
    /// <see cref="TryDeleteConsumedVolumeGroup"/>）—— 判不出就**连被消费的那一份都不删**：
    /// 只删得掉一部分，留下的就是**半套卷**，而定稿侧的分卷完整性闸门会因此把整份计划作废。</para>
    /// </summary>
    internal static void TryDeleteConsumedPackage(
        string publishedArchivePath,
        string consumedSourcePath,
        Dictionary<string, string> movedFromTo,
        string artifactRoot,
        List<string> errors,
        List<string> warnings,
        IReadOnlyCollection<string>? alreadyReclaimedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(publishedArchivePath) ||
            string.IsNullOrWhiteSpace(consumedSourcePath))
        {
            return;
        }

        try
        {
            string computed = SafePathHelper.GetFullPathSafe(publishedArchivePath);

            if (Directory.Exists(computed))
            {
                // 已经被同名目录占着：说明这一层的东西早就落在那儿了，什么都不用做。
                return;
            }

            /*
             * ⛔ 事实：这一份到底搬到哪去了？查不到 = 这一步没把它搬进产物 ——
             * 但**"查不到"有两种完全不同的原因**，说话必须分开（用户 2026-10-05）：
             *
             * ① **它已经被"逐层回收"当场删掉了**（`alreadyReclaimedPaths` 里点名的那一份）——
             *    那是这一档**要的**结果（AGENTS.md §11.3：每一层当场回收这一层的过程物）。
             *    照旧写"判不出哪一份才是我们自己搬进来的那一份"就是一句假话，
             *    用户会当成一次失败的搬运去找原因。
             * ② 别的（撞名 / 占用 / 移动失败）⇒ 维持原话，如实点名（那才是"判不出"）。
             */
            bool reclaimedOnPurpose =
                SafeFileWasReclaimed(alreadyReclaimedPaths, consumedSourcePath) &&
                !File.Exists(consumedSourcePath);

            if (!movedFromTo.TryGetValue(
                    SafePathHelper.GetFullPathSafe(consumedSourcePath),
                    out string? actual))
            {
                if (!reclaimedOnPurpose)
                {
                    warnings.Add(
                        $"{Path.GetFileName(consumedSourcePath)}（它已经被解开，但这一步没能把它搬进产物"
                        + "（撞名、占用或移动失败）—— 判不出哪一份才是我们自己搬进来的那一份，"
                        + "所以这一份连同同组的分卷一个字节都不删；删除不可恢复）");
                }

                return;
            }

            actual = SafePathHelper.GetFullPathSafe(actual);

            if (!string.Equals(actual, computed, StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⛔ 算出来的那个位置上站的是**别人的同名文件**（我们那一份因为撞名被改成了别的名字）。
                 * 一个字节都不动它 —— 老写法在这里会把用户/别的工序留下的那份删掉（不可逆）。
                 */
                warnings.Add(
                    $"{Path.GetFileName(computed)}（这个位置上的同名文件「不是我们这一步搬进来的那一份」"
                    + $"（我们那一份落在「{Path.GetFileName(actual)}」）—— 它一个字节都不动；"
                    + "这一档只清我们自己搬进来的那一份）");
            }

            if (!TryDeleteConsumedVolumeGroup(
                    actual,
                    consumedSourcePath,
                    computed,
                    movedFromTo,
                    artifactRoot,
                    errors,
                    warnings))
            {
                return;   // 闸门拦下了：整组一个都不删（含被消费的那一份）
            }

            if (File.Exists(actual))
            {
                FileOperations.Delete(actual);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("工作区", "降级处理", ex);
            errors.Add($"{Path.GetFileName(publishedArchivePath)}（已解开，但原文件没能删掉：{ex.Message}）");
        }
    }

    /// <summary>
    /// 这一份是不是**逐层回收按口径当场删掉的那一份**（判据 = 调用方给的清单 + 盘上事实）。
    ///
    /// <para>⛔ 两个条件缺一不可：清单里点名了它，**而且它确实已经不在盘上**。
    /// 只信清单会把"删失败（占用 / 只读）"也读成"已回收"；只信"不在盘上"会把用户自己删掉的
    /// 也算进来。两个一起看才是事实（AGENTS.md §9.5：要删 / 要写盘的动作判据只准读事实）。</para>
    /// </summary>
    private static bool SafeFileWasReclaimed(IReadOnlyCollection<string>? reclaimedPaths, string path)
    {
        if (reclaimedPaths == null || reclaimedPaths.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (string reclaimed in reclaimedPaths)
        {
            if (string.Equals(reclaimed, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 被解开的分卷组是**整组**一起被消费掉的：只删掉"这一层要解的那一卷"，同组的后续卷会缺了首卷
    /// 留在产物里 —— 而定稿那一侧的分卷完整性闸门
    /// （<c>ExtractionCoordinator.TryConfirmVolumeGroupComplete</c>：<c>.002</c> 必须能找到同目录的
    /// <c>.001</c>）会判定"整组不完整" ⇒ **整份计划作废、任务报失败**（2026-09-30 那 25 GB 踩的
    /// 正是同一道闸门）。这一组卷刚刚被我们成功解开、内容物也已经落位，所以它们**整个**是过程物，
    /// 一起清掉才不会留下半套。
    ///
    /// <para>
    /// 判据全部只读盘上的事实：组的成员从**父层产物那一层**（<paramref name="consumedSourcePath"/> 所在目录）
    /// 按"同目录 + 同族 + 同基名"收（判据唯一出口 <see cref="LegacyVolumeNames.BelongsToSameGroup"/>，
    /// ⛔ 不另写一套名字规则）；别的文件（真内容物、别的组）一个都不碰。
    /// </para>
    ///
    /// <para><b>2026-10-03 加的两道闸门（"判不出就不删"，整组一起判）</b>：老写法只看"同目录 + 同族 + 同基名"
    /// 就无条件连坐，于是同组还有一片**在别处**（别的目录）或**名字认不出**（`mid.z删除ip` 那种被改坏的）
    /// 时，删掉手上这几片就留下**半套**（§44.2 那 25 GB 正是这个形状）。现在：</para>
    /// <list type="number">
    /// <item><description><b>闸门 A</b>：整组的每一份都必须能查到**我们这一步把它搬到了哪**
    /// （<paramref name="movedFromTo"/>）—— 查不到就说明它不在我们能证明的范围里；</description></item>
    /// <item><description><b>闸门 B</b>：这一棵树里**不许还有同基名的归档件留在别处** ——
    /// 判据转调唯一出口 <see cref="RestVolumeCompletenessGate.DescribeBlockerForCandidates"/>
    /// （⛔ 不另写一套分类；它连"名字被改坏的那一片"也认得出同基名）。</description></item>
    /// </list>
    /// <para>两道闸门任意一道不过 ⇒ **这一组一个字节都不删**（**含被消费的那一份** —— 只删一半就是半套）
    /// + 一条 WARN 点名是哪一片不在我们能证明的范围里（宁可留一整套，也不许留半套）。</para>
    /// </summary>
    /// <param name="consumedPublishedPath">我们这一步**真搬进来的**那一份被消费的包的落点（事实）。</param>
    /// <param name="consumedSourcePath">它在父层产物里的位置（组就是从这一层收出来的）。</param>
    /// <param name="computedPublishedPath">
    /// 按相对路径**算出来的**落点。它只用于一件事：撞名时那份**不是我们的**同名残留要被闸门 B 排除 ——
    /// 它站在"我们这一份本该落的位置"上，是撞名的产物，不是"同组另一片落在别处"
    /// （用户 2026-10-03 批准的用例 ①：那份残留不动，我们自己的那一份照旧清掉）。
    /// </param>
    /// <returns>true = 闸门全过（调用方可以删整组）；false = 拦下了（一个都不许删）。</returns>
    private static bool TryDeleteConsumedVolumeGroup(
        string consumedPublishedPath,
        string consumedSourcePath,
        string computedPublishedPath,
        Dictionary<string, string> movedFromTo,
        string artifactRoot,
        List<string> errors,
        List<string> warnings)
    {
        string name = Path.GetFileName(consumedPublishedPath);

        if (name.Length == 0 || !LegacyVolumeNames.IsVolumePart(name))
        {
            // 不是分卷组：它自己删掉就是整组删掉（⛔ 绝不按"名字像"去连坐别的文件）。
            return true;
        }

        string sourceName = Path.GetFileName(consumedSourcePath);
        string consumedSourceFull = SafePathHelper.GetFullPathSafe(consumedSourcePath);
        string sourceDirectory = Path.GetDirectoryName(consumedSourceFull) ?? string.Empty;
        string publishedDirectory = Path.GetDirectoryName(consumedPublishedPath) ?? string.Empty;

        if (sourceDirectory.Length == 0 || publishedDirectory.Length == 0)
        {
            warnings.Add(
                $"{name}（它已经被解开，但拿不到这一组所在的目录 —— 判不出整组是不是都在我们自己搬进来的范围里，"
                + "所以这一组一个字节都不删；删除不可恢复）");

            return false;
        }

        /*
         * 组的成员从**事实**里收，不从目录里猜：
         * ① 我们这一步真搬过的那些（`movedFromTo` 的键）里，跟被消费的那一份**同目录 + 同族 + 同基名**的；
         * ② 父层产物那一层**还在盘上**的话，把它里面同组的也收进来 —— 那些"没跟着一起搬过来"的
         *    就会在下面那道闸门里被点名（这正是"同组有一片不在我们能证明的范围里"那一档）。
         *    ⚠ 多数情况下那一层已经被父层的搬运清空并删掉了（`MoveContent` 末尾那一步），
         *    所以这里必须容忍"目录不在了"——那不是异常，是正常收尾。
         */
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { consumedSourceFull };

        foreach (string source in movedFromTo.Keys)
        {
            if (IsSameGroupMemberInSameDirectory(source, consumedSourceFull, sourceName))
            {
                sources.Add(source);
            }
        }

        if (WorkspacePathRules.SafeDirectoryExists(sourceDirectory))
        {
            string[] sourceFiles;

            try
            {
                sourceFiles = Directory.GetFiles(sourceDirectory);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Problem("工作区", "降级处理", ex);
                errors.Add($"{name}（同组分卷没能一起清掉：读不动父层产物那一层 {ex.Message}）");
                return false;
            }

            foreach (string file in sourceFiles)
            {
                if (IsSameGroupMemberInSameDirectory(file, consumedSourceFull, sourceName))
                {
                    sources.Add(SafePathHelper.GetFullPathSafe(file));
                }
            }
        }

        // 闸门 A：整组的每一份都要查得到"我们把它搬到哪了"。
        var actualPaths = new List<string>();
        var computedPaths = new List<string>();

        foreach (string source in sources)
        {
            computedPaths.Add(SafePathHelper.GetFullPathSafe(
                SafePathHelper.Combine(publishedDirectory, Path.GetFileName(source))));

            if (!movedFromTo.TryGetValue(source, out string? actual))
            {
                warnings.Add(
                    $"{name}（它已经被解开，但同组的「{Path.GetFileName(source)}」不在我们这一步搬进来的范围里"
                    + "—— 它可能在别的目录里、也可能根本没跟着这一份一起搬过来，"
                    + "所以这一组一个字节都不删；删除不可恢复，宁可留一整套也不许留半套）");

                return false;
            }

            actualPaths.Add(SafePathHelper.GetFullPathSafe(actual));
        }

        // 闸门 B：这一棵树里不许还有同基名的归档件留在别处（含名字被改坏的那一片）。
        string? splitBlocker = RestVolumeCompletenessGate.DescribeBlockerForCandidates(
            actualPaths,
            artifactRoot,
            candidatePrefix: "就地替换要删的",
            blockerTail: "同组还有一片留在这一棵树里（判不出它是不是我们这一步搬进来的那一份），"
                + "所以这一组一个字节都不删（删除不可恢复；宁可留下整套，也不许留半套）",
            requireRecognizedCandidates: true,
            excludedPaths: computedPaths);

        if (splitBlocker != null)
        {
            warnings.Add(splitBlocker);
            return false;
        }

        // 闸门全过：整组一起清（被消费的那一份由调用方删，这里删其余几卷）。
        foreach (string actual in actualPaths)
        {
            if (string.Equals(actual, consumedPublishedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;   // 被消费的那一份由调用方删（它才是"位置让给同名目录"的那一份）
            }

            try
            {
                if (File.Exists(actual))
                {
                    FileOperations.Delete(actual);
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Problem("工作区", "降级处理", ex);
                errors.Add($"{Path.GetFileName(actual)}（同一分卷组，已解开，但没能删掉：{ex.Message}）");
            }
        }

        return true;
    }

    /// <summary>
    /// 这一份是不是**跟被消费的那一份同目录、同族、同基名**的分卷（组的成员判据）。
    ///
    /// <para>判据只有 <see cref="LegacyVolumeNames.IsVolumePart"/> +
    /// <see cref="LegacyVolumeNames.BelongsToSameGroup"/> 两个既有出口
    /// （⛔ 不在这里另写一套名字/分族规则）；目录用规范化全路径比。</para>
    ///
    /// <para><b>为什么是 internal</b>（2026-10-05）：<c>VolumeGroupRulerParityTests</c> 要把这一把尺子与
    /// 递归折叠那一把（<c>RecursiveExtractor.IsSameGroupContinuationVolume</c>）放在**同一份名字语料**上
    /// 逐格比对。⛔ 那个用例里**不许复制一份判据**（复制出来的守卫拦不住真身漂移）—— 所以只放宽可见性，
    /// 判据本体一个字没动。对外的引用点仍是 <see cref="ExtractionWorkspace.IsSameGroupMemberInSameDirectory"/>
    /// （薄转发到本方法），⛔ 判据只在这里写一份。</para>
    /// </summary>
    internal static bool IsSameGroupMemberInSameDirectory(
        string candidatePath,
        string consumedSourceFullPath,
        string consumedSourceName)
    {
        string full = SafePathHelper.GetFullPathSafe(candidatePath);

        if (full.Length == 0 || string.Equals(full, consumedSourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                Path.GetDirectoryName(full),
                Path.GetDirectoryName(consumedSourceFullPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(full);

        return LegacyVolumeNames.IsVolumePart(fileName)
               && LegacyVolumeNames.BelongsToSameGroup(consumedSourceName, fileName);
    }
}
