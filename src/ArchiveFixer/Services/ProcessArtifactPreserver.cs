using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// **失败 / 部分完成 / 取消那几条路上的"留现场"**：把过程物按③页「删除操作」那一档处置
/// （用户 2026-10-14 口径，链条模型 <c>A→B→C→D→a</c>）——「**删除操作（失败和部分完成）**只**管
/// "最后一层过程物"**……默认留下」＋「<c>D</c> 肯定会留下，而且是完整的……前面的 <c>B</c> 和 <c>C</c>
/// 都是成功的」（见 <c>docs\大改动-2026-10-14-口径与冲突.md</c> §2.1 / §2.2）。
///
/// <para><b>失败模型（整个类只有这一条）：非破坏性</b>。默认档的动作 = 在这一单的其余物里
/// **给这一份另起一个名字（硬链接）** —— ⛔ **不是搬走**：原件留在原处（别处还在用它），
/// 而工作区收尾把那一棵树整份删掉之后，这一份仍然活在其余物那个名字上（硬链接就是"同一份字节的
/// 第二个实名"）。「彻底删除」档 = **什么都不做**（它随工作区一起清掉），只如实写一行说清。
/// 建不了链接 / 推不出其余物目录 / 路径不在工作区 ⇒ **一行 WARN + 一个字节都不动**。</para>
///
/// <para>⚠ <b>为什么必须是"链接"而不是"搬"</b>（2026-10-16 实测踩到，写死）：第一版用的是
/// <c>TryMoveIntoRest</c>（搬走），当场把两条既有守门打红 ——
/// ① <c>GroupLandingFollowsEntryPackageTests</c> 两条：「批末收拢 / 接片 / 落地」那套机制要在**原地**
/// 找到那一片，搬走它 = 那一组永远收不拢（实测日志：<c>pack3.rar：链在「111_.part3.rar」这一层停下……
/// 留在其余物里</c>，随后三条片各自落在自己那一层、没并进入口包那一层）；
/// ② <c>WorkspaceRootTests.打开保留开关_失败但有文件_一律保留且说清在哪</c>：搬走工作区里**唯一那个文件**
/// 会让工作区变成"零文件空壳"⇒ 空壳无论如何都删 ⇒ 用户要的"留现场"没了。
/// ⇒ 判据与动作都改层：**原件一律原地不动**，只多一个名字。</para>
///
/// <para>⛔ <b>分卷片不进这一档</b>：那一片归"批末收拢 / 接片 / 落地"那套机制管（它们本来就会在落地层
/// 留下自己的名字、活得比工作区长），这里再给它起名字只会多一个看不懂的名字。判据 = 唯一出口
/// <see cref="VolumePieceFacts"/>（卡说它是某组的一片 **或** 名字读得出卷号 —— 两者取并，宁可少动）。</para>
///
/// <para>⛔ <b>红线</b>：只碰**递归工作区里**的东西（用户拖进来的源包永远不在那儿 —— 与逐层回收
/// 那条用的是同一道保险）；建不了链接 / 推不出其余物目录 ⇒ 一行 WARN + 什么都不做。
/// ⛔ 取消这一档不在这里判：取消的判定在 <c>TryPublishPartialProductsAsync</c> 与调用方，
/// 而"留下"本身是**非破坏性**动作（比删安全），失败/部分完成/取消都该留下（不变量 1）。</para>
/// </summary>
internal sealed class ProcessArtifactPreserver
{
    private readonly IPartialPublishStepHost _host;

    internal ProcessArtifactPreserver(IPartialPublishStepHost host)
    {
        _host = host;
    }

    /// <summary>
    /// 把“这一层正在解的当前过程物”按其它层同一口径留下/任其清理。
    /// 只接受工作区里的产物；源包/判不出位置一律不动。
    ///
    /// <para><paramref name="entryPath"/> = 这一层"要解的那个包"（批末收拢的指针只认它那一次）；
    /// 其余项只留下名字、不抢指针。</para>
    /// </summary>
    internal void PreserveCurrentProcessArtifacts(ArchiveTask task, IEnumerable<string>? artifactPaths, string? entryPath)
    {
        bool pointerSet = false;
        foreach (string path in (artifactPaths ?? Array.Empty<string>())
                     .Where(item => !string.IsNullOrWhiteSpace(item))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool isEntry = !pointerSet
                && string.Equals(path, entryPath, StringComparison.OrdinalIgnoreCase);
            if (PreserveCurrentProcessArtifact(task, path, isEntry) && isEntry)
            {
                pointerSet = true;
            }
        }
    }

