using ArchiveFixer.Helpers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **就地替换**那一档的发布（用户 2026-09-30 中午：递归 = 就地替换）。
///
/// <para>算法（层号升序，父层一定排在子层前面）：</para>
/// <list type="number">
/// <item><description>第 0 层：产物**原样**搬进发布目标（不做任何"外壳摊平"——
/// 那些文件夹是打包人自己的结构）；</description></item>
/// <item><description>之后每一层：它要解的那个包在父层产物里的相对路径是 <c>X\Y\DDDD.mp4</c>，
/// 那一层就落在 <c>&lt;父层落点&gt;\X\Y\DDDD\</c> —— **原位置 + 去掉假后缀的基名**；</description></item>
/// <item><description>落位之后把那个包文件**从产物里拿掉**：位置让给同名目录
/// （用户给的形状 <c>AAA\DDDD\内容物</c> 里没有 <c>DDDD.mp4</c>）。真文件原地不动。</description></item>
/// </list>
///
/// <para>
/// 三条边界（都是"宁可少动"的那一侧）：
/// ① <b>失败的层不参与</b> —— 它没产出任何东西，把它的包删掉等于把用户唯一的那一份弄丢；
/// ② 父子关系不额外记一份，仍从 <see cref="WorkspaceLayer.InputPath"/> 反推
/// （与 <see cref="WorkspaceStaging.ResolveLeafLayers"/> 同一个事实来源）；
/// ③ "中间层省不省 / 末层永不可省"全部读 <see cref="PackageLayerRules"/> 那一个出口，
/// 本类不自己判。
/// </para>
/// </summary>
internal sealed class WorkspaceInPlacePublisher
{
    private readonly WorkspaceStaging _staging;

    internal WorkspaceInPlacePublisher(WorkspaceStaging staging)
    {
        _staging = staging;
    }

