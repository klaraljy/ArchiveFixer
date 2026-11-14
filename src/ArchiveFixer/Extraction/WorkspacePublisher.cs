using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **发布**的编排：目标合法性、两种发布档（常规 / 就地替换）的择一、结果数字与话术。
///
/// <para>目标合法性三条，全部在**建目录之前**判完（不合法时连空目录都不留）：
/// 目标不能是工作区自己那棵树（否则产物搬进工作区、随后收尾清理会把成品不可逆地删掉）；
/// 目标不能落在任意一层产物目录内部（否则会一边搬一边往自己里面塞）；
/// 目标必须建得出来。</para>
///
/// <para>⛔ 本类**不决定**"哪一层留、那一层叫什么"（唯一出口是 <see cref="ResultFinalizer"/>），
/// 也不自己摊"外壳"；就地替换那一档的算法在 <see cref="WorkspaceInPlacePublisher"/>，
/// 搬运细节在 <see cref="WorkspaceContentMover"/>。</para>
/// </summary>
internal sealed class WorkspacePublisher
{
    private readonly WorkspaceStaging _staging;
    private readonly string _rootDirectory;
    private readonly WorkspaceInPlacePublisher _inPlacePublisher;

    internal WorkspacePublisher(WorkspaceStaging staging, string rootDirectory)
    {
        _staging = staging;
        _rootDirectory = rootDirectory;
        _inPlacePublisher = new WorkspaceInPlacePublisher(staging);
    }

    /// <summary>
    /// 把最终产物"发布"到目标目录（**移动**，绝不覆盖）。
    /// 契约（参数语义、两种发布档的差别、"一个外壳都不摊"）见
    /// <see cref="ExtractionWorkspace.Publish"/>。
    /// </summary>
    internal WorkspacePublishResult Publish(
        string targetDirectory,
        bool inPlaceInnerPackages = false,
        IReadOnlyCollection<string>? alreadyReclaimedPaths = null,
        bool intakeStaging = false)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return WorkspacePublishOutcome.Failure(string.Empty, "未指定发布目标目录");
        }

        if (_staging.Layers.Count == 0)
        {
            return WorkspacePublishOutcome.Failure(string.Empty, "工作区里还没有任何一层，没有可发布的产物");
        }

        string destination = SafePathHelper.GetFullPathSafe(targetDirectory);
        IReadOnlyList<WorkspaceLayer> leaves = _staging.ResolveLeafLayers();

        /*
         * ⛔ 发布目标**落在工作区自己那棵树里面**要立刻停手（用户 2026-09-30）。
         *
         * 工作区默认就建在目标目录里面（<目标目录>\.ArchiveFixer.work），于是"发布目标"与
         * "工作区"第一次成了**父子关系** —— 万一有人把工作区根本身（或它的子目录）当成发布目标，
         * 产物就会被搬进工作区，紧接着收尾清理会把工作区整份删掉：用户的成品**不可逆地消失**。
         * 下面那条"目标不能落在某一层产物目录内部"只挡住了反方向（往自己里面塞），挡不住这一边。
         */
        if (WorkspacePathRules.IsSameOrChildPath(destination, _rootDirectory))
        {
            return WorkspacePublishOutcome.Failure(destination, "发布目标目录就是工作区（或在工作区里面），已停止发布");
        }

        /*
         * 目标落在产物目录内部要立刻停手，而且要在**建目录之前**判断（任意一层命中就整体停）：
         * 那样会一边搬一边往自己里面塞，轻则无限套娃、重则丢文件；
         * 提前判断还能保证"目标不合法"时连空目录都不会留下。
         */
        foreach (WorkspaceLayer leaf in leaves)
        {
            if (WorkspacePathRules.IsSameOrChildPath(destination, leaf.OutputPath))
            {
                return WorkspacePublishOutcome.Failure(destination, "发布目标目录不能位于工作区产物目录内部，已停止发布");
            }
        }

        try
        {
            // 目标不存在就建；建不出来就没必要继续，否则每个文件都要失败一遍。
            if (!SafePathHelper.EnsureDirectoryExists(destination))
            {
                return WorkspacePublishOutcome.Failure(destination, $"发布目标目录不存在且创建失败：{destination}");
            }

            var renamed = new List<string>();
            var errors = new List<string>();
            var warnings = new List<string>();

            /*
             * 这一步**真的搬进来了哪些文件**：`来源全路径 -> 落点全路径`（落点是撞名改名之后的那个）。
             *
             * 为什么必须记：就地替换会把"已经被解开的那一份内层包"从产物里拿掉，
             * 而它的落点是**算出来的**（父层落点 + 相对路径）—— 只问 `File.Exists` 就删，
             * 删掉的可能是**本来就在那里的同名残留**（我们这一步真搬的那一份因为撞名被改成了
             * `名字(1).ext`）。判据只认事实：不在这一份表里的，一个字节都不删。
             */
            var movedFromTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int movedCount = 0;

            if (inPlaceInnerPackages)
            {
                movedCount += _inPlacePublisher.Publish(
                    destination,
                    movedFromTo,
                    renamed,
                    errors,
                    warnings,
                    alreadyReclaimedPaths);
            }
            else
            {
                // 按层号升序搬：同名的"谁先落位"因此是稳定的（先解出来的先落位，后来的改名）。
                foreach (WorkspaceLayer leaf in leaves)
                {
                    if (!WorkspacePathRules.SafeDirectoryExists(leaf.OutputPath))
                    {
                        errors.Add($"第 {leaf.Depth} 层的产物目录不存在（{leaf.OutputPath}）");
                        continue;
                    }

                    /*
                     * ⛔ **发布这一步一个外壳都不摊**（2026-10-02 真机修，用户原话见
                     * PackageLayerRules 第 1 条："如果是一个文件夹 1111 里面包裹真正的内容物，
                     * 这个时候你就会把 1111 省略，这是非常大忌"）。
                     *
                     * 现场：`风景01.7z.001`（只解一层、内容全在一个 `风景\` 文件夹里）——
                     * 这里以前传 `stripWrapper: true`，把 `风景\` 当"无意义外壳"摊掉，
                     * 落地成 `<包名>\1.mp4`；而"哪一层留、那一层叫什么"的**唯一出口**是
                     * <see cref="ResultFinalizer"/>（它本来就会正确处理：同名套娃 `pack\pack\`
                     * 由它自己的"同名不套层"那条收掉，普通文件夹则原样保留）。
                     * 两处各摊一次 ⇒ 用户的文件夹被吃过一层，而且**没有任何日志**。
                     */
                    movedCount += WorkspaceContentMover.MoveContent(
                        leaf.OutputPath,
                        destination,
                        renamed,
                        errors,
                        movedFromTo);
                }
            }

            int expectedFileCount = movedCount + errors.Count;

            return new WorkspacePublishResult
            {
                Success = expectedFileCount > 0 && movedCount == expectedFileCount,
                DestinationPath = destination,
                MovedFileCount = movedCount,
                ExpectedFileCount = expectedFileCount,
                FailedFileCount = errors.Count,
                Message = WorkspacePublishOutcome.Describe(
                    destination,
                    movedCount,
                    renamed,
                    errors,
                    countsMovedNotLanded: inPlaceInnerPackages,
                    intakeStaging: intakeStaging),
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("工作区", "降级处理", ex);
            // 发布跑在任务收尾阶段，任何意外都不能把已经解压好的产物带崩（AGENTS.md §6 第 9 条）。
            return WorkspacePublishOutcome.Failure(destination, $"发布过程中出现意外错误：{ex.Message}");
        }
    }
}