    /// <summary>
    /// 留下**一份**当前过程物：能留下就返回 true（并把它记成这一单的"最后一层过程物"指针）。
    /// ⛔ 只加一个名字，原件原地不动；任何一步判不出 ⇒ false + 一行 WARN/INFO。
    /// </summary>
    internal bool PreserveCurrentProcessArtifact(ArchiveTask task, string? artifactPath, bool setLastPointer = true)
    {
        if (task == null || string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
        {
            return false;
        }

        if (!_host.IsInsideRecursionWorkspace(artifactPath))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：当前过程物「{Path.GetFileName(artifactPath)}」不在工作区里"
                + "（可能是用户源包）—— 判不出位置，一个字节都不动。");
            return false;
        }

        bool spaceLastProcessKeep = _host.SpaceTightPolicy.Enabled
                                  && _host.SpaceTightPolicy.LastProcessOnFailureKeeps
                                  && _host.SpaceTightPolicy.RestOnFailureKeepsLastProcess;

        if (!spaceLastProcessKeep
            && string.Equals(_host.RestHandlingThisBatch, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog(
                "INFO",
                $"{task.LogName}：当前过程物「{Path.GetFileName(artifactPath)}」按「删除操作」处理，"
                + "不另起名字，随工作区清理。");
            return false;
        }

        string? restDirectory = ResolveRecursionRestDirectory(task, artifactPath);

        if (string.IsNullOrWhiteSpace(restDirectory))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：当前过程物「{Path.GetFileName(artifactPath)}」推不出其余物目录 —— 不动。");
            return false;
        }

        try
        {
            SafePathHelper.EnsureDirectoryExists(restDirectory);
        }
        catch (Exception ex)
        {
            _host.AppendLog("WARN", $"{task.LogName}：当前过程物其余物目录建不出来：{ex.Message}");
            return false;
        }