    /// <summary>就地替换发布：返回搬动的文件数（含随后被同名目录替换掉的内层包）。</summary>
    /// <param name="alreadyReclaimedPaths">
    /// **已经被"逐层回收"当场删掉的那些内层包**（默认 null = 没有这一档，行为与以前逐字相同）。
    ///
    /// <para>判据与执行体都在协调器那一个出口里（<c>ExtractionCoordinator.PurgeLayerSourcePackage</c>），
    /// 本类只把这一份事实用在**一处**：<see cref="WorkspaceConsumedCleanup.TryDeleteConsumedPackage"/>
    /// 里那句"它已经被解开，但这一步没能把它搬进产物" —— 那一份是**我们自己按口径删掉的**，
    /// 不是搬运失败，照旧写那条 WARN 就是一句假话（用户在日志里会去找一个根本没发生的失败）。</para>
    /// </param>
    internal int Publish(
        string destination,
        Dictionary<string, string> movedFromTo,
        List<string> renamed,
        List<string> errors,
        List<string> warnings,
        IReadOnlyCollection<string>? alreadyReclaimedPaths = null)
    {
        List<WorkspaceLayer> successful = _staging.Layers
            .Where(layer => layer.Successful)
            .OrderBy(layer => layer.Depth)
            .ToList();

        // 每一层下面挂着哪几个**真的解开了**的内层包（兄弟数 / "还有没有下一层"都从这里来）。
        var childrenOf = new Dictionary<WorkspaceLayer, List<WorkspaceLayer>>();
        var parentOf = new Dictionary<WorkspaceLayer, WorkspaceLayer?>();

        /*
         * ⛔ "该不该给这一层留一个包名目录"必须**在任何一次移动之前**全部算完。
         *
         * 判据里有一条是"父层除了交给下一层的那个包，还有没有别的东西"
         * （<see cref="PackageLayerRules.ProducedOwnContent"/>），读的是**父层产物目录**；
         * 而移动是**按层号升序**做的 —— 等到处理第 N 层时，第 N-1 层的产物目录已经被搬空、
         * 甚至被顺手删掉了（MoveContent 末尾那条 TryDeleteDirectoryIfEmpty）。
         * 那时再去读只会读到"目录不存在"（保守口径按"出了内容物"算），
         * 于是每一层都被判成"不该留目录" —— 五层链会被压成一层。
         */
        var keepFolderOf = new Dictionary<WorkspaceLayer, bool>();

        foreach (WorkspaceLayer layer in successful)
        {
            WorkspaceLayer? parent = layer.Depth == 0 ? null : WorkspaceStaging.ResolveParentLayer(layer, successful);

            parentOf[layer] = parent;

            if (parent == null)
            {
                continue;
            }

            if (!childrenOf.TryGetValue(parent, out List<WorkspaceLayer>? children))
            {
                children = new List<WorkspaceLayer>();
                childrenOf[parent] = children;
            }

            children.Add(layer);
        }

        foreach (WorkspaceLayer layer in successful)
        {
            WorkspaceLayer? parent = parentOf[layer];

            if (parent == null)
            {
                continue;
            }

            int siblingCount = childrenOf.TryGetValue(parent, out List<WorkspaceLayer>? siblings)
                ? siblings.Count
                : 0;

            bool parentProducedContent = PackageLayerRules.ProducedOwnContentFromEntryCount(
                parent.EntryCountBeforeReclaim ?? PackageLayerRules.CountEntriesOrUnknown(parent.OutputPath),
                siblingCount);
            keepFolderOf[layer] = PackageLayerRules.ShouldKeepLayerFolder(
                parentProducedContent,
                siblingCount,
                hasChildLayer: childrenOf.ContainsKey(layer));
        }

        var destinationOf = new Dictionary<WorkspaceLayer, string>();
        int movedCount = 0;

        foreach (WorkspaceLayer layer in successful)
        {
            if (!WorkspacePathRules.SafeDirectoryExists(layer.OutputPath))
            {
                errors.Add($"第 {layer.Depth} 层的产物目录不存在（{layer.OutputPath}）");
                continue;
            }

            WorkspaceLayer? parent = parentOf[layer];

            if (parent == null)
            {
                if (layer.Depth != 0)
                {
                    // 找不到父层说明这一层的输入不在任何一层的产物里（不该发生）：不猜落点。
                    errors.Add($"第 {layer.Depth} 层的输入（{layer.InputPath}）不在任何一层的产物里，已跳过");
                    continue;
                }

                destinationOf[layer] = destination;

                movedCount += WorkspaceContentMover.MoveContent(
                    layer.OutputPath,
                    destination,
                    renamed,
                    errors,
                    movedFromTo);

                continue;
            }

            string relArchive = WorkspacePathRules.RelativeUnder(parent.OutputPath, layer.InputPath);
            string relDirectory = Path.GetDirectoryName(relArchive) ?? string.Empty;
            string baseDirectory = relDirectory.Length == 0
                ? destinationOf[parent]
                : SafePathHelper.Combine(destinationOf[parent], relDirectory);

            string layerName = keepFolderOf[layer]
                ? PackageLayerRules.ResolveInPlaceLayerName(layer.InputPath)
                : string.Empty;

            string layerDirectory = layerName.Length == 0
                ? baseDirectory
                : SafePathHelper.Combine(baseDirectory, layerName);

            /*
             * 先把包文件从产物里拿掉，再把这一层的内容搬进同名目录 ——
             * 反过来的话同名目录会和那个文件抢同一个路径（Windows 上必然失败）。
             *
             * ⚠ 路径从**父层的落点**拼（`destinationOf[parent] + relArchive`），
             * 不是从 `baseDirectory`（那个已经把 `relDirectory` 算进去了，再拼一次 relArchive
             * 会得到 `…\BBBB\BBBB\CCCCC.mp4` 这种不存在的路径，于是包永远删不掉）。
             *
             * ⛔ 2026-10-03：「算出来的那个位置上站的到底是不是我们这一步搬进来的那一份」由**事实**回答
             * （`movedFromTo`：来源全路径 → 落点全路径），⛔ 不再只问 `File.Exists`
             * —— 撞名时我们真搬的那一份会被改名，算出来的位置上可能是**别处留下的同名残留**。
             */
            WorkspaceConsumedCleanup.TryDeleteConsumedPackage(
                SafePathHelper.Combine(destinationOf[parent], relArchive),
                layer.InputPath,
                movedFromTo,
                destination,
                errors,
                warnings,
                alreadyReclaimedPaths);

            destinationOf[layer] = layerDirectory;

            movedCount += WorkspaceContentMover.MoveContent(
                layer.OutputPath,
                layerDirectory,
                renamed,
                errors,
                movedFromTo);
        }

        return movedCount;
    }
}
