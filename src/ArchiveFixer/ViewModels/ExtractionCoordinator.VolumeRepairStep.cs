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
    /// <summary>缺卷补救与批前收卷（第 8 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 「我手动指定缺失卷所在目录」——WinRAR 参考 §2 G 组 / §3 第 2 条的采纳项。
        ///
        /// <para>
        /// 用户痛点很实在：真实分卷包常常"主体在这一层、后几卷在隔壁文件夹或另一个盘"，
        /// 而现在的行为是直接拒绝启动并报"缺 A、B"，用户只能自己去找、去挪，毫无下手的地方。
        /// </para>
        /// <para>
        /// ⛔ <b>不变量 7 一个字都不放松</b>：这里只做"帮你把卷找齐"，**绝不允许缺卷启动**。
        /// 重新归组之后仍然缺 → 返回 false，调用方照旧落成"分卷缺失"并拒绝开始。
        /// </para>
        /// <para>
        /// 归组**复用既有实现**（<see cref="VolumeGroupDetector.Group"/> +
        /// <see cref="VolumeGroupingService.ApplyGroupInfo"/>），不另写一套分卷命名规则 ——
        /// 那种规则一旦分叉，就会出现"界面上说齐了、7z 却说缺卷"。
        /// </para>
        /// </summary>
        /// <returns>true = 这一组现在齐了（可以开始）；false = 仍然不能开始。</returns>
        private async Task<bool> TryRepairMissingVolumesAsync(ArchiveTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_volumeRepairAsked)
            {
                if (!_volumeRepairAsked.Add(task))
                {
                    // 同一个任务这一批已经问过一次了：再问就是骚扰（用户已经说了"没有"或"找过还是不够"）。
                    return false;
                }
            }

            string missing = task.MissingVolumeNames.Count > 0
                ? string.Join("、", task.MissingVolumeNames)
                : "（引擎没给出具体缺哪几个）";

            string question =
                $"「{task.FileName}」的分卷不完整，现在缺：{missing}。{Environment.NewLine}{Environment.NewLine}" +
                $"分卷包里每一卷都是必需的数据片，缺一卷就一定解不开 —— 所以程序不会在缺卷时开始。" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "如果你的卷散在别的文件夹（或者另一个盘），可以现在指定那个目录：" +
                "程序会在那里找齐这一组，找齐了就继续；找不齐仍然不会开始。";

            // 确认框走既有实现：无 UI 宿主时它返回 false（保守档），绝不弹窗、绝不死等。
            bool wantPick = await DispatcherSafeConfirmAsync(question);

            if (!wantPick)
            {
                AppendLog("INFO", $"{task.LogName}：没有手动指定缺失卷所在目录，按缺卷处理（不启动）。");
                return false;
            }

            string directory = ShowFolderBrowserOnUiThread();

            if (string.IsNullOrWhiteSpace(directory))
            {
                AppendLog("INFO", $"{task.LogName}：没有选择目录，按缺卷处理（不启动）。");
                return false;
            }

            /*
             * 目录枚举（可能几万个文件）放后台：它跑在解压管线里，而这条路径的 await 续体在 UI 上下文。
             * 只在**指定的那一个目录**里找，不递归、不扫全盘（AGENTS.md §8 隐私红线：不读工作区以外的个人目录）。
             */
            List<string> volumeFiles = await Task.Run(
                () => CollectVolumeFilesInDirectory(directory),
                cancellationToken);

            AppendLog(
                "INFO",
                $"{task.LogName}：在 {directory} 里找到 {volumeFiles.Count} 个分卷文件，正在重新归组判定。");

            VolumeGroup? group = RebuildVolumeGroup(task, volumeFiles);

            if (group == null || !group.IsComplete)
            {
                string stillMissing = group != null && group.MissingVolumeNames.Count > 0
                    ? string.Join("、", group.MissingVolumeNames)
                    : missing;

                // 如实说清"找过哪儿、还是缺什么"——用户下一步就是拿着这句话去别处找。
                string summary = group == null
                    ? "在那个目录里没找到属于这一组的分卷（或目录读不了）"
                    : group.MissingVolumeNames.Count > 0
                        ? $"重新归组后仍缺 {stillMissing}"
                        : "重新归组后仍然不完整";

                task.Status = StatusText.VolumeMissing;
                task.ErrorMessage =
                    $"分卷仍然不完整：{summary}。已找过：{directory}（{volumeFiles.Count} 个候选分卷）。";

                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage} 仍然不启动（不变量 7）。");
                return false;
            }

            /*
             * 找齐了：把任务切到**这一组所在的那个目录**。
             *
             * 为什么必须切：分卷组必须整体在一处才能解（7-Zip 从第一卷顺着往后读），
             * 组里各卷分散在两个目录时，光"知道它们在哪儿"是解不开的。
             * 这里挑中的那个组，它的每一卷都真实存在于同一个目录里 —— 要么全在用户指定的目录，
             * 要么全在原目录（那种情况本来就不缺卷，走不到这里）。
             *
             * 切路径是安全的：CurrentPath 是"现在拿哪个文件去解压"，而 OriginalPath 仍然记着用户最初给的那个；
             * 改名、清理、报告都以 CurrentPath 为准，所以后续一切照常。
             */
            string firstVolume = group.FirstVolumePath;
            string previousPath = task.CurrentPath;

            if (!File.Exists(firstVolume))
            {
                AppendLog("ERROR", $"{task.LogName}：归组给出的第一卷不存在（{firstVolume}），按缺卷处理（不启动）。");
                return false;
            }

            new VolumeGroupingService().ApplyGroupInfo(task, group);

            task.CurrentPath = firstVolume;
            task.FileName = Path.GetFileName(firstVolume);
            task.CurrentExtension = Path.GetExtension(firstVolume);

            AppendLog(
                "INFO",
                $"{task.LogName}：分卷已找齐（{task.VolumeCount} 卷，起点 {firstVolume}）" +
                (string.Equals(previousPath, firstVolume, StringComparison.OrdinalIgnoreCase)
                    ? "，开始解压。"
                    : $"，起点由 {previousPath} 改为它（OriginalPath 仍指向你最初给的那个文件），开始解压。"));

            return true;
        }

        /// <summary>
        /// 在**指定的一个目录**里列出候选分卷文件（顶层，不递归）。
        ///
        /// 只按命名筛（能不能归组由 <see cref="VolumeGroupDetector"/> 说了算），
        /// 不读内容、不碰别的目录。
        /// </summary>
        private static List<string> CollectVolumeFilesInDirectory(string directory)
        {
            var found = new List<string>();

            try
            {
                if (!Directory.Exists(directory))
                {
                    return found;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);

                    if (VolumeGroupDetector.TryGetVolumeIndex(fileName).HasValue ||
                        VolumeGroupDetector.TryGetFirstVolumeName(fileName) != null)
                    {
                        found.Add(file);
                    }
                }
            }
            catch
            {
                // 目录读不了：返回空集合，调用方按"没找到"如实报告。
            }

            return found;
        }

        /// <summary>
        /// 把"任务现有各卷 + 用户指定目录里找到的卷"合成一份候选去重新归组，返回**可能已经补齐**的那一组。
        ///
        /// 判据：优先按原来的 <see cref="ArchiveTask.VolumeGroupKey"/> 找；找不到就退回
        /// "哪一组包含本任务的第一卷"。两条都对不上就返回 null（如实说"没找到属于这一组的"）。
        /// </summary>
        private static VolumeGroup? RebuildVolumeGroup(ArchiveTask task, IEnumerable<string> extraFiles)
        {
            var candidates = new List<VolumeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    return;
                }

                long size = -1;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 量不出大小不影响归组（大小只用于"同名不同组"的复核）。
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            foreach (string path in task.VolumePaths)
            {
                AddCandidate(path);
            }

            AddCandidate(task.CurrentPath);

            foreach (string path in extraFiles ?? Enumerable.Empty<string>())
            {
                AddCandidate(path);
            }

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(candidates);

            if (groups.Count == 0)
            {
                return null;
            }

            string currentPath = SafePathHelper.GetFullPathSafe(task.CurrentPath);

            VolumeGroup? byKey = groups.FirstOrDefault(
                g => !string.IsNullOrWhiteSpace(task.VolumeGroupKey) &&
                     string.Equals(g.GroupKey, task.VolumeGroupKey, StringComparison.OrdinalIgnoreCase));

            if (byKey != null)
            {
                return byKey;
            }

            return groups.FirstOrDefault(g => g.Volumes.Any(
                v => string.Equals(SafePathHelper.GetFullPathSafe(v.Path), currentPath, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// **开工前跨目录收卷**（用户 2026-10-05 原话：「各分卷在不同的目录，你就将其全部移动到头文件 .001
        /// 同级目录里面去」）。
        ///
        /// <para><b>判据与执行体都是既有出口</b>（§9.5）：<see cref="VolumeNameRepair.ResolveCrossLayerVolumeGather"/>
        /// 出纯计划、<see cref="VolumeNameRepair.TryApply"/> 动手（全成或全不成、绝不覆盖、失败倒序回滚、
        /// 只同盘）。这里只做三件事：**给窗口**、**同步任务路径**、**收完重新读一次盘上事实**。</para>
        ///
        /// <para><b>找卷的基准 = 手上那一卷</b>（用户口径：手上最浅的那一卷，⛔ 绝不是 001）：
        /// 窗口由 <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 按**这一单手上那一卷**
        /// 算出来 —— 真机形状是 <c>555\B250135(1)\B250135.7z.001</c> + <c>555\B250135(2)\B250135.7z.002</c>
        /// （两半在两个兄弟文件夹里），窗口的"父一层"正好把兄弟目录带进来；
        /// 拿一个解出来的 <c>001</c> 当基准就看不到那个父文件夹。</para>
        ///
        /// <para>⛔ 兜底一律落"什么都不做"：任何意外 ⇒ 返回 false，后面的流程照旧（让引擎去判）。
        /// ⛔ 不动内容：只搬 + 改规范卷名，绝不覆盖任何已存在的文件。</para>
        /// </summary>
        /// <returns>true = 入口那一层现在**自证完整**（引擎解得开）。false = 不确定 / 没收成。</returns>
        private static IEnumerable<string> CollectOriginalSourceFiles(IEnumerable<ArchiveTask> tasks)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks)
            {
                string original = task?.OriginalPath ?? string.Empty;

                if (original.Length == 0)
                {
                    continue;
                }

                string? cursor = Path.GetDirectoryName(original);

                for (int up = 0; up < 5 && !string.IsNullOrWhiteSpace(cursor); up++)
                {
                    string name = Path.GetFileName(cursor);

                    if (name.Contains("大测试原包", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("大测试指定位置", StringComparison.OrdinalIgnoreCase))
                    {
                        roots.Add(cursor!);
                        break;
                    }

                    cursor = Path.GetDirectoryName(cursor);
                }
            }

            var files = new List<string>();

            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (files.Count >= 400)
                        {
                            break;
                        }

                        if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(file))
                        {
                            files.Add(file);
                        }
                    }
                }
                catch
                {
                    // 源根读不动 ⇒ 少保护几份，不影响既有闸门。
                }
            }

            return files;
        }

        private bool TryGatherVolumesBeforeExtract(ArchiveTask task, out string detail)
        {
            detail = string.Empty;

            try
            {
                string current = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
                {
                    return false;
                }

                /*
                 * 起点 = 引擎这一趟要打开的那一份（分卷组只有"第 1 卷"这一个入口）。
                 * 手上是后续卷（用户只把 `.002` 拖进来）时，先在**找卷窗口**里找同一组的第 1 卷 ——
                 * 找得到就以它为入口（候选池 = 它的那一层），找不到就退回自己
                 * （`ResolveCrossLayerVolumeGather` 会按池子里的第 1 卷另判，判不出就一个字节都不动）。
                 */
                string entryPath = VolumeGatherStep.ResolveGatherEntryPath(current);

                /*
                 * 窗口的基准 = **手上那一卷**（口径 1）。⛔ 它只"看"：候选还要过下面那三道闸门
                 * （同族同基名 / 卷号一份对一份 / 整组自洽 + 7z 起始头字节数证据）。
                 *
                 * ⚠ 2026-10-12（用户口径 2）：「这个第一大步完成之后就是扫描所有剩余的，我之前说过的什么
                 * "父目录+自己这一层+所有子目录"在这个新框架下可能是不成立的」⇒ 候选来源**并上本批
                 * 所有剩下的路径各自所在目录**（`SnapshotTaskTable(Tasks)` 就是"这一批导入的 + 剩下的"）。
                 * ⛔ 放宽的只是"去哪儿找"：三道闸门与"整组完整才允许搬/删"一个字都没动。
                 */
                /*
                 * ⚠ 2026-10-16（真机样本 `10101010` 探针逐字）：候选来源原来只有**各单的 CurrentPath**，
                 * 而分卷的片是**别的单解出来的产物** —— 它们落在那一单的 `OutputPath` / 内容目录 /
                 * 其余物目录里，往往还在子目录中 ⇒ 窗口够不到 ⇒ 一整组的片永远"只有手上这一份"。
                 *
                 * 现场逐字（批末窗口）：入口 `pack111-L2.7z.001` 那一趟的候选是
                 * `pack111-L2.7z(26214400) ; pack111-L2.7z.001(26214400) ; pack111-W0-0.7z ; pack111-W1-0.zip ; pack111-L2.7z(10490213)`
                 * —— **`.002` 载荷（`…\pack111(2)\pack111-W1-0\pack111-L2.7z.002`）压根不在里面**，
                 * 因为它在 `pack111-W1-0.zip` 那一单的**产出目录的子目录**里。
                 *
                 * ⇒ 把"产出层"也并进候选来源（与用户 2026-10-12 口径 2 同一条：
                 * 「这个第一大步完成之后就是扫描所有剩余的」）。⛔ 放宽的只是**去哪儿找**，
                 * 下面那三道闸门与"整组完整才允许搬/删"一个字都没动。
                 */
                IReadOnlyList<VolumeCandidate> window = VolumeNameRepair.EnumerateVolumeCandidatesNearby(
                    current,
                    SnapshotTaskTable(Tasks)
                        .Where(candidate => candidate != null)
                        .SelectMany(candidate => new[]
                        {
                            candidate!.CurrentPath,
                            candidate.OutputPath,
                            candidate.ContentDirectoryPath,
                            candidate.RestDirectoryPath
                        })
                        .Concat(CollectOriginalSourceFiles(Tasks))
                        .Where(path => !string.IsNullOrWhiteSpace(path)));

                VolumeNameRepair.CrossLayerVolumeGather decision =
                    VolumeNameRepair.ResolveCrossLayerVolumeGather(current, null, window);

                if (!decision.Applicable)
                {
                    /*
                     * 收不成时**先说清缺的是哪一头**（2026-10-05 真机第八批）：**跨盘 ZIP 那一族的头尾是反的** ——
                     * 它的第 1 片叫 `111.z01`，而 `.zip` 是**末片**（7-Zip 就是拿 `.zip` 当入口的）。
                     * 按名字编号说话时"缺 index 1"其实等于"缺 `.zip`" ⇒ 对它必须说「缺的是末片」，
                     * ⛔ 不许说成「缺的是第 1 卷」：真机上那三片的第一片就躺在隔壁文件夹里，
                     * 用户会拿着"缺第 1 卷"这句去找一个根本不缺的东西。
                     *
                     * 判据只看一件事：手上这一份与 `<基名>.zip` **是不是同一族同基名**
                     * （既有出口 `BelongsToSameGroup`；⛔ 不新造族判据）。
                     */
                    if (string.Equals(entryPath, current, StringComparison.OrdinalIgnoreCase)
                        && VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(current)) != 1
                        && task.IsVolumeGroup)
                    {
                        string noFirstBaseName = FileNameHelper.GetArchiveBaseName(current);
                        bool spannedZipFamily = noFirstBaseName.Length > 0
                            && VolumeGroupDetector.BelongsToSameGroup(
                                Path.GetFileName(current),
                                noFirstBaseName + ".zip");

                        AppendLog(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                spannedZipFamily
                                    ? StatusText.VolumeDeficitNoTailNearbyFormat
                                    : StatusText.VolumeDeficitNoFirstVolumeNearbyFormat,
                                task.FileName));
                    }

                    /*
                     * 「这一组该怎么收」判不出来的那一档也把**判据输入**写出来（用户 2026-10-10 目标单③）：
                     * 上面那几行只说"判不出该怎么收"，而"窗口看了哪些目录、里面到底有几个同族候选、
                     * 都叫什么"才是下一轮排查要的东西 —— 真机 FFFF 那批就是卡在"附近没有第 1 卷"这一句上，
                     * 看不出是窗口没够到那几个目录，还是够到了但尺子不认。
                     * ⛔ 只列事实（候选来自 `EnumerateVolumeCandidatesNearby`，与判据同一份输入），⛔ 不改判据。
                     *
                     * ⚠ 措辞必须说清这是**收进来的池子**、不是"同组的成员"（2026-10-11 真机日志订正）：
                     * 三道闸门（同族同基名 / 卷号一份对一份 / 整组自洽）都在池子**之后**才过，
                     * 老措辞「同组候选」把 `111.rar`（RarOld 族）与 `111(2)_.zip`（另一个基名）也印成同组
                     * ⇒ 拿这份日志排查的人会顺着一个**假的同组名单**去查（文案唯一出口
                     * `StatusText.CriterionInputsGatherWindowFormat` 已同步改写）。
                     */
                    string[] sample = window
                        .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                        .Select(candidate => Path.GetFileName(candidate.Path))
                        .Take(8)
                        .ToArray();

                    AppendLog(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CriterionInputsGatherWindowFormat,
                            task.FileName,
                            Path.GetDirectoryName(current) ?? string.Empty,
                            window.Count,
                            sample.Length == 0 ? "（一个都没有）" : string.Join("；", sample))
                        // 闸门那一句也带上（2026-10-12：候选找齐了却"判不出"时，日志必须说清是哪一道闸门）。
                        + (string.IsNullOrWhiteSpace(decision.Detail) ? string.Empty : "｜闸门：" + decision.Detail));

                    return false;
                }

                /*
                 * 收拢的目标那一份 = 这一组的**第 1 卷**（`EntryPath`，唯一出口给出的）。
                 * 手上是后续卷时，下面 `TryApply` 会把那一片搬进第 1 卷所在层 ⇒ 起点必须跟着换过去，
                 * ⛔ 不能让这一单拿着一个已经被搬走（或被改成规范名）的旧路径往下走。
                 */
                if (!string.IsNullOrWhiteSpace(decision.EntryPath) && File.Exists(decision.EntryPath))
                {
                    entryPath = decision.EntryPath!;
                }

                if (decision.Plan is { CanRepair: true })
                {
                    int count = decision.MovedCount;
                    (string Directory, List<(string Original, string Backup)> Items) protection =
                        ProtectKeepInPlaceSourcesBeforeRepair(decision.EntryPath);
                    VolumeNameRepairResult applied = VolumeNameRepair.TryApply(decision.Plan);

                    if (applied.Success)
                    {
                        RestoreKeepInPlaceSourcesAfterRepair(protection);
                    }

                    CleanupKeepInPlaceSourceProtection(protection);

                    if (!applied.Success)
                    {
                        // 全成或全不成（TryApply 自己倒序回滚）：盘上仍是"没收"那一档，如实写。
                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeGatherBeforeExtractBlockedFormat,
                                task.FileName,
                                applied.Message));

                        return false;
                    }

                    string anchorNote = DescribeGatherAnchor(decision, entryPath);

                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeGatherBeforeExtractDoneFormat,
                            task.FileName,
                            count,
                            Path.GetFileName(entryPath),
                            decision.Detail)
                        + anchorNote);

                    /*
                     * 任务表里指着这几卷的路径**一起**改过来（唯一出口）；它会顺手重拍快照
                     * （不变量 11 的口径一个字没放宽：变的只是"谁的名字 / 在哪一层"）。
                     */
                    RememberGatheredUserSourcePieces(decision.Plan.Items);

                    SyncTasksAfterVolumeRename(decision.Plan.Items, task);

                    // 起点必须是这一组的第 1 卷（用户只把 `.002` 拖进来时，要解的是旁边那个 `.001`）。
                    //
                    // ⚠ 只在这一单**不是跟班卷**时才改起点：第 1 卷已经被**同批另一个任务**负责时，
                    // 改过去会让两单指着同一个文件（那一组的活该由首卷那一单干，这一单按既有口径
                    // 落成「跟班卷」并跳过 —— 见 SkipWhenAnotherTaskOwnsThisVolumeGroup）。
                    if (!string.Equals(
                            SafePathHelper.GetFullPathSafe(task.CurrentPath),
                            SafePathHelper.GetFullPathSafe(entryPath),
                            StringComparison.OrdinalIgnoreCase)
                        && !IsFirstVolumeOwnedByAnotherTask(task, entryPath))
                    {
                        AppendLog(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeGatherEntryRepointLogFormat,
                                task.FileName,
                                Path.GetFileName(entryPath)));

                        // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                        task.CurrentPath = entryPath;
                        task.CaptureSourceSnapshot();

                        /*
                         * ⛔ 列表那一行**仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06，
                         * 用户原话：「48MB 在列表里面显示到了 111(4)……这个你到目前还没改回来」）。
                         * 起点是**内部细节**（本方法上面那行日志"起点由 X 改为它"已经写清），
                         * ⛔ 不许把这一行的名称 / 大小整行变成那一份过程物；⛔ CurrentPath 一个字不动。
                         */
                        task.ShowUserFileIdentity(entryPath, FindUserOwnFileBesideItsDirectory(task, entryPath));
                    }

                    /*
                     * 收完**重新读一次盘上事实**（⛔ 不拿"刚刚搬成功了"当结论）——
                     * 这一组现在到底齐不齐，只能由盘上那几份回答。
                     */
                    VolumeNameRepair.CrossLayerVolumeGather after =
                        VolumeNameRepair.ResolveCrossLayerVolumeGather(
                            entryPath,
                            null,
                            VolumeNameRepair.EnumerateVolumeCandidatesNearby(entryPath));

                    detail = after.Detail;
                    return after.CompleteBesideEntry;
                }

                if (!decision.CompleteBesideEntry)
                {
                    detail = decision.Detail;
                }

                return decision.CompleteBesideEntry;
            }
            catch (Exception ex)
            {
                // 这一档是**加法**：判不出来一律退回"照旧让引擎去判"，⛔ 绝不把本来能解的包挡下来。
                AppendLog("WARN", $"{task.LogName}：开工前跨目录收卷跳过（{ex.Message}）。");
                return false;
            }
        }

        /// <summary>
        /// **任务表里有几份"某一组的第 1 卷"**（诊断用，只读）：判据 = 卡上 `BodyKind == VolumeFirstPiece`
        /// （§29.8 的权威，⛔ 不看首卷标志）。用途见接片那一档的判据输入（§29.24：目标层与锚只能按
        /// "这一片所属的那一组"算，这个数字判"上层交下来的够不够用"）。
        /// </summary>
        private int CountFirstVolumeTasksInTable()
        {
            int count = 0;

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                string path = task?.CurrentPath ?? string.Empty;

                if (path.Length > 0
                    && File.Exists(path)
                    && FileNature.Inspect(path).BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    count++;
                }
            }

            return count;
        }

    }
}