        string target = Path.Combine(restDirectory, Path.GetFileName(artifactPath));
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(target) || Directory.Exists(target))
        {
            target = ProcessArtifactLayout.MakeUniqueTarget(
                target,
                isDirectory: false,
                reserved,
                FileSystemArtifactTargetProbe.Instance);
        }

        if (!HardLinkHelper.TryCreateHardLink(target, artifactPath))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：当前过程物「{Path.GetFileName(artifactPath)}」没能留下第二个名字"
                + "（硬链接建不出来）—— 留在原处，随工作区清理。");
            return false;
        }

        task.RestDirectoryPath = restDirectory;
        if (setLastPointer)
        {
            task.LastProcessArtifactPath = target;
        }

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：当前过程物「{Path.GetFileName(artifactPath)}」按「留下」在其余物里留了一个名字：{target}");
        return true;
    }

    /// <summary>
    /// **「最后一层过程物」按③页「删除操作」那一档处置**。
    ///
    /// <para><b>哪一份是"最后一层过程物"</b>：**链停下来的那一层要解的那个包** =
    /// <see cref="PartialPublishFacts.ResolveStoppedLayer"/> 那一层的 <c>ArchivePath</c>
    /// （唯一出口，两个消费方共用）。</para>
    ///
    /// <para><b>为什么必须有这一档</b>（2026-10-16 运行期实测，不是推断）：递归路每一层的产物都留在
    /// 这一单的**工作区**里，而"失败 / 部分完成 ⇒ 工作区整份删掉" ⇒ 停下来的那一层要解的包**连同一份
    /// 完整的过程物一起被清掉**了 —— 与用户口径「默认留下」正好相反（实测：一条四层链在第三层失败，
    /// 跑完盘上只剩源包 <c>outer.7z</c>，<c>level3.7z</c> 一个字节都没留）。</para>
    ///
    /// <para><b>落法</b>（⛔ 只做"留下"这一半，"彻底删除"那一半的主动删还没做，见 §20.4）：
    /// 「删除操作 = 留下」（默认档）= 在这一单的其余物里**给这一份另起一个名字（硬链接）**。</para>
    /// </summary>
    internal void PreserveStoppedLayerProcessArtifact(ArchiveTask task, RecursionResult? result)
    {
        if (task == null)
        {
            return;
        }

        RecursionLayerReport? stopped = PartialPublishFacts.ResolveStoppedLayer(result);

        if (stopped == null || string.IsNullOrWhiteSpace(stopped.ArchivePath))
        {
            return;
        }

        string artifact = stopped.ArchivePath;

        if (!File.Exists(artifact))
        {
            // 已经不在了（没解到那一层 / 前面某一步收走了）⇒ 没有什么可留。
            return;
        }

        if (!_host.IsInsideRecursionWorkspace(artifact))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：停下的那一层要解的「{Path.GetFileName(artifact)}」不在递归工作区里"
                + "（它可能是用户拖进来的源包）—— 判不出它是不是本链的过程物，所以一个字节都不动。");

            return;
        }

        string fileName = Path.GetFileName(artifact);

        if (string.Equals(_host.RestHandlingThisBatch, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog(
                "INFO",
                $"{task.LogName}：链在「{fileName}」这一层停下，它是这一层的「最后一层过程物」；"
                + "按③页「删除操作 = 彻底删除」处理 —— 它随这一单的工作区一起清掉（⛔ 不搬进其余物、"
                + "不另起名字；链上方那些成功过的过程物本来就已经各层当场删掉了）。");

            return;
        }

        FileNatureCard card = FileNature.Inspect(artifact);

        if (VolumePieceFacts.CardSaysItIsPiece(card)
            || VolumePieceFacts.ResolveSlotIndex(artifact, card) is not null
            || LegacyVolumeNames.TryGetIndex(fileName) is not null)
        {
            _host.AppendLog(
                "INFO",
                $"{task.LogName}：链在「{fileName}」这一层停下，但它是「分卷的一片」 —— 这一片归"
                + "「批末收拢 / 接片 / 落地」那套机制管（它会带着自己的名字落在该落的那一层），"
                + "所以这一档不动它（⛔ 不给它另起名字）。");

            return;
        }

        string? restDirectory = ResolveRecursionRestDirectory(task, artifact);

        if (string.IsNullOrWhiteSpace(restDirectory))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但推不出这一单的其余物目录"
                + " —— 它留在原处（工作区收尾会把它清掉）。");

            return;
        }

        try
        {
            SafePathHelper.EnsureDirectoryExists(restDirectory);
        }
        catch (Exception ex)
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但其余物目录建不出来 —— {ex.Message}"
                + "（它留在原处；工作区收尾会把它清掉）。");

            return;
        }

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string target = Path.Combine(restDirectory, fileName);

        /*
         * ⚠ 撞名才让位（与 <see cref="TryMoveIntoRest"/> 同一个模式，⛔ 不自己另写一套）：
         * `ProcessArtifactLayout.MakeUniqueTarget` **只在"已经有人占了"时才该调** —— 它本身
         * 只会给出 `名字(1).ext` 这种形式（没有"空着就原样返回"那一支）。第一版无条件调它 ⇒
         * 明明空着也写成 `level3(1).7z`（运行期实测逮到的）。
         */
        if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
        {
            target = ProcessArtifactLayout.MakeUniqueTarget(
                target,
                isDirectory: false,
                reserved,
                FileSystemArtifactTargetProbe.Instance);
        }

        if (!HardLinkHelper.TryCreateHardLink(target, artifact))
        {
            _host.AppendLog(
                "WARN",
                $"{task.LogName}：链停下的那一层的过程物「{fileName}」没能留下第二个名字"
                + "（硬链接建不出来 —— 多半与其余物不在同一个卷）—— 它留在原处（工作区收尾会把它清掉）。");

            return;
        }

        // 其余物记到这一单上（其余物那一档收尾时才有对象可处理）。
        task.RestDirectoryPath = restDirectory;
        // 记下这一份过程物本身：后续整条单一物成功时，按“成功只处理源包”的规则清掉它。
        task.LastProcessArtifactPath = target;

        _host.AppendLog(
            "INFO",
            $"{task.LogName}：链在「{fileName}」这一层停下 —— 它是这一层的「最后一层过程物」，"
            + $"按③页「删除操作 = 留下」（默认档）在其余物里留了一个名字：{target}"
            + "（硬链接：原件一个字节都没搬、也没改名；链上方成功过的那些过程物已经各层当场删掉了；"
            + "最外层源包一律原地不动）。");
    }

    /// <summary>
    /// **这一单的其余物目录**（递归路：从"这一份在递归工作区里的路径"反推落点那一层）。
    ///
    /// <para>判据只读事实：路径里那一段工作区标记（<see cref="VolumeContentInference.WorkDirectoryName"/>，
    /// ⛔ 这里不另写标记名）**前面的那一层**就是落点，其余物就在它下面。</para>
    ///
    /// <para>推不出（标记那一段找不到 / 前面没有东西 / 路径形态怪）⇒ 返回 null ⇒ 调用方什么都不做 + 一行 WARN。
    /// ⚠ 2026-10-16：这个方法在「逐层回收与档位解绑」那一刀里被删过一次（当时它唯一的调用者
    /// "集中进其余物"那支被删了）；现在「最后一层过程物按档留下」重新需要它 —— 同一份判据，
    /// ⛔ 不另写一份。</para>
    /// </summary>
    private static string? ResolveRecursionRestDirectory(ArchiveTask task, string pathInsideWorkspace)
    {
        if (task != null && !string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            return task.RestDirectoryPath;
        }

        if (string.IsNullOrWhiteSpace(pathInsideWorkspace))
        {
            return null;
        }

        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(pathInsideWorkspace) ?? string.Empty);

            while (directory != null)
            {
                if (string.Equals(directory.Name, VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    string? landing = directory.Parent?.FullName;

                    return string.IsNullOrWhiteSpace(landing)
                        ? null
                        : Path.Combine(landing, ProcessArtifactLayout.ArtifactDirectoryName);
                }

                directory = directory.Parent;
            }
        }
        catch
        {
            // 路径形态怪 / 权限 ⇒ 判不出（⛔ 不猜）。
        }

        return null;
    }
}
