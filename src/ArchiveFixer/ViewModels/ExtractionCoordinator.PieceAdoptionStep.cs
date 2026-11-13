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
        /// <summary>
        /// 用户源文件路径集合：任务原始路径、当前路径、组的每一卷、链根路径。
        /// 别名清理**只许删这个集合之外的名字**。
        /// </summary>
        internal static HashSet<string> BuildUserSourcePathSet(IEnumerable<ArchiveTask>? tasks)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                Add(task.OriginalPath);
                Add(task.CurrentPath);
                Add(task.RootSourcePath);

                foreach (string volume in task.VolumePaths)
                {
                    Add(volume);
                }
            }

            return paths;

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    paths.Add(Path.GetFullPath(path));
                }
                catch
                {
                    // 路径坏掉时不给集合添乱；清理侧还有“判不出就不删”兜底。
                }
            }
        }

        internal void RememberUserSourcePaths(IEnumerable<ArchiveTask>? tasks)
        {
            // 只增不减：续解轮次、后续补跑都可能再次经过别名清理，
            // 不能因为新一轮只带来部分任务就把前一轮的用户源路径忘掉。
            _userSourcePaths.UnionWith(BuildUserSourcePathSet(tasks));
        }

        internal int PurgeRedundantPieceAliases()
        {
            int removed = 0;
            var userSourcePaths = new HashSet<string>(_userSourcePaths, StringComparer.OrdinalIgnoreCase);
            userSourcePaths.UnionWith(BuildUserSourcePathSet(SnapshotTaskTable(Tasks)));

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
                string? sourcePieceDirectory = StageFilePredicates.IsVolumePartFile(task.CurrentPath ?? string.Empty)
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

                            if (!completeGroupPiece && !StageFilePredicates.IsVolumePartFile(file))
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

                            /*
                             * 用户源路径是红线：即使它在临时组入口里还有硬链接，
                             * 也绝不许删掉用户这个名字。2026-10-29 `444` 就是
                             * `nali.pdf.001/.002` 在这里被当成“多余片名”删掉的。
                             */
                            string fullFile;

                            try
                            {
                                fullFile = Path.GetFullPath(file);
                            }
                            catch
                            {
                                continue;
                            }

                            if (userSourcePaths.Contains(fullFile))
                            {
                                continue;
                            }

                            string name = Path.GetFileName(file);


                            try
                            {
                                FileOperations.Delete(file);
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
        internal List<List<(ArchiveTask Task, string PieceName)>> SnapshotGroupPieceProducerLists()
        {
            lock (_groupPieceProducersGate)
            {
                return _groupPieceProducers.Values.Select(list => list.ToList()).ToList();
            }
        }

        /// <summary>批首/批末清账（并发安全）。</summary>
        internal void ClearGroupPieceProducers()
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
        internal void RememberAdoptedLandedPiece(
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
        internal bool IsAnyTaskSourcePath(string path)
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
        internal bool IsUserImportedSourcePath(string path)
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


        private ExtractionPieceHandoverRules? _pieceHandoverRules;
        private ExtractionPieceHandoverRules PieceHandoverRules =>
            _pieceHandoverRules ??= new ExtractionPieceHandoverRules(this);

        internal static string FindUserOwnFileBesideItsDirectory(ArchiveTask task, string startPointPath) =>
            ExtractionPieceHandoverRules.FindUserOwnFileBesideItsDirectory(task, startPointPath);

        internal static bool ShouldMovePieceIntoGroup(
            string piecePath,
            string ownerStageDirectory,
            IEnumerable<string>? liveChainWorkDirectories = null) =>
            ExtractionPieceHandoverRules.ShouldMovePieceIntoGroup(piecePath, ownerStageDirectory, liveChainWorkDirectories);

        internal static bool IsTargetSlotAlreadyThisPiece(string piecePath, string target) =>
            ExtractionPieceHandoverRules.IsTargetSlotAlreadyThisPiece(piecePath, target);

        private string ResolveOwnerChainDirectory(ArchiveTask? owner) =>
            PieceHandoverRules.ResolveOwnerChainDirectory(owner);

    }
}
