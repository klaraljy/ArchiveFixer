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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 分卷候选路径收集（从 `ExtractionCoordinator.VolumeGatherCollectStep.cs` 搬出）：
    /// 在入口层/同基名/组目录里按事实收集候选片，判据与日志文案沿用原实现。
    /// </summary>
    internal sealed class ExtractionVolumeCandidateCollector
    {
        private readonly ExtractionCoordinator _host;

        internal ExtractionVolumeCandidateCollector(ExtractionCoordinator host)
        {
            _host = host;
        }

        internal List<string> CollectVolumeCandidatePaths(ArchiveTask task, string entryLayer, out string referenceName, out int scannedInEntryLayer)
        {
            var paths = new List<string>();

            referenceName = string.Empty;
            scannedInEntryLayer = 0;

            if (string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return paths;
            }

            string observed = _host.ResolveDeferredGroupMemberPath(task);
            string reference = ExtractionCoordinator.ResolveGroupReferenceName(task, FileNameHelper.GetArchiveBaseName(observed), observed);

            referenceName = reference;

            if (reference.Length == 0)
            {
                return paths;
            }

            /*
             * ⚠ 2026-10-17（运行期判据输入定死）：参照名退回 `<基名>.zip` 时（`pack111-L2.7z.zip`、`aaa-L2.zip`），
             * 与 `.7z` / `.rar` 族的兄弟**一个都对不上**（逐字 `这一层文件 3 个｜同族候选 0 个`）——
             * 而那一层里明明躺着真成员。⇒ **参照名允许改由"真实成员"充当**：
             * 先把四档来源的文件都收集起来（不过滤），若参照名自己都过不了那把尺子（`BelongsToSameGroup(ref, ref)`），
             * 就从真实文件里挑第一个"卡说它是这一族一片"的，用它的名字当参照名。
             * ⛔ 判不出（一个成员都没有）⇒ 照旧空结果（兜底不变）。
             */
            var allFiles = new List<string>();

            void CollectAll(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in ExtractionCoordinator.CollectVolumeFilesInDirectory(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            /*
             * 源层只收任务账上明确登记的那一层（OriginalPath 的父目录）。
             * ⛔ 不再按祖先目录递归扫描/按样本目录名截断：目录名不是源包身份。
             */
            void CollectAllFromSourceLayer(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in ExtractionCoordinator.SafeEnumerateFiles(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            CollectAll(entryLayer);
            scannedInEntryLayer = allFiles.Count;

            string ownLayer = Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(observed)) ?? string.Empty;

            CollectAll(ownLayer);

            /*
             * ===== 卡自证"我是 zip 分卷的一片"时，**再往外找一层**（用户 2026-10-17：底层识别先）=====
             * 现场（新样本 `6666` 的 `recipe.json` 逐字）：同一组三片分处
             * `打包111\打包111-W0-0\`、`打包111(2)\打包111(2)\打包111(2)\`、`打包111(3)\打包111(3)\` ——
             * 名字也全烂（`打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`）。
             * ⛔ 只在"入口那一份卡上确实是 zip 分片"时才补扫（`Format=Zip` 且 成员/EOCD/本地头 之一）；
             * ⛔ 共用找卷窗口一个字不改（上一轮试放大它打红了既有守门：`CrossLayerVolumeGatherTests…`）；
             * ⛔ 补扫有上限（目录 ≤ 24 个、文件 ≤ 400 个），且收进来的还要过后面那几道闸门。
             */
            bool entryIsZipPiece;

            try
            {
                FileNatureCard entrySeat = FileNature.Inspect(observed);

                /*
                 * ⚠ 2026-10-18 新批 `3333`/`4444`/`7777`/`9999`：**7z / RAR 的分卷片同样会散在
                 * 包装壳的产出目录里**，补扫不能只给 ZIP 开。判据仍读卡：格式认得出，且卡上自证
                 * "是这一族的一片"（BodyKind / 成员位）—— 名字不参与这一道门。
                 */
                entryIsZipPiece = entrySeat.Format != VolumeContentFormat.Unknown
                    && (entrySeat.HasZipEndRecord
                        || entrySeat.HasZipLocalHeader
                        || entrySeat.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                        || entrySeat.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece);
            }
            catch
            {
                entryIsZipPiece = false;
            }

            /*
             * 补扫那一侧要用的两个判据输入（与本地那一侧同源）：基名 + "第 1 片有多大"的锚点。
             */
            string sweepReferenceBase = FileNameHelper.GetArchiveBaseName(reference);
            long sweepAnchorSize = 0;

            try
            {
                FileNatureCard sweepAnchorSeat = FileNature.Inspect(observed);

                if (sweepAnchorSeat.HasZipLocalHeader && sweepAnchorSeat.Size > 0)
                {
                    sweepAnchorSize = sweepAnchorSeat.Size;
                }
            }
            catch
            {
                sweepAnchorSize = 0;
            }

            if (entryIsZipPiece)
            {
                try
                {
                    string observedFull = SafePathHelper.GetFullPathSafe(observed);
                    string? parentDirectory = Path.GetDirectoryName(observedFull);
                    string? grandParent = string.IsNullOrWhiteSpace(parentDirectory)
                        ? null
                        : Directory.GetParent(parentDirectory)?.FullName;

                    /*
                     * 起点要**再往上两层**（现场 `6666`：入口在 `…\打包111(3)\打包111(3)\`，
                     * 同组另外两片在 `…\打包111\打包111-W0-0\` 与 `…\打包111(2)\打包111(2)\打包111(2)\` ——
                     * 从 `打包111(3)` 起扫永远够不到兄弟目录的下一层）。
                     */
                    /*
                     * ⚠ 2026-10-18（运行期坐实：`run-19191919` 09:20:09 逐字 `补扫根=…\大测试原包-困难
                     * ｜实际扫了 66 个目录 / 2 个文件`）：**往上 3 层会越过样本根**，指到装着 30 份样本的父目录，
                     * 目录预算（40）当场被兄弟样本目录吃光 ⇒ 这一组真正的片一片都没走到。
                     * ⇒ 收窄上界：从祖父目录再往上时，只有当"待上溯的那个目录"里存在**名字以本组基名前缀开头**
                     * 的子目录时才继续往上（`6666` 靠 `打包111(2)`/`打包111(3)` 成立；`19191919` 的那一层
                     * 兄弟叫 `6666`/`8888`/…，一个都对不上 ⇒ 停在样本根本身）。⛔ 判不出前缀 ⇒ 不上溯。
                     */
                    string? scanRoot = grandParent;

                    if (!string.IsNullOrWhiteSpace(grandParent))
                    {
                        string prefix = FileNameHelper.GetArchiveBaseName(referenceName);
                        int dash = prefix.IndexOf('-');

                        if (dash > 0)
                        {
                            prefix = prefix[..dash];
                        }

                        string? candidateRoot = grandParent;

                        for (int up = 0; up < 3; up++)
                        {
                            string? parent = Directory.GetParent(candidateRoot!)?.FullName;

                            if (string.IsNullOrWhiteSpace(parent) || prefix.Length == 0)
                            {
                                break;
                            }

                            bool related = false;

                            try
                            {
                                foreach (string sub in Directory.GetDirectories(parent))
                                {
                                    if (Path.GetFileName(sub).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                    {
                                        related = true;
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                related = false;
                            }

                            if (!related)
                            {
                                break;
                            }

                            candidateRoot = parent;
                        }

                        scanRoot = candidateRoot;
                    }

                    if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
                    {
                        int directories = 0;
                        int files = 0;
                        /*
                         * ⛔ **由近及远**（2026-10-18 困难批 `30303030` 定案）：原来只从 `scanRoot` 向下 BFS，
                         * 而那一轮 `scanRoot` 是**批次根**（30 份样本的父目录）⇒ 40 个目录预算全花在
                         * `10101010`/`1111` 等别的样本上，本组的兄弟目录一个都没轮到（逐字：
                         * `实际扫了 75 个目录 / 8 个文件`，全是别组的文件）⇒ 整组凑不齐。
                         * ⇒ 种子 = **这一单自己那一层 + 往上 3 层**（近的优先）；只有一层都取不到时才退回 `scanRoot`。
                         * ⛔ 判据（`AcceptsAsGroupPiece`）一个字不改，只改"先扫哪儿"。
                         */
                        var sweepSeeds = new List<string>();
                        string? seedCursor = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.CurrentPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(seedCursor); up++)
                        {
                            if (Directory.Exists(seedCursor))
                            {
                                sweepSeeds.Add(seedCursor!);
                            }

                            seedCursor = Path.GetDirectoryName(seedCursor);
                        }

                        if (sweepSeeds.Count == 0)
                        {
                            sweepSeeds.Add(scanRoot!);
                        }

                        /*
                         * 源层也要进补扫种子：分卷片可能先在别的包装壳里产出，再落回
                         * `OriginalPath` 那一层；只从 `CurrentPath`（过程物层）往上找够不到
                         * （2026-10-19 `3333`：`.002` / `.003` 在兄弟源目录里）。
                         */
                        string? sourceSeed = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.OriginalPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(sourceSeed); up++)
                        {
                            if (Directory.Exists(sourceSeed)
                                && !sweepSeeds.Any(existing => string.Equals(existing, sourceSeed, StringComparison.OrdinalIgnoreCase)))
                            {
                                sweepSeeds.Add(sourceSeed!);
                            }

                            sourceSeed = Path.GetDirectoryName(sourceSeed);
                        }

                        var frontier = new List<string>(sweepSeeds);
                        var sweepTrace = new List<string>();
                        var visited = new HashSet<string>(sweepSeeds, StringComparer.OrdinalIgnoreCase);

                        for (int level = 0; level < 8 && frontier.Count > 0 && directories < 200; level++)
                        {
                            var next = new List<string>();

                            foreach (string directory in frontier)
                            {
                                try
                                {
                                    foreach (string sub in Directory.GetDirectories(directory))
                                    {
                                        if (directories++ >= 40)
                                        {
                                            break;
                                        }

                                        if (visited.Add(sub))
                                        {
                                            next.Add(sub);
                                        }
                                    }
                                }
                                catch
                                {
                                    // 读不动的目录跳过（不影响已收到的候选）。
                                }
                            }

                            foreach (string directory in frontier.Concat(next))
                            {
                                /*
                                 * ⚠ 2026-10-18（运行期逐份判据输入定案）：这里原来用 `ExtractionCoordinator.CollectVolumeFilesInDirectory`
                                 * （按**后缀**筛"像不像卷文件"），于是 `bbb-L2.zip-658` 这种"后缀被改坏、内容却是
                                 * 末片"的文件**根本没被枚举**（同一份日志里逐份只出现 3 个名字，第三个名字一次没出现）。
                                 * ⇒ 补扫改枚举**整层所有文件**，由唯一出口 `AcceptsAsGroupPiece`（内容先行）来收/挡；
                                 * 文件预算 400 只当上限用，⛔ 不再用后缀当前置筛子。
                                 */
                                foreach (string file in ExtractionCoordinator.SafeEnumerateFiles(directory))
                                {
                                    if (++files > 400)
                                    {
                                        break;
                                    }

                                    try
                                    {
                                        FileNatureCard card = FileNature.Inspect(file);

                                        /*
                                         * ===== 补扫锚点：末片没有本地头时，从**本组中段片**取满片尺寸 =====
                                         * （2026-10-18 新批 `24242424` 运行期定案）跨盘 ZIP 的入口是末片
                                         * （`EOCD=True`、没有本地头）⇒ `sweepAnchorSize` 原来是 0 ⇒ 字节数守恒整档
                                         * 不启用 ⇒ 从数据中间切开的第 2 片（`333-L2.pdf`，`Format=Unknown`）永远进不了
                                         * 候选池，组只剩 2 片。
                                         * 判据：只拿**没有 EOCD 的、基名相关的、内容没证明不是成员**的片当锚点
                                         * （末片自己绝不能当锚点）；取第一份这样的片的大小，后续同尺寸中段片就能过 ③。
                                         * ⛔ 只是把"锚点"补上，`AcceptsAsGroupPiece` 的阶梯一个字没改。
                                         */
                                        if (sweepAnchorSize <= 0
                                            && sweepReferenceBase.Length > 0
                                            && !card.HasZipEndRecord
                                            && card.Size > 0
                                            && card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                                            && card.BodyKind != ArchiveBodyKind.SingleComplete
                                            && VolumeGatherStep.BaseNameRelatedToGroup(card.ArchiveBaseName, sweepReferenceBase))
                                        {
                                            sweepAnchorSize = card.Size;
                                        }

                                        /* 与本地那一侧**同一个出口**（含字节数守恒 ⇒ 中间片才收得进来）。 */
                                        bool accepted = VolumeGatherStep.AcceptsAsGroupPiece(card, reference, sweepReferenceBase, sweepAnchorSize);

                                        if (sweepTrace.Count < 20)
                                        {
                                            sweepTrace.Add(
                                                $"{FileNameHelper.GetFileName(file)}〔F={card.Format} EOCD={card.HasZipEndRecord} LH={card.HasZipLocalHeader} "
                                                + $"成员={card.Membership} 种类={card.BodyKind} 大小={card.Size} 准入={accepted}〕");
                                        }

                                        if (accepted
                                            && !allFiles.Any(existing => string.Equals(
                                                existing, file, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            allFiles.Add(file);
                                        }
                                    }
                                    catch
                                    {
                                        // 单份读不出来跳过。
                                    }
                                }
                            }

                            frontier = next;
                        }

                        /*
                         * **判据输入永久留证**（用户 2026-10-17：「判据输入必须写进日志」）：
                         * 补扫到底扫了哪儿、收进几份 —— 没有这一行，"远处那两片到底收没收进来"
                         * 只能靠猜（这一轮就是靠断点读 `paths` 才看出来的）。
                         */
                        _host.AppendLog(
                            "INFO",
                            $"{task.LogName}：收拢补扫 —— 入口卡自证是 zip 分片；补扫根={scanRoot}（入口往上 3 层）"
                            + $"｜下探最多 8 层、目录上限 200、文件上限 400｜实际扫了 {directories} 个目录 / {files} 个文件"
                            + $"｜锚点={sweepAnchorSize} 基名={sweepReferenceBase}"
                            + $"｜候选池里现在 {allFiles.Count} 份"
                            + (sweepTrace.Count > 0 ? $"｜逐份：[{string.Join("；", sweepTrace)}]" : string.Empty));
                    }
                }
                catch
                {
                    // 补扫失败 ⇒ 与没有这一档逐字相同。
                }
            }

            foreach (ArchiveTask candidate in ExtractionCoordinator.SnapshotTaskTable(_host.Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                CollectAll(Path.GetDirectoryName(candidate.CurrentPath ?? string.Empty));
                CollectAll(candidate.OutputPath);
                CollectAll(candidate.ContentDirectoryPath);
                CollectAll(candidate.RestDirectoryPath);
                CollectAllFromSourceLayer(Path.GetDirectoryName(candidate.OriginalPath ?? string.Empty));
            }

            /*
             * ⛔ **不能拿"参照名自己过不过尺子"当闸门**（2026-10-17 实测）：`pack111-L2.7z.zip` 自己是个合法的
             * zip 名 ⇒ `BelongsToSameGroup(ref, ref)` = **true**，于是兜底永远不触发、层里明明有真成员却判 0 个。
             * 现在的闸门 = **这个参照名在收集到的文件里到底存不存在**（不存在 ⇒ 说明它是拼出来的兜底名）。
             */
            bool referenceIsRealFile = allFiles.Any(file =>
                string.Equals(FileNameHelper.GetFileName(file), reference, StringComparison.OrdinalIgnoreCase));

            if (!referenceIsRealFile)
            {
                /*
                 * 三档递进找参照名（⛔ 都找不到就保持 `<基名>.zip` 的老兜底）：
                 * ① 卡说"我是这一族的一片"的；② **名字读得出卷号**的（7z/RAR 的伪装片按内容认不出，
                 * 只有名字这一条线索）；③ 一个都不满足 ⇒ 老兜底。
                 */
                string? byCard = null;
                string? byVolumeName = null;

                foreach (string file in allFiles)
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (byCard == null && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file)))
                    {
                        byCard = name;
                    }

                    if (byVolumeName == null && LegacyVolumeNames.TryGetIndex(name) != null)
                    {
                        byVolumeName = name;
                    }

                    if (byCard != null && byVolumeName != null)
                    {
                        break;
                    }
                }

                string? chosen = byCard ?? byVolumeName;

                if (chosen != null)
                {
                    reference = chosen;
                    referenceName = reference;
                }
            }

            string referenceBase = FileNameHelper.GetArchiveBaseName(reference);

            /*
             * 通用分片族的"第 1 片有多大" —— 文档 §6.6 A 的**字节数守恒**要用的锚点：
             * 从收集到的候选里找"开头是本地文件头"的那一份；找不到 ⇒ 0（这一档不启用）。
             */
            long zipFullSize = 0;

            if (entryIsZipPiece)
            {
                foreach (string file in allFiles)
                {
                    try
                    {
                        FileNatureCard anchorCard = FileNature.Inspect(file);

                        if (anchorCard.HasZipLocalHeader && anchorCard.Size > 0)
                        {
                            zipFullSize = anchorCard.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳到下一份。
                    }
                }
            }

            /*
             * ⚠ 本地这一侧也补同一个锚点（2026-10-18 新批 `24242424`）：本组可能**一片带本地头的都没有**
             * （跨盘 ZIP 的中段片起在数据中间）⇒ `zipFullSize=0` ⇒ 下面这一遍又把补扫刚收进来的
             * `Unknown` 中段片（`333-L2.pdf`）筛掉。补扫那一侧已经从"没有 EOCD、基名相关"的片取了锚点，
             * 这一侧原样沿用；⛔ 只补锚点，判据阶梯不改。
             */
            if (zipFullSize <= 0)
            {
                /*
                 * 7z/RAR 的首卷卡上有“自述片数 / 自述整包字节数”，用**首卷自己的大小**
                 * 当满片锚点；中间片/末片本身没有魔数，这是唯一能量出来的锚点。
                 */
                try
                {
                    FileNatureCard anchorSeat = FileNature.Inspect(observed);

                    if (anchorSeat.DeclaredVolumeCount > 0
                        && anchorSeat.Size > 0
                        && anchorSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                    {
                        zipFullSize = anchorSeat.Size;
                    }
                }
                catch
                {
                    // 读不出来就维持 0（这一档不启用）。
                }
            }

            if (zipFullSize <= 0)
            {
                /*
                 * `observed` 可能只是包装壳（卡上没有自述）；首卷自述在候选池里的另一份卡上。
                 * 这是唯一一个“内容能给出满片尺寸”的出口，⛔ 不按名字猜大小。
                 */
                foreach (string candidateFile in allFiles)
                {
                    try
                    {
                        FileNatureCard candidateSeat = FileNature.Inspect(candidateFile);

                        if (candidateSeat.DeclaredVolumeCount > 0
                            && candidateSeat.Size > 0
                            && candidateSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                        {
                            zipFullSize = candidateSeat.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳过。
                    }
                }
            }

            if (zipFullSize <= 0)
            {
                zipFullSize = sweepAnchorSize;
            }

            foreach (string file in allFiles)
            {
                /*
                 * **只问一个出口**（`AcceptsAsGroupPiece`）：底层识别先行、名字只保底。
                 * ⛔ 原来这里与补扫那一侧各有一套阶梯，于是"中间片"在补扫里永远进不来
                 * （`19191919` 的第二片就是这样被挡掉的）。
                 */
                if (VolumeGatherStep.AcceptsAsGroupPiece(FileNature.Inspect(file), reference, referenceBase, zipFullSize))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字不放行时**问卡**（用户 2026-10-17 总则：底层识别优先）=====
                 * 现场（调试器逐字，`10101010`，`VolumeGatherPlanner.Decide` 帧 0）：
                 * `Complete=false｜PieceCount=1｜自述 3 片 / 62919013｜合计 26214400` ——
                 * 卡完全认得出这一组，可决策那一刻候选里**只有 1 片**：`pack111-L2.7z` 的卷标记
                 * 被伪装法吃掉，名字那把尺子把它判成"不是这一组" ⇒ 自述永远对不上。
                 * 判据：卡说它是这一族的一片（`CardSaysItIsOneOfTheGroup`）**且基名与本组相关**
                 * （既有弱证据尺子 `BaseNameRelatedToGroup`）⇒ 收进候选。
                 * ⛔ 后面那些闸门（同号两份 / 体积规律 / 整组自洽 / 试开）一条不落。
                 */
                if (referenceBase.Length > 0
                    && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file))
                    && VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(file), referenceBase))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字与基名**全被改烂**的那一档：只认卡上的 zip 分片事实 =====
                 * （现场新样本 `6666`：三片叫 `打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`，
                 *  基名一个都对不上，但卡上全是 zip 流：一片有本地头、一片是中间片、一片尾部有 EOCD。）
                 * ⛔ 只在"入口那一份也是 zip 分片"时才这么做（`entryIsZipPiece`），
                 *   免得把同层别的 zip 包卷进来；后面那几道闸门照旧。
                 */
                if (entryIsZipPiece)
                {
                    FileNatureCard candidateCard = FileNature.Inspect(file);

                    /*
                     * ⚠ 单卷 zip 也有 EOCD，但它**可证不是分卷成员**（`BodyKind=SingleComplete`）⇒ 不收。
                     * ⚠ 中间片的名字与头都认不出（现场 `6666`：`打包111-W1-0.png`，头是数据中间切开的）
                     *   ⇒ 用文档 §6.6 A 的**字节数守恒**这条证据：与"第 1 片"同样大的、且没有被内容证明
                     *   不是成员的，才当中间片。
                     */
                    bool isZipPiece = candidateCard.Format == VolumeContentFormat.Zip
                        && candidateCard.BodyKind != ArchiveBodyKind.SingleComplete
                        && (candidateCard.HasZipEndRecord
                            || candidateCard.HasZipLocalHeader
                            || candidateCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember);

                    bool isByteConservedMiddle = !isZipPiece
                        && zipFullSize > 0
                        && candidateCard.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                        && candidateCard.Size > 0
                        && Math.Abs(candidateCard.Size - zipFullSize) <= Math.Max(1, zipFullSize / 100);

                    if (isZipPiece || isByteConservedMiddle)
                    {
                        paths.Add(file);
                    }
                }
            }

            /*
             * **被筛掉的那几份要能看见为什么**（用户 2026-10-17：「判据输入必须写进日志」）。
             * 只在"确实丢了东西"时写（收集到的比收进来的多），否则正常任务不加噪声。
             * 逐条给：文件名 / 名字那把尺子认不认 / 卡上的分卷身份与 BodyKind / 卡上的基名。
             */
            if (allFiles.Count > paths.Count)
            {
                var dropped = new List<string>();

                foreach (string file in allFiles)
                {
                    string candidateName = FileNameHelper.GetFileName(file);

                    if (paths.Contains(file))
                    {
                        continue;
                    }

                    FileNatureCard droppedCard = FileNature.Inspect(file);

                    dropped.Add(
                        candidateName
                        + "〔名字同组=" + (LegacyVolumeNames.BelongsToSameGroup(candidateName, reference) ? "是" : "否")
                        + $"｜分卷身份={droppedCard.Membership}"
                        + $"｜BodyKind={droppedCard.BodyKind}"
                        + $"｜卡上基名={droppedCard.ArchiveBaseName}"
                        + $"｜基名与本组相关={(VolumeGatherStep.BaseNameRelatedToGroup(droppedCard.ArchiveBaseName, referenceBase) ? "是" : "否")}〕");
                }

                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：这一层收集到 {allFiles.Count} 份、收进候选 {paths.Count} 份 —— 被筛掉的："
                    + string.Join("；", dropped)
                    + $"｜参照名=「{reference}」参照基名=「{referenceBase}」");
            }

            return paths;
        }
    }
}

