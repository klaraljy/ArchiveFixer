using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
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
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>被别单接手的片/组的结算（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **任务表里已经有一单"代表"这一组了吗**（= 有一单自己的文件就是这一组的一片）。
        ///
        /// <para>用途只有一个：决定"停在**别人**那一组"的单要不要进批末名单。真机形状
        /// （`SiblingFolderVolumeGatherTests` 的跨链收卷那几条）里**用户导入的那几片自己就是任务**
        /// ⇒ 那一组的活该由它们干（老口径本来就能把它们登记进来）⇒ 产出方那一单**不进名单**，
        /// 一个字节的行为都不变；而困难批 `11111111` 那一档里**没有任何一单是这一组的片**
        /// （用户导入的只有三条产出方链）⇒ 产出方那一单进名单，那一组才有人收拢、才有人解开。</para>
        ///
        /// <para>⛔ 判据仍走唯一那把尺子 <see cref="LegacyVolumeNames.BelongsToSameGroup"/>（族 + 基名）。</para>
        /// </summary>
        private bool AnyTaskRepresentsGroup(string piecePath)
        {
            string pieceName = FileNameHelper.GetFileName(piecePath);

            if (pieceName.Length == 0)
            {
                return false;
            }

            foreach (ArchiveTask? candidate in SnapshotTaskTable(Tasks))
            {
                string name = FileNameHelper.GetFileName(candidate?.CurrentPath ?? string.Empty);

                if (name.Length > 0 && LegacyVolumeNames.BelongsToSameGroup(name, pieceName))
                {
                    return true;
                }
            }

            return false;
        }

        /// `111(2)\111(2)_\111.z01`，200 MiB）。
        ///
        /// <para>为什么要补这一份：工作区收尾会把暂存那一份整份删掉；如果那一刻"这一组该在的那一层"
        /// 还认不出来（入口包还没被解出来 / 目录被占），这一片就会跟着工作区一起消失
        /// ⇒ 批末那一站到盘上再也找不到它（真机 `那一片已经不在盘上（…\output\111.zip）` 那一次）。</para>
        ///
        /// <para>⛔ **只在"那一层还没有它"时补**：接片成功之后那一层已经有同一个名字了
        /// （硬链接、零字节），再在落点里留一个名字就是**没人认领的多余名**
        /// —— 收尾谁也不认它，用户只会看到 200 MiB 的残留。⛔ 这里只建链接，不改名、不搬、不覆盖。</para>
        /// </summary>
        /// <param name="pieces">暂存目录里的那几份（<see cref="PublishUnresolvedVolumePieces"/> 的返回值）。</param>
        /// <summary>两份文件是不是**字节完全相同**（大小 + SHA-256；读不了 ⇒ false ⇒ 当作不同，⛔ 不乱判）。</summary>
        private static bool IsByteIdentical(string left, string right)
        {
            try
            {
                var leftInfo = new FileInfo(left);
                var rightInfo = new FileInfo(right);

                if (leftInfo.Length != rightInfo.Length || leftInfo.Length == 0)
                {
                    return false;
                }

                return string.Equals(SafeSha256(left), SafeSha256(right), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeSha256(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();

                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 「过路层那一单的其余物按档删」那一档"判不出 ⇒ 一个字节都不删"时把**判据输入**写进日志
        /// （与 <see cref="LogAdoptCriterionInputs"/> 同一规矩，用户 2026-10-10 目标单③）。
        ///
        /// <para>为什么必须有它（2026-10-11 实测）：这一档**五道闸门全是静默早退**
        /// （没记下其余物目录 / 目录不在盘上 / 其余物里有不属于这一组的文件 / 完整性证据不允许 /
        /// 删除档是「不动其余物」），而它的两条结论日志只在**真删**时才写 ⇒
        /// 拿一份用户日志**分不出**"这一档压根没被走到"和"走到了、被哪一道闸门挡下"
        /// —— 实测：整个 <c>SiblingFolderVolumeGatherTests</c>（19 条）跑完，
        /// 那两条结论日志出现 **0 次**，加之前无法判断是没调用还是被拦。</para>
        ///
        /// <para>⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 它一处判据都不改。</para>
        /// </summary>
        private void LogPassThroughRestCriterionInputs(
            ArchiveTask producer,
            ArchiveTask? consumer,
            string groupBaseName,
            string restDirectory,
            string why)
        {
            string[] files;

            try
            {
                files = string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory)
                    ? Array.Empty<string>()
                    : Directory
                        .EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories)
                        .Select(FileNameHelper.GetFileName)
                        .ToArray();
            }
            catch
            {
                // 读不出来 ⇒ 如实写"判不出"，⛔ 不猜。
                files = Array.Empty<string>();
            }

            /*
             * 「没有记下其余物目录」和「记过但目录已不在」不是同一档：
             * 前者常见于跟班卷（根本没有自己的成品/其余物），这次真机 2222 的风景02 就是
             * 0 文件的空判据；后者可能是真的被删掉/丢了，仍按 WARN 留证据。
             */
            AppendLog(
                string.IsNullOrWhiteSpace(restDirectory) ? "INFO" : "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsPassThroughRestFormat,
                    producer.LogName,
                    why,
                    consumer == null ? "判不出" : consumer.LogName,
                    string.IsNullOrWhiteSpace(groupBaseName) ? "判不出" : groupBaseName,
                    string.IsNullOrWhiteSpace(restDirectory) ? "判不出" : restDirectory,
                    files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    files.Length == 0 ? "（空）" : string.Join("；", files.Take(8))));
        }


        /// <summary>
        /// 这一单**自己那一份**的两条路径（<c>OriginalPath</c> 在前、<c>CurrentPath</c> 在后；去重，且**不含**分卷账）。
        /// </summary>
        internal static IEnumerable<string> EnumerateOwnPaths(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && seen.Add(task.OriginalPath))
            {
                yield return task.OriginalPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath) && seen.Add(task.CurrentPath))
            {
                yield return task.CurrentPath;
            }
        }

        /// <summary>
        /// 这一单自己的全部路径（<c>OriginalPath</c> + <c>CurrentPath</c> + 账上那几卷；去重）。
        ///
        /// <p>⚠ <b>`OriginalPath` 必须排在**最前**（2026-10-06 真机 CCCC 实测）</b>：批末补判那一站会把
        /// 补齐了的这一单的 <c>CurrentPath</c> 改到**入口包**（跨盘 ZIP 的末片 `111.zip`）上 ⇒
        /// 拿 <c>CurrentPath</c> 去问"这一单自己那一片在不在借片账里"必然问不出来（那是入口包、不是它那一片），
        /// 源片就留在盘上了（真机盘上实测：`111.z0删除3` 被处理了，`111.z0删除2` 留在原处）。
        /// `OriginalPath` 是**最初导入路径**，搬到其余物之前一直指向它自己那一片
        /// （搬运成功后会跟着更新，见 <c>ExecuteSourcePackageMove</c>）。</p>
        /// </summary>
        internal static IEnumerable<string> EnumerateTaskPaths(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && seen.Add(task.OriginalPath))
            {
                yield return task.OriginalPath;
            }

            if (!string.IsNullOrWhiteSpace(task.CurrentPath) && seen.Add(task.CurrentPath))
            {
                yield return task.CurrentPath;
            }

            foreach (string path in task.VolumePaths ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// 这一单现在**能不能**凑出完整的一组（只读盘上事实）：候选 = 账上那几卷 + 自己那一层 +
        /// 找卷窗口，交给既有出口 <see cref="RebuildVolumeGroup"/> 归组；补齐且第 1 卷真的在盘上 ⇒ 返回那一组。
        /// 判不出（归不出组 / 仍然不完整 / 第 1 卷不在）⇒ null（⛔ 什么都不做）。
        /// </summary>
        /// <param name="preferDirectory">
        /// 另外再看一层（用户 2026-10-06：「你把分卷移过来不就是了吗」）：这一组**入口包所在的那一层**
        /// （<c>ResolveBatchGroupDirectory</c> 的回答）。收片那一档已经把散着的片按规范卷名接到那一层了，
        /// 而这一单自己那一层（散着一片的那一层）永远凑不齐 —— 只看自己那一层就会得出
        /// "仍然缺卷"这种**会把人指错**的结论。判据本身一个字没放宽：补齐与否仍然只由
        /// <see cref="VolumeGroupDetector.Group"/> 的"完整 / 卷号连续 / 除末片外等大"回答，
        /// ⛔ 这里只是**把候选池看宽一层**（多喂一个目录）。
        /// </param>
        internal static VolumeGroup? ResolveCompleteGroupFromDisk(ArchiveTask task, string? preferDirectory = null)
        {
            try
            {
                var extra = new List<string>();
                string? directory = Path.GetDirectoryName(task.CurrentPath ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    extra.AddRange(CollectVolumeFilesInDirectory(directory));
                }

                if (!string.IsNullOrWhiteSpace(preferDirectory) && Directory.Exists(preferDirectory))
                {
                    extra.AddRange(CollectVolumeFilesInDirectory(preferDirectory));
                }

                foreach (VolumeCandidate candidate in VolumeNameRepair.EnumerateVolumeCandidatesNearby(task.CurrentPath))
                {
                    if (candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                    {
                        extra.Add(candidate.Path);
                    }
                }

                VolumeGroup? group = RebuildVolumeGroup(task, extra);

                if (group == null || !group.IsComplete || string.IsNullOrWhiteSpace(group.FirstVolumePath))
                {
                    return null;
                }

                if (!File.Exists(group.FirstVolumePath))
                {
                    return null;
                }

                /*
                 * ⛔ **账上点过名的缺卷必须真的出现**才叫"补齐了"。
                 *
                 * 为什么不能只看 `group.IsComplete`：命名里**没有"共几卷"这个信息**
                 * （`VolumeGroupDetector` 类注释第 2 条），所以"只有一个 `.001`"在名字上看起来
                 * 就是一组完整的卷 —— 而账上明明白白记着"缺 `.002`"。
                 * 少了这一条，真机那种"缺一卷"的包会被当成"已经补齐"直接开跑
                 *（2026-10-05 复核逮到：`RealMachineDefectFixesTests.缺卷预检拦下_机器终态必须是失败` 当场变红）。
                 */
                foreach (string missing in task.MissingVolumeNames)
                {
                    if (string.IsNullOrWhiteSpace(missing))
                    {
                        continue;
                    }

                    bool appeared = group.Volumes.Any(v => string.Equals(
                        SafeFileNameOf(v.Path),
                        SafeFileNameOf(missing),
                        StringComparison.OrdinalIgnoreCase));

                    if (!appeared)
                    {
                        return null;
                    }
                }

                return group;
            }
            catch
            {
                return null;
            }

            static string SafeFileNameOf(string? path) =>
                Path.GetFileName(SafePathHelper.GetFullPathSafe(path ?? string.Empty));
        }

        // ===== 搬出后的入口薄转发（实现见独立类）=====
        private ExtractionConsumedPieceSettler? _consumedPieceSettler;
        private ExtractionConsumedPieceSettler ConsumedPieceSettler =>
            _consumedPieceSettler ??= new ExtractionConsumedPieceSettler(this);

        private ExtractionConsumedGroupSettler? _consumedGroupSettler;
        private ExtractionConsumedGroupSettler ConsumedGroupSettler =>
            _consumedGroupSettler ??= new ExtractionConsumedGroupSettler(this);

        internal void DeleteHarvestTargets(ArchiveTask consumer, List<string> targets) =>
            ConsumedPieceSettler.DeleteHarvestTargets(consumer, targets);

        internal void SettleOneConsumedPieceTask(
            ArchiveTask piece,
            ArchiveTask consumer,
            string groupBaseName,
            string producedPieceName,
            bool moveSources,
            List<(string Level, string Message)> logEntries,
            string? producedPiecePath = null) =>
            ConsumedPieceSettler.SettleOneConsumedPieceTask(
                piece, consumer, groupBaseName, producedPieceName, moveSources, logEntries, producedPiecePath);

        internal void SettleConsumedGroupVolumeSources(List<(string Level, string Message)>? logEntries) =>
            ConsumedPieceSettler.SettleConsumedGroupVolumeSources(logEntries);

        internal static string ResolveOwnPiecePath(ArchiveTask task) =>
            ExtractionConsumedPieceSettler.ResolveOwnPiecePath(task);

        internal void SettleSucceededGroupPieceProducers() => ConsumedGroupSettler.SettleSucceededGroupPieceProducers();

        internal void SettleParentOfSucceededVolumeGroupForTests() =>
            ConsumedGroupSettler.SettleParentOfSucceededVolumeGroupForTests();

        internal void SettleParentOfSucceededVolumeGroup() => ConsumedGroupSettler.SettleParentOfSucceededVolumeGroup();

        internal void SettleVerifiedGroupFollowers() => ConsumedGroupSettler.SettleVerifiedGroupFollowers();

        internal bool TryGetGroupConsumerPending(ArchiveTask task, out string consumer) =>
            ConsumedGroupSettler.TryGetGroupConsumerPending(task, out consumer);

        internal bool TryResolveConsumedByAnotherTask(ArchiveTask task, out string consumerName) =>
            ConsumedGroupSettler.TryResolveConsumedByAnotherTask(task, out consumerName);
        internal bool IsBorrowedPieceTask(ArchiveTask task) => ConsumedGroupSettler.IsBorrowedPieceTask(task);
    }
}
