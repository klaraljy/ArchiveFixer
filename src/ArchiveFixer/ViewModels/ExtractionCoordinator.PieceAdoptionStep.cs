using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>收片采纳与路径红线（接片判据/产出方链/是否可搬/任务表同步；第 9 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        internal int PurgeRedundantPieceAliases()
        {
            int removed = 0;

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null)
                {
                    continue;
                }

                /*
                 * 第三个根 = **这一单源片所在的那个目录**（2026-10-17 真机 `6666` 实测）：
                 * 残留 `pack111\pack111-L2.7z.003` 躺在**源目录** `pack111\` 里，而那一单的
                 * `OutputPath` 是它下面的 `pack111\pack111-L2` ⇒ 只扫落点根会漏掉它。
                 * ⛔ 仍然只碰"名字像分卷片 + 还另有别的名字"的，用户自己那份一个字节都不动。
                 */
                string? sourcePieceDirectory = IsVolumePartFile(task.CurrentPath ?? string.Empty)
                    ? Path.GetDirectoryName(task.CurrentPath ?? string.Empty)
                    : null;

                foreach (string? root in new[] { task.OutputPath, task.ContentDirectoryPath, sourcePieceDirectory })
                {
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    {
                        continue;
                    }

                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(root))
                        {
                            bool completeGroupPiece = _volumeGroupUnits.Values.Any(unit =>
                                !string.IsNullOrWhiteSpace(unit.BaseName)
                                && string.Equals(
                                    FileNameHelper.GetArchiveBaseName(unit.BaseName),
                                    FileNameHelper.GetArchiveBaseName(file),
                                    StringComparison.OrdinalIgnoreCase));

                            if (!completeGroupPiece && !IsVolumePartFile(file))
                            {
                                continue;
                            }

                            if (!completeGroupPiece
                                && (!FileIdentity.TryGetLinkCount(file, out uint links) || links <= 1))
                            {
                                continue;
                            }

                            bool isTaskEntry = SnapshotTaskTable(Tasks).Any(candidate => candidate != null
                                && (string.Equals(
                                        SafePathHelper.GetFullPathSafe(candidate.OriginalPath ?? string.Empty),
                                        SafePathHelper.GetFullPathSafe(file),
                                        StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(
                                        SafePathHelper.GetFullPathSafe(candidate.CurrentPath ?? string.Empty),
                                        SafePathHelper.GetFullPathSafe(file),
                                        StringComparison.OrdinalIgnoreCase)));

                            if (completeGroupPiece && isTaskEntry)
                            {
                                continue;
                            }

                            string name = Path.GetFileName(file);


                            try
                            {
                                File.Delete(file);
                                removed++;

                                AppendLog(
                                    "INFO",
                                    $"{name}：这一份在盘上还有别的名字（同一个文件）—— 收掉产品目录里这个多余的片名，"
                                    + "一个字节都不丢。");
                            }
                            catch (Exception ex)
                            {
                                AppendLog("WARN", $"{name}：多余的片名没能收掉（{ex.Message}）。");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("WARN", $"{task.LogName}：清点多余的片名失败（{ex.Message}）。");
                    }
                }
            }

            return removed;
        }

        private void CleanupConfirmedOrderStagingDirectories()
        {
            foreach (string directory in _confirmedOrderStagingDirectories.ToList())
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    string? parent = Path.GetDirectoryName(directory);

                    if (!string.IsNullOrWhiteSpace(parent)
                        && Directory.Exists(parent)
                        && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                }
                catch
                {
                    // 清不掉只是多一个零字节链接目录；绝不因为清理失败影响结论。
                }
            }

            _confirmedOrderStagingDirectories.Clear();
        }

        /// <summary>
        /// 记下"**这一单**吐出了「<paramref name="baseName"/>」这一组的一片"（去重；⛔ 只记账，不动盘上任何东西）。
        /// 整组由别单解开之后由 <see cref="CollectConsumedSourcePiecesIntoRest"/> 让它按跟班卷收场。
        /// </summary>
        /// <summary>
        /// **这一片是从哪一单自己的源片库里来的** ⇒ 把**那一单**记成"这一组的一片由它供给"
        /// （唯一出口 <see cref="RememberGroupPieceProducer"/>，⛔ 记的是**任务身份**）。
        ///
        /// <para><b>真机 CCCC 2026-10-06 22:50</b>（用户原话：「`111(4)\111.z03` 留着干什么」）：
        /// 那一单永远收不了场 —— 批末是**按路径 / 文件名**回查它"这一片借给谁了"，
        /// 而它的起点已被改写到入口包上、`OriginalPath` 还停在改名前的脏名上
        /// ⇒ 判不出 ⇒ 红线「什么都不做」⇒ 用户的源片一直留着。
        /// 按身份记进既有那份账之后，批末由 <see cref="SettleGroupPieceProducers"/> **按引用**收场，
        /// ⛔ 再也不用事后反推（路径会过期、文件会被删，身份不会）。</para>
        ///
        /// <para>⛔ 只认"账上某条路径 = 这一片"或"与这一片是**同一份文件**"（唯一判据
        /// <see cref="FileIdentity"/>）；⛔ 判不出就什么都不记（照旧走老路，行为与改动前一致）。</para>
        /// </summary>
        private void RememberPieceSupplyingTask(string baseName, string piecePath)
        {
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(piecePath))
            {
                return;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                foreach (string path in EnumerateTaskPaths(candidate).Concat(EnumerateOwnPaths(candidate)))
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (SafePathHelper.PathEquals(path, piecePath) || FileIdentity.IsSamePhysicalFile(path, piecePath))
                    {
                        RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(piecePath), candidate);
                        return;
                    }
                }
            }
        }

        /// <summary>「谁吐出过哪一组的片」这本账的**键快照**（并发安全；见 <c>_groupPieceProducersGate</c>）。</summary>
        private List<string> SnapshotGroupPieceProducerBaseNames()
        {
            lock (_groupPieceProducersGate)
            {
                return _groupPieceProducers.Keys.ToList();
            }
        }

        /// <summary>
        /// 某一组"吐出过片的那几单"的**快照**（拿不到 ⇒ false）。
        /// ⛔ 绝不把活列表交出去：调用方拿到的是副本。
        /// </summary>
        private bool TrySnapshotGroupPieceProducers(
            string baseName,
            out List<(ArchiveTask Task, string PieceName)> producers)
        {
            lock (_groupPieceProducersGate)
            {
                if (_groupPieceProducers.TryGetValue(baseName, out List<(ArchiveTask Task, string PieceName)>? live)
                    && live != null)
                {
                    producers = live.ToList();
                    return true;
                }
            }

            producers = new List<(ArchiveTask Task, string PieceName)>();
            return false;
        }

        /// <summary>整本账的**逐组快照**（收场那一档遍历用；并发安全）。</summary>
        private List<List<(ArchiveTask Task, string PieceName)>> SnapshotGroupPieceProducerLists()
        {
            lock (_groupPieceProducersGate)
            {
                return _groupPieceProducers.Values.Select(list => list.ToList()).ToList();
            }
        }

        /// <summary>批首/批末清账（并发安全）。</summary>
        private void ClearGroupPieceProducers()
        {
            lock (_groupPieceProducersGate)
            {
                _groupPieceProducers.Clear();
            }
        }

        /// <summary>
        /// 记下"**产出方那一份已经落地的片**被接进了这一组"（唯一写入点；读点是整组收场时的
        /// <see cref="PurgeGroupLandedPieces"/>）。⛔ 只记账，不动盘上任何东西。
        ///
        /// <para><b>⛔ 产出方必须按"盘上事实"认，不能按调用方给的那个 <paramref name="owner"/></b>
        /// （真机 2026-10-10 20:09 那次真跑逮到）：`TryGatherGroupPiecesInto`（`:10624` 调）在第二圈
        /// 扫"别人那两棵树"时，把**被重判的那一单**当 owner 传下去（`:9764`），而那一单的
        /// <see cref="ArchiveTask.OutputPath"/> 那一刻还是空的 —— 按 owner 判就会把这一片判成
        /// "不在产出方的落点树里" ⇒ 一个字都不记 ⇒ 收场时没得删，`111(3)\111(3)\111.z02`
        /// 200 MiB 原样留在盘上（用户 20:09 那次复跑就是这么留着的）。
        /// ⇒ 判据改成：**这一片落在哪一单的落点树里**（先看调用方给的那一单，不是它就在全表里按
        /// <see cref="ArchiveTask.OutputPath"/> 找那一单）—— 谁产的按盘上事实认，与谁在调无关。</para>
        ///
        /// <para><b>其余判据也全是运行期事实</b>（2026-10-10 调试器在 <c>TryAdoptUnresolvedVolumePiece</c>
        /// 接片点逐次读出来的）：</para>
        /// <list type="number">
        /// <item><description><paramref name="renamedInPlace"/> == false —— 接片**没有**把原位置那一份搬走；
        /// == true 时（搬的是工作区里那一份）改记**产出方落点里那一份"落地副本"**
        /// （见方法体里那段注释；两者都没有 ⇒ 什么都不记）；</description></item>
        /// <item><description>认得出**是哪一单**的落点树里那一份（认不出 ⇒ 一个字都不记）；</description></item>
        /// <item><description>⛔ 不在**过程物目录**里（<see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/>）
        /// —— 那一档是"整份会被删掉"的地方（工作区 / 其余物），不归这条账管
        /// （调试器实测第三形状：`…\111\111\111\其余物\111.z01` 被接片重新接回组层，它必须被排除）；</description></item>
        /// <item><description>⛔ 不是**任何一单**的源包路径（<see cref="ArchiveTask.CurrentPath"/> /
        /// <see cref="ArchiveTask.OriginalPath"/> / <see cref="ArchiveTask.VolumePaths"/>）—— 红线兜底：
        /// **用户的源文件一个字节都不许被这条账碰到**（25 GB 事故那一档）；</description></item>
        /// <item><description>组基名非空；同一 (组, 路径) 只记一次。</description></item>
        /// </list>
        /// </summary>
        private void RememberAdoptedLandedPiece(
            string piecePath,
            ArchiveTask? owner,
            string groupBaseName,
            bool renamedInPlace)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(groupBaseName))
            {
                return;
            }

            /*
             * ⚠ **「搬」那一档也算产出方那一份**（2026-10-11 实测补上，第 ③ 条断言打红现场）：
             *
             * 原来这里对 `renamedInPlace == true` 直接 return，理由是"搬走那一档原位置已经没有了"——
             * 工作区里那一份确实没了，**但产出方落点里还留着"落地那一份"**（定稿发布时补的那个同名副本，
             * 与它同一份字节的第二个名字）。于是那一个名字谁都不要：
             * `PurgeGroupLandedPieces` 账上没有它 ⇒ 一整批跑完它还在盘上。
             * 实测残留逐字：`…\out-0530d2770fb145bc85e282068d695a9c\111(2)_\111.z01`
             * （守门用例 `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
             * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它；HEAD 之所以绿，是当年那条"先到先得"兜底
             * 让定稿那一档压根没补这个副本 —— 兜底已按用户口径删除 ⇒ 这里必须自己认领）。
             *
             * ⛔ 判不出产出方 / 落点里没有那一个名字 ⇒ 与改动前逐字相同（什么都不记）。
             */
            if (renamedInPlace)
            {
                string landedCopy = FindLandedCopyBesideProducer(piecePath, owner);

                if (landedCopy.Length == 0)
                {
                    return;
                }

                piecePath = landedCopy;
            }

            if (ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath))
            {
                return;
            }

            ArchiveTask? producer = ResolveLandedPieceProducer(piecePath, owner);

            if (producer == null)
            {
                // 判不出这一片是哪一单产出的（谁的落点树里都没有它）⇒ 什么都不记。
                return;
            }

            string outputRoot = SafePathHelper.GetFullPathSafe(producer.OutputPath);

            if (outputRoot.Length == 0)
            {
                outputRoot = SafePathHelper.GetFullPathSafe(producer.ContentDirectoryPath);
            }

            if (outputRoot.Length == 0 || !ArchivePathGuard.IsInsideRoot(outputRoot, piecePath, out _))
            {
                return;
            }

            if (IsAnyTaskSourcePath(piecePath))
            {
                // 这一份还是**某一单自己的源**（用户给的包 / 用户目录里的片）⇒ 不归这条账管。
                return;
            }

            foreach ((ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot) item
                     in _adoptedLandedPieces)
            {
                if (string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.PiecePath, piecePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _adoptedLandedPieces.Add((producer, groupBaseName, piecePath, outputRoot));
        }

        /// <summary>
        /// **这个路径是不是任何一单自己的源**（用户给的包 / 用户目录里的片）—— 唯一出口。
        ///
        /// <para>红线：**用户的源文件一个字节都不许被"产出方那一份 / 已经落地的片"这两条账碰到**
        /// （25 GB 事故那一档）。所以凡是要按这两条账删/认领一个路径之前，先问这一句；
        /// 判"是源" ⇒ 调用方什么都不做。</para>
        /// </summary>
        private bool IsAnyTaskSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                foreach (string sourcePath in EnumerateTaskPaths(task))
                {
                    if (!string.IsNullOrWhiteSpace(sourcePath) && SafePathHelper.PathEquals(sourcePath, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// **这个路径是不是某一单的「用户最初导入路径」**（只读 `OriginalPath`，⛔ 不看 `VolumePaths` /
        /// `CurrentPath`）。给"按试开顺序收掉旧规范名"用：旧规范名通常是组任务 `VolumePaths` 里的占位名，
        /// 但**不是**用户手里的那份原包；只有 `OriginalPath` 才是用户原件。
        /// </summary>
        private bool IsAnyTaskOriginalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && SafePathHelper.PathEquals(task.OriginalPath, path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **同一个物理文件是不是某一单用户最初导入的那一份**（改名/硬链接后仍认得出）。
        /// `28282828` 的源 `.001` 在盘上已经被清洗成标准名，路径与 `OriginalPath` 不同，
        /// 但 inode 相同 ⇒ 逐层回收必须按身份保护它。
        /// </summary>
        private bool IsAnyTaskOriginalIdentity(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && File.Exists(task.OriginalPath)
                    && FileIdentity.IsSamePhysicalFile(task.OriginalPath, path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **这个路径是不是「用户导入的原包」自己** —— 第二大步收卷那一档的**窄版**红线
        /// （⛔ 与上面那把宽尺子并存，⛔ 宽尺子的任何调用点一个字都不改）。
        ///
        /// <para><b>为什么这一档不能用宽尺子</b>（2026-10-11 调试器取到的运行期事实）：
        /// 宽尺子问"是不是任何一单的路径"，而第二大步要收的**恰恰是这一组自己的那几片** ——
        /// 它们正是入口那一单（续解任务）自己的路径。断点停在
        /// `HarvestOneSolvedGroupVolumes` 的删除循环里，EEE 第 2 轮边界那一趟逐字：
        /// `directory=…\in\111\111\111`、`referenceName=111.parts1.racr`、`members.Count=2`
        /// （`…\111\111\111\111.part1.rar` / `111.part2.rar`）、`consumer=111.part1.rar`、
        /// `mode=Delete`，而求值 `this.IsAnyTaskSourcePath(path)` = **true** ⇒ 两份都被那句
        /// `continue` 跳过（静默）⇒ 盘上一个字节没少（判据 ③ 的 `还有 5 个` 就是它）。</para>
        ///
        /// <para>判据取既有事实位 <see cref="ArchiveTask.IsContinuationTask"/> —— 项目自己的定义逐字：
        /// 「续解任务的"源"是上一层交出来的内层包 —— 对用户来说它是过程物」
        /// （同一句话也是 `ProcessArtifactLayout` 里"续解出来的内层包不参与源包处理"那条的唯一出口）。
        /// ⇒ **非续解任务的那几单，它们的路径就是用户给的原包**（含同组被收进
        /// <see cref="ArchiveTask.VolumePaths"/> 的那几卷）一个字节不碰；续解任务自己的路径
        /// **不是**用户的原包 ⇒ 不在这道红线的保护范围内。</para>
        /// </summary>
        private bool IsUserImportedSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null || task.IsContinuationTask)
                {
                    continue;
                }

                foreach (string sourcePath in EnumerateTaskPaths(task))
                {
                    if (!string.IsNullOrWhiteSpace(sourcePath) && SafePathHelper.PathEquals(sourcePath, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// **这一片落在哪一单的落点树里**（唯一出口；⛔ 与"谁在调"无关）：
        /// 先看调用方给的那一单，不是它就在全表里按 <see cref="ArchiveTask.OutputPath"/> /
        /// <see cref="ArchiveTask.ContentDirectoryPath"/> 找 —— 判不出 ⇒ <c>null</c>
        /// （调用方一个字都不记）。
        /// </summary>
        private ArchiveTask? ResolveLandedPieceProducer(string piecePath, ArchiveTask? owner)
        {
            if (IsInsideOwnLandingTree(owner, piecePath))
            {
                return owner;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (!ReferenceEquals(candidate, owner) && IsInsideOwnLandingTree(candidate, piecePath))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>这一份在不在这一单**自己那条链的落点树**里（输出目录，或内容物那一层）。</summary>
        private static bool IsInsideOwnLandingTree(ArchiveTask? task, string piecePath)
        {
            if (task == null || string.IsNullOrWhiteSpace(piecePath))
            {
                return false;
            }

            foreach (string root in new[] { task.OutputPath, task.ContentDirectoryPath })
            {
                if (!string.IsNullOrWhiteSpace(root)
                    && ArchivePathGuard.IsInsideRoot(SafePathHelper.GetFullPathSafe(root), piecePath, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 盘上某个文件**改了名（同盘移动）**之后，把**全表每一单**账上指向它的路径都改过来。
        ///
        /// <para>⛔ 为什么是全表而不是"改产出它的那一单"：**同一份字节会被多处账引用**
        /// （真机 CCCC：入口包 `111.zip` 那一片同时被"吐出它的那一单"与"正等这一组的另一单"记着）。
        /// 只改一处 ⇒ 别的任务再去按旧名字找 / 解就落空 —— 上一轮"只同步 owner"那一版就是这样
        /// 把真机形状两条 E2E 打红的（收片之后整组凑不齐 / 产物找不到）。</para>
        ///
        /// <para>内部仍只转调**唯一出口** <see cref="TaskPathSync.ApplySingleMove"/>（它同时改
        /// <c>CurrentPath</c> 与 <c>VolumePaths</c>，AGENTS.md §38 那条踩过两次的坑）。</para>
        /// </summary>
        private void SyncEveryTaskPathAfterMove(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                TaskPathSync.ApplySingleMove(task, from, to);

                /*
                 * ⛔ **快照也要重拍**（2026-10-06 第三处候选，实测确认）：任务的源文件快照
                 * （不变量 11 用它对"源文件有没有被改过"）是按**当时的路径**记的。
                 * 就地改名之后账上的路径改了、快照还指着旧名 ⇒ 下一次比对会判「源文件已变化」
                 * ⇒ 整组被拦下、产物出不来（真机形状 E2E 的 `Assert.NotNull()` 就是这样红的）。
                 * 改名不是"源文件被改动"（内容一个字节没动，AGENTS.md 不变量 11 的边界里写着
                 * "改名 / 源包搬进其余物不算变化"）⇒ 这里按新路径重拍一次，让账与盘一致。
                 */
                if (SafePathHelper.PathEquals(task.CurrentPath, to)
                    || task.VolumePaths.Any(path => SafePathHelper.PathEquals(path, to)))
                {
                    task.CaptureSourceSnapshot();
                }
            }

            /*
             * ⚠ **账上的键也要跟着搬**（2026-10-06 实测：这是"第三处读旧名"的候选之一）：
             * `_consumedVolumeSources` 的**键就是那一份源片的路径**（`RememberOneConsumedVolumeSource`
             * 与 `RememberGroupPiecesConsumedBy` 两处写入）。源片就地改名之后键还指着旧名字 ⇒
             * 批末重判再问"这一单那一片在不在借片账里"必然问不出来 ⇒ 整组凑不齐、产物出不来
             * （真机形状 E2E 的 `Assert.NotNull()` 就是这样红的）。
             * ⇒ 这里把键一起重映射（值 = 消费方名字，不动）。
             */
            if (_consumedVolumeSources.Count > 0)
            {
                string? consumer = null;

                foreach ((string path, string owner) in _consumedVolumeSources.ToList())
                {
                    if (SafePathHelper.PathEquals(path, from))
                    {
                        consumer = owner;
                        _consumedVolumeSources.Remove(path);
                    }
                }

                if (consumer != null)
                {
                    _consumedVolumeSources[to] = consumer;
                }
            }
        }

        /// <summary>
        /// 「判不出 ⇒ 什么都不做」时把**判据输入**写进日志（用户 2026-10-10 目标单③）。
        ///
        /// <para>为什么必须有它：这些分支原先只有**结论**（「接不进去」），没有**判据吃进去的东西** ——
        /// 拿一份用户日志看不出"这一片被判成哪一族、第几卷"，也看不出"规范名/目标层算成了什么"。
        /// 真机 FFFF 那一轮就是卡在这里：只知道没接上，不知道是名字认不出还是入口层推不出来。</para>
        ///
        /// <para>⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 它一处判据都不改。</para>
        /// </summary>
        /// <summary>
        /// **接片没接上**这句话本身（唯一出口）—— 与 `IsTargetSlotAlreadyThisPiece` **同一把尺子**。
        ///
        /// <para>为什么单独收一个出口（用户 2026-10-12，大测试 `run-8888` 逐字）：
        /// 那一行写着 `目标名已经被别的文件占着（⛔ 绝不覆盖）`，而**目标名就是这一片自己的路径**
        /// —— 老写法只用 `File.Exists(target)` 判，于是把"这一片早就在位（或它已经被接过一次、
        /// 原位置那份已经不在了）"说成了"被别人占着"，下一轮排查顺着这句去找一个根本不存在的"别人"。</para>
        ///
        /// <para>三种事实分开说（⛔ 判据与行为一个字不改，只把话说对）：
        /// ① 目标位上就是**这一片自己** ⇒ 那根本不该走到"没接上"（`alreadyInPlace` 那一支已经先接走）；
        /// ② 这一片**已经不在原位置**（被前一次接片搬走/改名）⇒ 判不出目标位上那份是谁的，照实说；
        /// ③ 其余（目标位上确实站着**别人**的文件）⇒ 照旧"绝不覆盖"。</para>
        /// </summary>
        private static string DescribeAdoptRejection(string piecePath, string target)
        {
            if (!File.Exists(target))
            {
                return "硬链接 / 同盘改名都没成功（同名已在、或跨了卷）";
            }

            if (IsTargetSlotAlreadyThisPiece(piecePath, target))
            {
                return "目标位上就是这一片自己（应当按「已经在位」接走）";
            }

            return File.Exists(piecePath)
                ? "目标名已经被别的文件占着（⛔ 绝不覆盖）"
                : "这一片已经不在原来的位置了（判不出目标位上那份与它是不是同一份，⛔ 绝不覆盖）";
        }

        private void LogAdoptCriterionInputs(
            string piecePath,
            string reason,
            string canonical,
            string targetDir,
            string target)
        {
            string name = FileNameHelper.GetFileName(piecePath);
            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);
            string? first = VolumeGroupDetector.TryGetFirstVolumeName(name);
            int? index = VolumeGroupDetector.TryGetVolumeIndex(name);

            /*
             * ⚠ **同名位上到底是什么**：必须写出来（2026-10-11 环台实测逮到的假拒绝）——
             * 同一秒的日志里出现过自相矛盾的两句：这句说「目标名已经被别的文件占着（⛔ 绝不覆盖）」，
             * 而同一单的定稿那一句说「已经在目标位上、而且是「同一份文件」（接片时建的硬链接）」。
             * 光看"被占了"分不出是**别人**的文件（该拒）还是**自己这一片**早就链接在那儿了（该当成功），
             * 下一步无从下手 ⇒ 这里直接把物理同一性写进判据输入（⛔ 只写事实，不改判据）。
             */
            string sameNameSlot = target.Length == 0
                ? "（没有目标名可看）"
                : !File.Exists(target)
                    ? "没有同名文件"
                    : FileIdentity.IsSamePhysicalFile(piecePath, target)
                        ? "同一份文件（就是这一片自己，只是名字已经在位）"
                        : "另一个文件（⛔ 绝不覆盖）";

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsAdoptFormat,
                    name,
                    reason,
                    name,
                    baseName,
                    string.IsNullOrWhiteSpace(first) ? "判不出" : first,
                    index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出",
                    canonical.Length == 0 ? "判不出" : canonical,
                    targetDir.Length == 0 ? "判不出（推不出这一组该拼到哪一层）" : targetDir,
                    target.Length == 0 ? "判不出" : target,
                    sameNameSlot));
        }

        /// <summary>
        /// **用户自己那一份现在在哪**（改名之后也要找得到）：他最初放的那个目录里、
        /// 与起点（这一组的入口包）**同族**、且**包基名与最初导入那一份相同**的那个文件。
        ///
        /// <para>为什么要这么找：`OriginalPath` 是"最初导入路径"（改名不会跟着变，真机实测），
        /// 而账上那几卷可能散在别的目录（别的单的片）⇒ 只有"同目录 + 同族 + 同基名"这一条
        /// 能把"用户自己那一片"认出来（⛔ 只用于**列表显示**，不参与任何搬运 / 删除判据）。</para>
        /// <para>判不出 ⇒ 空串（调用方那一行显示照旧，⛔ 不猜）。</para>
        /// </summary>
        private static string FindUserOwnFileBesideItsDirectory(ArchiveTask task, string startPointPath)
        {
            string original = task.OriginalPath ?? string.Empty;
            string directory = Path.GetDirectoryName(original) ?? string.Empty;

            if (directory.Length == 0 || string.IsNullOrWhiteSpace(startPointPath) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            string startName = FileNameHelper.GetFileName(startPointPath);
            string originalBaseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(original));

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (string.Equals(name, startName, StringComparison.OrdinalIgnoreCase)
                        || !VolumeGroupDetector.BelongsToSameGroup(name, startName)
                        || !string.Equals(
                            FileNameHelper.GetArchiveBaseName(name),
                            originalBaseName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return file;
                }
            }
            catch
            {
                // 读不动 ⇒ 判不出。
            }

            return string.Empty;
        }

        /// <summary>
        /// **这一片该"搬"还是该"只建链接"** —— 判据只有两条，都是路径事实：
        /// ① 落在**会被整份删掉的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 倾向搬
        ///    （留一份在那种目录里，等于接了一个马上就要消失的名字）；
        /// ② ⛔ **但它正躺在"本单自己的暂存目录"里 ⇒ 只建链接、绝不搬走**：那是收尾链
        ///    （结果校验 / 定稿）要读的那一份，搬走 = 把自己这一单唯一的产物拿走。
        ///
        /// <para><b>真机 CCCC 2026-10-06 18:13</b>（用户原话：「出现了解压失败的文字，明明成功了」）：
        /// 某一单（`111(2)_.zip`）自己解出来的那一片先被"接片"那一档从**它的暂存目录**里搬走，
        /// 收尾链紧接着读暂存目录读到空 ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示
        /// 「解压失败」+ 源包一个字节都不处理。触发它是一段**竞态**：同一批另一单（`111.rar`）恰好在
        /// 两次接片调用之间把入口包 `111.zip` 落到了落点层 ⇒ 第一次接片还不认识那一层（什么都不做）、
        /// 第二次已经认识 ⇒ 搬走的就是暂存目录里那一份。</para>
        ///
        /// <para>⛔ 这条判据**不看顺序、不看时序**：暂存目录里的那一份永远不搬
        /// （它随任务收尾一起清掉，落点那一份是硬链接、零字节、照样在）。</para>
        ///
        /// <para><b>为什么还要第 ③ 条</b>（同一类竞态的另一半，2026-10-11 定案到行）：收拢那一档
        /// （<c>TryGatherGroupPiecesInto</c> → <c>TryAdoptUnresolvedVolumePiece</c>）传进来的
        /// <paramref name="ownerStageDirectory"/> 是**消费方**（要解开这一组的那一单）的暂存目录，
        /// 而被收的那一片常常属于**另一条还在跑的链**（躺在它自己的
        /// <c>…\recursive\&lt;那一单&gt;_&lt;ts&gt;_&lt;hash&gt;\layer-000\output\</c> 里）。
        /// 按老判据它是"过程物目录里的东西"、又不在**调用方**的暂存目录里 ⇒ 判成"搬"
        /// ⇒ 那条链的定稿/校验读到空 ⇒ **假「解压失败」**（全量并行负载下实测：两单落
        /// <c>Failed</c>、整组凑不齐；同一份夹具单跑 6/6 全绿）。
        /// ⛔ 判不出（一条还没收场的链都拿不到）⇒ 照旧按老口径办。</para>
        /// </summary>
        internal static bool ShouldMovePieceIntoGroup(
            string piecePath,
            string ownerStageDirectory,
            IEnumerable<string>? liveChainWorkDirectories = null)
        {
            if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath))
            {
                return false;
            }

            // ② 本单自己的暂存目录：只建链接。
            if (ownerStageDirectory.Length > 0
                && ArchivePathGuard.IsInsideRoot(ownerStageDirectory, piecePath, out _))
            {
                return false;
            }

            /*
             * ③ 别的**还没收场**那条链的工作树：也只建链接。
             * 硬链接是实名的第二份名字 —— 那条链随后删掉自己的过程物目录，不会连带把这一份弄没；
             * 反过来"搬"就是把它这条链唯一的产物拿走（正是上面那条红线的形状）。
             */
            if (liveChainWorkDirectories != null)
            {
                foreach (string directory in liveChainWorkDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(directory)
                        && ArchivePathGuard.IsInsideRoot(directory, piecePath, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// **还没收场的那几条链的工作树**（收拢那一档用来判"这一片是不是别人的在制品"，
        /// 见 <see cref="ShouldMovePieceIntoGroup"/> 第 ③ 条）。
        ///
        /// <para>⛔ 只看**机器终态**：<see cref="TaskOutcome.Pending"/> = 还没落结论（含"正在跑"
        /// 与"停在需要用户决定的层"）—— 那些链的过程物目录里那一片**还不能搬**。
        /// 判不出（算不出工作树 / 拿不到任务表）⇒ 空表，调用方照旧按老口径办，⛔ 不放宽。</para>
        /// </summary>
        private List<string> CollectLiveChainWorkDirectories()
        {
            var directories = new List<string>();

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null || task.Outcome != TaskOutcome.Pending)
                {
                    continue;
                }

                try
                {
                    string directory = _pathService.BuildTaskWorkDirectory(task);

                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        directories.Add(directory);
                    }
                }
                catch
                {
                    // 算不出来就不算它（少一条保护，⛔ 不改任何结论）。
                }
            }

            return directories;
        }

        /// <summary>
        /// 目标位上已经有同名文件时：那到底**是不是这一片自己**（同一份物理文件）。
        ///
        /// <para><b>为什么必须有它</b>（2026-10-11 环台实测逮到的假拒绝）：接片那一档原来只判
        /// `File.Exists(target)` 就返回"接不进"，而目标位上那一份**恰恰就是这一片自己**
        /// （早前已经硬链接过去、或它自己就是从那儿来的）——
        /// 于是"接片成功"被写成 WARN 假警报，**更要命的是后面那套记账整段被跳过**
        /// （`RememberGroupPieceProducer` / `RememberPieceSupplyingTask` / `RememberAdoptedLandedPiece`）
        /// ⇒ 产出方那一份"已经落地的片"没人收（用户 2026-10-10 点名要收的那一条）。
        /// 实测对照（同一份样本、同一个环台）：修前 6 条假拒绝 / 0 条"早就在位"；修后 0 条假拒绝 / 11 条"早就在位"。</para>
        ///
        /// <para>判据只读事实：**同一份物理文件**（<see cref="FileIdentity.IsSamePhysicalFile"/>）。
        /// true ⇒ 接片算成功（⛔ 一个字节都不搬、不覆盖）；false ⇒ 别人的文件 ⇒ 照旧判"接不进"。</para>
        /// </summary>
        internal static bool IsTargetSlotAlreadyThisPiece(string piecePath, string target)
            => !string.IsNullOrWhiteSpace(target)
               && File.Exists(target)
               && FileIdentity.IsSamePhysicalFile(piecePath, target);

        /// <summary>本单自己的暂存目录（算不出来 / 判不出 ⇒ 空串 = 调用方落回老口径"按过程物目录那一档"）。</summary>
        private string ResolveOwnerStageDirectory(ArchiveTask? owner)
        {
            if (owner == null)
            {
                return string.Empty;
            }

            try
            {
                return _pathService.BuildTaskStageDirectory(owner);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ResolveOwnerChainDirectory(ArchiveTask? owner)
        {
            if (owner == null || string.IsNullOrWhiteSpace(owner.CurrentPath))
            {
                return string.Empty;
            }

            /*
             * ⚠ 2026-10-09 真机 `EEEE`（18:48 那一趟）**实测的根因**：这一组的"家"被**已经搬走的源包**带偏了。
             *
             * 现场（逐条有出处）：行 161 先把产出方那一单的源包搬进其余物
             * （`111.rar → …\111\111\其余物\111.rar`）⇒ 下面那两条路**都读 `owner.CurrentPath`**
             * （`ResolveOutputPlacement` 内部读它；退路 `Path.GetDirectoryName(owner.CurrentPath)` 也读它）
             * ⇒ 算出来的"这一组的家"变成 **`…\111\111\其余物\111`** —— 与真机 6 条收片的 `targetDir`
             * **逐字相同**（行 164/171/177/178/186）。片全被收进"其余物"里 ⇒ 组永远凑不齐，
             * 批末「其余物 = 彻底删除」再把它们**连同入口包一起删掉**（行 296）⇒ 盘上 `111\111\` 空掉。
             *
             * ⇒ **先用产出方当初记下的"实际输出目录"**（`owner.OutputPath`）：那是"产物到底在哪"的
             * **唯一权威来源**（写入点 `ExtractSingleTaskAsync` 里 `task.OutputPath = outputPath;`，
             * 那段注释自己写着"解压后校验、结果归集、清理源包、界面那一列、以及一键处理的续解全都以它为准"）。
             * ⛔ 不再用"会动的 `CurrentPath`"现算一遍 —— 源包一被搬走，那个算式就指向其余物。
             */
            if (!string.IsNullOrWhiteSpace(owner.OutputPath))
            {
                return owner.OutputPath;
            }

            try
            {
                OutputPlacementResult placement = _pathService.ResolveOutputPlacement(
                    owner,
                    BuildExtractOptions(tryExtractUnknownFormat: false));

                if (placement.Success && !string.IsNullOrWhiteSpace(placement.DestinationDirectory))
                {
                    return placement.DestinationDirectory;
                }

                /*
                 * ⛔ **这一刻 `task.OutputPath` 往往还没落**（真机 2026-10-06 当场逮到：这一档"搬了 0 份"，
                 * 就是因为这里返回空 ⇒ 目标层算不出来 ⇒ 整档静默不做）。⇒ 按**同一套落点公式现算一份**：
                 * 未指定位置那一档 = `源包所在目录 + 包名文件夹`（`111\111.rar` ⇒ `111\111\`），
                 * 与 <see cref="OutputPlacement.ResolveDestinationDirectory"/> 的公式逐字同口径
                 * （用包基名，⛔ 不是"剥后缀再加一层"的自创写法）。
                 */
                string? ownerDirectory = Path.GetDirectoryName(owner.CurrentPath);

                if (string.IsNullOrWhiteSpace(ownerDirectory))
                {
                    return string.Empty;
                }

                string packageName = FileNameHelper.GetArchiveBaseName(
                    FileNameHelper.GetFileName(owner.CurrentPath));

                return packageName.Length > 0
                    ? Path.Combine(ownerDirectory, packageName)
                    : ownerDirectory;
            }
            catch (Exception ex)
            {
                // 加法：算不出落点 ⇒ 退回既有口径（这一片落在哪儿与改动前一个字都不差）。
                AppendLog("WARN", $"跨链收卷算不出这一片的落点（{ex.Message}）。");
            }

            return string.Empty;
        }

    }
}