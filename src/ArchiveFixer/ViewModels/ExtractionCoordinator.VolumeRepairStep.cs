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
        /// 收集本批任务**账上明确登记**的用户源路径：OriginalPath、CurrentPath、VolumePaths。
        ///
        /// <para>⛔ 不按祖先目录递归扫描：目录名不是源包身份；扫描会把用户同层的无关文件带进来。
        /// 源包保护只认任务账/卷清单，判不出就少保护几份，由既有源包删除闸门兜底。</para>
        /// </summary>
        private static IEnumerable<string> CollectOriginalSourceFiles(IEnumerable<ArchiveTask> tasks)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path) || files.Count >= 400)
                {
                    return;
                }

                string full = SafePathHelper.GetFullPathSafe(path);

                if (string.IsNullOrWhiteSpace(full)
                    || ProcessArtifactLayout.IsInsideDeletableProcessFolders(full)
                    || !seen.Add(full))
                {
                    return;
                }

                files.Add(full);
            }

            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                Add(task.OriginalPath);
                Add(task.CurrentPath);

                foreach (string volume in task.VolumePaths)
                {
                    Add(volume);
                }
            }

            return files;
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


        /// <summary>兼容出口（测试/轮次层读它）：实现已搬进 <see cref="ArchiveFixer.Extraction.VolumeNameOrchestration"/>。</summary>
        internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note)
            => ArchiveFixer.Extraction.VolumeNameOrchestration.TryConfirmVolumeGroupComplete(filePath, out note);

    }
}