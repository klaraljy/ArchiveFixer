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
        /// <para>⛔ 判据仍走唯一那把尺子 <see cref="VolumeGroupDetector.BelongsToSameGroup"/>（族 + 基名）。</para>
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

                if (name.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(name, pieceName))
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

            AppendLog(
                "WARN",
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
        /// **过路层那一单的其余物按档处理**（用户 2026-10-06 拍板：「其余物会在每层的过程中会删掉，
        /// 我无法理解为什么还会有其余物留着」；他两次追问的 `111\111\其余物\111.zip`（48.35 MB）就是这一格）。
        ///
        /// <para><b>形状</b>：某一单只出**过程物**（它解出来的入口包 / 分卷片就是下一层的输入），
        /// 自己因此停在中途；那一组后来由**另一个当场核过清单、可证完整**的消费方解开
        /// ⇒ 它自己那份其余物不再有用，按「删除操作」处理。</para>
        ///
        /// <para>⛔ **六道门槛一条不绕过**（机器终态 / 完整性证据 / 其余物目录 / 形状 / 输出范围 /
        /// 半套分卷），唯一差别是**证据来源**：这一单自己停在中途 ⇒ 它自己的完整性判不出来，
        /// 该按证据的是**接手它的那一单**（`completenessEvidence`）。⛔ 那条证据只用于这一次删除，
        /// 不写回它自己的账。</para>
        ///
        /// <para>⛔ 放行之前还要逐条确认：其余物里**每一个文件的名字都属于这一组**
        /// （= 都是被那次成功解压接手的过程物）；只要有一个不属于 ⇒ 什么都不做（判不出就不动）。</para>
        ///
        /// <para><b>⚠ 2026-10-11 环台实测（取证，⛔ 不是推断）</b>：把这一档与
        /// <see cref="SweepPassThroughRestDirectoriesAsync"/> **两条都整条关掉**
        /// （各加一句 early return），整个 <c>SiblingFolderVolumeGatherTests</c>（19 条）
        /// **照旧 18 通过 / 1 跳过 / 0 失败**，`彻底删除：111\其余物（8 项 / 7.0 MB）`照旧执行 ⇒
        /// 现在这几条用例真正覆盖的是**链尾那一遍**「这一单自己的其余物按档处理」
        /// （关掉它 ⇒ `真机形状_过路层那一单的其余物按档删掉_48MB那处` 当场红）。
        /// ⇒ 本档在现有夹具里**没有一次真的删成功**（19 次调用全部停在闸门上，逐条见日志里那句
        /// `过路层的其余物这次没删 —— …`），⛔ **不许把"用例绿"读成"这一档验过了"**；
        /// 哪一天真机日志里出现 `过路层的其余物已按「删除操作」处理`，那才是它第一次真干活。</para>
        /// </summary>
        private void PurgePassThroughRestOfSettledProducer(
            ArchiveTask producer,
            ArchiveTask consumer,
            string groupBaseName)
        {
            if (producer == null || !_passThroughRestHandled.Add(producer))
            {
                return;
            }

            string restDirectory = producer.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory))
            {
                // 「判不出 ⇒ 什么都不做」：把判据输入写进日志（⛔ 判据本身一个字不动）。
                LogPassThroughRestCriterionInputs(
                    producer,
                    consumer,
                    groupBaseName,
                    restDirectory,
                    "没记下这一单定稿实际用的其余物目录（或它已经不在盘上）");

                return;
            }

            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                // 「不动其余物」这一档：一个字节都不删（不变量 1 的默认档）。
                return;
            }

            try
            {
                /*
                 * ⛔ 前置闸门的引用名走**唯一出口**（`AllRestFilesBelongToGroup` → `ResolveGroupReferenceName`）：
                 * 原来写死 `<组基名>.zip` ⇒ 非 ZIP 族这一层永远判"里面有别人的东西" ⇒ 永远不删
                 * （用户选了「彻底删除」也不动）；ZIP 族却删 —— 同一个设置两种结果（2026-10-11 统一）。
                 */
                if (!AllRestFilesBelongToGroup(
                        consumer,
                        groupBaseName,
                        Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories)))
                {
                    // 里面还有不属于这一组的东西 / 读不到 ⇒ 判不出 ⇒ 什么都不做（原样留着）。
                    LogPassThroughRestCriterionInputs(
                        producer,
                        consumer,
                        groupBaseName,
                        restDirectory,
                        "其余物里有不属于这一组的文件（判不出它是不是被接手的过程物）");

                    return;
                }
            }
            catch
            {
                return;
            }

            ResultCompletenessVerdict evidence = ResultCompletenessClassifier.Classify(consumer);

            if (!evidence.AllowsSourceRemoval)
            {
                LogPassThroughRestCriterionInputs(
                    producer,
                    consumer,
                    groupBaseName,
                    restDirectory,
                    "接手方那一单的完整性证据不允许删源（不是「可证完整 + 校验通过」）");

                return;
            }

            bool cancelled = IsStopping || _operationCts?.IsCancellationRequested == true;

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 这里（Keep 已在上面挡掉）只剩永久删。
            DeleteMode deleteMode = DeleteMode.Permanent;

            RestPurgeOutcome outcome;

            try
            {
                outcome = new RestItemPurger(
                        RestDeleteExecutor,
                        null,
                        ContentKeepRules.FromSettings(Settings))
                    .Purge(
                        producer,
                        cancelled,
                        deleteMode,
                        evidence,

                        /*
                         * 输出范围那一道门槛的锚点：这一单被收场时 `OutputPath` 已经被清空
                         * （跟班卷没有自己的成品目录）⇒ 用**唯一落点出口**现算一份它那条链的落点目录
                         * —— 其余物正是建在那一层底下（真机 `111\111\其余物`）。
                         */
                        /*
                         * ⛔ 锚点 = **其余物目录的父目录**（其余物就建在它自己那一层底下），
                         * ⛔ **绝不现算这一单的落点**：真机 2026-10-06 21:24 实测 —— 走到这里时
                         * 它的源包**已经被搬进组层其余物**，现算出来是 `…\其余物\111` 这种荒唐路径，
                         * 于是"不在本任务输出目录之内"把删除拒了 4 次，48.35 MB 一直留着。
                         * 其余五道门槛（终态 / 完整性证据 / 目录形状 / 组内文件 / 半套分卷）照旧一条不绕过。
                         */
                        Path.GetDirectoryName(restDirectory) ?? string.Empty);
            }
            catch (Exception ex)
            {
                // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                AppendLog("WARN", $"{producer.LogName}：处理过路层的其余物时出现意外错误：{ex.Message}");
                return;
            }

            AppendLog(
                outcome.Succeeded ? "INFO" : "WARN",
                outcome.Succeeded
                    ? $"{producer.LogName}：过路层的其余物已按「删除操作」处理（{outcome.EntryCount} 项 / {outcome.FreedBytes} 字节）"
                      + $"—— 这一组的内容已由「{consumer.FileName}」解开。"
                    : $"{producer.LogName}：过路层的其余物这次没删成 —— {outcome.Message}");
        }




        /// <summary>第二大步收卷那一档的措辞（候选 = 这一趟按组定下来的那几份卷）。</summary>
        private const string HarvestCandidatePrefix = "第二大步收卷准备收的";

        /// <summary>第二大步收卷那一档的结论措辞（与「逐层回收」那一档同一件事、不同入口）。</summary>
        private const string HarvestBlockerTail =
            "当场收掉这几份就等于把这一组拆开，所以这一趟一个字节都不收"
            + "（删除不可恢复；这一组留到确认完整之后再收）";


        /// <summary>
        /// **真删**这一组的那几份（调用方已经过完全部门槛，含半套分卷那道闸门）。
        ///
        /// <para>执行体仍只有 <see cref="Storage.RecycleBinService"/> 一个出口
        /// （<c>AllowedRoot</c> = 这一层的目录）；删不掉只写 WARN，⛔ 不动任何结论、⛔ 不写回任务账。</para>
        /// </summary>
        private void DeleteHarvestTargets(ArchiveTask consumer, List<string> targets)
        {
            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 只剩永久删（调用方已保证这一批不是 Keep 档）。
            DeleteMode deleteMode = DeleteMode.Permanent;

            string reason = RestItemPurger.SettledPieceCopyPurgeReason;

            var service = new RecycleBinService(RestDeleteExecutor, null, null);
            int deleted = 0;

            foreach (string path in targets)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    string name = FileNameHelper.GetFileName(path);
                    string directory = Path.GetDirectoryName(path) ?? string.Empty;

                    DeleteResult result = service.Delete(
                        new DeleteRequest(path, reason),
                        new DeleteOptions
                        {
                            AllowedRoot = directory,
                            UserConfirmed = true,
                            Mode = deleteMode,
                            Reason = reason
                        });

                    if (result.SuccessCount > 0)
                    {
                        deleted++;

                        AppendLog(
                            "INFO",
                            deleteMode == DeleteMode.RecycleBin
                                ? $"{consumer.LogName}：这一组已经解开，它这一份卷 {name} 按「删除操作」移入回收站了。"
                                : $"{consumer.LogName}：这一组已经解开，它这一份卷 {name} 按「删除操作」彻底删除"
                                  + $"（{result.Outcomes.FirstOrDefault()?.EntryCount ?? 0} 个条目 / "
                                  + $"释放 {TaskSpaceEstimate.FormatSize(result.Outcomes.FirstOrDefault()?.FreedBytes ?? 0)}，不进回收站）。");
                    }
                    else
                    {
                        AppendLog(
                            "WARN",
                            $"{consumer.LogName}：这一组的卷 {name} 这次没删成 —— "
                            + $"{result.Outcomes.FirstOrDefault()?.Message ?? result.Message}（内容物不受影响，这一份仍在原处）。");
                    }
                }
                catch (Exception ex)
                {
                    // 删东西绝不许把已经成立的结论拖成异常：落成"没删成"，别的什么都不变。
                    AppendLog("WARN", $"{consumer.LogName}：收这一组卷时出现意外错误：{ex.Message}");
                }
            }

            if (deleted > 0)
            {
                AppendLog("INFO", $"{consumer.LogName}：这一组的卷已按「删除操作」收掉 {deleted} 份（这一层的过程物，不进回收站那一档）。");
            }
        }

        /// <summary>
        /// 产出方落点子目录里、与已被接手的片同一物理文件的多余名：只收名字不删字节
        /// （链接数 &gt; 1；⛔ 不碰 adopted 目标本身、用户源、`其余物`/工作区）。
        /// `28282828` 的 `aaa-W2-1\aaa-W2-0\aaa-L2.zip` 就是这一档。
        /// </summary>
        private void PurgeRedundantLandedAliases(string groupBaseName)
        {
            if (string.IsNullOrWhiteSpace(groupBaseName))
            {
                return;
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adoptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _adoptedLandedPieces)
            {
                if (!string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.PiecePath))
                {
                    adoptedPaths.Add(SafePathHelper.GetFullPathSafe(item.PiecePath));
                }

                if (FileIdentity.TryGetIdentity(item.PiecePath, out ulong volume, out ulong index))
                {
                    identities.Add(volume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrWhiteSpace(item.OutputRoot) && Directory.Exists(item.OutputRoot))
                {
                    roots.Add(item.OutputRoot);
                }
            }

            if (identities.Count == 0 || roots.Count == 0)
            {
                return;
            }

            foreach (string root in roots)
            {
                IEnumerable<string> files;

                try
                {
                    files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (!FileIdentity.TryGetIdentity(file, out ulong fileVolume, out ulong fileIndex))
                    {
                        continue;
                    }

                    string identity = fileVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    if (!identities.Contains(identity)
                        || ProcessArtifactLayout.IsInsideDeletableProcessFolders(file)
                        || IsAnyTaskSourcePath(file)
                        || adoptedPaths.Contains(SafePathHelper.GetFullPathSafe(file))
                        || !FileIdentity.TryGetLinkCount(file, out uint linkCount)
                        || linkCount <= 1)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);

                        AppendLog(
                            "INFO",
                            $"{FileNameHelper.GetFileName(file)}：产出方落点子目录里这一份与已接手的片是同一个文件 —— "
                            + "收掉这个多余的名字，一个字节都不丢（其余名字还在）。");
                    }
                    catch (Exception ex)
                    {
                        AppendLog(
                            "WARN",
                            $"{FileNameHelper.GetFileName(file)}：多余名字没能收掉（{ex.Message}）");
                    }
                }
            }
        }


        /// <summary>
        /// **批末补判**：把名单里那几个任务逐条重判一次（用户口径 2 的后半句）。
        ///
        /// <para>三步，全部读**盘上事实**：① 再收一次卷（<see cref="TryGatherVolumesBeforeExtract"/>）；
        /// ② 现归一次组（<see cref="RebuildVolumeGroup"/>，既有出口）—— 看这一组现在齐不齐；
        /// ③ 齐了 ⇒ 把这一单的起点改到第 1 卷、返回给调用方**照常跑这一组**；
        /// 仍然缺 ⇒ <paramref name="finalPass"/> 为 true 才如实报缺卷，否则继续留在名单里。</para>
        ///
        /// <para>⛔ 判据只有"盘上有没有那几卷"，⛔ 不比任何中文文案、⛔ 不拿任务账上那份过期结论当数。</para>
        /// </summary>
        /// <returns>现在该补跑的那几个任务（调用方负责启动 + 等它们收尾）。</returns>
        /// <summary>
        /// **(c) 硬链接试开证实**（用户 2026-10-17 四步里的第三步）：把这一组按**规范卷名链**交给引擎真开一次。
        ///
        /// <para>返回 null = 通过；非 null = 不过的原因。⛔ 判不出也算不过（没有引擎 / 认不出第 1 卷）——
        /// 整组不动，⛔ 绝不"没试过就当过了"（2026-10-17 日志里那句「可证完整且试开通过」的假话
        /// 就是这么来的：那一趟其实一次引擎都没调）。</para>
        ///
        /// <para>只读：<see cref="VolumeProbeVerifier"/> 只在试开目录里建硬链接、收工把整棵试开目录删掉，
        /// 用户那一份一个字节都不动。</para>
        /// </summary>
        /// <summary>试开定序的结论（<see cref="Confirmed"/> = 赢的那一版；<see cref="Failure"/> 非空 = 不做）。</summary>
        private sealed class GroupOrderTrialResult
        {
            /// <summary>这一档**适用**（通用分片、片数 ≤5、末片可定）—— 失败时调用方不再按名字硬拼。</summary>
            public bool Applicable { get; init; }

            /// <summary>试开确认了顺序。</summary>
            public bool Confirmed { get; init; }

            /// <summary>赢的那一版（第 1 片 … 末片）。</summary>
            public IReadOnlyList<string> OrderedPieces { get; init; } = Array.Empty<string>();

            /// <summary>规范名的基名（由末片卡上取）。</summary>
            public string Stem { get; init; } = string.Empty;

            /// <summary>内容判定的命名族（`ZipNumbered` / `ZipSpanned`）。</summary>
            public VolumeNamingFamily Family { get; init; } = VolumeNamingFamily.ZipNumbered;

            public string? Failure { get; init; }

            public string Reason { get; init; } = string.Empty;

            public static GroupOrderTrialResult Fail(string reason, bool applicable = false) =>
                new() { Failure = reason, Applicable = applicable };
        }

        private async Task<GroupOrderTrialResult> ProbeGatherGroupAsync(
            IReadOnlyList<string> pieces,
            IReadOnlyList<string> candidatePool,
            string entryLayer,
            string? cardEntryPath,
            string referenceName,
            bool allowFallback,
            CancellationToken cancellationToken)
        {
            if (_archiveEngine == null || !_archiveEngine.IsAvailable)
            {
                return GroupOrderTrialResult.Fail("硬链接试开没做成：当前没有可用的解压引擎 ⇒ 整组不动");
            }

            string referenceBase = FileNameHelper.GetArchiveBaseName(referenceName);

            if (referenceBase.Length == 0
                && !string.IsNullOrWhiteSpace(cardEntryPath)
                && File.Exists(cardEntryPath))
            {
                try
                {
                    referenceBase = FileNature.Inspect(cardEntryPath).ArchiveBaseName;
                }
                catch
                {
                    // 读不出参照基名 ⇒ 下面按名字那把尺子兜底。
                }
            }

            /*
             * ===== 通用分片（`X.zip.001..00N`）的**内容试开定序**（用户 2026-10-18 口径）=====
             *
             * 只有这一族需要"猜中间片"：RAR / 7z / 跨盘 ZIP 的卷号由内容或名字明确给出；通用分片的中间片
             * 内容里没有卷号，名字被伪装时也读不出次序。做法（用户原话）：先按后缀相似度排一个高概率顺序，
             * 试开；试不出再把剩余排列全试一遍；全不成立 ⇒ 判不出（这一趟先不收）。
             *
             * ⛔ 候选只从"内容确认是这一组"的卡里来，并按**盘上物理身份**去重（同一份文件的多个名字只算一片）；
             * ⛔ 片数上限 5（用户口径：超过就不猜）。
             */
            List<FileNatureCard> numberedCandidates = CollectNumberedZipTrialPieces(
                pieces,
                candidatePool,
                referenceBase,
                referenceName);

            if (numberedCandidates.Count >= 2)
            {
                AppendLog(
                    "INFO",
                    "试开定序候选 —— "
                    + string.Join(
                        "、",
                        numberedCandidates.Select(card =>
                            $"{FileNameHelper.GetFileName(card.Path)}〔F={card.Format} EOCD={card.ZipEndRecordMeansSplit} "
                            + $"LH={card.HasZipLocalHeader} 成员={card.Membership} 基名={card.ArchiveBaseName}〕")));
            }

            /*
             * ⛔ 只有**内容判成通用分片**（`ZipNumbered`）才走这一档：
             * 跨盘 ZIP 的入口是末片 `.zip`、片名 `.z01/.z02`，它的顺序本来就由名字与内容盘号明确给出
             * —— 拿通用分片的 `.zip.00N` 去试开，会在真实跨盘组旁边再多造一套名字（15151515 那类回归）。
             */
            VolumeNamingFamily? contentFamily = numberedCandidates.Count >= 2
                ? VolumePiecesFromCards.Read(numberedCandidates).NamingFamily
                : null;

            if (!allowFallback
                && contentFamily == VolumeNamingFamily.ZipNumbered
                && numberedCandidates.Count > VolumeOrderTrial.MaxPieces)
            {
                /*
                 * 超过 5 片：按用户口径**不猜顺序**；但"不猜"不等于"把整组判死"——
                 * 名字/内容卡已经能归出完整组的，照旧走原来的收拢/解压路，⛔ 不置
                 * `contentOrderUndetermined`（2026-10-18 全量批 `19191919` 现场：
                 * 同一组在别的任务上已经可证完整，被这一条的"适用失败"压住了）。
                 */
                return new GroupOrderTrialResult
                {
                    Applicable = false,
                    Reason = $"这一组有 {numberedCandidates.Count} 份不同的片（超过 {VolumeOrderTrial.MaxPieces} 片）⇒ 不试开顺序，照旧按名字/内容收拢"
                };
            }

            if (!allowFallback
                && contentFamily == VolumeNamingFamily.ZipNumbered
                && numberedCandidates.Count >= 2)
            {
                string? tailPath = ResolveNumberedZipTail(numberedCandidates);
                string stem = ResolveOrderTrialStem(numberedCandidates, tailPath, referenceBase);

                /*
                 * 末片自述"共 N 片"而候选只有 M&lt;N 份 ⇒ 这一趟**不是"顺序试不出"，是候选还没到齐**
                 * （2026-10-18 新批 `29292929`：一个任务只看见 2 片，按"顺序失败"把整组压住，后面
                 * 另一个任务已经看见 3 片并试开成功 ⇒ 不能再让少片那一次形成判决）。
                 * 返回"不适用" ⇒ 调用方照旧走名字/收拢，不置 `contentOrderUndetermined`。
                 */
                int declaredPieces = numberedCandidates
                    .FirstOrDefault(card => string.Equals(card.Path, tailPath, StringComparison.OrdinalIgnoreCase))
                    ?.DeclaredVolumeCount ?? 0;

                if (declaredPieces > numberedCandidates.Count)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"只看见 {numberedCandidates.Count} 片，末片自述 {declaredPieces} 片 ⇒ 候选还没到齐，不做顺序判决"
                    };
                }

                if (tailPath == null || stem.Length == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没做成：末片（内容带 EOCD 的那一片）或基名判不出 ⇒ 整组不动",
                        applicable: true);
                }

                IReadOnlyList<IReadOnlyList<string>> orders = VolumeOrderTrial.BuildCandidateOrders(
                    numberedCandidates.Select(card => card.Path).ToList(),
                    tailPath,
                    VolumeNamingFamily.ZipNumbered,
                    stem);

                if (orders.Count == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没做成：排不出可试的顺序 ⇒ 整组不动",
                        applicable: true);
                }

                AppendLog(
                    "INFO",
                    $"试开定序 —— 内容确认的候选 {numberedCandidates.Count} 份"
                    + "（同一份文件的多个名字只算一份）"
                    + $"｜末片=「{FileNameHelper.GetFileName(tailPath)}」"
                    + $"｜族=通用分片｜基名=「{stem}」｜可试顺序 {orders.Count} 种");

                var verifier = new VolumeProbeVerifier(_archiveEngine);
                VolumeProbeOutcome outcome = await verifier.VerifyNumberedZipOrderAsync(
                        orders,
                        Path.Combine(entryLayer, ".ArchiveFixer.work"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.Confirmed)
                {
                    /*
                     * ⛔ **加密组不能拿"试开没通过"当"顺序判不出"**（2026-10-18 `EncryptedVolumePipelineTests`
                     * 回归）：空密码试开必然报密码错，而那是"没有密码"，不是"顺序不对"。
                     * ⇒ 这一档退回原来的密码/名字流程（照常报「密码错误」），⛔ 不把整组判死。
                     */
                    if (outcome.NeedsPassword)
                    {
                        return new GroupOrderTrialResult
                        {
                            Applicable = false,
                            Reason = "这一组是加密的，试开定序没有可用密码 ⇒ 退回原来的密码/名字流程"
                        };
                    }

                    return GroupOrderTrialResult.Fail(
                        "硬链接试开定序没通过（引擎真的解了一遍，每个条目还是对不上）⇒ 整组不动："
                        + outcome.Reason,
                        applicable: true);
                }

                List<string> winning = outcome.OrderedVolumes
                    .Select(volume => volume.Path)
                    .ToList();

                /*
                 * ⛔ 这里**只交顺序、不自己建链接**：接片仍走既有出口 `TryGatherGroupPiecesInto`
                 * （它会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净）。
                 * 自己建链接会绕过那本账 ⇒ 产出方那一份变成残留（2026-10-18 `19191919` / `6666` 的 c3 现场）。
                 */
                return new GroupOrderTrialResult
                {
                    Applicable = true,
                    Confirmed = true,
                    OrderedPieces = winning,
                    Stem = stem,
                    Reason = $"内容试开定序成立（第 {outcome.Attempts} 种顺序）—— 按这个顺序把这一组接进入口那一层"
                        + "（零字节硬链接，原件一个字节不动）"
                };
            }

            /*
             * ===== 跨盘 ZIP：末片 + 中间片的内容试开定序 =====
             * 新批 `24242424`（2026-10-18 运行期逐字）：末片自述"共 3 片"，而两个中段片的名字是
             * `.pdf` / `.zip`、内容一个判不出；名字族给不出次序 ⇒ 与通用分片同一套高概率顺序，
             * 只是硬链接名换成跨盘族 `.z01/.z02/.zip`，验证仍是真的解一遍（逐条目 CRC）。
             */
            if (contentFamily == VolumeNamingFamily.ZipSpanned
                && numberedCandidates.Count >= 2
                && !allowFallback)
            {
                if (numberedCandidates.Count > VolumeOrderTrial.MaxPieces)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"这一组有 {numberedCandidates.Count} 份不同的片（超过 {VolumeOrderTrial.MaxPieces} 片）⇒ 不试开顺序，照旧按名字/内容收拢"
                    };
                }

                string? spannedTail = ResolveSpannedZipTail(numberedCandidates);
                string spannedStem = ResolveOrderTrialStem(numberedCandidates, spannedTail, referenceBase);

                int spannedDeclaredPieces = numberedCandidates
                    .FirstOrDefault(card => string.Equals(card.Path, spannedTail, StringComparison.OrdinalIgnoreCase))
                    ?.DeclaredVolumeCount ?? 0;

                if (spannedDeclaredPieces > numberedCandidates.Count)
                {
                    return new GroupOrderTrialResult
                    {
                        Applicable = false,
                        Reason = $"只看见 {numberedCandidates.Count} 片，末片自述 {spannedDeclaredPieces} 片 ⇒ 候选还没到齐，不做顺序判决"
                    };
                }

                if (spannedTail == null || spannedStem.Length == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 试开定序没做成：末片（内容自证最后一片）或基名判不出 ⇒ 整组不动",
                        applicable: true);
                }

                IReadOnlyList<IReadOnlyList<string>> spannedOrders = VolumeOrderTrial.BuildCandidateOrders(
                    numberedCandidates.Select(card => card.Path).ToList(),
                    spannedTail,
                    VolumeNamingFamily.ZipSpanned,
                    spannedStem);

                if (spannedOrders.Count == 0)
                {
                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 试开定序没做成：排不出可试的顺序 ⇒ 整组不动",
                        applicable: true);
                }

                AppendLog(
                    "INFO",
                    $"跨盘试开定序 —— 内容确认的候选 {numberedCandidates.Count} 份"
                    + $"｜末片=「{FileNameHelper.GetFileName(spannedTail)}」"
                    + $"｜族=跨盘 ZIP｜基名=「{spannedStem}」｜可试顺序 {spannedOrders.Count} 种");

                var spannedVerifier = new VolumeProbeVerifier(_archiveEngine);
                /*
                 * 先用**空密码**试：这一族很多组本来就没加密码；拿密码本去试反会被引擎报
                 * "Wrong password"（2026-10-18 `15151515` 回归现场）。
                 * 空密码不成立时，再用本批候选试一遍 —— 有些加密组的引擎原话是「缺卷 / 数据错」
                 * 而不是「密码错」，不能靠 `NeedsPassword` 单条判据决定要不要补密码。
                 */
                VolumeProbeOutcome spannedOutcome = await spannedVerifier.VerifySpannedZipOrderAsync(
                        spannedOrders,
                        passwordCandidates: null,
                        Path.Combine(entryLayer, ".ArchiveFixer.work"),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!spannedOutcome.Confirmed)
                {
                    IReadOnlyList<string> spannedPasswords = BuildVolumeProbePasswordCandidates(spannedTail);

                    if (spannedPasswords.Count > 0)
                    {
                        spannedOutcome = await spannedVerifier.VerifySpannedZipOrderAsync(
                                spannedOrders,
                                spannedPasswords,
                                Path.Combine(entryLayer, ".ArchiveFixer.work"),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (!spannedOutcome.Confirmed)
                {
                    if (spannedOutcome.NeedsPassword)
                    {
                        return new GroupOrderTrialResult
                        {
                            Applicable = false,
                            Reason = "这一组跨盘 ZIP 是加密的，试开定序没有可用密码 ⇒ 退回原来的密码/名字流程"
                        };
                    }

                    return GroupOrderTrialResult.Fail(
                        "跨盘 ZIP 硬链接试开定序没通过（引擎真的解了一遍，每个条目还是对不上）⇒ 整组不动："
                        + spannedOutcome.Reason,
                        applicable: true);
                }

                List<string> winningSpanned = spannedOutcome.OrderedVolumes
                    .Select(volume => volume.Path)
                    .ToList();

                return new GroupOrderTrialResult
                {
                    Applicable = true,
                    Confirmed = true,
                    OrderedPieces = winningSpanned,
                    Stem = spannedStem,
                    Family = VolumeNamingFamily.ZipSpanned,
                    Reason = $"跨盘 ZIP 内容试开定序成立（第 {spannedOutcome.Attempts} 种顺序）—— 按这个顺序把这一组接进入口那一层"
                        + "（零字节硬链接，原件一个字节不动）"
                };
            }

            /*
             * 不是"通用分片、片数不多于 5、末片可定"那一档 ⇒ 照旧只按名字给的这一种顺序列一次目录
             * （RAR / 7z / 跨盘 ZIP 的卷序本来就由内容或名字明确给出；这一档一个字没改）。
             * ⛔ 决策器说 Leave 时**不跑这一遍**（没要搬，就别多调一次引擎）。
             */
            if (!allowFallback)
            {
                return new GroupOrderTrialResult { Applicable = false };
            }
            string? firstVolume = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ? cardEntryPath
                : pieces.FirstOrDefault(piece => File.Exists(piece)
                    && VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(piece)) == 1);

            if (string.IsNullOrWhiteSpace(firstVolume))
            {
                return GroupOrderTrialResult.Fail("硬链接试开没做成：这一组里认不出第 1 卷 ⇒ 整组不动");
            }

            var rest = pieces
                .Where(piece => File.Exists(piece)
                    && !string.Equals(piece, firstVolume, StringComparison.OrdinalIgnoreCase))
                .OrderBy(piece => VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(piece)) ?? int.MaxValue)
                .Select(piece => new VolumeCandidate { Path = piece, Size = FileSizeOrZero(piece) })
                .ToList();

            var verifierFallback = new VolumeProbeVerifier(_archiveEngine);

            /*
             * 7z / RAR 的名字也可能被伪装成 `.png` / 裸 `.7z`（2026-10-19 `3333`）：
             * 名字只给出一种顺序时，`.002` 与 `.003` 会被排反 ⇒ 引擎必然报缺卷。
             * 按第 ④ 条：名字给不出确定顺序时，先用高概率顺序、再逐种排列试开；
             * 试开只决定顺序，不把整组判死。⛔ 只在 ≤5 片时排列（用户口径）。
             */
            VolumeContentFormat trialProbeFamily;

            try
            {
                trialProbeFamily = FileNature.Inspect(firstVolume).Format;
            }
            catch
            {
                trialProbeFamily = VolumeContentFormat.Unknown;
            }

            VolumeNamingFamily? fallbackOrderFamily = trialProbeFamily switch
            {
                VolumeContentFormat.SevenZip => VolumeNamingFamily.SevenZipNumbered,
                _ => null
            };

            if (fallbackOrderFamily is VolumeNamingFamily orderFamily
                && pieces.Count >= 2
                && pieces.Count <= VolumeOrderTrial.MaxPieces)
            {
                IReadOnlyList<IReadOnlyList<string>> fallbackOrders = VolumeOrderTrial.BuildCandidateOrders(
                    pieces,
                    null,
                    orderFamily,
                    referenceBase);

                if (fallbackOrders.Count > 1)
                {
                    AppendLog(
                        "INFO",
                        $"试开定序（{orderFamily}）—— 片数 {pieces.Count}｜可试顺序 {fallbackOrders.Count} 种");

                    foreach (IReadOnlyList<string> order in fallbackOrders)
                    {
                        string first = order[0];
                        var restOrder = order
                            .Skip(1)
                            .Where(piece => File.Exists(piece))
                            .Select(piece => new VolumeCandidate { Path = piece, Size = FileSizeOrZero(piece) })
                            .ToList();

                        var outcomeOrder = await verifierFallback.VerifyAsync(
                                first,
                                new List<IReadOnlyList<VolumeCandidate>> { restOrder },
                                cancellationToken,
                                Path.Combine(entryLayer, ".ArchiveFixer.work"),
                                trialProbeFamily)
                            .ConfigureAwait(false);

                        if (outcomeOrder.Confirmed)
                        {
                            return new GroupOrderTrialResult
                            {
                                Applicable = true,
                                Confirmed = true,
                                OrderedPieces = order,
                                Stem = referenceBase,
                                Family = orderFamily,
                                Reason = $"按内容/名字的高概率顺序试开通过（第 {outcomeOrder.Attempts} 种顺序）"
                            };
                        }
                    }
                }
            }

            /*
             * 试开用的硬链接名要**按卡上的族**拼（用户 2026-10-17：「底层识别先，名字配合行动」）：
             * RAR 组若仍按 7z 的 `.001` 建链接，引擎必然报「分卷压缩包缺少必要分卷」⇒ 整组永远"试开没通过"。
             */
            VolumeContentFormat probeFamily;

            try
            {
                probeFamily = FileNature.Inspect(firstVolume).Format;
            }
            catch
            {
                probeFamily = VolumeContentFormat.Unknown;
            }

            var outcomeFallback = await verifierFallback.VerifyAsync(
                    firstVolume,
                    new List<IReadOnlyList<VolumeCandidate>> { rest },
                    cancellationToken,
                    Path.Combine(entryLayer, ".ArchiveFixer.work"),
                    probeFamily)
                .ConfigureAwait(false);

            if (outcomeFallback.Confirmed)
            {
                return new GroupOrderTrialResult
                {
                    Reason = "按名字/内容给出的顺序试开通过（没有走通用分片的内容定序那一档）"
                };
            }

            return GroupOrderTrialResult.Fail(
                "硬链接试开没通过（引擎打不开这一组）⇒ 整组不动：" + outcomeFallback.Reason);
        }

        /// <summary>
        /// 通用分片试开定序的候选：**内容确认是 ZIP 分片 + 基名与本组相关**，并按盘上物理身份去重。
        /// ⛔ 不读名字的卷号来排次序（名字只拿来算相似度、给高概率顺序）；⛔ 内容可证不是分卷成员的一律剔除。
        /// </summary>
        private List<FileNatureCard> CollectNumberedZipTrialPieces(
            IReadOnlyList<string>? pieces,
            IReadOnlyList<string>? candidatePool,
            string referenceBase,
            string referenceName)
        {
            var cards = new List<FileNatureCard>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string path in (pieces ?? Array.Empty<string>())
                         .Concat(candidatePool ?? Array.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !seenPaths.Add(path)
                    || !File.Exists(path))
                {
                    continue;
                }

                FileNatureCard card;

                try
                {
                    card = FileNature.Inspect(path);
                }
                catch
                {
                    continue;
                }

                if (VolumePieceFacts.ContentProvesNotAVolumePiece(card))
                {
                    continue;
                }

                bool contentSaysPiece = card.ZipEndRecordMeansSplit
                    || card.HasZipLocalHeader
                    || card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || card.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece;

                /*
                 * **中间片可能连格式都认不出**（2026-10-18 `30303030` 组合 6 运行期定案）：
                 * 通用分片从数据中间切开 ⇒ 第 2 片开头既不是本地文件头、也没有 EOCD，
                 * `FileNature` 只能答 `Unknown`；而它确实是这一组的一片（它的字节数、与相邻片的关系
                 * 都已经由收拢候选池的准入判据 `AcceptsAsGroupPiece` 过了）。
                 * ⇒ 这一档**允许 `Unknown` 进试开候选**（基名相关是调用方已过的弱证据；
                 * 最终顺序仍由引擎真的解一遍回答，⛔ 不是靠这条放行它就当真）。
                 * ⛔ 内容可证"不是分卷成员"的仍然一律剔除（上面那一条）。
                 */
                bool unknownMiddleStillPossible = card.Format == VolumeContentFormat.Unknown
                    && card.BodyKind == ArchiveBodyKind.Unknown;

                if (!contentSaysPiece && !unknownMiddleStillPossible)
                {
                    continue;
                }

                bool related = referenceBase.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(card.ArchiveBaseName, referenceBase);

                if (!related && referenceName.Length > 0)
                {
                    related = VolumeGroupDetector.BelongsToSameGroup(
                        FileNameHelper.GetFileName(path),
                        referenceName);
                }

                if (!related)
                {
                    continue;
                }

                string identityKey = card.HasIdentity
                    ? "I:"
                      + card.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + ":"
                      + card.FileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "P:" + path;

                if (indexByIdentity.TryGetValue(identityKey, out int existing))
                {
                    /*
                     * 同一份物理文件的第二条名字：留**卡上自证规范名**的那一条（用户口径「同一份不出现两条名字」）；
                     * 两条都不是规范名 ⇒ 留先出现的那条（判不出就不动）。
                     */
                    if (!VolumePieceFacts.IsNameItsOwnCanonicalName(cards[existing])
                        && VolumePieceFacts.IsNameItsOwnCanonicalName(card))
                    {
                        cards[existing] = card;
                    }

                    continue;
                }

                indexByIdentity[identityKey] = cards.Count;
                cards.Add(card);
            }

            return cards;
        }

        /// <summary>
        /// 通用分片的末片：**只认内容**（带 EOCD 的那一片）。用户口径：通用分片只有末卷能从内容确认。
        /// 多个 EOCD / 一个都没有 ⇒ 判不出（⛔ 不猜）。
        /// </summary>
        private static string? ResolveNumberedZipTail(IReadOnlyList<FileNatureCard> cards)
        {
            var endPieces = cards.Where(card => card.ZipEndRecordMeansSplit).ToList();

            if (endPieces.Count == 1)
            {
                return endPieces[0].Path;
            }

            if (endPieces.Count > 1)
            {
                var byName = endPieces
                    .Select(card => new
                    {
                        card,
                        number = VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(card.Path))
                    })
                    .Where(item => item.number != null)
                    .OrderByDescending(item => item.number)
                    .ToList();

                return byName.Count > 0 ? byName[0].card.Path : null;
            }

            return null;
        }

        /// <summary>
        /// 跨盘 ZIP 的末片：**只认内容**（`VolumePieceFacts.IsSpannedZipTailPiece`：格式 ZIP + 内容成员位 +
        /// 卷号 == 自述总片数）。判不出 / 多个互不相同 ⇒ null（⛔ 不猜）。
        /// </summary>
        private static string? ResolveSpannedZipTail(IReadOnlyList<FileNatureCard> cards)
        {
            var tails = cards.Where(card => VolumePieceFacts.IsSpannedZipTailPiece(card)).ToList();

            if (tails.Count == 0)
            {
                tails = cards
                    .Where(card => card.ZipEndRecordMeansSplit
                        && card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)
                    .ToList();
            }

            if (tails.Count == 1)
            {
                return tails[0].Path;
            }

            if (tails.Count > 1)
            {
                var byNumber = tails
                    .Where(card => card.VolumeNumber != null)
                    .OrderByDescending(card => card.VolumeNumber)
                    .ToList();

                return byNumber.Count > 0 ? byNumber[0].Path : null;
            }

            return null;
        }

        /// <summary>试开定序用的基名：优先取末片卡上的归档基名（唯一出口），判不出才用参照基名。</summary>
        private static string ResolveOrderTrialStem(
            IReadOnlyList<FileNatureCard> cards,
            string? tailPath,
            string referenceBase)
        {
            if (!string.IsNullOrWhiteSpace(tailPath))
            {
                FileNatureCard? tail = cards.FirstOrDefault(card =>
                    string.Equals(card.Path, tailPath, StringComparison.OrdinalIgnoreCase));

                if (tail != null && tail.ArchiveBaseName.Length > 0)
                {
                    return tail.ArchiveBaseName;
                }
            }

            return referenceBase;
        }

        /// <summary>拿不到大小就按 0（<see cref="FileInfo"/> 对正被别的进程占着的文件会抛）。</summary>
        private static long FileSizeOrZero(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private async Task<List<ArchiveTask>> RecheckDeferredVolumeDeficitsAsync(bool finalPass, CancellationToken cancellationToken)
        {
            var toRun = new List<ArchiveTask>();

            /*
             * 先给"吐出这一片的那几单"收场（真机第九批 CCCC 的第二个现场问题：`111.rar` / `111(2)_.zip`
             * 两个原包为什么还留着）。判据只有两条事实位：那份"谁在解"的账 + 消费方的**机器终态**（成功）。
             * 位置在名单判据之前 —— 与"缺卷留到最后再判"那份名单无关，幂等。
             */
            SettleSucceededGroupPieceProducers();

            /*
             * ⛔ **已验证组里的跟班片也在这里收场**（2026-10-18 用户 `&&` 口径，`22222222` 现场）：
             * 跟班片（`IsVolumeGroupFollower` + 机器终态成功）的源片有时不在“谁吐过片”那本账里
             * （它没吐过东西、只是被借去拼组）⇒ 上面那一档扫不到它，源片一直留着。
             * 判据只有两条事实位：① 它是跟班 + 成功；② 这一组里有一个**可证完整且校验通过**的消费方
             * （`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——`&&` 的第二步成立。
             * ⛔ 判不出消费方 / 组不完整 / 校验没过 ⇒ 什么都不做。
             */
            SettleVerifiedGroupFollowers();

            /*
             * 再给"这一组的内容已经由本批另一单解出来了"的那几单收场（真机 CCCC 2026-10-06）。
             *
             * 现场（用户：「原包怎么还没有删除」「其余物怎么还留着」）：`111.z0删除2` / `111.z0删除3`
             * 是散在 `111(3)`/`111(4)` 的**续卷片**，整组由 `111.rar` 那一单解开 ⇒ 它们按跟班卷收场、
             * **源片却一直留在盘上** —— 因为上面那一档只认"吐出过片的那几单"（`_groupPieceProducers`），
             * 而这两单自己没吐过任何东西，从没进过那份账。
             *
             * 判据只有两条**事实位**：① 这一单的路径在既有那份"借来用过的源片"账（`_consumedVolumeSources`）
             * 里；② 那位消费方**真的成功了**（机器终态）。⛔ 与"如实报缺卷"那条路**同一个事实位**，
             * ⛔ 不另造第二套判据；⛔ 搬运仍只有既有出口（六道闸门一条都不绕过）。
             */
            SettleConsumedGroupVolumeSources(logEntries: null);

            if (_volumeDeficitDeferred.Count == 0)
            {
                return toRun;
            }

            foreach (ArchiveTask task in _volumeDeficitDeferred.ToList())
            {
                if (task == null)
                {
                    _volumeDeficitDeferred.Remove(task!);
                    continue;
                }

                /*
                 * 这一单**进批末重判之前**的落点（试开暂存会把 `task.CurrentPath` 改到暂存组入口，
                 * 不能让产物跟着写进 `.ArchiveFixer.work`）—— 恢复时用这一份，⛔ 不用改过之后的。
                 */
                string taskOutputPathBeforeRecheck = task.OutputPath;

                // ① 再收一次卷：那时缺的那几卷可能已经被解出来 / 被收拢到位。
                TryGatherVolumesBeforeExtract(task, out _);

                /*
                 * ①.5 这一组的内容**正在由本批另一单解出来**、而它还没收尾 ⇒ 这一单**先不落结论**。
                 *
                 * 真机第九批 CCCC 的形状：同一组的片各自是一单（`111.z0删除2` / `111.z0删除3`），
                 * 批末补齐之后只有其中一单去把整组解开；另一单若在**同一趟**里就去判"仍然缺"，
                 * 会得到一句"分卷缺失、未开始解压"—— 而那一刻内容正在被解开（用户原话：
                 * 别在解压过程中就报分卷缺失）。等消费方收尾之后，下一次重判会走既有的
                 * <see cref="TryResolveConsumedByAnotherTask"/> 把它按**跟班卷**收场。
                 */
                if (TryGetGroupConsumerPending(task, out string pendingConsumer))
                {
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：这一组的内容正在由「{pendingConsumer}」解出来 —— 这一单先不动，等它收尾再判（不提前把话说死）。");

                    continue;
                }

                /*
                 * ② 现归一次组（盘上事实）—— **先把散着的片收到入口包那一层，再看齐不齐**
                 * （用户 2026-10-06：「111.zip 的位置已经确定，你把分卷移过来不就是了吗」）。
                 *
                 * 为什么必须先收：`VolumeGroupDetector.Group` 的桶键是**目录 + 基名 + 族** ⇒
                 * 片散在 `111(3)\`、入口在 `111\111\` 时，两边会各自成一个"不完整桶"，
                 * 直接问它必然回"仍然缺卷"（一句会把人指错的结论）。
                 * ⛔ 判据一个字没放宽：收完仍然只由它回答"齐不齐"。
                 */
                string entryLayer = ResolveBatchGroupDirectory(
                    FileNameHelper.GetArchiveBaseName(ResolveDeferredGroupMemberPath(task)),
                    task,
                    ResolveDeferredGroupMemberPath(task));

                /*
                 * ===== 族规则判不出"该拼到哪一层"时，让**底层识别卡**补一次 =====
                 * （用户 2026-10-17 第 4 条：「第一卷竟然还有弄不成的情况，就是识别出入口包的情况」）
                 *
                 * 只在 `entryLayer` **为空**时补：手上那一份如果自证是**首卷**（`VolumeFirstPiece`，
                 * 名字被改到族规则认不出时它是唯一还认得出"我是入口"的证据），那它所在的层就是入口层 ——
                 * 引擎就是拿它开始解的。⛔ 已经判出层的一个字都不改；⛔ 卡也判不出 ⇒ 照旧什么都不做。
                 */
                if (entryLayer.Length == 0)
                {
                    string ownMemberPath = ResolveDeferredGroupMemberPath(task);

                    if (!string.IsNullOrWhiteSpace(ownMemberPath) && File.Exists(ownMemberPath))
                    {
                        try
                        {
                            CardVolumeGroupReading ownCard =
                                VolumePiecesFromCards.Read(new[] { FileNature.Inspect(ownMemberPath) });

                            if (ownCard.EntryPath.Length > 0)
                            {
                                entryLayer = Path.GetDirectoryName(ownCard.EntryPath) ?? string.Empty;

                                if (entryLayer.Length > 0)
                                {
                                    AppendLog(
                                        "INFO",
                                        $"{task.LogName}：这一组该拼到哪一层 —— 族规则判不出，改由底层识别卡指出入口"
                                        + $"（卡上自证首卷=「{FileNameHelper.GetFileName(ownCard.EntryPath)}」）");
                                }
                            }
                        }
                        catch
                        {
                            // 卡读不出来 ⇒ 照旧什么都不做。
                        }
                    }
                }

                /*
                 * ===== 层判不出 ⇒ 读**接片那一档算过的层**（唯一出口 `_groupLayerByBaseName`）=====
                 *
                 * <para>2026-10-18 困难批 `30303030` 现场：三片都已按内容规范名 `.001/.002/.003`
                 * 接到 `333(2)` 那一层，可批末重判时这一单手上那一份在**已删掉的工作区**里 ⇒
                 * 族规则与卡都判不出入口层 ⇒ 逐字 `该拼到哪一层=「」｜这一层不在盘上 ⇒ 判不出完整的一组`
                 * ⇒ 整组明明齐了却不开始解压。</para>
                 *
                 * <para>判据不另造：`_groupLayerByBaseName` 就是接片真的落地那一刻记下的层
                 * （`ExtractionCoordinator` 里"层只有一个出口"那条注释指的就是它）。⛔ 只在这一单判不出时用；
                 * 目录不存在 ⇒ 照旧空（什么都不做）。</para>
                 */
                if (entryLayer.Length == 0)
                {
                    string ledgerBaseName = FileNameHelper.GetArchiveBaseName(
                        ResolveDeferredGroupMemberPath(task));

                    if (ledgerBaseName.Length > 0
                        && _groupLayerByBaseName.TryGetValue(ledgerBaseName, out string? assembledLayer)
                        && !string.IsNullOrWhiteSpace(assembledLayer)
                        && Directory.Exists(assembledLayer))
                    {
                        entryLayer = assembledLayer!;

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：这一组该拼到哪一层 —— 族规则与卡都判不出，"
                            + $"改用接片那一档算过的层「{assembledLayer}」");
                    }
                }

                ArchiveFixer.Detection.CardVolumeGroupReading? gatherCardEvidence = null;
                bool contentOrderUndetermined = false;
                bool stagedOrderGroup = false;
                IReadOnlyList<string>? confirmedOrderForGroup = null;
                VolumeNamingFamily? confirmedFamilyForGroup = null;
                bool confirmedGroupForTask = false;

                /*
                 * 组解析用的层：老路 = 入口层；试开确认后 = 全新的硬链接暂存目录。
                 * ⛔ `entryLayer` 本身（落点/任务层）**不改**，否则组任务会把产物写进 `.ArchiveFixer.work`。
                 */
                string groupResolutionLayer = entryLayer;

                if (entryLayer.Length > 0)
                {
                    /*
                     * ⚠ 一行"这一组该拼到哪一层 = 哪儿"（2026-10-11 加）：这是批末唯一会**收片**的地方，
                     * 而它此前一个字都不写 —— 判出来没判出来、"片收去哪儿了"在用户日志里都看不见
                     * （真机 FFFF 那一批就是这么卡住的）。⛔ 只写程序当场算出来的值。
                     */
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.GroupLayerResolvedAtBatchEndFormat,
                            task.LogName,
                            entryLayer));

                    /*
                     * **收拢决策器接线（第一步：只观测、不改行为）** —— 2026-10-17 第 161 轮。
                     *
                     * `VolumeGatherPlanner` 实现用户口径的 ②分组（族+基名+卷号，至少两片）与
                     * ③硬链接试开证实（体积规律 + 试开回调）。这一轮先只把结论与理由写进日志
                     * （⛔ 不改变任何搬运行为），拿到运行期分布之后，下一轮才按 `Gather/Leave` 分流。
                     *
                     * 候选 = 这一层里与"这一单手上那一片"同族的文件（既有尺子 `BelongsToSameGroup`），
                     * ⛔ 不递归、⛔ 不猜。
                     */
                    List<string> gatherCandidates = CollectGroupCandidatePaths(
                        task,
                        entryLayer,
                        out string gatherReference,
                        out int gatherScannedInEntryLayer);

                    /*
                     * ===== 底层识别卡那一行（用户 2026-10-17：「分卷的底层识别算法还要加强」）=====
                     * 7z 的中间卷**内容里没有卷号**，只有首卷的起始头自述"整包多少字节 / 共几片" ——
                     * 所以收拢这一步要能一眼看出"这一层里到底有没有首卷、合计对不对得上"。
                     * 判据唯一出口 <see cref="VolumePiecesFromCards"/>（三条证据：首卷自述 → 体积规律 → 合计）。
                     * ⛔ 只写事实、不改搬运行为（这一轮只接线这一行）。
                     */
                    var gatherCards = new List<FileNatureCard>();

                    foreach (string candidatePath in gatherCandidates)
                    {
                        try
                        {
                            gatherCards.Add(FileNature.Inspect(candidatePath));
                        }
                        catch
                        {
                            // 读不出来就少一张卡（判据那边会如实说"判不出"）。
                        }
                    }

                    CardVolumeGroupReading cardReading = VolumePiecesFromCards.Read(gatherCards);

                    gatherCardEvidence = cardReading;

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：底层识别卡 —— {cardReading.Reason}"
                        + $"（卡上首卷={(cardReading.HasFirstPiece ? "有" : "没有")}"
                        + $"｜片数={cardReading.PieceCount}"
                        + $"｜自述={cardReading.DeclaredTotalBytes}"
                        + $"｜合计={cardReading.SumBytes}"
                        + (cardReading.Slots.Count == 0
                            ? "｜槽位=判不出"
                            : "｜槽位=" + string.Join(
                                " → ",
                                cardReading.Slots.Select(slot =>
                                    slot.Index + ":" + FileNameHelper.GetFileName(slot.Path)))) + "）");

                    /*
                     * ===== (d) 目标层 = **入口包所在层**，而"入口包是哪一份"由底层识别优先回答 =====
                     * 名字那把尺子判出来的层与卡指出的入口层不一致时，**以卡为准**（用户 2026-10-17 总则）。
                     * ⛔ 只在两边都判得出、且确实不同时才换；卡判不出/盘上没有 ⇒ 一个字不动。
                     */
                    if (cardReading.EntryPath.Length > 0)
                    {
                        string cardEntryLayer = Path.GetDirectoryName(cardReading.EntryPath) ?? string.Empty;

                        if (cardEntryLayer.Length > 0
                            && !string.Equals(cardEntryLayer, entryLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：这一组该拼到哪一层 —— 名字判的是「{Path.GetFileName(entryLayer)}」，"
                                + $"底层识别卡指出入口在「{Path.GetFileName(cardEntryLayer)}」⇒ 以卡为准");

                            entryLayer = cardEntryLayer;
                        }
                    }

                    /*
                     * 分组决策：名字那把尺子先分组；**归不出组时把卡证据喂进去**（底层识别优先）。
                     */
                    VolumeGatherPlan gatherPlan = VolumeGatherPlanner.Decide(
                        gatherCandidates,
                        entryLayer,
                        probeOpens: null,
                        cardEvidence: cardReading);

                    /*
                     * ⛔ 底层识别卡说"还差字节" ⇒ 这一趟不搬（用户 2026-10-17：「三条证据任何一条不成立 ⇒ 不动」）。
                     * 只认一条硬事实：**首卷自述的整包字节数 > 手上这几片的合计**。
                     * ⚠ 措辞仍然只是"这一趟先不收"—— 对整组的判决留给第二大步（与试开那条同一个口径）。
                     */
                    if (gatherPlan.Action == VolumeGatherAction.Gather
                        && cardReading.HasFirstPiece
                        && cardReading.MissingBytes > 0)
                    {
                        long missingByCard = cardReading.MissingBytes;

                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一趟先不收（底层识别卡说还差 {missingByCard} 字节，留到第二大步再判）");

                        gatherPlan = VolumeGatherPlan.Leave(
                            $"这一趟先不收（卡上说还差 {missingByCard} 字节，留到第二大步再判）");
                    }

                    /*
                     * ===== (c) 硬链接试开 / 内容定序（用户 2026-10-17 四步里的第三步 + 2026-10-18 顺序口径）=====
                     *
                     * ① **通用分片**（`X.zip.001..00N`）：中间片内容里没有卷号 ⇒ 先按相似度排高概率顺序、
                     *    真的解一遍验证（硬链接试开）；全试不出 ⇒ 判不出（这一趟不按名字硬拼）。
                     * ② 其余族（RAR / 7z / 跨盘 ZIP）：卷序由内容或名字明确给出，照旧只按那一种顺序列一次目录。
                     *
                     * ⛔ 试开是只读的；赢的那一版**只交顺序**，接片仍走既有出口 `TryGatherGroupPiecesInto`
                     * （它会记"谁吐出的片、谁借的片"，批末收场才能把产出方那一份按档收干净）。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：试开前决策 = {gatherPlan.Action}（{gatherPlan.Reason}）"
                        + $"｜同族候选 {gatherCandidates.Count} 个");

                    GroupOrderTrialResult probe = await ProbeGatherGroupAsync(
                            gatherPlan.Pieces,
                            gatherCandidates,
                            entryLayer,
                            cardReading.EntryPath,
                            gatherReference,
                            allowFallback: gatherPlan.Action == VolumeGatherAction.Gather,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (probe.Failure != null)
                    {
                        /*
                         * ⛔ 试开**只是"这一次先不搬"的闸门，不是对整组的判决**（用户 2026-10-17：
                         * 「只是简单的判断一下，不能够形成判决情况，因为这个是第二大步才做的」）。
                         * ⇒ 这里只写"这一趟先不收"，把结论留给第二大步；⛔ 不写"整组不动/判不出"。
                         *
                         * ⚠ 但**内容定序这一档适用却失败**（片数 ≤5、末片可定、引擎全试不出）⇒ 名字那把尺子
                         * 也不能再拿来硬拼一个"完整的一组"（那正是 `30303030` 反复假完整的来源）：
                         * 记下"判不出"，下面认组那一站据此跳过。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一趟先不收（硬链接试开没通过，留到第二大步再判）—— {probe.Failure}");

                        gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（试开没通过，留到第二大步再判）");
                        contentOrderUndetermined = probe.Applicable;
                    }
                    else if (probe.Confirmed)
                    {
                        confirmedOrderForGroup = probe.OrderedPieces;
                        confirmedFamilyForGroup = probe.Family;
                        confirmedGroupForTask = true;
                        /*
                         * 试开真的解开了这个顺序 ⇒ 先把它按规范卷名落进一个**全新的硬链接暂存目录**，
                         * 用暂存目录当组入口（2026-10-18 新批 `24242424`）：原入口层里可能已经有早先建的
                         * `.z01/.z02` 旧名字，与内容定出的新槽位撞名；而那些旧名字同时被组任务写进
                         * `VolumePaths`，受"用户源"红线保护，原地删不得。暂存目录是全新的，不撞任何名字，
                         * 也不碰用户/任务源；接片仍只建零字节硬链接。
                         */
                        ArchiveFixer.Detection.CardVolumeGroupReading? stagedReading =
                            VolumeOrderMaterializer.TryStageConfirmedOrder(
                                entryLayer,
                                probe.OrderedPieces,
                                probe.Stem,
                                probe.Family,
                                out string stagingDirectory,
                                out string stageFailure);

                        if (stagedReading != null)
                        {
                            cardReading = stagedReading;
                            gatherCardEvidence = stagedReading;
                            groupResolutionLayer = stagingDirectory;
                            stagedOrderGroup = true;
                            _confirmedOrderStagingDirectories.Add(stagingDirectory);
                            _stagedGroupOutputPaths[task] = taskOutputPathBeforeRecheck;

                            /*
                             * 这一组用的那几片里，来自**包装壳产出**的（`333-W1-0\333-L2.pdf`）
                             * 要记进"产出方那一份已经落地的片"那本账 —— 批末收场才会把包装壳的源包
                             * 与其产出片按档收干净（⛔ 用户源片由 `IsAnyTaskSourcePath` 自动排除）。
                             */
                            foreach (string orderedPiece in probe.OrderedPieces)
                            {
                                RememberAdoptedLandedPiece(
                                    orderedPiece,
                                    task,
                                    probe.Stem,
                                    renamedInPlace: false);
                            }

                            gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                                VolumeGatherAction.Gather,
                                "内容试开定序成立 ⇒ 用硬链接暂存目录当组入口",
                                probe.OrderedPieces);

                            AppendLog(
                                "INFO",
                                $"{task.LogName}：{probe.Reason}；已按规范卷名接进硬链接暂存目录"
                                + $"「{Path.GetFileName(stagingDirectory)}」，原入口层一个名字都不动");
                        }
                        else
                        {
                            AppendLog(
                                "WARN",
                                $"{task.LogName}：硬链接暂存目录没建成（{stageFailure}）⇒ 退回入口层接片老路");

                            ArchiveFixer.Detection.CardVolumeGroupReading? confirmedReading =
                                VolumeOrderMaterializer.BuildConfirmedReading(
                                    probe.OrderedPieces,
                                    probe.Stem,
                                    probe.Family);

                            if (confirmedReading == null)
                            {
                                AppendLog(
                                    "WARN",
                                    $"{task.LogName}：这一趟先不收（内容试开定序成立了，但折不出可用的内容读取）—— 整组不动");

                                gatherPlan = VolumeGatherPlan.Leave("这一趟先不收（内容读取折不出来）");
                                contentOrderUndetermined = true;
                            }
                            else
                            {
                                cardReading = confirmedReading;
                                gatherCardEvidence = confirmedReading;

                                gatherPlan = new ArchiveFixer.Extraction.VolumeGatherPlan(
                                    VolumeGatherAction.Gather,
                                    "内容试开定序成立 ⇒ 按赢的那一版接进入口层",
                                    probe.OrderedPieces);

                                AppendLog("INFO", $"{task.LogName}：{probe.Reason}");
                            }
                        }
                    }

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：收拢决策 = {gatherPlan.Action} —— {gatherPlan.Reason}"
                        + $"（这一层同族候选 {gatherCandidates.Count} 个｜这一层文件 {gatherScannedInEntryLayer} 个"
                        + $"｜参照名=「{(gatherReference.Length == 0 ? "判不出" : gatherReference)}」）");

                    /*
                     * ⛔ **名字归不出组、但底层识别卡可证完整 ⇒ 允许搬**（用户 2026-10-17 总则：底层优先）。
                     * 现场形状：片名被改烂到 `VolumeGroupDetector.Group` 一个组都归不出来，
                     * 而卡上"首卷自述 + 体积规律 + 合计对上"三条全成立 —— 这时名字只是线索坏了，
                     * 事实（整组齐）是成立的。搬运本体仍走 `TryGatherGroupPiecesInto`（它自己的
                     * 卡证据收源 + 那几道闸门一条不落），这里只负责**放行**。
                     */
                    bool allowMoveIntoGroup = gatherPlan.Action == VolumeGatherAction.Gather;

                    if (!allowMoveIntoGroup
                        && cardReading.Complete
                        && cardReading.EntryPath.Length > 0
                        && File.Exists(cardReading.EntryPath))
                    {
                        allowMoveIntoGroup = true;

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：收拢决策 = Gather（名字归不出组，但底层识别卡可证完整）—— {cardReading.Reason}");
                    }

                    if (!stagedOrderGroup)
                    {
                        TryGatherGroupPiecesInto(
                            task,
                            entryLayer,
                            _deferredVolumeGroupMember.TryGetValue(task, out string? stalledMember) ? stalledMember : null,
                            allowMoveIntoGroup: allowMoveIntoGroup,
                            cardEntryPath: cardReading.EntryPath,
                            cardEvidence: cardReading);

                        /*
                         * ⚠ 2026-10-13（第 15 修之后）：收片只认名字 ⇒ 名字被伪装到"读不出卷号"的那几片收不进来。
                         * 再走一步**字节数**那条硬证据（7z 起始头自述）把缺口按规范槽位名**硬链接**补上
                         * （⛔ 原件一个字节都不动；凑不出正好相等就一份都不补）。
                         */
                        TryLinkMissingPiecesByByteBudget(
                            task,
                            entryLayer,
                            _deferredVolumeGroupMember.TryGetValue(task, out string? stalledForBudget) ? stalledForBudget : null,
                            cardEntryPath: cardReading.EntryPath);
                    }
                }

                /*
                 * ⛔ **优先按内容定的规范名认这一组**（2026-10-18，困难批 `19191919`）：
                 * 入口 = 槽位 1 的规范名（通用分片 `X.zip.001`），⛔ 不让用户那一份原来的名字
                 * （`X.zip`，同一份文件的别名）冒充入口 —— 否则引擎按跨盘族入口名去找 `.z01`
                 * ⇒ `Unexpected end of archive` ⇒ 整组判缺卷（真跑逐字）。判不出 ⇒ 照旧老口径。
                 */
                VolumeGroup? group = (gatherCardEvidence == null
                        ? null
                        : ResolveCanonicalGroupFromCardSlots(groupResolutionLayer, gatherCardEvidence))
                    ?? ResolveGatheredGroupFromEntryLayer(
                            task,
                            entryLayer,
                        _deferredVolumeGroupMember.TryGetValue(task, out string? stalledForGroup) ? stalledForGroup : null)
                    ?? ResolveCompleteGroupFromDisk(task, entryLayer);

                /*
                 * ⛔ **内容定序判不出 ⇒ 不许名字那把尺子再拼一个"完整的一组"**（用户 2026-10-18 口径：
                 * 全试不出 ⇒ 判不出、没内容物就如实显示失败；⛔ 不按名字硬拼）。
                 * 现场（`30303030` 组合 6 批内）：三份名字里有两份是同一份物理片的两个别名 ⇒ 名字判「3 卷完整」，
                 * 引擎打开必然 `Unexpected end of archive`；而内容试开已经回答"这一组凑不齐"。
                 */
                if (group != null && contentOrderUndetermined)
                {
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：内容试开定序判不出 ⇒ 这一组这一趟不动"
                        + "（⛔ 不按名字硬拼一个完整组；留下现场，等补片/下一轮再判）");

                    group = null;
                }

                if (group != null)
                {
                    string firstVolume = group.FirstVolumePath;

                    /*
                     * ===== 组事实只接“源层”这一项（2026-10-18 第 64 轮回退后的增量接线）=====
                     * 组齐这一刻把源层记下来；入口层若落在过程物目录（`其余物`/工作区），
                     * 用组事实的源层，⛔ 不把工作区当入口层。owner/顺序本轮不接，避免一次改三处。
                     */
                    string groupBaseName = string.IsNullOrWhiteSpace(group.BaseName)
                        ? FileNameHelper.GetArchiveBaseName(firstVolume)
                        : group.BaseName;

                    VolumeNamingFamily? groupFamily = gatherCardEvidence?.NamingFamily;

                    /*
                     * 源层候选按“第一个不在过程物里的层”取：当前落点 → 用户原始路径所在层 →
                     * 第 1 卷所在层 → 入口层。⛔ 不再无条件用 `Path.GetDirectoryName(firstVolume)`：
                     * 第 1 卷本身可能已经被搬进 `其余物`（`19191919` 现场），那一层不是源层。
                     */
                    string groupSourceLayer = new[]
                        {
                            taskOutputPathBeforeRecheck,
                            Path.GetDirectoryName(task.OriginalPath ?? string.Empty) ?? string.Empty,
                            Path.GetDirectoryName(firstVolume) ?? string.Empty,
                            entryLayer
                        }
                        .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)
                            && !ProcessArtifactLayout.IsInsideDeletableProcessFolders(candidate))
                        ?? string.Empty;

                    string groupUnitKey = groupBaseName + "|" + (groupFamily?.ToString() ?? "?");

                    if (!_volumeGroupUnits.TryGetValue(groupUnitKey, out VolumeGroupUnit? groupUnit))
                    {
                        groupUnit = new VolumeGroupUnit(
                            groupBaseName,
                            groupFamily,
                            groupSourceLayer,
                            firstVolume);

                        _volumeGroupUnits[groupUnitKey] = groupUnit;

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：组事实建立 —— 基名「{groupBaseName}」｜族={groupFamily?.ToString() ?? "判不出"}"
                            + $"｜源层「{Path.GetFileName(groupSourceLayer)}」｜入口「{FileNameHelper.GetFileName(firstVolume)}」");
                    }

                    TryPurgeConfirmedSevenZipGroupSourceAtFact(task, groupUnit, group);

                    /*
                     * 试开确认的顺序 + 唯一 owner **落在组事实上**（⛔ 不能只留在局部变量里）：
                     * 下面“这一单已经成功过”的守卫要读它；没有它，`3333` 那种“只先解出一片”的成功
                     * 会把整组跳过，组一次都开不了。
                     */
                    if (confirmedGroupForTask && confirmedOrderForGroup != null)
                    {
                        if (groupUnit.ConfirmedOrder == null)
                        {
                            groupUnit.ConfirmedOrder = confirmedOrderForGroup;
                        }

                        if (groupUnit.OwnerTaskFileName == null)
                        {
                            groupUnit.OwnerTaskFileName = task.FileName;
                        }
                    }

                    string groupLanding = string.Equals(
                        Path.GetFileName(groupUnit.SourceLayer),
                        groupBaseName,
                        StringComparison.OrdinalIgnoreCase)
                            ? groupUnit.SourceLayer
                            : Path.Combine(groupUnit.SourceLayer, groupBaseName);

                    if (!stagedOrderGroup
                        && groupUnit.SourceLayer.Length > 0
                        && (entryLayer.Length == 0
                            || ProcessArtifactLayout.IsInsideDeletableProcessFolders(entryLayer)))
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一组的入口层是过程物层「{Path.GetFileName(entryLayer)}」—— "
                            + $"改用组事实里的源层「{Path.GetFileName(groupUnit.SourceLayer)}」");

                        entryLayer = groupUnit.SourceLayer;
                        groupResolutionLayer = groupUnit.SourceLayer;

                        /*
                         * 只改入口层不改任务落点 ⇒ 引擎仍会按原 CurrentPath/OutputPath 把产物写回
                         * `其余物`（`19191919` 批内逐字：入口层已改，落点仍是 `其余物\pack111-L2`）。
                         * ⇒ 起点与落点一起改到源层。
                         */
                        task.CurrentPath = firstVolume;
                        task.OutputPath = groupLanding;
                        _stagedGroupOutputPaths[task] = groupLanding;
                    }
                    else if (!stagedOrderGroup
                        && groupUnit.SourceLayer.Length > 0
                        && !string.IsNullOrWhiteSpace(task.OutputPath)
                        && ProcessArtifactLayout.IsInsideDeletableProcessFolders(task.OutputPath))
                    {
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一单的落点在过程物层「{Path.GetFileName(task.OutputPath)}」—— "
                            + $"按组事实改到源层落点「{Path.GetFileName(groupLanding)}」");

                        task.CurrentPath = firstVolume;
                        task.OutputPath = groupLanding;
                        _stagedGroupOutputPaths[task] = groupLanding;
                    }

                    /*
                     * 只在“跟班互相把对方记成 consumer”这一种现场接管：把已被标成跟班成功的这一单
                     * 复位并让它开组；同一组只允许一个这样的接管者。⛔ 不动正常成功单，⛔ 不接普通 owner。
                     */
                    bool forceGroupOwner = false;

                    if (task.Outcome == TaskOutcome.Succeeded && task.IsVolumeGroupFollower)
                    {
                        if (groupUnit.OwnerTaskFileName == null)
                        {
                            AppendLog(
                                "WARN",
                                $"{task.LogName}：这一单原来被标成「跟班已成功」，但这一组还没有 owner —— "
                                + "复位成待跑，由这一单把整组正经解一次（⛔ 不清它的源片、不碰用户原件）");

                            task.Outcome = TaskOutcome.Pending;
                            task.IsVolumeGroupFollower = false;
                            task.SettledWithGroupUnitName = string.Empty;
                            forceGroupOwner = true;
                            groupUnit.OwnerTaskFileName = task.FileName;
                        }
                        else if (!string.Equals(groupUnit.OwnerTaskFileName, task.FileName, StringComparison.Ordinal))
                        {
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：这一组已有跟班接管者「{groupUnit.OwnerTaskFileName}」—— "
                                + "这一单不重复开组");

                            _volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;
                            continue;
                        }
                    }

                    /*
                     * 收齐之后**当场把这一组的状态写回任务**（`ApplyGroupInfo`：卷清单 / 缺卷名单 /
                     * 「共 N 卷」那一句）。⛔ 不能省：下一步 `ResolveCompleteGroupFromDisk` 的
                     * "账上点过名的缺卷必须真的出现"读的就是这份**名单** —— 盘上明明已经收齐、
                     * 名单却还是批首那一份（`111.zip`、`111.z01`…），就会把"已经齐了"判成"仍然缺"。
                     */
                    new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);

                    /*
                     * ⚠ 2026-10-17（困难批 `12121212`，运行期实测）：**这一单这一批已经成功过的，不许再为这一组跑第二遍**。
                     *
                     * <para>现场逐字：入口那一单（`打包111-W2-0.zip`，名字是伪装基名）第 1 轮就成功了
                     * （`定稿完成 —— 内容物 1 个文件`、内容物 = `打包111-L2.mp4`）；到了批末补判，
                     * 这一组的入口被认成**产物扫描导进来的那一份**（`打包111-L2.zip`）⇒ 判「现在齐了…照常跑这一组」
                     * ⇒ 日志逐字「落点 …\打包111-W2-0\打包111-W2-0 —— 取的是这一组分卷入口包「打包111-L2.zip」
                     * 所在的那一层…分卷组从入口包开解，产物就跟它走」⇒ **同一单又跑了一遍**，
                     * 成品两份 `content-60mb.bin`（判据 ① 红）。</para>
                     *
                     * <para>判据只读**机器终态**这一条事实（⛔ 不比中文、⛔ 不看名字）：已经 `Succeeded`
                     * = 「成功得到完整内容物」这件事已经成立 ⇒ 这一组的活不该再交给它。
                     * ⛔ 不碰它的终态、落点与账（它自己的成功结论一个字都不动）；
                     * 停在缺卷上那些单**照旧**走下面那两支（它们的终态不是 `Succeeded`）。</para>
                     */
                    if (task.Outcome == TaskOutcome.Succeeded)
                    {
                        bool ownsConfirmedGroup = groupUnit.ConfirmedOrder != null
                            && (groupUnit.Family == VolumeNamingFamily.SevenZipNumbered
                                || groupUnit.Family == VolumeNamingFamily.RarPart)
                            && string.Equals(
                                groupUnit.OwnerTaskFileName,
                                task.FileName,
                                StringComparison.Ordinal);

                        if (!ownsConfirmedGroup)
                        {
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：这一单这一批已经成功过（内容物已经得到）—— 这一组不再交给它跑第二遍"
                                + "（不重复解压：同一组只解一次）。");

                            _volumeDeficitDeferred.Remove(task);
                            task.IsVolumeDeficitDeferred = false;

                            continue;
                        }

                        /*
                         * 它只是先解出了**一片**，真正的整组顺序是刚刚试开确认的 ⇒ 把这一次成功
                         * 复位成待跑，由唯一 owner 把整组开一次（⛔ 不是“解了两遍”：上一遍不是这一组）。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：这一单先前只解出了一片，组事实已确认整组顺序 —— "
                            + "复位成待跑，由这一单把整组正经开一次（⛔ 不重复：上一遍不是这一组）");

                        task.Outcome = TaskOutcome.Pending;
                        task.EndTime = null;
                        task.IsVolumeGroupFollower = false;
                        forceGroupOwner = true;
                    }

                    /*
                     * ⛔ **这一批已经用「完全同一组片」试过一遍 ⇒ 不再排第二趟**（2026-10-17 真机 `10101010`）。
                     *
                     * <para>现场逐行：批末补判把同一组排了 21 趟，每趟都以「判出完整的一组（1 卷）」放行 ⇒
                     * 跑 ⇒ 第一枚候选就撞结构性失败（自述 62919013 / 手上 26214400 ⇒ `VolumeMissing`）⇒
                     * 组仍不完整 ⇒ 下一趟再来。这 21 趟里有 20 趟是**同一组片、同一结论**，纯属空转。</para>
                     *
                     * <para>判据只读**既有事实**（组签名 = `GroupKey` + 每一卷「路径|字节数」）：
                     * 签名相同 ⇒ 同一批同样的片已经试过 ⇒ 拒；**只要有新片进来签名就变 ⇒ 照旧放行**
                     * （批末补判"后来又补齐了"那条合法路径不受影响）。⛔ 不放宽"缺卷 ⇒ 源包原地不动"，
                     * 也⛔ 不动本节其它判据。</para>
                     */

                    /*
                     * 补齐了 ⇒ 收拢 + 正常跑这一组（起点改到第 1 卷、快照重拍）。
                     *
                     * ⚠ 第 1 卷已经由**同批另一个任务**负责时**不改起点、也不改账**：那一组的活该由
                     * 首卷那一单干（很可能它刚刚已经解成功了）。这一单照旧按"跟班卷"走既有的跳过口径 ——
                     * 改起点会让两单指着同一个文件、把同一份内容解第二遍。
                     */
                    if (IsFirstVolumeOwnedByAnotherTask(task, firstVolume) && !forceGroupOwner)
                    {
                        task.CaptureSourceSnapshot();
                    }
                    else
                    {
                        if (!string.Equals(
                                SafePathHelper.GetFullPathSafe(task.CurrentPath),
                                SafePathHelper.GetFullPathSafe(firstVolume),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            /*
                             * ⚠ 2026-10-06 试过一版"入口包在别的目录时不动 CurrentPath"，**已撤回**：
                             * 真机形状那两条 E2E 当场变成"产物一个都没有"（`Assert.NotNull` 失败）——
                             * 这一单正是靠改到入口包那一份才能把整组解开。⇒ 起点照旧改，
                             * "批末给它收场 / 搬源片"那一档改用 `OriginalPath` 判它自己那一片
                             * （见 `ResolveOwnPiecePath`），⛔ 不再动这里的起点口径。
                             */
                            new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);
                            task.CurrentPath = firstVolume;

                            /*
                             * ⛔ 起点改了，但**列表那一行仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06：
                             * "48MB 在列表里面显示到了 111(4) 那一行"）。只改显示，⛔ 起点口径一个字不动。
                             */
                            task.ShowUserFileIdentity(firstVolume, FindUserOwnFileBesideItsDirectory(task, firstVolume));
                        }

                        task.CaptureSourceSnapshot();
                    }

                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitRecheckResolvedFormat,
                            task.FileName,
                            task.VolumeInfoText));

                    _volumeDeficitDeferred.Remove(task);
                    _volumeDeficitFinalPass.Add(task);

                    /*
                     * ⚠ 2026-10-10 修：**离开名单就得把那个显示/统计事实位一起清掉**——
                     * 与下面"按跟班卷收场"那一支（`task.IsVolumeDeficitDeferred = false;`）同一套口径，
                     * 这一支原来漏了。
                     *
                     * <para><b>真机后果（运行期实测，不是推断）</b>：`111.z03` 那一单走的就是这一支
                     * （批末补齐 ⇒ 改起点 ⇒ 交 `toRun` 去把整组解开），它**成功**了（Status=「这一组解压成功」、
                     * 机器终态成功），可事实位一直是 true ⇒ 批末汇总把它算进「跳过」，
                     * 印出「成功 1 / 跳过 1」—— 那一个"跳过"其实是一件**做成了**的事。
                     * 断点证据（debug-mcp 停在 `AppendBatchSummary`）：`tasks[3].Status = "解压成功"`、
                     * `tasks[3].IsVolumeDeficitDeferred = true`、`tally.Succeeded = 1 / tally.Skipped = 1`。</para>
                     */
                    task.IsVolumeDeficitDeferred = false;

                    /*
                     * 试开定序时 `task.CurrentPath` 被改到暂存组入口（引擎要从那里开），
                     * 但**产物仍然落在这一单原来的落点**（`OutputPath`）—— 暂存目录只在
                     * `.ArchiveFixer.work` 里、批末整份清掉；⛔ 不让产物写进工作区。
                     */
                    if (stagedOrderGroup)
                    {
                        task.OutputPath = taskOutputPathBeforeRecheck;
                    }

                    toRun.Add(task);

                    /*
                     * ⚠ **这一单从现在起是"第二大步"**（用户 2026-10-11 轮次模型）：分卷阶段**只解一层** ——
                     * 解出来的东西一律交回轮判定（里面又有分卷也不就地接着解，下一轮从第二大步再来）。
                     * 位置就在"那一组齐了 ⇒ 照常跑这一组"这一刻；⛔ 不动用户的 `递归档位（已删除）` 设置。
                     */
                    RememberVolumeStepTask(task);

                    /*
                     * 这一组**其余的片**：内容已经由这一单解出来 ⇒ 记进既有那份"借来用过的源片"账
                     * （`_consumedVolumeSources`），让它们按**跟班卷**收场
                     * （`TryResolveConsumedByAnotherTask` 读的就是它；⛔ 不另造第二套判据）。
                     *
                     * 真机第九批 CCCC 的现场：这一档必须也覆盖"批末补判才补上的那一组" ——
                     * 否则同一组里另一单在批末仍然会如实报「分卷缺失」，而内容其实刚刚被这一单解出来了
                     * （用户原话：别在解压过程中就报分卷缺失）。
                     */
                    RememberGroupPiecesConsumedBy(task);

                    // 这一组的消费方是谁：同一组别的单在它收尾之前**不落任何结论**（见下面的判据）。
                    _groupConsumerByName[FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty)] = task.FileName;
                    continue;
                }

                string missing = DescribeMissingVolumeNames(task);

                /*
                 * ===== 这一组的内容**已经由本批另一个任务解出来了** ⇒ 按跟班卷收场（2026-10-05 真机第八批）=====
                 *
                 * 位置刻意排在"仍缺 ⇒ 如实报缺卷"**之前**：那三片确实还没在同一个目录里凑齐，
                 * 但它们的末片是从包里解出来的、整组已经解开并发布 —— 这一单再报「分卷缺失」就是一句
                 * 会把人指错的话（汇总写"失败 3"，批末还教用户去搬卷）。
                 */
                if (TryResolveConsumedByAnotherTask(task, out string consumerName))
                {
                    string consumedNote = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitConsumedByOtherTaskFormat,
                        task.FileName,
                        consumerName);

                    /*
                     * 跟班卷 = **不算没做成**（唯一事实位 `IsVolumeGroupFollower` ⇒ `CountsTowardBatchOutcome`）：
                     * 四处消费点（色带 / 批末诊断 / 一键汇总行 / 本批汇总）读的都是它。
                     *
                     * ⚠ **机器终态必须显式落 `Skipped`**（2026-10-05 真机复跑当场逮到）：`MarkSkipped` 只写
                     * `Status`，不写 `Outcome` ⇒ 汇总照旧把它们算成「未处理 3」。这里与既有那条跳过路
                     * （`SkipWhenAnotherTaskOwnsThisVolumeGroup`）**逐行同一套**：清校验 / 清落点 + 落终态 + 标跟班。
                     */
                    task.IsOutputVerified = false;
                    task.OutputVerification = OutputVerificationOutcome.NotAttempted;
                    task.OutputPath = string.Empty;
                    task.Outcome = TaskOutcome.Skipped;
                    task.IsVolumeGroupFollower = true;
                    task.MarkSkipped(consumedNote);

                    AppendLog("INFO", consumedNote);

                    // ⚠ 2026-10-08：离开名单 ⇒ 事实位一起清（理由见下面那一处）。
                    task.IsVolumeDeficitDeferred = false;

                    _volumeDeficitDeferred.Remove(task);
                    _volumeDeficitFinalPass.Add(task);
                    continue;
                }

                if (!finalPass)
                {
                    // 还有后续轮次 ⇒ 继续留着（⛔ 不提前把话说死）。
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeDeficitStillDeferredLogFormat,
                            task.FileName,
                            missing));

                    continue;
                }

                // ③ 到这一步才如实报缺卷（不变量 7：从头到尾没有"带着缺卷开始解"这回事）。
                string diagnosis = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeDeficitFinalDiagnosisFormat,
                    task.FileName,
                    missing);

                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitRecheckStillFormat,
                        task.FileName,
                        missing));

                /*
                 * ⚠ 2026-10-06 试过一版"批末这一档也改成「部分完成」+ WARN"，**已撤回**：
                 * 全量当场 6 条红线用例变红（`对照_一直补不上_批末才如实报缺卷`、
                 * `PipelineWiringTests.缺卷_真案一的现场_批末补判才拦下并点名缺第2卷` 等）——
                 * **"缺卷不开始解 + 如实报缺卷"是用户早先拍过并有用例钉住的口径**，
                 * 真正该改的是"内容已被接手的那些单不该走到这一支"（见下面那行排障）。
                 */
                MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);
                AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");

                /*
                 * ===== 到最后一刻仍然不完整 ⇒ **部分完成**，⛔ 不是失败（用户 2026-10-06）=====
                 *
                 * 用户原话：「假如，1_最后剩一个 `111.7z.001` 你不能说是解压失败了，这应该是部分完成，
                 * 因为这个分卷不完整，到了最后检测不到完整的；2_如果最后完整了，但是解压不出来，那才是失败了」。
                 *
                 * 判据只读**盘上事实**（`VolumeGroupDetector` 的结论）：这一组到底完不完整 ——
                 * · 不完整（缺片）⇒ `PartiallyCompleted`：我们**根本没拿到全部数据**，不是"解压失败"；
                 * · 完整了却解不出来 ⇒ 走既有的失败收口 ⇒ `Failed`（那才是真失败）。
                 * ⛔ 「源包一个字节都不动」这条红线一个字不改（下面既有的收尾照旧）。
                 */
                var leftoverCandidates = new List<VolumeCandidate>();

                foreach (string path in task.VolumePaths)
                {
                    if (File.Exists(path))
                    {
                        leftoverCandidates.Add(new VolumeCandidate { Path = path });
                    }
                }

                foreach (string path in CollectVolumeFilesInDirectory(
                             Path.GetDirectoryName(task.CurrentPath ?? string.Empty) ?? string.Empty))
                {
                    leftoverCandidates.Add(new VolumeCandidate { Path = path });
                }

                bool stillIncomplete = VolumeGroupDetector
                    .Group(leftoverCandidates)
                    .Any(candidateGroup => !candidateGroup.IsComplete);

                if (stillIncomplete)
                {
                    task.Outcome = TaskOutcome.PartiallyCompleted;
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：这一组到最后一刻仍然不完整（缺 {missing}）—— 按「部分完成」记，⛔ 不是解压失败。");
                }

                // 机器终态收口：这一单是在**任务收尾之后**才落的状态，走不到单任务那个 finally ⇒ 这里补。
                FinalizeOutcomeIfPending(task);

                /*
                 * ⚠ 2026-10-08：离开"缺卷待批末判"名单的**那一刻**就要把显示/统计事实位清掉
                 * （手动档不走链尾 `FinalizeDeferredVolumeDeficits`，只靠那一处会漏 ⇒ 真机表现是
                 * 诊断里仍按"跳过"分组、拿不到"缺哪几卷 + 下一步：补卷"）。
                 */
                task.IsVolumeDeficitDeferred = false;

                _volumeDeficitDeferred.Remove(task);
                _volumeDeficitFinalPass.Add(task);
            }

            return toRun;
        }



        /// <summary>
        /// **一单"已经借给本批另一单用过的源片/片"的收场 + 搬运**（唯一实现，两处转调：
        /// <see cref="SettleGroupPieceProducers"/> 遍历那份"吐出过片"的账，
        /// <see cref="SettleConsumedGroupVolumeSources"/> 处理"从没吐过东西、只是一片散着的续卷"那几单）。
        ///
        /// <para><b>机器终态为什么是「完成」而不是「跳过」</b>（真机 CCCC 2026-10-06 实测逮到）：
        /// 它的源片要随这一组进其余物、再按「删除操作」删掉，而删除侧那道闸门读的正是机器终态
        /// —— 终态落「跳过」时实测逐字写着「任务的机器终态不是「完成」（当前：Skipped）—— 其余物一个字节都不删」
        /// ⇒ 源片被搬进其余物之后**永久留在盘上**（用户问的"其余物怎么还留着"）。
        /// 这一单**确实没有失败**：它的内容已经被本批另一单完整解出来、校验通过、源片也已经归集到位
        /// —— 与消费方自己的源包同一档，所以终态落 <see cref="TaskOutcome.Succeeded"/>，
        /// 同时保留 <see cref="ArchiveTask.IsVolumeGroupFollower"/>（四处"怎么数"的判据照旧不算它）。</para>
        /// </summary>
        private void SettleOneConsumedPieceTask(
            ArchiveTask piece,
            ArchiveTask consumer,
            string groupBaseName,
            string producedPieceName,
            bool moveSources,
            List<(string Level, string Message)> logEntries,
            string? producedPiecePath = null)
        {
            if (piece == null)
            {
                return;
            }

            /*
             * ⛔ **"自己解开的组"也要收自己那一片**（真机 CCCC 2026-10-07 00:15，排障行逮到）：
             * 批末会有一单被借去当"解开这一组的单元"；如果**供给这一片的就是它自己**
             * （排障行逐字：`Original=…\111(4)\111.z0删除3`、`Current=…\其余物\111.zip`、
             * `组生产者账=111`、`账里是本单的键=空`），老写法在这里一句 `ReferenceEquals` 就返回了
             * ⇒ 它的源片 `…\111(4)\111.z03` 一直留在盘上（用户连问三次的那一个）。
             * ⇒ 分开处理：**"标跟班"那一套只对别的单做**（它自己是单元，结论是"这一组解压成功"）；
             * **源片搬运照做**（这一组确实已经解开、那一片已被接手）。
             */
            bool selfConsumed = ReferenceEquals(piece, consumer);

            if (!selfConsumed && (!piece.IsVolumeGroupFollower || piece.Outcome != TaskOutcome.Succeeded))
            {
                /*
                 * ⛔ **族必须对得上**（2026-10-11 真机 EEE 定案，调试器 + 程序判据输入 + 引擎三方取证）：
                 *
                 * 「谁吐出过哪一组的片」这本账的键是**基名**（`_groupPieceProducers`），而同一个基名底下
                 * 可能同时存在**两族**：EEE 的跨盘 ZIP 组 `111.z01/z02/z03/111.zip`，与它解开之后得到的
                 * RAR 对 `111.parts1.racr` / `111.partɾ2.ra??r`（引擎证实这一对含 5 个 mp4）——
                 * 两族都叫 `111`。缺这一道闸门时，RAR 那两卷被记成"ZIP 组的片"⇒ 在这里被**静默**
                 * 清掉落点 / 清校验 / 标跟班（真机逐字：`校验=NotAttempted，落点=空，归集=空`）
                 * ⇒ 轮尾扫描没有目录可扫 ⇒ 链条停在内层那一对卷上，用户要的 mp4 永远出不来。
                 *
                 * 判据只有一条**既有尺子**：这一片的族与**消费方那一组**的族是不是同一组
                 * （`BelongsToSameGroup` = 族 + 基名）。⛔ 不新造判据、⛔ 不放宽"证明完整"那一步。
                 * 对不上 ⇒ **判不出 ⇒ 什么都不做**（不清落点、不清校验、不标跟班），并如实写判据输入。
                 */
                string pieceName = FileNameHelper.GetFileName(producedPieceName ?? string.Empty);
                string consumerGroupName = FileNameHelper.GetFileName(consumer.CurrentPath ?? string.Empty);

                /*
                 * ⛔ **名字认不出族时，用底层内容卡回答**（用户 2026-10-18 的 `&&` 口径：
                 * 第一大步的片是不是这一族的成员，看内容；名字只是辅助）。
                 * 现场（`19191919`）：`bbb-L2.zip-658` 的名字读不出 `.zip.00N` 族 ⇒ 老判据拒绝跟班收场
                 * ⇒ 它的原包一直留着；而它的内容卡（Zip + 卷成员）与接手方同一族。
                 * 判据：① 两边**内容格式**相同且不是 Unknown；② 这一片的内容不是 `ProvablyNotVolumeMember`；
                 * ③ 基名与组基名有关（既有 `BaseNameRelatedToGroup`）。三条都成立才算同一族；
                 * 判不出 ⇒ 老口径（拒绝，不清、不删）。
                 */
                bool sameGroup = pieceName.Length > 0
                    && consumerGroupName.Length > 0
                    && VolumeGroupDetector.BelongsToSameGroup(pieceName, consumerGroupName);

                if (!sameGroup)
                {
                    try
                    {
                        FileNatureCard producedCard = FileNature.Inspect(producedPiecePath ?? piece.CurrentPath);
                        FileNatureCard consumerCard = FileNature.Inspect(consumer.CurrentPath);

                        sameGroup = producedCard.Format == consumerCard.Format
                            && producedCard.Format != VolumeContentFormat.Unknown
                            && producedCard.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                            && VolumeGatherStep.BaseNameRelatedToGroup(pieceName, groupBaseName);
                    }
                    catch
                    {
                        // 卡读不出来 ⇒ 判不出 ⇒ 老口径（拒绝）。
                        sameGroup = false;
                    }
                }

                if (pieceName.Length > 0
                    && consumerGroupName.Length > 0
                    && !sameGroup)
                {
                    AppendLog(
                        "WARN",
                        $"「{piece.LogName}」不按跟班卷收场 —— 它吐出的那一片的族与接手方那一组对不上"
                        + $"（同一基名下的两族不许混算）｜判据输入：它吐出的片=「{pieceName}」，"
                        + $"接手方那一组的起点=「{consumerGroupName}」，账上记的组基名=「{groupBaseName}」"
                        + "，同组判据（族 + 基名）= 不成立 ⇒ 不清落点、不清校验、不标跟班");
                }
                else
                {
                    string consumedNote = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumePieceProducerConsumedByOtherTaskFormat,
                    piece.LogName,
                    consumer.LogName,
                    groupBaseName);

                    /*
                     * 与既有那条跳过路**逐行同一套**（清校验 / 清落点 + 落终态 + 标跟班）：
                     * `MarkSkipped` 只写 Status，机器终态必须显式落，四处消费点才算得对。
                     */
                    piece.IsOutputVerified = false;
                    piece.OutputVerification = OutputVerificationOutcome.NotAttempted;
                    piece.OutputPath = string.Empty;
                    piece.Outcome = TaskOutcome.Succeeded;
                    piece.IsVolumeGroupFollower = true;

                    /*
                     * ⛔ **另一半事实当场抄下来**（用户 2026-10-10 拍板：那一行「两句都写」）：
                     * 这一单自己那一趟是不是**已经跑完并定稿成功**过。判据与批末账目同一个出口 ——
                     * 还在"缺卷待批末判"名单里（`IsVolumeDeficitDeferred`）= 它自己是**停在中途**的
                     * （真机 `111.rar` / `111(2)_.zip`）；不在名单里却把片交出去 = 它自己**跑成了**、
                     * 只是产出的那片随后被整组接手（真机 `111(3).rar` + `111(3)\111(3)\111.z02`）。
                     * ⚠ 名单是**过程事实**、批末会被清（`RecheckDeferredVolumeDeficits`）⇒ 必须在这一刻
                     * 抄成持久事实，显示才不会随名单抖动。⛔ 它**只喂显示**：统计 / 删除 / 清单都不读它。
                     */
                    piece.OwnRunSucceededBeforeGroupSettlement = !piece.IsVolumeDeficitDeferred;

                    /*
                     * ⛔ 记下"是谁把这一组解开的"（显示名）—— 状态格靠这条**结构化事实**如实说
                     * 「这一片随整组解开（由「X」那一单解的）」，不再永远停在「已跳过 100%」
                     * （用户 2026-10-07：「列表里面最下面的两个，从来没有变化」）。
                     */
                    piece.SettledWithGroupUnitName = string.IsNullOrWhiteSpace(consumer.DisplayFileName)
                        ? consumer.FileName
                        : consumer.DisplayFileName;

                    // `MarkSkipped` 写的是"跳过"那句话；这里要的是"内容已由别单解出"的如实说法。
                    piece.MarkSkipped(consumedNote);

                    AppendLog("INFO", consumedNote);
                }
            }

            /*
             * 这一单的**其余物**也要按档处理（用户 2026-10-06 两次追问的那 48.35 MB）：
             * 它只出过程物、那份过程物已被这一组那次**可证完整**的解压接手 ⇒ 它的其余物不再有用了。
             */
            PurgePassThroughRestOfSettledProducer(piece, consumer, groupBaseName);

            /*
             * ⛔ **这一趟没删成就解除"已处理"标记、排进批末重试**（真机 2026-10-06 20:07 实测）：
             * 这一刻半套分卷那道闸门**拦得对** —— 同组的 `111.z01`（名字不同的一片）还在成品树里，
             * 删掉装着**入口包**的其余物就等于把这一组拆开。可那一组随后就解完了
             * （`彻底删除：111\其余物（7 项 / 1.07 GB）`），而旧写法把它钉在"已处理"上
             * ⇒ `111\111\其余物\111.zip`（48.35 MB）从此没有任何人再看一眼。
             */
            if (!string.IsNullOrWhiteSpace(piece.RestDirectoryPath) && Directory.Exists(piece.RestDirectoryPath))
            {
                _passThroughRestHandled.Remove(piece);
                RememberPendingPassThroughRest(piece, consumer, groupBaseName);
            }

            if (!moveSources || !_producerSourcesCollected.Add(piece))
            {
                return;
            }

            /*
             * ⛔ **源包那一档的闸门不许绕过**（与 `PostProcessSuccessAsync` 里那三支**逐字同一套**）：
             * 「不动其余物」= 源包一个字节都不搬（不变量 1 的默认档）；「空间不足 + 不删原包」= 安全档，
             * 同样什么都不做。少了这一道，`KeepInPlace` 下**用户的源片会被搬走** —— 真回归，
             * 守门用例 `真机形状_一组跨盘ZIP的片分别压在两个包里_批末照样解开`
             * 那句「用户的源片不许动」当场变红（实测逮到）。
             */
            /*
             * ⛔ **源包那一档必须读"本批的选择"，⛔ 不是⑥页那份常驻设置**（真机 2026-10-07 07:49 逮到）：
             * 一键处理的「本次选项」放在 `RunOptions` 里（同一表达式在别处用了两处：`:1235` / `:2881`），
             * 而这一档原来直接读 `Settings.SourceHandling` ⇒ 用户在本次选项里选了「放入其余物」，
             * 收场这条路却按默认档「留在原地」判 ⇒ 源片一个字节都不搬
             * （同一批里 `111.zip` 那一单却搬了 4 个 —— 它走的是读 `RunOptions` 的那条路）。
             * 这就是"一处改动要落到每一条路径上"那条规矩的反面现场。
             */
            SourceHandlingMode sourceHandling = _spaceTightThisBatch
                ? SourceHandlingMode.DeleteInPlace
                : RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (_spaceTightThisBatch && _spaceTightKeepSourceThisBatch)
            {
                logEntries.Add((
                    "INFO",
                    $"{piece.LogName}：不删原包 —— 源片一个字节都不动（空间不足的安全档）。"));

                return;
            }

            if (sourceHandling != SourceHandlingMode.DeleteInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{piece.LogName}：按「源包留下」这一档，源片一个字节都不动（留在原处，随时可手工处理）。"));

                return;
            }

            /*
             * ⚠ 2026-10-15：这里原来是 `ExecuteSourcePackageMove(piece, consumer.RestDirectoryPath, …)`
             * —— 把源片**搬进其余物**。用户 2026-10-14 订正之后那一档不存在了（源包操作只剩
             * 「留下 / 彻底删除」，而彻底删除是**就地删、⛔ 不搬进其余物**）⇒ 改走**同一条闸门链**的
             * 永久删那一支（唯一出口 `PurgeLayerSourcePackage`）。
             *
             * ⛔ **闸门一条都没绕过**：这一支不喂本任务的 verification / commit（这里手上没有"这一单自己定稿"
             * 的事实），改喂**接手方那一单的完整性结论**（`ResultCompletenessClassifier.Classify(consumer)`）——
             * 这正是用户 2026-10-18 拍板的 `&&`：第一大步成功 + 第二大步（组）可证完整且内容校验通过，
             * 才允许按「彻底删除」删这个原包；判不出 / 第二大步没成 ⇒ `PurgeLayerSourcePackage` 一个字节都不动。
             */
            PurgeLayerSourcePackage(
                piece,
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.SourceHandlingDelete,
                reclaimVerdict: ResultCompletenessClassifier.Classify(consumer));

            return;
        }

        /// <summary>
        /// **"这一组的内容已经由本批另一单解出来了"的那几单：源包按跟班卷收场 + 按删除档处理**
        /// （真机 CCCC 2026-10-06，用户原话「原包怎么还没有删除」「其余物怎么还留着」）。
        ///
        /// <para>与 <see cref="SettleGroupPieceProducers"/> 的分工：那一档管"**吐出过片**的那几单"
        /// （它自己解到一半停了、源包留在盘上）；这一档管"**从没吐过东西、只是一片散着的续卷**"的那几单
        /// —— 它们整组由别人解开，按跟班卷收场，可源片一直没人处理。</para>
        ///
        /// <para>判据只有两条事实位（<see cref="TryResolveConsumedByAnotherTask"/> 用的**同一对**）：
        /// ① 这一单的路径在既有那份"借来用过的源片"账 <see cref="_consumedVolumeSources"/> 里；
        /// ② 那位消费方**真的成功了**（机器终态）。⛔ 不碰消费方自己、⛔ 幂等、⛔ 搬运仍只有既有出口。</para>
        /// </summary>
        /// <param name="logEntries">非空 ⇒ 日志先攒起来交给调用方统一落（批内并发路径要回 UI 线程）；空 ⇒ 这里直接落。</param>
        private void SettleConsumedGroupVolumeSources(List<(string Level, string Message)>? logEntries)
        {
            if (_consumedVolumeSources.Count == 0)
            {
                return;
            }

            var collected = logEntries == null ? new List<(string Level, string Message)>() : null;

            foreach (ArchiveTask task in EnumerateCandidateTasksForConsumedSettle())
            {
                if (task == null)
                {
                    continue;
                }

                /*
                 * 兜底必须比"借片账"宽一档：这一组**确实由某个已成功的消费方解开**时，就算这一单的路径
                 * 没记进那份账，也照样收场 —— 判据仍是两个**事实位**（账上那一组谁在解 + 消费方成功）。
                 *
                 * 真机 CCCC 的 `111.z0删除2` 就是这样：同组的 `111.z0删除3` 走了账、被处理掉了，
                 * 而它没进账（收卷那一刻它那一片还没被采纳）⇒ 源片一直留在盘上。
                 */
                bool consumedHit = TryResolveConsumedByAnotherTask(task, out string consumerName);

                string owner = consumedHit ? consumerName : ResolveSuccessfulGroupConsumerName(task);

                if (owner.Length == 0)
                {
                    (collected ?? logEntries!).Add((
                        "INFO",
                        $"{task.LogName}：这一组的源片先不动 —— 既有账里查不到「这一组由哪个已成功的单解开」"
                        + "（判不出 ⇒ 什么都不做，源片原样留在原处）。"));

                    continue;
                }

                if (!consumedHit)
                {
                    // 记账与收场一起补（唯一不变量：两边同时写，⛔ 不留下写了一半的账）。
                    // 记的是**它自己那一片**的路径（`OriginalPath` 优先）—— 批末补判可能已经把
                    // `CurrentPath` 改到入口包上了（真机实测），记错了下一步就会去搬入口包。
                    RememberOneConsumedVolumeSource(ResolveOwnPiecePath(task), owner);
                }

                ArchiveTask? consumer = null;

                foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
                {
                    if (candidate != null && string.Equals(candidate.FileName, consumerName, StringComparison.Ordinal))
                    {
                        consumer = candidate;
                        break;
                    }
                }

                if (consumer == null || consumer.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                // 记账 + 收场 + 搬源包：唯一实现 SettleOneConsumedPieceTask（跟班 / 幂等都只有那一处写）。
                SettleOneConsumedPieceTask(
                    task,
                    consumer,
                    FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty),
                    FileNameHelper.GetFileName(SourcePackageMover.ResolveOwnFileOnDisk(task) ?? string.Empty),
                    moveSources: true,
                    collected ?? logEntries!);

                if (task.IsVolumeGroupFollower)
                {
                    _volumeDeficitDeferred.Remove(task);
                    _volumeDeficitFinalPass.Add(task);
                }
            }

            if (collected != null)
            {
                foreach ((string level, string message) in collected)
                {
                    AppendLog(level, message);
                }
            }
        }

        /// <summary>
        /// **这一单自己那一片在盘上的路径**（优先 <c>OriginalPath</c>）。
        ///
        /// <p>⚠ 为什么不能直接用 <c>CurrentPath</c>：批末补判那一站会把补齐了的那一单的起点改到
        /// **入口包**上（真机 CCCC 的 `111.z0删除2` 实测：`CurrentPath` 变成了
        /// `…\111\其余物\111.zip`）—— 拿它去判"这一单的源片还在不在"会判到入口包头上。
        /// `OriginalPath` 是**最初导入路径**（搬运成功后才跟着更新）。判不出 ⇒ 空串。</p>
        /// </summary>
        private static string ResolveOwnPiecePath(ArchiveTask task)
        {
            /*
             * ⛔ **先问"这一份还在不在"**（真机 CCCC 2026-10-06 22:50）：`OriginalPath` 是**最初导入路径**，
             * 而已知改名/搬运**不会**把它改过来（实测它一直停在改名前的脏名 `111.z0删除3` 上）。
             * 拿一个不存在的路径去判"源片还在不在"、去搬源片 ⇒ 一定落空 ⇒ 源片永远留在盘上。
             * ⇒ 顺序：账上那条**真的在**就用它；不在就按"同目录同包基名"问盘（`FindOwnPieceOnDisk`）；
             * 再不行才退回 `CurrentPath`。⛔ 只读、⛔ 一个字都不改。
             */
            if (!string.IsNullOrWhiteSpace(task.OriginalPath) && File.Exists(task.OriginalPath))
            {
                return task.OriginalPath;
            }

            string onDisk = SourcePackageMover.ResolveOwnFileOnDisk(task);

            if (onDisk.Length > 0)
            {
                return onDisk;
            }

            return task.CurrentPath ?? string.Empty;
        }

        /// <summary>
        /// **这一单所在那一组的消费方是谁**（只读既有那份"谁在解"的账 `_groupConsumerByName`），
        /// 而且那位消费方**真的成功了**（机器终态）。判不出 / 还没成功 ⇒ 空串 ⇒ 调用方什么都不做。
        /// </summary>
        private string ResolveSuccessfulGroupConsumerName(ArchiveTask task)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

            if (baseName.Length == 0
                || !_groupConsumerByName.TryGetValue(baseName, out string? name)
                || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || !string.Equals(candidate.FileName, name, StringComparison.Ordinal))
                {
                    continue;
                }

                /*
                 * ⛔ **借片的那一单不是消费方**（真机 CCCC 2026-10-06 实测）：批末补判会把补齐了那一单的
                 * `CurrentPath` 改到入口包上 ⇒ 它与真正的消费方**撞名**（两个都叫 `111.zip`）。
                 * 判据 = 它自己那一片也在"借片账"的键里（`IsBorrowedPieceTask`）。
                 */
                if (ReferenceEquals(candidate, task) || IsBorrowedPieceTask(candidate))
                {
                    continue;
                }

                if (candidate.Outcome == TaskOutcome.Succeeded)
                {
                    return name;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 这一趟该检查哪些单：**在册的延迟缺卷单** + **记账里那几单的生产者** + **全表**（三处并集，去重）。
        ///
        /// <para>为什么要有"全表"那一档：真机那一批里那两单在批末已经是「跟班卷 + 跳过」了
        /// （`TryResolveConsumedByAnotherTask` 那条路给它们落的），既不在延迟名单里、也从没吐过片
        /// —— 只有"全表"才扫得到它们。代价可控：真正的判据是"这一单的路径在不在这份借片账里"，
        /// 绝大多数单第一条就 false；搬过的由 <c>_producerSourcesCollected</c> 挡住，不会搬第二遍。</para>
        /// </summary>
        private IEnumerable<ArchiveTask> EnumerateCandidateTasksForConsumedSettle()
        {
            var seen = new HashSet<ArchiveTask>();

            foreach (ArchiveTask task in _volumeDeficitDeferred.ToList())
            {
                if (task != null && seen.Add(task))
                {
                    yield return task;
                }
            }

            foreach (List<(ArchiveTask Task, string PieceName)> producers in SnapshotGroupPieceProducerLists())
            {
                foreach ((ArchiveTask producer, string _) in producers.ToList())
                {
                    if (producer != null && seen.Add(producer))
                    {
                        yield return producer;
                    }
                }
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null && seen.Add(task))
                {
                    yield return task;
                }
            }

            /*
             * 还有一档非扫不可：**名字出现在"借片账"里的那些路径**对应的单。
             *
             * 真机 CCCC 的 `111.z0删除2` 就是这样：它的路径在账上（借给消费方用过），可那一单既不在
             * 延迟名单里（已经被别的分支收场过了）、也没吐过片 ⇒ 上面三处一处都覆盖不到它
             * ⇒ 源片留在盘上（实测：同组的 `111.z0删除3` 被处理了，它没有）。
             */
            foreach (string piecePath in _consumedVolumeSources.Keys.ToList())
            {
                foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
                {
                    if (task == null || seen.Contains(task))
                    {
                        continue;
                    }

                    if (EnumerateTaskPaths(task).Any(path => SafePathHelper.PathEquals(path, piecePath)))
                    {
                        seen.Add(task);
                        yield return task;
                    }
                }
            }
        }

        /// <summary>遍历"谁在解"的账：消费方**已经成功**的那几组，给它们的生产者收场（幂等；见 <see cref="SettleGroupPieceProducers"/>）。</summary>
        private void SettleSucceededGroupPieceProducers()
        {
            if (_groupConsumerByName.Count == 0 || GroupPieceProducerCount == 0)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();

            foreach (string consumerName in _groupConsumerByName.Values.ToList())
            {
                ArchiveTask? consumer = SnapshotTaskTable(Tasks).FirstOrDefault(
                    candidate => candidate != null && string.Equals(candidate.FileName, consumerName, StringComparison.Ordinal));

                if (consumer == null || consumer.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                SettleGroupPieceProducers(consumer, moveSources: true, logEntries);
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// **已验证组里的跟班片**：源片按「彻底删除」收掉（2026-10-18 用户 `&&` 口径，`22222222` 现场）。
        ///
        /// <para>跟班片（<see cref="ArchiveTask.IsVolumeGroupFollower"/> + 机器终态成功）的源片有时
        /// 不在“谁吐过片”那本账里（它没吐过东西、只是被借去拼组）⇒ `SettleGroupPieceProducers` 扫不到它，
        /// 源片一直留着。判据只要两条事实位：① 跟班 + 成功；② 同一组里有一个**可证完整 + 内容校验通过**
        /// 的消费方（`ResultCompletenessClassifier.Classify(consumer).AllowsSourceRemoval`）——
        /// 这就是 `&&` 的第二步。⛔ 组不完整 / 校验没过 / 判不出消费方 ⇒ 什么都不做；幂等（Done 跳过）。</para>
        /// </summary>
        private void SettleVerifiedGroupFollowers()
        {
            SourceHandlingMode handling = _spaceTightThisBatch
                ? SourceHandlingMode.DeleteInPlace
                : RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (handling != SourceHandlingMode.DeleteInPlace)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();
            ArchiveTask[] tasks = SnapshotTaskTable(Tasks);

            foreach (ArchiveTask task in tasks)
            {
                if (task != null && task.IsVolumeGroupFollower)
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：跟班源片结算判据｜成功={task.Outcome == TaskOutcome.Succeeded}"
                        + $"｜已处理={task.SourcePackageMove}｜组名=「{task.SettledWithGroupUnitName}」"
                        + $"｜当前=「{task.CurrentPath}」｜原始=「{task.OriginalPath}」"));
                }

                /*
                 * ⛔ **已标 Done、但改名前的原始那一份还在盘上**（2026-10-18 `22222222` 现场）：
                 * `part1869删除2` 的 `CurrentPath` 被改写成组里的 `part1.rar`，早先那次清理删的是
                 * `part1.rar` 并把它标成 Done；可用户导入的那个**原始名字**（`OriginalPath`）还在
                 * —— 两个名字是同一份字节的硬链接，删一个不等于删干净。⇒ 只对“跟班里已证完整 +
                 * 原始那份确实还在”这一种，允许再走一次删源（`ignoreDoneState`），别的 Done 一律不动。
                 */
                bool recoveryOfOriginal = task != null
                    && task.SourcePackageMove == SourcePackageMoveState.Done
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && File.Exists(task.OriginalPath);

                if (task == null
                    || task.IsContinuationTask
                    || !task.IsVolumeGroupFollower
                    || (task.SourcePackageMove == SourcePackageMoveState.Done && !recoveryOfOriginal)
                    || task.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

                /*
                 * 消费方先从**跟班片自己记下的那个事实**找（`SettledWithGroupUnitName`，由
                 * `SettleOneConsumedPieceTask` 在“这一组由 X 解开”那一刻写死）—— 脏名字底下
                 * `GetArchiveBaseName` 可能给出两个不同的基名，按基名会漏（`22222222` 现场：
                 * `part1869删除2` vs `part391392885删除1`）。判不出再退回基名那一档。
                 */
                ArchiveTask? consumer = null;
                string settledWith = task.SettledWithGroupUnitName ?? string.Empty;

                if (settledWith.Length > 0)
                {
                    consumer = tasks.FirstOrDefault(candidate =>
                        candidate != null
                        && candidate.Outcome == TaskOutcome.Succeeded
                        && candidate.IsVolumeGroup
                        && candidate.IsVolumeComplete
                        && (string.Equals(candidate.DisplayFileName, settledWith, StringComparison.Ordinal)
                            || string.Equals(candidate.FileName, settledWith, StringComparison.Ordinal)));
                }

                if (consumer == null && baseName.Length > 0)
                {
                    consumer = tasks.FirstOrDefault(candidate =>
                        candidate != null
                        && candidate.Outcome == TaskOutcome.Succeeded
                        && candidate.IsVolumeGroup
                        && candidate.IsVolumeComplete
                        && string.Equals(
                            FileNameHelper.GetArchiveBaseName(candidate.CurrentPath ?? string.Empty),
                            baseName,
                            StringComparison.OrdinalIgnoreCase));
                }

                if (consumer == null || !consumer.IsVolumeComplete)
                {
                    continue;
                }

                ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(consumer);

                if (!verdict.AllowsSourceRemoval)
                {
                    continue;
                }

                /*
                 * 组任务的删除清单只装 `VolumePaths`；改名前的**原始那一份**不在里面，而这一单自己的
                 * 校验又被跟班收场清过 ⇒ `BuildTargetList` 不会把 `OriginalPath` 放进去（`22222222` 现场：
                 * `part1869删除2` 的当前路径是组里的 `part1.rar`，原始名字还在盘上）。
                 * ⇒ 用一个只代表“这一份原始文件”的临时任务走**同一个删除出口**（⛔ 不另写删除代码），
                 * 并沿用跟班 + 消费方 verdict 那两道事实。
                 */
                ArchiveTask purgeTask = task;
                string originalPath = task.OriginalPath ?? string.Empty;
                bool purgeOriginalOnly = task.IsVolumeGroup
                    && !string.IsNullOrWhiteSpace(originalPath)
                    && File.Exists(originalPath);

                if (purgeOriginalOnly)
                {
                    purgeTask = new ArchiveTask(originalPath)
                    {
                        IsVolumeGroupFollower = true
                    };
                }

                PurgeLayerSourcePackage(
                    purgeTask,
                    verification: null,
                    commit: null,
                    logEntries,
                    LayerPurgeTrigger.SourceHandlingDelete,
                    reclaimVerdict: verdict,
                    ignoreDoneState: recoveryOfOriginal || purgeOriginalOnly);

                if (purgeOriginalOnly && !File.Exists(originalPath))
                {
                    // 原始那一份已经删掉 ⇒ 把真任务也记成“处理过”，避免后续每轮再扫一遍。
                    task.SourcePackageMove = SourcePackageMoveState.Done;
                }
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// 这一单所在的那一组，**是不是正在被本批另一单解**（而它还没收尾）⇒ 这一单先不落结论。
        /// 判据只有两条事实位：① 组基名在那份"谁在解"的账上；② 那位消费方**还没有机器终态**。
        /// </summary>
        private bool TryGetGroupConsumerPending(ArchiveTask task, out string consumer)
        {
            consumer = string.Empty;

            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

            if (baseName.Length == 0
                || !_groupConsumerByName.TryGetValue(baseName, out string? name)
                || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, task.FileName, StringComparison.Ordinal))
            {
                return false;   // 自己就是那个消费方
            }

            ArchiveTask? owner = SnapshotTaskTable(Tasks).FirstOrDefault(
                candidate => candidate != null && string.Equals(candidate.FileName, name, StringComparison.Ordinal));

            bool unfinished = owner == null
                || owner.Outcome is not (TaskOutcome.Succeeded
                    or TaskOutcome.Failed
                    or TaskOutcome.PartiallyCompleted
                    or TaskOutcome.Cancelled
                    or TaskOutcome.Skipped);

            if (!unfinished)
            {
                return false;
            }

            consumer = name;
            return true;
        }

        /// <summary>这一单的分卷，**是不是已经被本批另一个任务解出来了**（2026-10-05 真机第八批）。
        ///
        /// <para><b>现场</b>：一组跨盘 ZIP 的末片压在两层层层加密的 RAR 里、其余三片散在三个源目录里。
        /// 递归层用硬链接把那三片接过来、整组解开、结果发布成功 —— 可那三片自己那一单在批末仍然
        /// 如实报「分卷缺失」⇒ 汇总写成「成功 1 / 失败 3」，批末还指路"把缺的那几卷放到同一个目录里"
        /// （用户按这条去搬，只会白忙一场：内容已经在出来了）。</para>
        ///
        /// <para>判据只有两件事，都是**事实位**：① 这一单自己（或账上那几卷）出现在
        /// <see cref="_consumedVolumeSources"/> 里（写入点唯一 = 递归结果带回来的那份名单）；
        /// ② 那个消费方这一批**真的成功了**（机器终态）。两条都成立 ⇒ 按跟班卷处理。</para>
        /// </summary>
        private bool TryResolveConsumedByAnotherTask(ArchiveTask task, out string consumerName)
        {
            consumerName = string.Empty;

            if (task == null || _consumedVolumeSources.Count == 0)
            {
                return false;
            }

            /*
             * ===== 判据 = **这一单自己那一片**有没有进过那份"借片账" =====
             *
             * ⛔ 比的是**同一份文件**，⛔ 不是路径字符串（真机 CCCC 2026-10-06 20:55，用户原话：
             * 「为什么文件真正的名字不看」）：接片那一档在"这一组该在的那一层"另起了一个**零字节硬链接**
             * （`…\111\111\111.z03`），而用户那一份还在他自己那个目录里（`…\111(4)\111.z03`）
             * —— 两条路径指着**同一份字节**。账里记的可能只是其中一条 ⇒ 按路径比永远对不上
             * ⇒ 那一单落到「判不出 ⇒ 什么都不做」，源片一直留着。
             */
            var ownPaths = new List<string>(EnumerateTaskPaths(task));
            string ownOnDisk = SourcePackageMover.ResolveOwnFileOnDisk(task);

            if (ownOnDisk.Length > 0)
            {
                ownPaths.Add(ownOnDisk);
            }

            foreach ((string piecePath, string ownerName) in _consumedVolumeSources.ToList())
            {
                bool mine = false;

                foreach (string path in ownPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (SafePathHelper.PathEquals(path, piecePath) || FileIdentity.IsSamePhysicalFile(path, piecePath))
                    {
                        mine = true;
                        break;
                    }
                }

                if (!mine || string.IsNullOrWhiteSpace(ownerName))
                {
                    continue;
                }

                /*
                 * ⚠ **判据输入**（2026-10-11，真机 EEE 定案用）：这一档一旦命中，调用方就**清落点 / 清校验 /
                 * 落「跟班卷」**，可"到底是哪一条账目匹配上的"原来一个字都不写 —— 于是真机 EEE 只看到
                 * `111.part1.rar` 被算成跟班卷，看不到它被**哪一份文件**（物理同一 / 同一条路径）匹配上的。
                 * 按"判不出就把判据输入写进日志"那条规矩补一行 WARN。⛔ 只写事实，不改判据。
                 */
                AppendLog(
                    "WARN",
                    $"借片账命中：「{FileNameHelper.GetFileName(task.CurrentPath)}」这一单自己的文件与账上那一片"
                    + $"（{piecePath}）是同一份物理文件（或同一条路径）⇒ 按已由「{ownerName}」接手收场"
                    + $"｜判据输入：本单路径={task.CurrentPath}，匹配上的账目=「{piecePath}」→ 接手方「{ownerName}」");

                ArchiveTask? owner = null;

                foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
                {
                    if (candidate == null || !string.Equals(candidate.FileName, ownerName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    /*
                     * ⛔ **借片的那一单不是消费方**（真机 CCCC 2026-10-06 实测）：批末补判会把补齐了那一单的
                     * `CurrentPath` 改到入口包上 ⇒ 它与真正的消费方**撞名**（两个都叫 `111.zip`）。
                     */
                    if (IsBorrowedPieceTask(candidate))
                    {
                        continue;
                    }

                    owner = candidate;
                    break;
                }

                if (owner == null || owner.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                consumerName = ownerName;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 这一单**自己那一片借给过别的单**吗（判据只有一条：它自己的某条路径是那份"借片账"的**键**）。
        ///
        /// <p>为什么要它：那份账记的消费方是文件名，而"补齐之后起点被改到入口包上"的那一单会与真正的
        /// 消费方**撞名** ⇒ 按名字回查必须先认出"这是借片的那一单，不是消费方"（真机 CCCC 2026-10-06）。</p>
        /// </summary>
        private bool IsBorrowedPieceTask(ArchiveTask task)
        {
            if (task == null || _consumedVolumeSources.Count == 0)
            {
                return false;
            }

            /*
             * ⚠ **只问"它自己那一份"两条路径**（`OriginalPath` / `CurrentPath`），⛔ **不许连 `VolumePaths` 一起问**：
             * `VolumePaths` 是"这一组那几卷"的账，里面有**别的单**的片（真机：消费方 `111.zip` 那一单的
             * `VolumePaths` 里就有 `111.z0删除2/3`）—— 一起问会把**真正的消费方**误判成"借片的那一单"，
             * 于是谁也当不成消费方（实测踩到）。
             */
            foreach (string path in EnumerateOwnPaths(task))
            {
                if (!string.IsNullOrWhiteSpace(path) && _consumedVolumeSources.ContainsKey(path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一单**自己那一份**的两条路径（<c>OriginalPath</c> 在前、<c>CurrentPath</c> 在后；去重，且**不含**分卷账）。
        /// </summary>
        private static IEnumerable<string> EnumerateOwnPaths(ArchiveTask task)
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
        private static IEnumerable<string> EnumerateTaskPaths(ArchiveTask task)
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
        private static VolumeGroup? ResolveCompleteGroupFromDisk(ArchiveTask task, string? preferDirectory = null)
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

        /// <summary>
        /// **链尾**：整条续解链跑完之后，名单里剩下的那几个才如实报缺卷（用户口径 2 的最后一站）。
        ///
        /// <para>已经补齐、但这一批已经没有机会再跑它的，**如实说清"这一次没跑"**（⛔ 不假装跑过、
        /// ⛔ 也不改机器终态：它仍然是「未处理」，再点一次就会跑）。</para>
        ///
        /// <para><b>它必须有机会跑到</b>（2026-10-05 真机第八批当场逮到的那条死路）：延迟中的任务
        /// **按设计就是没有机器终态**，而一键档那道"这一轮有没有任务没轮到"的守卫读的正是终态 ——
        /// 于是"延迟"本身把整条链判成"没轮到"，链尾整段不执行 ⇒ 补判永远不发生、汇总把它报成「没轮到」。
        /// ⇒ 两处一起改：守卫不再把在册的延迟任务算成"没轮到"（<see cref="IsDeferredVolumeDeficit"/>），
        /// 而且**用户没按停止**时这一步照跑（<c>internal</c> 就是给那条路用的）。</para>
        /// </summary>

        internal async Task<IReadOnlyList<ArchiveTask>> FinalizeDeferredVolumeDeficitsAsync(CancellationToken cancellationToken = default)
        {

            /*
             * ⚠ 2026-10-08：**这一站才是"真缺卷"下结论的地方** ⇒ 先把进来时那份名单快照下来，
             * 补判跑完之后把已经离开名单的那些单的"待判"事实位清掉（它们要么被补判判出了真结论、
             * 要么已经被放行去跑）。仍在名单里的（例如"消费方还没跑完"那一支）保持"待判"。
             */
            ArchiveTask[] pending = _volumeDeficitDeferred.ToArray();

            /*
             * ⚠ 2026-10-09 **试做后已撤回**（如实记账）：本想在这里加一趟"最后一轮分卷操作"——
             * 对全表每一单再跑一次 `AdoptPublishedVolumePieces`，把"某一组还缺的那一片"接进那一层，
             * 以解掉真机 EEEE 的时序竞争（`111(3).rar` 收尾 22:44:30 早于入口 `111.zip` 落地 22:44:32
             * ⇒ 那一次接片是空操作，`111(3)\111(3)\111.z02` 一直没被接走）。
             *
             * ⛔ 实做第一步就打红：`SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
             * ③"盘上收口只留成品" ⇒ 留下孤儿 `111_outer\111.z01` —— 全表扫会把片接进**产出方自己的落点层**，
             * 而收尾的删除档不认那个新名字。⇒ 要做的不是"再扫一遍全表"，而是
             * **接片的目标层与收尾的清理口径必须一致**（唯一出口仍是 `ResolveBatchGroupDirectory` +
             * `PublishUnresolvedPieceLandingCopies`）。撤回后该用例恢复绿。
             */
            List<ArchiveTask> chainEndCompleted = await RecheckDeferredVolumeDeficitsAsync(
                finalPass: true,
                cancellationToken).ConfigureAwait(false);

            foreach (ArchiveTask task in chainEndCompleted)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndCompleteLogFormat,
                        task.FileName));

                /*
                 * ⚠ 2026-10-09 **B1：链尾给它们一个落定结论**（原话：「链尾 toRun 落结论、Pending 收口」）。
                 *
                 * 改之前这里**只打一行日志**，那几单的机器终态一直停在 `Pending`、状态停在「等待解压」
                 * ⇒ 批末汇总按终态把它们算进「未处理」，而它其实"这一批没轮到"（原因很具体：
                 * 整组是在链尾这一趟才凑齐的，而这一批的轮次已经跑完了）。
                 *
                 * ⛔ **这不是"放宽批中间不落结论"那条判据**：判据管的是**批中间**，而走到这一行时
                 * 整条链已经跑完 —— 就地落结论正是「分卷和其他未识别的东西统统跳过，到了最后再重新
                 * 检验一步」那条口径的最后一格。
                 *
                 * ⛔ 结论**不许写成缺卷 / 跟班卷**：这一组的完整性刚在这里被判成"齐了"，
                 * 写"缺卷"是假话；内容也确实没有任何一单解过（解过的话上面
                 * `TryResolveConsumedByAnotherTask` 早就把它按跟班卷收场了）。⇒ 如实写
                 * 「这一批没轮到它」，并且**明确告诉用户再点一次就会解**。
                 */
                task.Outcome = TaskOutcome.Skipped;
                task.MarkSkipped(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndNotRunText,
                        task.FileName));
            }

            foreach (ArchiveTask task in pending)
            {
                if (!_volumeDeficitDeferred.Contains(task))
                {
                    task.IsVolumeDeficitDeferred = false;
                }
            }

            /*
             * ⚠ 2026-10-08（EEEE 真机）：上面这一趟是**整批跑完之后**才改状态的那一处 ——
             * 导出的日志若在这里就结束，"①页每一行显示什么"那份快照会停在批首的中性档
             * （真机那两行写着「等待解压」，用户据此判定"显示不对"）。⇒ 补一份定稿后的快照。
             */
            AppendTaskRowSnapshot();

            /*
             * ⚠ 返回"链尾这一趟才凑齐、但这一批已经没有机会再跑"的那几单：调用方（一键档）拿它写汇总，
             * 测试拿它断言"一单都不许留在 `Pending`"。⛔ 它们的状态已经在上面的循环里落定。
             */
            return chainEndCompleted;
        }

        /// <summary>
        /// **把①页每一行实际显示的那几格写进日志**（用户 2026-10-07：「你日志里面能否看清楚列表里面
        /// 显示什么内容」）。显示层（列绑定 / 行刷新）本来不进日志，于是"列表显示对不对"只能靠截图；
        /// 这一行把名称 / 大小 / 当前后缀 / 完整路径 / 状态 / 错误信息**按显示口径**原样打出来，
        /// 用户导出的日志就能与界面逐格对齐（⛔ 取的是同一批计算属性，不另算一套）。
        ///
        /// <para>两处调用：批循环收尾那一处、<see cref="FinalizeDeferredVolumeDeficits"/> 那一处
        /// （整批跑完之后的定稿）。⛔ 快照格式只此一个出口。</para>
        ///
        /// <para>⛔ 只在「详细日志（排查用）」打开时才写：默认关（成功任务只留一行是既有红线，
        /// 有守门用例钉着），所以这些行默认不出现。</para>
        /// </summary>
        private void AppendTaskRowSnapshot()
        {
            if (!Settings.VerboseLog)
            {
                return;
            }

            foreach (ArchiveTask row in SnapshotTaskTable(Tasks))
            {
                if (row == null)
                {
                    continue;
                }

                AppendLog(
                    "WARN",
                    $"[行] 名={row.DisplayFileName} ｜ 大小={row.DisplaySizeText} ｜ 后缀={row.DisplayExtension}"
                        + $" ｜ 路径={row.DisplayPath} ｜ 状态={row.StatusDisplayText} ｜ 错误={row.DisplayErrorMessage}");
            }
        }

        /// <summary>
        /// 这一单是不是"缺卷留到最后再判"名单里那一单（**事实位**，供一键档那道守卫用）。
        ///
        /// <para>⛔ 只读这份名单本身：不看状态、不看终态、不重推判据 —— 名单的写入点唯一
        /// （<see cref="RecordDeferredVolumeDeficit"/>）。</para>
        /// </summary>
        internal bool IsDeferredVolumeDeficit(ArchiveTask? task) =>
            task != null && _volumeDeficitDeferred.Contains(task);

        /// <summary>
        /// 同目录里还有没有"同一分卷组的别的卷"（只按名字判，**只用于兜底拒搬**）。
        ///
        /// <para>用于"分组信息没拿到时宁可不动"那一条：搬走一卷、把同组别的卷留在原地，
        /// 等于把一套完整的包拆成废件（2026-09-28 真机：内层包 4 卷只搬走了 `.001`）。</para>
        /// </summary>
        internal static bool HasSiblingVolumeBeside(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                string name = FileNameHelper.GetFileName(path);

                if (!FileNameHelper.IsVolumePartFileName(name))
                {
                    return false;
                }

                string directory = Path.GetDirectoryName(path) ?? string.Empty;

                foreach (string sibling in Directory.EnumerateFiles(directory))
                {
                    string siblingName = Path.GetFileName(sibling);

                    if (string.Equals(siblingName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (FileNameHelper.IsVolumePartFileName(siblingName) &&
                        VolumeGroupDetector.BelongsToSameGroup(name, siblingName))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 读目录失败就当"没有兄弟卷"：调用点那边保守方向是"不搬"，不会因为这里失败而多搬东西。
            }

            return false;
        }

        /// <summary>
        /// 解压前把"名字被伪装的整组卷名"改回标准名（用户 2026-09-28 三层方案的第 3 步）。
        ///
        /// <para>判据只有一处（<see cref="VolumeNameRepair.Plan"/>），执行体也只有一处
        /// （<see cref="VolumeNameRepair.TryApply"/>）—— 手动档的「修复分卷名并重试」调的是**同一对**。
        /// 这里把它挪到"每单开工前"自动做一次：一键处理与手动解压都从这个入口走，两条路行为一致。</para>
        ///
        /// <para>⛔ 底线：只改名字，不动内容；目标名被占 / 证据不足 → 一个字节都不动，写清为什么。</para>
        /// </summary>
        private async Task NormalizeDisguisedVolumeNamesAsync(ArchiveTask? task, CancellationToken cancellationToken)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                string current = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(current))
                {
                    return;
                }

                VolumeNameRepairPlan? plan = null;

                /*
                 * 试开探针的落点 = **本批的目标工作区根**（`<目标目录>\.ArchiveFixer.work`，
                 * 见 ApplyBatchWorkspaceRoot；不变量 12：需要临时物的地方一律由调用方把工作区根传进去）。
                 * ⛔ 它不能为空时"另找一个地方"：空 / 跨盘 ⇒ VolumeNameRepair 直接不试开、如实报"无法确认"，
                 * 代价是这一单要么按原名继续、要么报缺卷 —— 比在用户盘上开工作区划算。
                 */
                string volumeProbeWorkRoot = _pathService.WorkDirectory;
                string currentName = FileNameHelper.GetFileName(current);

                /*
                 * 跨盘 zip 那一档要拿密码去**试拼验证**（末片的中央目录把大部分片钉住了，
                 * 剩下几片"内容里一个文件都没开始"的，只能靠"按这个顺序真解一遍"来证）。
                 * 候选与解压那一步**同一套算法**（§9.5 同一件事只有一个出口），
                 * 密码只活在内存里、绝不进日志（不变量 5）。
                 */
                IReadOnlyList<string> volumeTrialPasswords = BuildVolumeProbePasswordCandidates(current);

                /*
                 * 什么时候值得问"名字那条路"：
                 * ① 自己的名字里带卷标记（老口径）；
                 * ② **账上已经归过组**（`task.IsVolumeGroup`）—— 真机 DDD（用户 2026-10-01 18:09）：
                 *    跨盘 ZIP 分了 4 片，本体 `222.zip` 是**标准名**（它不是卷标记），被改坏的是三个续卷
                 *    （`222.z0删1` / `222.z除02` / `222.z文03`）。只按 ① 判 ⇒ 名字那条路**压根不跑**，
                 *    内容那条路又只支持 2 片的跨盘 zip ⇒ 两边都不动 ⇒ 7-Zip 报 `Missing volume : 222.z01`、
                 *    整包解不开（用户原话：「我这次将 zip 多分了几个卷你就弄不了了」）。
                 * ③ **同目录里躺着一片"自述带盘号的跨盘 zip 末片"** —— 真机 FFF（用户 2026-10-01 19:23）：
                 *    一组 7 片的名字里**一个卷号都没有**（`222.z0删除A` / `222.删除C` / `222.zDDD` …），
                 *    扫描期按名字归组得到 **0 组**（他盘上实测就是这个数）⇒ ② 也不成立 ⇒ 这条路又一次
                 *    压根不会被问到（新写的"按归档自己的索引定盘"再准也轮不到它跑）。
                 *    这一档的判据只有一条事实：**目录里真有一片自述是末片**（EOCD 盘号 > 0）。
                 */
                bool namePathWorthAsking = FileNameHelper.IsVolumePartFileName(currentName)
                    || task.IsVolumeGroup
                    || VolumeNameRepair.HasSpannedZipTailNearby(current);

                /*
                 * ===== 底层识别优先（用户 2026-10-17 总则：「一开始肯定是用底层识别来弄……只有底层
                 * 做不到或者哪些地方需要名字的修改来弄的」）=====
                 * 先用**内容**那条路（族/卷号来自内容 + 硬链接试开证实），名字那条路只在它判不出时兜底。
                 * ⚠ 候选池口径一个字不改：先同目录；判不出仍由下面"放宽到邻近目录"那一段接手。
                 */
                plan = await VolumeNameRepair.PlanByContentAsync(
                    current,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(current),
                    _archiveEngine,
                    volumeProbeWorkRoot,
                    volumeTrialPasswords,
                    cancellationToken).ConfigureAwait(false);

                if ((plan == null || !plan.CanRepair) && namePathWorthAsking)
                {
                    VolumeNameRepairPlan? byName = VolumeNameRepair.Plan(
                        current,
                        VolumeNameRepair.EnumerateFileNamesInDirectory(current));

                    if (byName != null && (byName.CanRepair || plan == null))
                    {
                        plan = byName;
                    }
                }

                /*
                 * **跨目录只在"同目录判不出来"时才放宽**（用户 2026-10-01 点名的跨目录找卷 + 「这种是小概率，
                 * 一般都会放在一起」）。
                 *
                 * ⚠ 顺序很要紧：候选池一放宽，7z 那条路（"尺寸排序 + 硬链接试开"）的排列数就会跟着涨
                 * —— 邻近目录里**别的包**的卷会一起进候选，把真正那一组的顺序挤出排列上限（`MaxOrderings`）。
                 * 真机夹具上实测过：先放宽就直接让两组解不出来（产物 20 → 10）。
                 * 所以：**先用老口径（只有自己这一层）试一次；判不出来，才把范围放宽到邻近目录**。
                 */
                if (plan == null || !plan.CanRepair)
                {
                    VolumeNameRepairPlan? widened = await VolumeNameRepair.PlanByContentWithNearbyCandidatesAsync(
                        current,
                        VolumeNameRepair.EnumerateVolumeCandidatesNearby(current),
                        _archiveEngine,
                        volumeProbeWorkRoot,
                        volumeTrialPasswords,
                        cancellationToken).ConfigureAwait(false);

                    if (widened != null && widened.CanRepair)
                    {
                        plan = widened;
                    }
                    else if (plan == null)
                    {
                        plan = widened;
                    }
                }

                if (plan == null || !plan.CanRepair || plan.Items.Count == 0)
                {
                    /*
                     * ===== 名字路 / 内容路都改不出来 ⇒ 再试**跨目录收卷**（2026-10-05 口径 1）=====
                     *
                     * 真机形状：`555\B250135(1)\B250135.7z.001` + `555\B250135(2)\B250135.7z.002`
                     * —— 两半被网盘拆到两个**兄弟文件夹**里。这一档两条改名路都插不上手
                     *（`.001` 的名字本来就是标准的 ⇒ 内容路判"问题不在名字上"直接返回），
                     * 而 7-Zip 找兄弟卷**只看入口文件旁边那一层** ⇒ 引擎只看到一片 ⇒ 必然解不开。
                     *
                     * 判据与执行体都是既有出口（`ResolveCrossLayerVolumeGather` + `TryApply`），
                     * 这里给的是**找卷窗口**（基准 = 手上那一卷，⛔ 绝不是 001）；收完重新读一次盘上事实。
                     * ⛔ 判不出 / 收不成 ⇒ 一个字节都不动，照旧往下走（让引擎去判）。
                     */
                    if (task.IsVolumeGroup)
                    {
                        if (TryGatherVolumesBeforeExtract(task, out string gatheredDetail))
                        {
                            /*
                             * 收完并且**盘上自证完整** ⇒ 这一档的活干完了：名字与位置都对，
                             * 下面的流程照旧（引擎自己解得开）。
                             */
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(gatheredDetail))
                        {
                            AppendLog(
                                "WARN",
                                string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.VolumeGatherBeforeExtractBlockedFormat,
                                    task.FileName,
                                    gatheredDetail));
                        }
                    }

                    /*
                     * ⚠ 这里**只**对"试开真跑过、结论是不成立"的情形写一行 —— 用户 2026-09-29 真机
                     * 查不出断在哪，正是因为这条路上从前**一句日志都没有**（内容级推断默默返回，
                     * 用户看到的只有后面那句「分卷缺失」，看不出程序到底试没试）。
                     *
                     * ⛔ 不能对所有"不能改"都写：内容级推断对**每个**普通包都会问一遍（名字里没有卷号 → 走内容路），
                     * 全都记一行会把第 45 条的日志纪律打掉（成功的任务只留一行，见 Item45LogAndPasswordTests）。
                     * 判据是 `TrialAttempted`（闸门放行过、硬链接与引擎调用真发生过），不是文案。
                     */
                    if (plan != null
                        && !plan.CanRepair
                        && (plan.TrialAttempted || plan.SpannedTailMissing || plan.ByteBudgetMismatch))
                    {
                        /*
                         * 三种"值得说一句"的结论共用这一行，措辞按事实分开：
                         * ① 试开真跑过（硬链接 + 引擎列目录）而不成立；
                         * ② 一次引擎都没调、但**判出了缺的是末片**（满片规律 + 跨盘标记，用户要的那句诊断）；
                         * ③ 一次引擎都没调、但**按 7z 起始头算出了还差多少字节**（2026-10-03 阶段 B：
                         *    起始头是明文、与卷序无关，"缺多少"是算得出来的事实 —— 用户问过"到底缺多少"）。
                         */
                        AppendLog(
                            "INFO",
                            plan.TrialAttempted
                                ? $"{task.LogName}：按内容试开过这一组分卷（同卷硬链接 + 引擎列目录），不成立 —— "
                                  + $"{plan.Reason}；这一单按原名继续（源文件一个字节都没动）。"
                                : $"{task.LogName}：没有动它 —— {plan.Reason}；这一单按原名继续（源文件一个字节都没动）。");
                    }

                    return;
                }

                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                if (!result.Success)
                {
                    AppendLog("WARN", $"{task.LogName}：分卷名没改（{result.Message}），这一单可能因此解不开。");
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：分卷名不标准，已按标准名改好（{plan.Items.Count} 卷，只改名字、内容一个字节没动）：{plan.Describe()}"
                    + (plan.GatheredVolumes > 0
                        ? $"。⚠ 这一组有几卷散在别的文件夹里（{plan.GatheredVolumes} 卷），已一并收进入口那一层"
                          + "（引擎找兄弟卷只看入口文件旁边那一层，散着放即使名字都对也解不开；⛔ 只在同一盘上收，不跨盘搬）"
                        : string.Empty)
                    + (plan.ProbeNeedsPassword
                        ? "。⚠ 这一组是「文件名也加密」的归档（7z -mhe / RAR -hp）：试开时引擎只认得出它是一份加密归档、"
                          + "列不出清单 —— 解压那一步要靠「密码」页里的密码本，没有可用密码就会停在「密码错误」。"
                        : string.Empty));

                // 记在任务上：状态那一列会写成"已跳过（已改回标准名）"——
                // 免得"改过名"与"这一单没解成"读起来自相矛盾（用户 2026-09-28 真机反馈）。
                task.VolumeNameAutoRenamed = true;

                // 任务上的路径同步成新名字（"谁负责更新任务路径 + 快照"的唯一出口）。
                RememberGatheredUserSourcePieces(plan.Items);

                SyncTasksAfterVolumeRename(plan.Items, task);
            }
            catch (Exception ex)
            {
                // 改名失败不该拖垮整单：写清楚，后面的流程按原名去跑（它会如实报缺卷 / 打不开）。
                AppendLog("WARN", $"{task.LogName}：分卷名自动修正跳过（{ex.Message}）。");
            }
        }

        /// <summary>
        /// **批级整组改名**：在**任何任务开工之前**，把这一批里"名字被改坏的分卷组"改回标准名。
        ///
        /// <para>为什么必须串行、必须在批首（而不是每个任务开工前各改各的）：并发跑 N 个任务时，
        /// "改名 → 归组 → 解压"三步不是原子的，而同一个包的多卷是**多个任务** ——
        /// 谁先开工不定，另一单就可能拿着**旧名字**判完归组（认不出组）再往下跑，
        /// 于是同一组被解两遍、第二棵产物树落成 `名字(1)`、后续卷那一单报「分卷缺失」，
        /// 连整组的链尾处理也被它拦住（用户 2026-10-01 真机现场）。</para>
        ///
        /// <para>位置要求两条：① 工作区根**已经定下来**（名字路不用试开，但内容级推断要拿工作区
        /// 做硬链接试开，拿不到就只能降级成"无法确认"）；② **并发启动之前**。
        /// 调用点只有 <c>StartExtractForOneClickAsync</c> 里、<c>ApplyBatchWorkspaceRoot</c> 之后那一处。</para>
        /// </summary>
        internal async Task NormalizeDisguisedVolumeNamesForBatchAsync(
            IReadOnlyList<ArchiveTask>? tasks,
            CancellationToken cancellationToken = default)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            foreach (ArchiveTask task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

            // 第一大步不改分卷名（改名归第二大步）
            }
        }

        /// <summary>
        /// **这一单是不是"同一分卷组的后续卷"** —— 是就跳过，不重复解一遍（一组只解一次）。
        ///
        /// <para>判据只读事实，两步：① 先按**当前真实文件名**重新归组
        /// （<see cref="OneClickCoordinator.ApplyVolumeGroupingFromDirectory"/>，与扫描期、
        /// 与手动档补救走的是同一条路 —— 扫描期那一份是按改名**前**的名字算的，已经过期）；
        /// ② 再看这一组的**第一卷**是不是任务表里**另一个任务**在负责。</para>
        ///
        /// <para>⛔ 首卷不在任务表里时**不跳**（用户只把 `.002` 拖进来那一档）：照旧让引擎去报缺卷，
        /// 绝不因为"组里没人负责"就把这一单静默吞掉 —— 那等于把一个真失败说成"已跳过"。</para>
        ///
        /// <para>跳过的这一单**不拍快照、不碰盘、不写任何产物**；它的源包跟着整组一起被处理
        /// （首卷那一单的 <c>VolumePaths</c> 记的是整组）。终态落 <see cref="TaskOutcome.Skipped"/>
        /// （既不是成功也不是失败）：批末汇总与批末诊断读的都是它。</para>
        /// </summary>
        /// <returns>true = 已经跳过（调用方必须立刻 return，什么都不许做）。</returns>
        /// <summary>
        /// 这一趟**真解出来的东西**够不够证明"密码是对的"（带这一趟引擎的结构化事实）。
        ///
        /// <para>判据本体**唯一出口** <see cref="ProducedContentGate"/> —— 单层这条路与递归那条路
        /// （<c>RecursiveExtractor</c>）问的是同一个方法，⛔ 不许各写一份（2026-10-01 只接了单层那条路，
        /// 而真机 `giu910` 走的是递归那条 ⇒ 修复等于没生效，用户白等 13 分钟）。</para>
        ///
        /// <para>为什么必须把引擎事实带上（2026-10-12 真机 `2222`）：RAR4 + 存储 + 数据加密的包上，
        /// **错密码**也会把垃圾明文按原尺寸写满盘，光数文件数与字节数会把错密码判成"密码已证实"
        /// ⇒ 候选循环当场 break、正确密码（密码本第 7 位）永远没机会被试。这里只负责把两个计数递过去。</para>
        /// </summary>
        private static bool TryMeasureProducedContent(
            string? directory,
            ArchiveOperationResult engineResult,
            out int files,
            out long bytes) =>
            ProducedContentGate.TryMeasure(
                directory,
                engineResult.ReportedSubItemErrors,
                engineResult.FailedEntryNames.Count,
                out files,
                out bytes);

        private bool SkipWhenAnotherTaskOwnsThisVolumeGroup(ArchiveTask task)
        {
            try
            {
                string currentPath = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return false;
                }

                // 每次开工重算（任务对象是复用的：上一次的结论不能留到这一次）。
                task.IsVolumeGroupFollower = false;

                /*
                 * 只有"看着像分卷"或"扫描期已经被归过组"的任务才值得重新归组：
                 * 普通单卷包每次开工都去扫一遍目录，换不来任何结论。
                 *
                 * ⚠ 2026-10-01 补第三档：**同目录里有没有和它同基名、名字带卷标记的兄弟**
                 * （`222.zip` 旁边躺着 `222.z01`）—— 这一档名字上看不出来（`.zip` 不是卷标记），
                 * 而真机上恰恰就是它（用户第三 / 第四次报的那 400 MB 源卷）。
                 * 判据抽在 `OneClickCoordinator.NeedsVolumeGroupRefreshByName`（唯一出口，有用例钉着）。
                 */
                bool looksLikeVolume = OneClickCoordinator.NeedsVolumeGroupRefreshByName(task, currentPath);

                if (!looksLikeVolume)
                {
                    return false;
                }

                List<string> pathsBeforeGrouping = task.GetSnapshotPaths();

                /*
                 * **只看不写**：按本目录算一次组，用来判"自己是不是这一组的后续卷"。
                 * ⛔ 不用 `ApplyVolumeGroupingFromDirectory`：那个会把结论写回任务账上，
                 * 而账上那份可能来自改名同步 / 扫描期归组（认得出跨目录的整组）——
                 * 按目录归组只会给出本目录里那几卷，照它覆盖就把"整组一起搬"砍成"只搬第一卷"。
                 */
                ArchiveFixer.Detection.VolumeGroup? group =
                    OneClickCoordinator.ResolveVolumeGroupFromDirectory(task);

                /*
                 * ⚠ 2026-10-01（用户真机第三 / 第四 / 第五次报）：**两个来源都要问，取更全的那份**
                 * （`OneClickCoordinator.PickFullerGroup`，判据只此一份、有用例钉着）。
                 *
                 * 现场：批首整组改名把 `222.zscip` → `222.zip`、`222.z删除01` → `222.z01`（都改对了），
                 * 可任务账上的清单只是扫描期那一份（1 卷）。而 `ResolveVolumeGroupFromDirectory(222.zip)`
                 * **返回的不是 null，而是一个"只有 1 卷"的组** ⇒ 老写法下"只增不减"的闸门 `1 > 1` 不成立
                 * ⇒ 按名字补清单（2 卷）那一路永远轮不到 ⇒ 源包搬运只搬 `222.zip` 自己，
                 * 400 MB 的 `222.z01` 留在源目录。
                 */
                ArchiveFixer.Detection.VolumeGroup? byName =
                    OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName(task, currentPath);

                group = OneClickCoordinator.PickFullerGroup(group, byName);

                if (group == null)
                {
                    return false;
                }

                string firstVolume = group.FirstVolumePath;

                if (string.Equals(firstVolume, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 自己就是首卷：把**整组清单**补到账上（缺卷预检、源包搬运都按整组走）。
                     *
                     * ⚠ **只增不减**：新的归组结果不比账上多时，一个字都不动 ——
                     * 账上那份可能更全（见上面那段），而且"少"往往只是因为别的卷不在这个目录里。
                     * 这条也顺手保住了既有用例的语义：手工构造的组信息不会被这里悄悄改写
                     *（`整组改名之后_同批别的任务也要搬到新名字` 那条当场逮到过被砍短）。
                     */
                    /*
                     * ⚠ 2026-10-01（用户真机第五次，诊断日志给出答案）：**补清单的判据不能只看"自己是不是首卷"**。
                     *
                     * 诊断现场：`[222.zip] isGroup=True volPaths=2[222.zscip,222.z01] byName=2[222.z01,222.zip]`
                     * —— 按名字补出来的清单把**续卷排在了第 1 位**（`FirstVolumePath` 落到 `222.z01` 头上），
                     * 于是下面那个"自己是不是首卷"判不过（自己叫 `222.zip`）⇒ 清单一条都没写进账 ⇒
                     * `ResolveSourceGroup` 发现"自己的路径不在清单里"⇒ 只搬了 `222.zip` 自己，
                     * 400 MB 的 `222.z01` 留在源目录（用户连报三次的就是它）。
                     *
                     * 判据改成：**账里的卷数确实能变多**就补（这正是本方法的唯一目的）。
                     * "自己是不是首卷"只用来决定**要不要把自己也拒掉**（下面那段跟班判定），不再拦补账。
                     */
                    if (group.Volumes.Count > task.VolumePaths.Count)
                    {
                        /* 清单必须"自己在前、其余按卷序跟在后"：源包搬运按顺序读它。 */
                        var ordered = new List<ArchiveFixer.Detection.VolumeCandidate>();

                        ordered.Add(new ArchiveFixer.Detection.VolumeCandidate { Path = currentPath, Size = -1 });
                        ordered.AddRange(group.Volumes.Where(
                            v => !string.Equals(v.Path, currentPath, StringComparison.OrdinalIgnoreCase)));

                        new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(
                            task,
                            new ArchiveFixer.Detection.VolumeGroup
                            {
                                GroupKey = group.GroupKey,
                                DirectoryPath = group.DirectoryPath,
                                BaseName = group.BaseName,
                                Volumes = ordered,
                                FirstVolumePath = currentPath,
                                KnownVolumeCount = group.KnownVolumeCount,
                                ExpectedVolumeCount = group.ExpectedVolumeCount,
                                IsComplete = group.IsComplete,
                                MissingVolumeNames = group.MissingVolumeNames,
                                Note = group.Note
                            });

                        /*
                         * 账变长了 ⇒ 源文件快照盯的路径集合也变了，必须跟着重拍。
                         *
                         * 快照是**按位**比对的（GetSnapshotPaths() = CurrentPath + VolumePaths，顺序即比对位置）。
                         * 名单一变长，按位比对必然错位 ⇒ 误报「源文件已变化（修改时间（没有记录）→ …）」——
                         * 这一版第一次回归时就是这样：整批 4 个任务全红，根因就在这一句之前。
                         *
                         * ⛔ 这不叫"放宽不变量 11"：变的这一项是**程序自己刚记的账**
                         *（VolumePaths 是按目录里真实存在的文件填的），不是用户改了文件；
                         * 而且只在真的写长了时才重拍 —— 用户改文件仍然照旧被拦下。
                         */
                        if (!SamePaths(pathsBeforeGrouping, task.GetSnapshotPaths()))
                        {
                            task.CaptureSourceSnapshot();
                        }
                    }

                    return false;
                }

                ArchiveTask[] table = SnapshotTaskTable(Tasks);
                bool ownedByAnotherTask = false;

                foreach (ArchiveTask candidate in table)
                {
                    if (candidate == null || ReferenceEquals(candidate, task))
                    {
                        continue;
                    }

                    if (string.Equals(candidate.CurrentPath, firstVolume, StringComparison.OrdinalIgnoreCase))
                    {
                        ownedByAnotherTask = true;
                        break;
                    }
                }

                if (!ownedByAnotherTask)
                {
                    return false;
                }

                string ownerName = FileNameHelper.GetFileName(firstVolume);

                /*
                 * 这一轮没有任何产物、也没有任何校验：上一轮留下的校验结论与落点都要清干净
                 * （任务对象是复用的 —— 不清的话界面上会挂着一条已经不成立的"输出目录"）。
                 */
                task.IsOutputVerified = false;
                task.OutputVerification = OutputVerificationOutcome.NotAttempted;
                task.OutputPath = string.Empty;
                task.Outcome = TaskOutcome.Skipped;
                task.IsVolumeGroupFollower = true;

                task.MarkSkipped(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeGroupFollowerSkippedFormat,
                    ownerName));

                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeGroupFollowerSkippedLogFormat,
                        task.FileName,
                        ownerName));

                return true;
            }
            catch (Exception ex)
            {
                // 判不出来就**照常解**（保守一侧：宁可让引擎去报缺卷，也不静默吞掉一单）。
                AppendLog("WARN", $"{task.LogName}：分卷归组跳过判定失败（{ex.Message}），这一单按原名照常处理。");

                return false;
            }
        }

        /// <summary>两份路径名单是不是**逐位相同**（大小写不敏感，与 Windows 上其它路径比较同一口径）。</summary>
        private static bool SamePaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// **一次改名之后，谁负责把任务路径 + 源文件快照更新到新名字** —— 全仓唯一出口。
        ///
        /// <para><b>为什么必须覆盖整个任务表、而不是"驱动改名的那一单"</b>（用户 2026-09-30 真机）：
        /// <see cref="VolumeNameRepair.TryApply"/> 改的是**整组每一卷**（<c>VolumeNameRepair.cs</c> 里
        /// `plan.Items` 逐项 <c>File.Move</c>），而"组里别的卷"完全可能是**同一批里另一个任务**指着的文件。
        /// 只同步自己那一单，别的任务就会继续指着**已经不在磁盘上**的旧名字 ——
        /// 紧接着的不变量 11 比对（<see cref="StopIfSourceChanged"/>）当场报「源文件已变化（文件不见了）」，
        /// 那一单连解压都没开始（真机：一键档 5 个任务里 2 个因此失败，报的是"文件不见了"，
        /// 而文件其实好端端地躺在同一个目录里、只是改了名）。</para>
        ///
        /// <para><b>为什么快照也必须重拍</b>：路径不是不变量 11 要保护的东西（大小与修改时间才是），
        /// 但"改名"这件事实必须由程序自己记账 —— 重拍之后基准与新名字对齐，后面各层比对的才是同一份文件。
        /// ⛔ 这**不是**"放宽成文件不在也算没变"：比对口径一个字没动，变的只是"谁的名字"。</para>
        ///
        /// <para>返回被同步到的任务（调用方要重新识别的那条手动路会用到）。名字没变的项不重拍快照
        /// （<c>BuildPlanFromOrder</c> 会把"本来就标准"的那一卷也放进 Items）。</para>
        /// </summary>
        /// <param name="items">改名计划里的每一卷（旧路径 → 新路径）。</param>
        /// <param name="primary">驱动这次改名的任务（可能在任务表里、也可能在表外，比如测试直接造的）。</param>
        internal IReadOnlyList<ArchiveTask> SyncTasksAfterVolumeRename(
            IEnumerable<VolumeRepairItem>? items,
            ArchiveTask? primary = null)
        {
            var affected = new List<ArchiveTask>();

            List<VolumeRepairItem> moves = (items ?? Enumerable.Empty<VolumeRepairItem>())
                .Where(item => item != null &&
                               !string.IsNullOrWhiteSpace(item.CurrentPath) &&
                               !string.IsNullOrWhiteSpace(item.TargetPath) &&
                               !string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (moves.Count == 0)
            {
                return affected;
            }

            /*
             * 任务表的快照：这一步跑在后台线程上（解压管线），而任务表归 UI 线程写 ——
             * 必须用**下标**拷贝，不能用 LINQ 的 ToArray：List 的枚举器带版本号检查，
             * UI 线程正在导入 / 移除时枚举会抛 "Collection was modified"
             * （AGENTS.md §11.2 记过同形状的假红），而这一抛会连带把"改名已做完"的同步丢掉。
             * 下标拷贝最坏只是拿到一份稍微过期的名单，绝不会抛。
             */
            ArchiveTask[] table = SnapshotTaskTable(Tasks);

            foreach (ArchiveTask candidate in EnumerateDistinct(primary, table))
            {
                bool touched = false;

                foreach (VolumeRepairItem move in moves)
                {
                    if (string.Equals(candidate.CurrentPath, move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                        candidate.CurrentPath = move.TargetPath;

                        /*
                         * **这一步变换也要记进那一行的链里**（唯一写入出口）：整组改名走的是这条路，
                         * 只有"改名记录"本身的两个名字 —— ⛔ 不许靠账上字段比对推
                         * （用户 2026-10-07：「最简单的改名操作你都要有一个变换的过程」）。
                         */
                        candidate.AppendTransformationStep(
                            $"{Path.GetFileName(move.CurrentPath)} → {Path.GetFileName(move.TargetPath)}");

                        touched = true;
                    }

                    for (int i = 0; i < candidate.VolumePaths.Count; i++)
                    {
                        if (string.Equals(candidate.VolumePaths[i], move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate.VolumePaths[i] = move.TargetPath;
                            touched = true;
                        }
                    }
                }

                if (!touched)
                {
                    continue;
                }

                // 基准跟着新名字重拍（不变量 11 的"改名不算变化"就落在这里，而不是落在比对里放宽判据）。
                candidate.CaptureSourceSnapshot();
                affected.Add(candidate);

                if (!ReferenceEquals(candidate, primary))
                {
                    /*
                     * 别的任务被顺带改了名必须**说出来**：用户看到那一行的文件名变了、
                     * 而日志里一个字都没有的话，会以为程序动错了东西（AGENTS.md §9.5"值 + 通知"）。
                     */
                    AppendLog(
                        "INFO",
                        $"{candidate.LogName}：这一卷在同一个改名批次里被改成了标准名，"
                        + "任务路径与源文件快照已同步到新名字（内容一个字节没动）。");
                }
            }

            return affected;
        }

        /// <summary>把"驱动改名的任务"与任务表合成一个**不重复**的枚举（表里已有就不重复处理）。</summary>
        private static IEnumerable<ArchiveTask> EnumerateDistinct(ArchiveTask? primary, IEnumerable<ArchiveTask> table)
        {
            var seen = new HashSet<ArchiveTask>();

            if (primary != null && seen.Add(primary))
            {
                yield return primary;
            }

            foreach (ArchiveTask candidate in table)
            {
                if (candidate != null && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        /// 任务表的**不会抛**的快照（下标拷贝；理由见调用处）。
        /// 中途被 UI 线程改了就以已经拷到的那部分为准 —— 少同步一个任务，下一次改名还会再同步一遍。
        /// </summary>
        private static ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks)
        {
            var copy = new List<ArchiveTask>();
            int count = tasks.Count;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    copy.Add(tasks[i]);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // 名单在拷贝途中变短了：用已经拷到的部分。
            }

            return copy.ToArray();
        }

        /// <summary>
        /// 在 UI 线程上弹一个"是 / 否"确认框（**无 UI 宿主返回 false**，不弹窗口也不死等）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 的区别只有降级行为：那个走
        /// <see cref="DialogService.ShowConfirm(string)"/>，无界面宿主时返回 false 已是保守档，
        /// 但它仍然要求调用方在 UI 上下文里；这里显式调度，后台线程调用也安全。
        /// </summary>
        private Task<bool> DispatcherSafeConfirmAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return Task.FromResult(false);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上打开"选择文件夹"对话框（**无 UI 宿主返回空串**）。
        ///
        /// 只挑目录、不扫盘：真正的枚举在调用方丢给后台线程做（UI 线程纪律）。
        /// </summary>
        private string ShowFolderBrowserOnUiThread()
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return string.Empty;
            }

            if (dispatcher.CheckAccess())
            {
                return _dialogService.ShowFolderBrowserDialog();
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowFolderBrowserDialog()).Result;
        }

        /// <summary>
        /// 本批允许同时跑几个任务（并发上限）。
        ///
        /// <para>
        /// 「全速」开着时返回本批的任务数（= 不节流）—— 这是"自动等待/节流必须配一个立即继续的出口"
        /// 那条采纳项的落地（<c>docs/WinRAR功能参考.md</c> §2 D 组）。它有意的取舍：
        /// **不管磁盘、不管别的程序**，跑成什么样由用户自己负责；界面上的勾选框文案把这一点写明了。
        /// </para>
        /// <para>
        /// 关着时读设置项 <see cref="AppSettings.MaxParallelExtractCount"/>（1–8，默认 4 ——
        /// 2026-09-22 的并行实测：并发 1→4 是 3.08×，4→8 只再快 1.9% 却把单包耗时拉长一倍）。
        /// 夹一次是兜底：设置对象不一定走过 <c>AppSettings.Normalize</c>（测试里直接 new），
        /// 0 或负数会让"等并发位"的循环条件永远成立 —— 那个任务会永远等下去。
        /// </para>
        /// </summary>
        /// <summary>
        /// 排队等空位**超过这个秒数**才单独写一行（否则只进批末汇总）。
        ///
        /// <para>用户 2026-09-26 第 45 条：原来每个被节流挡住的包都写两行同义反复的话，真机上 76 个任务
        /// 出了 74 条 —— 而绝大多数等待只有一两秒，根本不值得占一行。</para>
        /// </summary>
        private const int ThrottleNoticeThresholdSeconds = 30;

        private int ResolveMaxParallel(IReadOnlyList<ArchiveTask> tasks, out bool fullSpeed)
        {
            fullSpeed = _vm.RunAtFullSpeed;

            if (fullSpeed)
            {
                return Math.Max(1, tasks?.Count ?? 1);
            }

            return Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);
        }

        /// <summary>
        /// 「空间不足」模式下**本批开几个**（用户 2026-09-27 拍板：忽略设置里的并发档与「全速」）。
        ///
        /// <para>判据全部在 <see cref="ExtractionScheduler.ResolveSpaceTightParallelCount"/> 里
        /// （"很多个体积相近的小包"→5，其余→3；再与"按空间算得出的建议并行数"取小）。
        /// 这里只做两件事：把那个数夹进"不超过任务数"，以及把**为什么是这个数**写进日志 ——
        /// 空间不足模式下用户最需要知道的正是"你凭什么只开 3 个"。</para>
        /// </summary>
        private int ResolveSpaceTightParallelForBatch(ExtractionSchedulePlan plan)
        {
            int count = ExtractionScheduler.ResolveSpaceTightParallelCount(
                plan.Ordered,
                plan.BudgetBytes,
                plan.RecommendedParallelCount,
                out string basis);

            count = Math.Clamp(count, 1, Math.Max(1, plan.Ordered.Count));

            /*
             * ⚠ 用户档只用来**往下压**（用户 2026-09-27 第二次追加："反正空间是弄好了，时间就是速度和卡顿
             * 你想怎么解决"，而这一批在 USB 外置盘上 3 个并发把磁头抢满了、界面也卡）。
             *
             * 口径：模式自己的上限（5 / 3）与「全速」照旧不生效，但**用户的并发档允许更低** ——
             * 慢盘上 1~2 个并发不但不卡，总时间常常还更短（磁头不用来回抢）。
             * ⛔ 只取小、绝不取大：拿用户档把并发**抬高**到模式上限之上是不允许的（那就把这个模式的意义拆掉了）。
             */
            int userCap = Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);

            if (userCap < count)
            {
                AppendLog(
                    "WARN",
                    $"「空间不足」模式：模式自己算出最多跑 {count} 个，但②页的「最大并发解压数」是 {userCap} —— "
                    + $"按更保守的 {userCap} 个跑（慢盘上并发少反而更快、也更不卡）。");

                count = userCap;
            }

            AppendLog(
                "WARN",
                $"「空间不足」模式：本批同时最多跑 {count} 个（忽略「全速」，不写设置）。{basis}。");

            return count;
        }

        /// <summary>并发上限的硬顶（与设置界面的 1–8 一致；这里只做兜底夹取）。</summary>
        private const int MaxParallelExtractCountCeiling = 8;

        /// <summary>
        /// 本批的"完成后打开输出目录"记账清零（设置项 <see cref="AppSettings.OpenOutputFolderWhenDone"/>）。
        /// </summary>
        private void ResetBatchOutputFolderState()
        {
            lock (_outputFolderLock)
            {
                _outputFolderOpenedThisBatch = false;
            }
        }

        /// <summary>
        /// 试着打开"这一批的"输出目录，**整批只成功打开一次**。
        ///
        /// 只允许在"最外层任务 + 内容物已定稿 + 输出校验通过 + 未取消"的收尾处调用
        /// （判定在 <see cref="PostProcessSuccessAsync"/> 里，那里才知道结论是否成立）。
        /// 内层包（续解出来的）一律不触发：它们与父任务共用同一个目录，反复打开就是刷屏。
        /// </summary>
        /// <returns>真的打开了返回 true；关着开关 / 已经开过 / 目录不存在返回 false。</returns>
        private bool TryOpenOutputFolderOnce(ArchiveTask task)
        {
            if (!Settings.OpenOutputFolderWhenDone)
            {
                return false;
            }

            if (task.IsContinuationTask)
            {
                return false;
            }

            lock (_outputFolderLock)
            {
                if (_outputFolderOpenedThisBatch)
                {
                    return false;
                }

                _outputFolderOpenedThisBatch = true;
            }

            /*
             * ⚠ 顺序有意：先"占位"再打开。
             *
             * 并发下两个任务可能同时走到这里，先占位保证只有一个真的去开资源管理器；
             * 万一打开失败（目录被删了之类），这一批也不会再补开一次 —— 用户看到一句 WARN 就够，
             * 反复尝试开一个不存在的目录只会刷日志。
             *
             * 调用**只打开文件夹**的实现（不置前、不最大化、不抢焦点，见 MainViewModel 的说明）。
             */
            return _vm.OpenCompletedOutputDirectory(task.OutputPath);
        }

        /// <summary>
        /// 批开始时把手动密码清干净：**只对本次运行有效**（不变量 5）。
        ///
        /// 不是"记住上次输的密码"—— 那是持久化，红线。上一批输过什么，这一批重新问。
        /// </summary>
        private void ResetManualBatchPassword()
        {
            _manualBatchPasswords.Clear();
            _manualPasswordPrompted = false;
            _batchHadPasswordFailures = false;
        }

        // ================================================================ 解压前的提醒（无用物 / 无可用密码）

        /*
         * 用户 2026-09-22 需求第 8 条原话：
         * 「无用物提醒 —— 源文件夹里可能有打包者的工具/诱饵文件，我们识别不出来；每次解压前弹一个提醒，
         *   列出可能的无用物；同时提醒那些没有可用密码的压缩包（即使密码本已经预加载）。」
         *
         * 两段合并成**一个**弹窗（项目先例：多任务清理合并成 1 次确认）。四条纪律（都有测试钉住）：
         * ① **无界面宿主按「继续处理」放行** —— 与 ShowConfirm 的 fallback=false（=取消）方向相反，
         *    理由见 DialogService.ShowReminderConfirm：这是一条纯提示，把整批解压拦死才是事故；
         * ② **「本次运行不再提示」只记在内存里**（下面那个字段），绝不写设置文件；
         * ③ **两段都为空时不弹**（不许弹空对话框）；
         * ④ 判据**窄**：无用物只看文件名 + 魔数体检，宁可漏报不可误报。
         */

        /// <summary>
        /// 本次运行内不再弹"解压前的提醒"（用户在弹窗里勾了那个勾选项）。
        ///
        /// <para>它是**实例字段**，不是静态、更不是设置项：用户勾的是"这次运行别再烦我"，
        /// 关掉程序重开就该再提醒一次（用户点名："只在本次运行内记忆，绝不写设置文件"）。
        /// 也因此**没有**任何把它写盘 / 读盘的代码 —— 测试直接断言设置文件一个字节都没变。</para>
        /// </summary>
        private bool _remindersSuppressedThisRun;

        /// <summary>
        /// "无用物"魔数体检用的识别器：**复用现有那一套**
        /// （<see cref="MagicArchiveProber"/> → <c>ArchiveDetectService</c>：文件头魔数 + 认不出来时再看尾部内嵌归档），
        /// 绝不另写一份魔数表 —— 两份必然会漂移，而漂移的后果是把用户的真包（伪装后缀 / 双面文件）
        /// 叫成"无用物"，正好砸在本程序最拿手的场景上。
        /// </summary>
        private readonly IArchiveProber _reminderProber = new MagicArchiveProber();

        /// <summary>
        /// 解压前提醒的"用户答案"（可注入；单测用它模拟"点了继续 / 点了先不处理 / 勾了不再提示"）。
        ///
        /// <para>为什么需要它：无界面宿主里那个框根本不会显示，而"勾了不再提示之后到底还会不会再弹"
        /// 只有让调用方答一次才测得出来。正式路径永远是 null（走真弹窗）。</para>
        /// </summary>
        internal Func<string, ReminderAnswer>? ReminderAnswerOverride { get; set; }

        /// <summary>一次"解压前的提醒"的用户答案。</summary>
        internal sealed class ReminderAnswer
        {
            /// <summary>true = 继续处理；false = 先不处理（这一批不开始，一个字节都不动）。</summary>
            public bool Confirmed { get; init; }

            /// <summary>是否勾了「本次运行不再提示这类提醒」。</summary>
            public bool OptionChecked { get; init; }
        }

        /// <summary>
        /// 一次「解压前的提醒」扫描的结论（A 段无用物 + B 段无可用密码）。
        ///
        /// <para>为什么把"扫出事实"和"弹框问人"拆开（用户 2026-09-24 第 17 条）：一键处理那条路
        /// 只允许有**一个**弹窗，所以它得在**动手之前**先拿到这两段事实、写进自己的确认框；
        /// 手动「只解压」那条路仍然用同一个扫描结果去弹原来那个提醒框。两段判据因此只有一份实现
        /// （<see cref="SourceJunkScanner"/> + <see cref="FindTasksWithoutUsablePassword"/>），
        /// 不会出现"确认框里说没有、解压时又提醒有"这种自相矛盾。</para>
        /// </summary>
        internal sealed class BatchReminderFacts
        {
            /// <summary>A 段：源目录里疑似打包者附带的文件。</summary>
            public SourceJunkScanResult Junk { get; init; } = new();

            /// <summary>B 段：需要密码但当前一个可用候选都没有的包。</summary>
            public IReadOnlyList<ArchiveTask> NoUsablePassword { get; init; } = Array.Empty<ArchiveTask>();

            /// <summary>两段都空 = 没什么可提醒的（既不弹框、也不写日志）。</summary>
            public bool HasAnything => Junk.HasAnything || NoUsablePassword.Count > 0;
        }

        /// <summary>
        /// 这一批的「解压前提醒」已经并进调用方的确认框了（用户 2026-09-24 第 17 条）。
        ///
        /// <para>置上之后 <see cref="ConfirmBatchRemindersAsync"/> **只写日志、不再弹第二个框**。
        /// 由 <c>OneClickCoordinator</c> 在问它那一个确认框前后设置，整条一键处理**全程有效**
        /// （续解的第 2/3 轮也不再弹 —— 那正是"弹窗太多"的来源之一），跑完由它复位
        /// （<see cref="EndCallerHandledReminderBatch"/>）。手动「只解压」不设它 —— 那条路本来就只有这一个框。</para>
        /// </summary>
        private bool _callerHandlesBatchReminderDialog;

        /// <summary>
        /// 已经替调用方扫好的那一次结论（见 <see cref="PrepareBatchReminderForCallerAsync"/>）。
        ///
        /// <para>为什么不直接让它再扫一遍：扫描要枚举源目录 + 做魔数体检（最坏 2000 次读文件），
        /// 同一批扫两遍纯属白花时间。**只用一次**（用完即清），第 2/3 轮的内层包重新扫 ——
        /// 那是新出现的包，本来就该重判。</para>
        /// </summary>
        private BatchReminderFacts? _prefetchedReminderFacts;

        /// <summary>
        /// 一键处理那条路在动手前调它：把两段事实拿走写进确认框，并声明"提醒已经并进去了"。
        /// </summary>
        internal async Task<BatchReminderFacts> PrepareBatchReminderForCallerAsync(
            IReadOnlyList<ArchiveTask> selectedTasks)
        {
            _callerHandlesBatchReminderDialog = true;

            BatchReminderFacts facts = await ScanBatchRemindersAsync(selectedTasks).ConfigureAwait(false);

            _prefetchedReminderFacts = facts;

            return facts;
        }

        /// <summary>
        /// 调用方**不打算弹任何框**（用户勾过「以后不再询问」）：提醒也一并不弹。
        ///
        /// <para>为什么不是"退回原来的提醒框"：用户勾的意思是"直接开始"，再弹一个提醒等于没听他的。
        /// 判定与日志一条都没少 —— 少的只是弹窗，而日志里照样写清"无用物 N 个 / 无可用密码 M 个"。</para>
        /// </summary>
        internal void SuppressBatchReminderDialogForThisRun()
        {
            _callerHandlesBatchReminderDialog = true;
            _prefetchedReminderFacts = null;
        }

        /// <summary>这一批跑完了：把"由调用方负责弹框"的状态收回去（下一批回到默认口径）。</summary>
        internal void EndCallerHandledReminderBatch()
        {
            _callerHandlesBatchReminderDialog = false;
            _prefetchedReminderFacts = null;
        }

        /// <summary>扫一遍"解压前的提醒"的两段（**只读事实、不弹框**）。</summary>
        internal async Task<BatchReminderFacts> ScanBatchRemindersAsync(
            IReadOnlyList<ArchiveTask> selectedTasks)
        {
            SourceJunkScanResult junk = await SourceJunkScanner
                .ScanAsync(selectedTasks, _reminderProber, CancellationToken.None)
                .ConfigureAwait(false);

            return new BatchReminderFacts
            {
                Junk = junk,
                NoUsablePassword = FindTasksWithoutUsablePassword(selectedTasks)
            };
        }

        /// <summary>
        /// 一键处理确认框的正文（用户 2026-09-24 第 17 条）：把三个结论汇总成四行文本。
        ///
        /// <para>三个结论各有**唯一来源**，这里只做汇总，绝不另算一遍：</para>
        /// <list type="bullet">
        /// <item><description>内容物落点 → <see cref="PathService.ResolveOutputPlacement"/>（与真正开跑同一条推导链）；</description></item>
        /// <item><description>其余物会怎么处理 → ③页「删除操作」那一档（<see cref="AppSettings.RestHandlingAfterVerify"/>，用户 2026-09-25 第 32 条定的三档）；</description></item>
        /// <item><description>两行提醒 → <see cref="BatchReminderFacts"/>（同一个无用物扫描器 + 同一套密码候选判据）。</description></item>
        /// </list>
        /// </summary>
        internal async Task<OneClickConfirmFacts> BuildConfirmFactsAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? pendingOptions,
            BatchReminderFacts? reminders,
            CancellationToken cancellationToken = default)
        {
            string destination = await DescribePlannedDestinationAsync(targets, pendingOptions, cancellationToken)
                .ConfigureAwait(false);

            /*
             * 「其余物会怎么处理」= 「删除操作」那一档（用户 2026-09-25 第 32 条：三档
             * 不动 / 移入回收站 / 彻底删除，判据只有一处实现 RestHandlingModes.Normalize）。
             * 这里只是把它**原样说给用户听**，一个字都不另算。
             *
             * ⚠ 第 33 条：**「本次选项」优先** —— 确认框里的折叠区现在也能改这一档，
             * 用户改完再点「开始处理」时，正文那一行必须跟着说这一次将会怎么处理，
             * 不能仍念设置里的旧值（那正是"弹窗没跟着设置/选择更新"的另一半）。
             */
            string restHandling = pendingOptions != null
                ? RestHandlingModes.Normalize(pendingOptions.RestHandling)
                : RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);

            SourceHandlingMode sourceHandling = pendingOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            /*
             * 「特定解压」那一行（用户 2026-09-24）：开关开着**且**至少有一条规则会真的跑时才写 ——
             * 用户最恨"我以为它按默认跑的"，所以动手前的那个框里必须写明这一次用了哪条规则。
             * 开关开着但一条规则都没勾时**不写**（那条路上行为与默认完全一样，写一行反而误导）。
             */
            SpecialExtractionPlan specialExtraction = SpecialExtractionPlan.FromSettings(Settings);

            /*
             * 「空间不足」模式：**动手前就把话说清**（用户 2026-09-27 要求确认框里有一条红字）——
             * 这个模式会覆盖上面那两档，而且会永久删除源包，属于"必须在点开始之前看见"的事。
             *
             * ⚠ 正文那两行也换成**覆盖后**的值：只加一条红字、却让"源包：留在原地"那一行照旧显示
             * 设置里的值，等于让用户在同一个框里读到两句互相矛盾的话。
             */
            bool spaceTight = _vm.SpaceTightMode;

            /*
             * 安全档（「不删原包」）：源包那一档要按"一个字节都不动"说，
             * 而不是按上面那样被覆盖成「彻底删除」—— 那会与安全档的真实行为相反。
             * ⚠ 2026-10-15：空间不足模式（非安全档）的真实动作 = **定稿 + 校验通过后当场就地删源包**
             * ⇒ 那一档的措辞对应 `DeleteInPlace`（原来是 `MoveToRest`，已随用户口径删除）。
             */
            bool spaceTightKeepSource = spaceTight && _vm.SpaceTightKeepSource;

            if (spaceTight)
            {
                sourceHandling = spaceTightKeepSource
                    ? SourceHandlingMode.KeepInPlace
                    : SourceHandlingMode.DeleteInPlace;

                restHandling = RestHandlingModes.Delete;
            }

            /*
             * 「多层 + 空间小 ⇒ 中途才报空间不足」这句话必须在**动手之前**就看得见
             * （用户 2026-09-29 第 2 条："开工前就要看得见……在批首的日志与确认框里写明"）。
             *
             * 判据与批首那条 WARN **是同一处**（EvaluateMultiLayerSpaceRisk），空间口径也是同一个
             * BuildSpaceAdvice（导入体检用的就是它）—— ⛔ 这里绝不另算一套。
             * 花的是 stat 文件 + 探一次盘，几十到几百个包也只是毫秒级，所以放后台线程跑。
             */
            MultiLayerSpaceRisk spaceRisk = await Task.Run(
                () => EvaluateMultiLayerSpaceRisk(BuildSpaceAdvice(targets)),
                cancellationToken).ConfigureAwait(false);

            string spaceRiskEcho = spaceRisk.Applies ? DescribeMultiLayerSpaceRisk(spaceRisk) : string.Empty;

            return new OneClickConfirmFacts
            {
                DestinationEcho = destination,
                RestEcho = StatusText.OneClickConfirmRestLabel + restHandling switch
                {
                    // ⚠ 2026-10-15：回收站那一档整条下线 ⇒ 这里只剩两档。
                    RestHandlingModes.Delete => StatusText.OneClickConfirmRestAutoDelete,
                    _ => StatusText.OneClickConfirmRestKeep
                },
                SourceEcho = StatusText.OneClickConfirmSourceLabel
                    + OneClickRunOptions.DescribeSourceHandling(sourceHandling),

                /*
                 * 空间不足模式下把「源包：」那一行**锁住**（用户 2026-09-27 真机逮到）：
                 * 折叠区里的单选框显示的是**设置**里的档（源包=留在原地），而这一批真实行为是覆盖后的 ——
                 * 不锁的话同一个框里会出现"源包：留在原地"与红字"会自动覆盖为「源包 → 放入其余物」"两句矛盾的话。
                 */
                SourceEchoLocked = spaceTight,
                SpaceTightEcho = spaceTight
                    ? spaceTightKeepSource
                        ? StatusText.SpaceTightKeepSourceConfirmText
                        : StatusText.SpaceTightConfirmText
                    : string.Empty,
                SpecialExtractionEcho = specialExtraction.IsActive
                    ? StatusText.SpecialExtractionConfirmLabel + specialExtraction.RuleNames
                    : string.Empty,
                NoticeEcho = BuildConfirmNotice(reminders, spaceRiskEcho)
            };
        }

        /// <summary>
        /// 确认框里那段"要说一声"的提醒（无用物 / 没有可用密码 / 多层可能中途空间不足）。
        ///
        /// <para>措辞与判定都沿用既有那一套（<c>StatusText.JunkReminder*</c> 的同源事实），
        /// 这里只是**变短**：确认框正文要能一眼看完，所以各只给一行、最多举三个例子。
        /// 详细清单仍然在日志里（<see cref="LogReminderFindings"/> 一字未改）。</para>
        ///
        /// <para>⚠ 空间那条排在最前（它关系"这一批能不能跑完"），而且它**不依赖**
        /// <paramref name="reminders"/> —— 没有无用物、没有密码问题时它照样要出现。</para>
        /// </summary>
        private static string BuildConfirmNotice(BatchReminderFacts? reminders, string spaceRiskEcho)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(spaceRiskEcho))
            {
                builder.AppendLine(spaceRiskEcho);
            }

            if (reminders == null || !reminders.HasAnything)
            {
                return builder.ToString().TrimEnd();
            }

            if (reminders.Junk.HasAnything)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmJunkFormat,
                    reminders.Junk.TotalCount,
                    string.Join("、", reminders.Junk.SampleNames.Take(3))));
            }

            if (reminders.NoUsablePassword.Count > 0)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmNoPasswordFormat,
                    reminders.NoUsablePassword.Count,
                    string.Join(
                        "、",
                        reminders.NoUsablePassword.Take(3).Select(DisplayNameOf))));
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 「内容物会生成在…」那一行（用户点名要看的第一件事）。
        ///
        /// <para>走**落点唯一实现**（<c>PathService.ResolveOutputPlacement</c> → <c>OutputPlacement</c>），
        /// 与真正开跑时同一个 <c>ExtractOptions</c> 构造（含"续解各占一层 / 一律不塌缩"那套口径）——
        /// 所以这一行说的就是"真会落到哪"，不是另写一套公式算出来的近似值。
        /// 算不出来时**如实说明原因**（绝不编一个假路径让用户放心）。</para>
        ///
        /// <para>多包时补一句后缀：现在只剩"每个包各建一层（含相对子路径）"这一种形状 ——
        /// 旧的"所有包落进同一层"（场景 B / 共用根）已随落点模型 v2 退役。</para>
        /// </summary>
        internal async Task<string> DescribePlannedDestinationAsync(
            IReadOnlyList<ArchiveTask> targets,
            OneClickRunOptions? pendingOptions,
            CancellationToken cancellationToken = default)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            ExtractOptions extractOptions = BuildExtractOptions(tryExtractUnknownFormat: false);
            pendingOptions?.ApplyTo(extractOptions);

            ArchiveTask first = targets[0];

            OutputPlacementResult placement = _pathService.ResolveOutputPlacement(first, extractOptions);

            if (!placement.Success || string.IsNullOrWhiteSpace(placement.DestinationDirectory))
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.OneClickConfirmDestinationUnknownFormat,
                    placement.Message);
            }

            string head = placement.DestinationDirectory;

            if (targets.Count <= 1)
            {
                return head;
            }

            ArchiveTask? second = targets.FirstOrDefault(
                task => !ReferenceEquals(task, first)
                        && !string.Equals(task.CurrentPath, first.CurrentPath, StringComparison.OrdinalIgnoreCase));

            if (second != null)
            {
                OutputPlacementResult other = _pathService.ResolveOutputPlacement(second, extractOptions);

                if (other.Success
                    && SafePathHelper.PathEquals(other.DestinationDirectory, placement.DestinationDirectory))
                {
                    return head + string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OneClickConfirmMultiSharedFormat,
                        targets.Count);
                }
            }

            return head + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.OneClickConfirmMultiPerArchiveFormat,
                targets.Count);
        }

        /// <summary>
        /// 造一份这一批用的解压选项（**唯一构造处**）。
        ///
        /// <para>为什么必须是一个方法：一键处理确认框要预告"内容物会落到哪"，而它必须与真正开跑时
        /// 用**完全相同的字段**去算 —— 两处各抄一份字段清单，迟早会漂移成"预告说 A、实际落 B"。</para>
        /// </summary>
        private ExtractOptions BuildExtractOptions(bool tryExtractUnknownFormat)
        {
            var extractOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,

                /*
                 * ⚠ 引擎的"覆盖档"**刻意不跟着设置走**（用户 2026-09-25 真机铁证）。
                 *
                 * 设置里那一档（默认 SkipExisting → `-aos`）是给"最终落位的同名冲突"用的，
                 * 而我们自己的定稿逻辑（SafePathHelper 自动改名 / 冲突档）**根本不看引擎这个参数** ——
                 * 引擎写的永远是**我们的暂存目录**（`engineOutputPath = stageDirectory`），
                 * 里面的东西每一轮都是垃圾。
                 *
                 * 跟着设置走的代价实测过一次：`-aos` 让"上一次错密码留下的 0 字节桩文件"把
                 * **下一个候选（可能就是正确密码）**直接跳过、退出码 0 → 正确密码被当成废票扔掉
                 * （详见下面候选循环里那段说明）。所以暂存提取一律**覆盖写**。
                 */
                OverwriteMode = "OverwriteAll",
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                TryEmptyPasswordFirst = Settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = tryExtractUnknownFormat,
                MaxParallelExtractCount = Settings.MaxParallelExtractCount,

                /*
                 * 「解压到当前文件夹」（用户 2026-09-27）：本批是不是摊平只看这一个运行期字段。
                 * 落点由 OutputPlacement 唯一实现推导（destDir = 源包自己那一层），
                 * "不套包名那一层"由定稿那一侧按同一字段决定（见 PlanFinalLayoutForTask）——
                 * 两处读的是同一个字段，不会出现"落点摊平了、定稿又套了一层"。
                 */
                ExtractIntoSourceFolder = _extractIntoSourceFolderThisRun
            };

            extractOptions.Normalize();

            return extractOptions;
        }

        /// <summary>
        /// 解压前的合并提醒。
        /// </summary>
        /// <returns><c>false</c> = 用户在提醒里选了「先不处理」，调用方**直接返回**（这一批不开始）。</returns>
        private async Task<bool> ConfirmBatchRemindersAsync(IReadOnlyList<ArchiveTask> selectedTasks)
        {
            if (_remindersSuppressedThisRun)
            {
                return true;
            }

            /*
             * 设置里关掉了「解压前的提醒」= **不扫、不弹**，直接开始（用户 2026-09-24 第 15 条）。
             *
             * ⚠ 判在**扫描之前**：那条扫描会枚举整批任务的源目录（最坏约十万条目录项），
             * 关掉开关的人不该付这份代价。这里只读设置、只写一行日志，不碰任何文件。
             */
            if (!Settings.RemindBeforeExtract)
            {
                AppendLog("INFO", StatusText.RemindBeforeExtractDisabledLog);
                return true;
            }

            // 调用方刚为确认框扫过一次就复用它（用完即清）；否则现在扫。
            BatchReminderFacts? prefetched = _prefetchedReminderFacts;
            _prefetchedReminderFacts = null;

            BatchReminderFacts facts = prefetched
                ?? await ScanBatchRemindersAsync(selectedTasks).ConfigureAwait(false);

            if (!facts.HasAnything)
            {
                // 两段都为空：不弹、也不写日志（没有结论可写）。空对话框是纯噪声。
                return true;
            }

            LogReminderFindings(facts.Junk, facts.NoUsablePassword);
            /*
             * ⛔ **"无用物移出任务列表"也挂在这里**（真机 2026-10-07 07:49）：
             * 判据与结论**这一处已经算好了**（上面那句提醒用的就是 `facts.Junk`）；
             * 而 `ScanCoordinator.RemoveJunkTasksFromListAsync` 那两个调用点（导入 / 一键处理）
             * 在真机那一批里**一次都没进到扫描**（我加的排障行没出现，列表里那 4 个 `.txt` 也没被移走）
             * ⇒ 与其去猜它们为什么没跑，不如在"结论已经在手上"的这一刻**直接落一步**：
             * 复用既有出口 `RemoveTasksBySourcePaths`（⛔ 只动列表、⛔ 不删/不改名/不搬磁盘上的文件），
             * 移了谁、移了几个**逐条写日志**。⛔ 不在这里另写一套扫描判据（唯一实现仍是 SourceJunkScanner）。
             */
            /*
             * ⛔ 判据用**唯一出口** `AllHits`（= 全部命中），⛔ 不是那份"最多列 10 条"的提示名单（`Items`）：
             * 真机 CCCC 2026-10-07 实测扫到 17 条、`Items` 只有前 10 条 ⇒ 按 `Items` 移就会剩下几条
             * 留在列表里（用户当场问「为什么一开始只清理 10 个」）。
             */
            IReadOnlyList<SourceJunkItem> junkHits = facts.Junk.AllHits;

            if (junkHits.Count > 0)
            {
                int junkRemoved = _vm.RemoveTasksBySourcePaths(junkHits.Select(item => item.FullPath));

                if (junkRemoved > 0)
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.OneClickJunkRemovedLogFormat,
                            junkRemoved,
                            string.Join("、", junkHits.Take(5).Select(item => item.FileName))));
                }
                else
                {
                    /*
                     * 列表里一条都没对上 ⇒ **中性地说一声**（INFO）：正常情形就是"导入那一步已经把它们
                     * 移走了"（用户看到的"两波"正是这两条路各移一部分）。⛔ 只写日志，⛔ 不动磁盘上任何文件。
                     */
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.JunkAlreadyOffListLogFormat,
                            junkHits.Count,
                            string.Join("、", junkHits.Take(5).Select(item => item.FileName))));
                }
            }

            if (_callerHandlesBatchReminderDialog)
            {
                /*
                 * 用户 2026-09-24 第 17 条："点击完一键处理，就只能有一个弹窗提醒"。
                 * 这一批的确认框（OneClickOptionsWindow）已经把这两段写在正文里了 ——
                 * 再弹一个就是把同一件事说两遍，正是他嫌啰嗦的那种。
                 * 判定与日志一个都没少，少的只是第二个弹窗。
                 */
                AppendLog("INFO", StatusText.BatchReminderMergedLog);

                return true;
            }

            ReminderAnswer answer = await AskReminderAsync(BuildReminderMessage(facts.Junk, facts.NoUsablePassword))
                .ConfigureAwait(false);

            if (answer.OptionChecked)
            {
                _remindersSuppressedThisRun = true;
                AppendLog("INFO", StatusText.JunkReminderOptionLog);
            }

            if (!answer.Confirmed)
            {
                AppendLog("INFO", StatusText.JunkReminderDeclinedLog);
            }

            return answer.Confirmed;
        }

        /// <summary>
        /// B 段：需要密码、但当前**一个可用候选都没有**的包（"即使密码本已经预加载"的那种）。
        ///
        /// <para>判据与真正解压时**完全同一套参数**（同一个 <c>GetPasswordCandidates</c>、同一个统一密码开关、
        /// 同一个旁路说明文件开关）：判据不一样就会出现"提醒说没有密码、结果它解开了"这种自相矛盾。</para>
        ///
        /// <para>⚠ 只判"有没有候选"，不判"到底加没加密" —— 后者是 <see cref="ArchiveTask.IsEncrypted"/>
        /// （识别阶段的结论）。没有密码的普通包不在这一段范围内。</para>
        /// </summary>
        private List<ArchiveTask> FindTasksWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null || !(task.IsEncrypted ||
                    string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)))
                {
                    continue;
                }

                List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                    task,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword);

                if (!SourceJunkScanner.HasUsablePasswordCandidate(candidates))
                {
                    result.Add(task);
                }
            }

            return result;
        }

        /// <summary>批首写一条 WARN，把两段的结论都落进日志（数量 + 前几条名字）。</summary>
        private void LogReminderFindings(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            string junkPart = junk.HasAnything
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    junk.TotalCount,
                    string.Join("、", junk.SampleNames))
                : StatusText.JunkReminderLogNoneText;

            string passwordPart = noPassword.Count > 0
                ? string.Format(
                    StatusText.JunkReminderLogFoundFormat,
                    noPassword.Count,
                    string.Join(
                        "、",
                        noPassword.Take(MaxPasswordFailureNamesInDialog).Select(DisplayNameOf)))
                : StatusText.JunkReminderLogNoneText;

            AppendLog("WARN", string.Format(StatusText.JunkReminderLogFormat, junkPart, passwordPart));
        }

        /// <summary>
        /// 拼提醒正文。三段话的顺序见 <see cref="StatusText"/> 里那一组的说明（**不要重排**）：
        /// ① 这些文件是什么 → ② 本程序不会动它们 → ③ 解压完你自己判断要不要删。
        /// </summary>
        private static string BuildReminderMessage(SourceJunkScanResult junk, IReadOnlyList<ArchiveTask> noPassword)
        {
            var builder = new StringBuilder();

            builder.AppendLine(StatusText.JunkReminderIntro);

            if (junk.HasAnything)
            {
                builder.AppendLine();
                builder.AppendLine(StatusText.JunkReminderJunkHeader);
                builder.AppendLine(string.Format(StatusText.JunkReminderJunkCountFormat, junk.TotalCount));

                foreach (IGrouping<string, SourceJunkItem> group in
                         junk.Items.GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine("  " + group.Key);

                    foreach (SourceJunkItem item in group)
                    {
                        // 目录要标出来（第 32 条）：真实资源包把密码提示做成一个文件夹，
                        // 与"说明.txt"混在一起不标就分不清哪个是文件夹。
                        builder.AppendLine("    " + item.FileName + (item.IsDirectory ? "（文件夹）" : string.Empty));
                    }
                }

                if (junk.ExtraCount > 0)
                {
                    builder.AppendLine(string.Format(StatusText.JunkReminderJunkMoreFormat, junk.ExtraCount));
                }

                if (junk.Truncated)
                {
                    builder.AppendLine(StatusText.JunkReminderTruncatedNote);
                }

                builder.AppendLine(StatusText.JunkReminderJunkFooter);
            }

            if (noPassword.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(string.Format(StatusText.JunkReminderPasswordHeaderFormat, noPassword.Count));

                foreach (ArchiveTask task in noPassword.Take(MaxPasswordFailureNamesInDialog))
                {
                    builder.AppendLine("    " + DisplayNameOf(task));
                }

                if (noPassword.Count > MaxPasswordFailureNamesInDialog)
                {
                    builder.AppendLine(string.Format(
                        StatusText.JunkReminderPasswordMoreFormat,
                        noPassword.Count - MaxPasswordFailureNamesInDialog));
                }

                builder.AppendLine(StatusText.JunkReminderPasswordOutcome);
                builder.AppendLine(StatusText.JunkReminderPasswordExit);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>任务在提示里的显示名（文件名优先，退回完整路径）。</summary>
        private static string DisplayNameOf(ArchiveTask task) =>
            string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName;

        /// <summary>
        /// 拿用户的答案。
        ///
        /// <para><b>无界面宿主（单测 / 控制台宿主）在这里按「继续处理」返回</b>，而且**连弹窗都不尝试**：
        /// 既不产生 DialogService 的降级记录（那会让"这个宿主没有界面"的既有断言被这条纯提示污染），
        /// 也不会有任何等待。整批解压因此照常跑完 —— 这一点由管线测试证明。</para>
        /// </summary>
        private async Task<ReminderAnswer> AskReminderAsync(string message)
        {
            if (ReminderAnswerOverride != null)
            {
                return ReminderAnswerOverride(message);
            }

            if (System.Windows.Application.Current == null)
            {
                AppendLog("WARN", StatusText.JunkReminderNoHostLog);

                return new ReminderAnswer { Confirmed = true, OptionChecked = false };
            }

            return await ShowReminderConfirmOnUiThreadAsync(message).ConfigureAwait(false);
        }

        /// <summary>
        /// 在 UI 线程上弹提醒（与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套调度纪律：
        /// 管线可能已经不在 UI 线程上，直接弹会造出一个没人泵消息的窗口）。
        ///
        /// <para>降级方向由 <see cref="DialogService.ShowReminderConfirm"/> 定义成"继续"，
        /// 所以这里即使调度器已经关掉也只是放行，不会把整批拦下。</para>
        /// </summary>
        private async Task<ReminderAnswer> ShowReminderConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            bool confirmed;
            bool optionChecked = false;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                confirmed = _dialogService.ShowReminderConfirm(
                    StatusText.JunkReminderTitle,
                    message,
                    StatusText.JunkReminderYesText,
                    StatusText.JunkReminderNoText,
                    StatusText.JunkReminderOptionText,
                    optionCheckedByDefault: false,
                    out optionChecked);
            }
            else
            {
                confirmed = await dispatcher.InvokeAsync(() =>
                {
                    bool checkedNow;

                    bool result = _dialogService.ShowReminderConfirm(
                        StatusText.JunkReminderTitle,
                        message,
                        StatusText.JunkReminderYesText,
                        StatusText.JunkReminderNoText,
                        StatusText.JunkReminderOptionText,
                        optionCheckedByDefault: false,
                        out checkedNow);

                    optionChecked = checkedNow;

                    return result;
                }).Task.ConfigureAwait(false);
            }

            return new ReminderAnswer { Confirmed = confirmed, OptionChecked = optionChecked };
        }

        /// <summary>
        /// 整批**一次性**地问"要不要手动给一个密码"（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
        ///
        /// <para>
        /// 什么时候问：本批里有任何任务"可能带密码"时才问 —— 判据是既有的两个任务字段
        /// （<see cref="ArchiveTask.IsEncrypted"/> 或 <see cref="ArchiveTask.PasswordStatus"/> 为"需要密码"）。
        /// 都没有的批次一个字都不问，免得每次解一堆无密码的包都被挡一下。
        /// </para>
        /// <para>
        /// <b>无 UI 宿主（单测 / 控制台宿主）：不弹窗、不死等</b>，直接返回并写一条降级日志 ——
        /// 与 <c>DialogService.ShowConflictDecisionAsync</c> 同一口径。
        /// </para>
        /// <para>
        /// 用户答"不用"也一样记 <see cref="_manualPasswordPrompted"/>：一次性就是一次性，
        /// 不能因为"没填"就每次遇到觉得需要密码的包再问一遍。
        /// </para>
        /// </summary>
        private async Task PromptForManualBatchPasswordAsync(IReadOnlyList<ArchiveTask> tasks)
        {
            if (_manualPasswordPrompted)
            {
                return;
            }

            /*
             * 只问"**真有包连一个可用候选都没有**"的时候（2026-09-29 用户提问后的改动）。
             *
             * 老判据是"本批有包看起来要密码就弹" —— 哪怕密码本里明明有它的密码也弹，
             * 用户的原话就是"我的密码本里面明明有这个密码，为什么还是会出现"。现在复用
             * 与解压**同一套参数**算候选（GetPasswordCandidates：统一密码 / 密码本 / 空密码 / 旁路说明文件），
             * 只要有一个包一个候选都拿不出来才问；有候选就一个字都不打扰。
             */
            List<ArchiveTask> suspects = FindSuspectsWithoutUsablePassword(tasks);

            if (suspects.Count == 0)
            {
                return;
            }

            _manualPasswordPrompted = true;

            List<ArchiveTask> named = suspects.Take(MaxPasswordFailureNamesInDialog).ToList();

            string message =
                $"本批有 {suspects.Count} 个包可能带密码，而当前一个可用候选都没有。" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, named.Select(t => t.FileName)) +
                Environment.NewLine + Environment.NewLine +
                "可以现在手动给一个：本批所有任务都会把它当候选试一遍。" +
                Environment.NewLine +
                "⚠ 只对本次运行有效，不会写进任何文件、也不会进密码列表（关掉程序就没了）。";

            string? entered = await ShowPasswordPromptOnUiThreadAsync(message);

            if (string.IsNullOrEmpty(entered))
            {
                AppendLog("INFO", "没有手动输入密码（或当前宿主没有界面），本批按密码本与统一密码继续。");
                return;
            }

            _manualBatchPasswords.Add(entered);

            /*
             * 日志只说"收到一个手动密码"，**绝不记录内容**（不变量 5）。
             * 措辞上不给"密码"配冒号：PasswordMasker 的兜底规则是"密码 + 冒号 → 本行剩余全部打码"，
             * 那样这一行的后半句会被吃掉（旧版实测踩过这个坑）。
             */
            AppendLog(
                "INFO",
                $"已收到一个手动密码（{entered.Length} 个字符），只对本次运行有效，不落盘；" +
                "本批所有任务都会试它。");

            if (entered.Length > MaxSupportedPasswordLength)
            {
                /*
                 * 抄 WinRAR 6.10 才补上的那个坑：超长密码会被**静默截断**，
                 * 用户输对了却看到"密码错误"是最难查的一类现象，所以当场说清。
                 */
                AppendLog(
                    "WARN",
                    $"⚠ 你输入的手动密码有 {entered.Length} 个字符，超过 7-Zip 支持的 {MaxSupportedPasswordLength} 个，" +
                    "更长的部分会被截断 —— 如果解不开，先确认密码本身有没有这么长。");
            }
        }

        /// <summary>
        /// 「看起来要密码」**并且**用与解压同一套参数算下来**一个可用候选都没有**的那些包
        /// —— 手动密码弹窗只在这些包存在时才问（2026-09-29 用户提问后收紧）。
        ///
        /// <para>⚠ 与 <c>FindTasksWithoutUsablePassword</c>（确认框那一段）的区别只有一个：
        /// 这里把"引擎已经说过需要密码"（<see cref="StatusText.PasswordNeed"/>）也算嫌疑人 ——
        /// RAR 这类容器的加密在识别阶段读不出来，只能等引擎列目录时才知道。</para>
        /// </summary>
        private List<ArchiveTask> FindSuspectsWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null)
                {
                    continue;
                }

                bool suspect = task.IsEncrypted ||
                    string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal);

                if (!suspect)
                {
                    continue;
                }

                List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                    task,
                    Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                    _passwordService.Passwords,
                    Settings.TryEmptyPasswordFirst,
                    Settings.EnableSidecarPassword);

                if (!SourceJunkScanner.HasUsablePasswordCandidate(candidates))
                {
                    result.Add(task);
                }
            }

            return result;
        }

        /// <summary>
        /// 手动密码的候选位置：插在**空密码之后**（<paramref name="insertAfterEmpty"/> 为 true 时），
        /// 否则追加到末尾。
        ///
        /// <para>
        /// 为什么不是最前：空密码是"无密码包"的必经一步，而且它是零成本的一次尝试（不进密码循环也不亏）。
        /// 为什么排在密码本之前：手动输入是用户**刚刚**给出的信息，比密码本里的历史条目更"新"；
        /// 而且它通常就是那个包真正需要的密码，早点试能省掉一大圈无谓试错。
        /// </para>
        /// <para>
        /// 传 null / 空串时什么都不做（空密码已经由调用方自己保证了）。
        /// </para>
        /// </summary>
        private static void InsertManualPasswordCandidates(
            List<PasswordItem> candidates,
            IReadOnlyList<string>? manualPasswords,
            string remark = "本次运行手动输入（不落盘）")
        {
            if (candidates == null || manualPasswords == null || manualPasswords.Count == 0)
            {
                return;
            }

            var additions = new List<PasswordItem>();

            foreach (string manual in manualPasswords)
            {
                if (string.IsNullOrEmpty(manual))
                {
                    continue;
                }

                if (candidates.Any(item => string.Equals(item?.Value, manual, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (additions.Any(item => string.Equals(item.Value, manual, StringComparison.Ordinal)))
                {
                    continue;
                }

                additions.Add(new PasswordItem
                {
                    Value = manual,
                    Source = ManualPasswordSource,
                    IsEnabled = true,
                    Remark = remark
                });
            }

            if (additions.Count == 0)
            {
                return;
            }

            /*
             * 整批插在**空密码之后**：空密码是"无密码包"的必经一步、零成本；
             * 手动输入排在密码本之前 —— 它是用户刚刚给出的信息，比历史条目更"新"。
             * ⛔ 用 InsertRange 一次插完（逐条插会把先插的挤到后面，顺序就反了）。
             */
            int index = candidates.FindIndex(
                item => string.Equals(item?.Source, "Empty", StringComparison.Ordinal));

            if (index >= 0 && index < candidates.Count - 1)
            {
                candidates.InsertRange(index + 1, additions);
                return;
            }

            candidates.AddRange(additions);
        }

        /// <summary>
        /// 一键档的密码提示（用户 2026-09-29 第二次改口径）：**不在批中间弹任何框、也不在面板里放输入框**
        /// （他原话："这个一键处理点击后出现一个输入框非常的奇怪，这个给他移除，可以弄一个提醒字样，
        /// 本次的解压可能需要密码，请在密码页一键导入"）。
        ///
        /// <para>所以这里只做两件事：① 把"要补密码就去「密码」页一键导入"写进日志；
        /// ② 标记本轮不再走"动手前问一次"那条路（续解的第 2/3 轮同一条口径）。</para>
        /// </summary>
        private void LogOneClickPasswordHint(IReadOnlyList<ArchiveTask> selectedTasks)
        {
            _manualPasswordPrompted = true;

            bool mayNeedPassword = selectedTasks.Any(task =>
                task != null &&
                (task.IsEncrypted ||
                 string.Equals(task.PasswordStatus, StatusText.PasswordNeed, StringComparison.Ordinal)));

            if (mayNeedPassword && _manualBatchPasswords.Count == 0)
            {
                AppendLog("INFO", StatusText.OneClickConfirmManualPasswordSkippedLog);
            }
        }

        /// <summary>手动密码候选的来源标记（只在这里出现一次，别在别处再写字面量）。</summary>
        internal const string ManualPasswordSource = "ManualBatch";

        /// <summary>
        /// 7-Zip 支持的密码长度上限（超过会被截断）。抄 WinRAR 参考里"6.10 才补上截断警告"那条教训：
        /// 我们**当场提示**，不静默截断。
        /// </summary>
        private const int MaxSupportedPasswordLength = 127;

        /// <summary>
        /// 在 UI 线程上要一个密码。**无 UI 宿主返回 null（不弹窗、不死等）**。
        ///
        /// <para>
        /// 为什么自己建一个小窗口而不是复用 <c>DialogService</c>：它的自绘对话框
        /// （<c>Views/AppDialogWindow</c>）没有输入框，而"改一个通用对话框控件"不在本次授权范围内
        /// （任务书明确要求"需要什么就写进报告"）。这里用 <see cref="PasswordBox"/> 而不是 TextBox：
        /// 手动密码同样不许明文显示在屏幕上（不变量 5 的同一精神），而且 <c>PasswordBox.Password</c>
        /// 不会进 WPF 的绑定/日志路径。
        /// </para>
        /// <para>
        /// 返回 null 的两种情况（都不算失败）：当前宿主没有 WPF 界面；用户点了"跳过"或直接关窗口。
        /// </para>
        /// </summary>
        private Task<string?> ShowPasswordPromptOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 无界面宿主（单测 / 控制台）：只写日志，绝不弹模态框 —— 那会阻塞调用方且没人点得掉。
                AppendLog("INFO", "当前宿主没有 WPF 界面，跳过「手动输入密码」这一步（不影响其它流程）。");
                return Task.FromResult<string?>(null);
            }

            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(ShowPasswordPromptModal(message));
            }

            return dispatcher.InvokeAsync(() => ShowPasswordPromptModal(message)).Task;
        }

        /// <summary>
        /// 密码输入小窗口本体（**只允许在 UI 线程上调用**）。
        ///
        /// 刻意**不设** <c>WindowStartupLocation</c> 以外的任何窗口行为：不置前、不最大化、不改尺寸。
        /// 默认按钮是「跳过」而不是「确定」——回车不该把可能错的密码当真（同 Ask 档"默认不能是危险动作"）。
        /// </summary>
        private static string? ShowPasswordPromptModal(string message)
        {
            var window = new System.Windows.Window
            {
                Title = "手动输入密码（只对本次运行有效）",
                Width = 520,
                Height = 300,
                MinWidth = 460,
                MinHeight = 280,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Owner = System.Windows.Application.Current?.MainWindow,
                Background = System.Windows.Media.Brushes.White
            };

            /*
             * ⛔ 模态子窗一律不许最小化（用户 2026-10-05 拍板 A2）—— 这个框也是**代码里内联建的**，
             * 不在 XAML 名单里。它现在是 `NoResize`（压根点不动最小化），接上策略是为了①系统菜单那一项
             * 也置灰、②万一被 Win+D 这类系统级操作最小化，只要主窗口没跟着最小化就立刻还原。
             */
            ArchiveFixer.Views.WindowMinimizePolicy.Apply(window);

            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };

            for (int i = 0; i < 3; i++)
            {
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = i == 1 ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
                                    : System.Windows.GridLength.Auto
                });
            }

            var messageBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(messageBlock, 0);
            root.Children.Add(messageBlock);

            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Height = 30,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };

            System.Windows.Controls.Grid.SetRow(passwordBox, 1);
            root.Children.Add(passwordBox);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "用这个密码",
                MinWidth = 110,
                Height = 30,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };

            var skipButton = new System.Windows.Controls.Button
            {
                Content = "跳过",
                MinWidth = 86,
                Height = 30,
                IsDefault = true,
                IsCancel = true
            };

            string? result = null;

            okButton.Click += (_, _) =>
            {
                result = passwordBox.Password;
                window.DialogResult = true;
                window.Close();
            };

            skipButton.Click += (_, _) =>
            {
                result = null;
                window.DialogResult = false;
                window.Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(skipButton);

            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            window.Content = root;

            /*
             * 提醒（用户 2026-09-22 反馈的原话：「密码本那个窗口弹出来之后躲到主窗口后面了，
             * 只有声音、没有闪烁，声音还很轻」）。
             *
             * ⚠ 这一条**修正**了本方法早先那句"不置前、不 Topmost"的注释：那个口径来自 AGENTS.md §13，
             * 而 §13 管的是**代理脚本不许抢用户前台**（免得用户没法干别的事）。这里是**程序自己**
             * 在"整批卡在等密码"时把输入框拎出来 —— 用户明确要求这个行为，两者不冲突。
             * 强度是 Strong：一直闪任务栏（闪到被点）+ 连响三声。
             */
            ArchiveFixer.Helpers.WindowAttention.Attach(window, ArchiveFixer.Helpers.AttentionStrength.Strong);

            // 焦点给输入框（窗口被激活之后才拿得到键盘焦点）。
            window.Loaded += (_, _) => passwordBox.Focus();

            window.ShowDialog();

            return result;
        }

        // ================================================================ 危险条目提示（只提示不阻断）

        /// <summary>
        /// 可执行 / 脚本类后缀 —— 判据只有这一份（别在别处再列一遍）。
        ///
        /// 取自 WinRAR 的"解压时排除的文件类型"示例（<c>*.scr *.pif *.exe</c>），
        /// 再加上脚本类与快捷方式。**只用于统计提示**，不做任何过滤（见 <see cref="AnalyzeDangerousEntries"/>）。
        /// </summary>
        private static readonly string[] DangerousEntryExtensions =
        {
            ".exe", ".scr", ".pif", ".com", ".msi", ".msp", ".cpl",
            ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".hta", ".lnk", ".reg", ".jar", ".inf"
        };

        /// <summary>提示里最多列几个条目名（列太多会把详情刷屏，用户反而不看）。</summary>
        private const int MaxDangerousEntriesInHint = 5;

        /// <summary>
        /// 「危险条目提示」的展示前缀。它**不是**任务状态（不进 StatusText / 配色 / 统计），
        /// 只是一句提示文本的前缀，所以按 §7 的要求用常量收在这里，不散落字面量。
        /// </summary>
        internal const string DangerousEntriesHintPrefix = "可疑条目提示：";

        /// <summary>
        /// 在**已有的那一遍 list 结果**里统计"可执行 / 脚本类条目"，产出给用户看的一句话。
        ///
        /// <para>
        /// 三条纪律（<c>docs/WinRAR功能参考.md</c> §3 第 4/5 条）：
        /// ① **绝不为此再 list 一遍** —— 只吃调用方已经拿到手的那份清单（加密包每多列一次目录就多一次失败机会）；
        /// ② **只提示，绝不阻断** —— 真实资源包里安装器 / 补丁经常就是内容物，不做全局硬排除掩码；
        /// ③ 关掉设置项时返回空串，调用方据此不写任务字段、也不打日志。
        /// </para>
        ///
        /// 返回空串 = "没统计" 或 "统计了但没有可疑条目"，两种情况调用方都不必区分：
        /// 提示的意义只在"有东西要说"的时候。
        /// </summary>
        internal static string AnalyzeDangerousEntries(IEnumerable<ArchiveEntry>? entries, bool enabled)
        {
            if (!enabled || entries == null)
            {
                return string.Empty;
            }

            var found = new List<string>();
            int dangerousCount = 0;

            foreach (ArchiveEntry? entry in entries)
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                string path = entry.Path ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string extension = Path.GetExtension(path);

                if (string.IsNullOrWhiteSpace(extension))
                {
                    continue;
                }

                if (!DangerousEntryExtensions.Any(
                        known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                dangerousCount++;

                if (found.Count < MaxDangerousEntriesInHint)
                {
                    found.Add(Path.GetFileName(path));
                }
            }

            if (dangerousCount == 0 || found.Count == 0)
            {
                return string.Empty;
            }

            string samples = string.Join("、", found);

            if (dangerousCount > found.Count)
            {
                samples += $"…等 {dangerousCount} 个";
            }

            return $"{DangerousEntriesHintPrefix}本包含 {dangerousCount} 个可执行 / 脚本类条目（{samples}）。" +
                   "这不是错误，只是提醒你先看清楚再打开；程序不会因此阻断解压。";
        }

        /// <summary>
        /// 这条提示去哪儿显示：任务字段（失败清单里逐归档的第二级会原样带出）＋ 界面日志。
        ///
        /// 与"文件名已加密"/"路径过长"同一处产出（同一次 list），保证三个结论口径一致。
        /// </summary>
        private void PublishDangerousEntriesHint(ArchiveTask task, string hint, string level = "WARN")
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                return;
            }

            task.DangerousEntriesWarning = hint;
            AppendLog(level, $"{task.LogName}：{hint}");
        }

        /// <summary>
        /// 把本批"密码错误 / 密码错误或文件损坏 / 达到密码尝试上限"的任务**合并成一次**提示。
        ///
        /// 旧做法：在每个任务内部各弹一次模态框，50 个错包 = 50 次阻塞点击 ——
        /// 批量处理根本跑不动，用户看到的就是"点了没反应"。
        /// 新做法：循环里只登记（<see cref="RecordPasswordFailure"/>），批次结束后在这里一次说清：
        /// 有几个、都是谁、其中几个是"到上限"（不是密码错误）、几个是"两义档"（引擎只说"可能"）。
        /// 日志与弹窗是同一句话，所以没有界面时（无头宿主）也留得下证据。
        /// </summary>
        private async Task ShowPasswordFailuresSummaryAsync(bool showDialog = true)
        {
            List<(string FileName, string Status)> failures;

            lock (_passwordFailuresLock)
            {
                if (_passwordFailures.Count == 0)
                {
                    return;
                }

                failures = _passwordFailures.ToList();
                _passwordFailures.Clear();
            }

            /*
             * 三档而不是两档（2026-10-01）：`PasswordOrCorrupted`（RAR 的"文件已损坏或密码错误"）
             * 也要算进"密码类失败"—— 它恰恰是最可能缺密码的一档。⚠ 原来只分两档时，
             * 把它登记进来会被算成"达到密码尝试上限"，那是对用户的**误报**，
             * 所以登记判据与这里的分档必须**同时**改（§9.5：同一件事在多处出现，值 + 通知都要钉住）。
             */
            int wrongPasswordCount = failures.Count(f => f.Status == StatusText.WrongPassword);
            int ambiguousCount = failures.Count(f => f.Status == StatusText.PasswordOrCorrupted);
            int attemptLimitCount = failures.Count - wrongPasswordCount - ambiguousCount;

            /*
             * 措辞有个坑：日志会被 PasswordMasker 兜底脱敏，规则是"密码 + 冒号 → 本行剩余全部打码"。
             * 所以这里**不要在"密码"后面直接跟冒号**，否则用户看到的是"本批 3 个包…密码：******"，
             * 文件名与原因全被吃掉（实测踩过）。原因写成"原因：密码错误…"这种形态是安全的。
             *
             * ⚠ 前两档（全是"密码错误" / 全是"到上限"）的**原话一个字都不改** ——
             * 有用例逐字钉着它们（`ExtractionPipelineFixTests` 的"本批 3 个包没能解开"那一条）。
             */
            string reason;

            if (ambiguousCount == 0 && attemptLimitCount == 0)
            {
                reason = "密码错误或缺少正确密码";
            }
            else if (wrongPasswordCount == 0 && ambiguousCount == 0)
            {
                reason = $"{StatusText.PasswordAttemptLimitReached}（候选还没试完就按上限停了，不等于密码错误）";
            }
            else if (wrongPasswordCount == 0 && attemptLimitCount == 0)
            {
                reason = $"{StatusText.PasswordOrCorrupted}（引擎在那条路上只说了「可能」："
                         + "要么密码不对、要么数据真的坏了，程序不当它是确认的密码问题）";
            }
            else
            {
                // 混合档：有几档说几档，每档都带上它自己的免责（⛔ 不许笼统说成"密码错误"）。
                var parts = new List<string>();

                if (wrongPasswordCount > 0)
                {
                    parts.Add($"密码错误 {wrongPasswordCount} 个");
                }

                if (ambiguousCount > 0)
                {
                    parts.Add($"{StatusText.PasswordOrCorrupted} {ambiguousCount} 个（只说了「可能」，不等于确认是密码问题）");
                }

                if (attemptLimitCount > 0)
                {
                    parts.Add(
                        $"{StatusText.PasswordAttemptLimitReached} {attemptLimitCount} 个"
                        + "（候选还没试完就按上限停了，不等于密码错误）");
                }

                reason = string.Join(" / ", parts);
            }

            var builder = new StringBuilder();

            builder.Append(failures.Count == 1
                ? "有 1 个包没能解开"
                : $"本批 {failures.Count} 个包没能解开");

            builder.Append("，原因：");
            builder.Append(reason);
            builder.AppendLine();

            foreach (string name in failures.Select(f => f.FileName).Take(MaxPasswordFailureNamesInDialog))
            {
                builder.AppendLine(name);
            }

            if (failures.Count > MaxPasswordFailureNamesInDialog)
            {
                builder.AppendLine($"…等共 {failures.Count} 个（完整清单见「复制失败列表 / 导出失败清单」）。");
            }

            /*
             * 手动输入密码的**出口指引**（WinRAR 参考 §2 H 组采纳项）。
             *
             * 关键的一句是"重跑这一批会自动再问一次"：用户在这里看到"密码错误"时，
             * 下一步该做什么不是"去设置里改密码本"，而是"再点一次、把密码输进去"。
             * 只写"可以用手动密码"而不说怎么再触发，等于没说。
             */
            builder.AppendLine();

            /*
             * 撞上「每层密码尝试上限」时的指路（用户 2026-10-02 真机要求）：没试出来**不等于**这些包
             * 一定需要密码 —— 可能本来就没有密码，也可能对的密码排在候选更靠后（上限截断了）。
             * ⛔ 只在真的有包撞上限时写；⛔ 上面那两句 reason 原话一个字不动（有用例逐字钉着）。
             */
            if (attemptLimitCount > 0)
            {
                builder.AppendLine(string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.PasswordAttemptLimitHintFormat,
                    MaxPasswordAttemptsPerLayer));
            }

            builder.AppendLine(
                _manualBatchPasswords.Count > 0
                    ? "本次运行你已经手动给过密码；如果还是解不开，说明它不是这些包的密码 —— 换一个再试，或者去核对密码本。"
                    : _batchHadPasswordFailures
                        ? "下一步：再点一次「一键处理 / 只解压」——「只解压」会在开始前问你要不要手动输一个密码；"
                          + "一键处理则是把密码填进开始前的那个确认框（填了会加到「密码」页列表末尾，以后一直有效）。"
                        : "如果这些包需要密码：「只解压」会在开始前问你；一键处理则在开始前的确认框里有「本批手动密码」一格。");

            string message = builder.ToString().TrimEnd();

            // 日志与弹窗同源：日志一定写（无界面宿主也留得下证据），弹窗只在真的有界面时弹。
            // 这里把换行换成"；"是为了让日志一行看完 —— 换行后的内容不会被脱敏规则吃掉。
            AppendLog("WARN", message.Replace(Environment.NewLine, "；"));

            if (showDialog)
            {
                await ShowWarningOnUiThreadAsync(message);
            }
        }

        /// <summary>
        /// 一键处理期间**关掉所有"需要用户回答"的框**（用户 2026-09-27："以后不要出现弹窗"）。
        ///
        /// <para>
        /// 只做一件事：把同名冲突询问标成"问不到答案"—— 那条判据在
        /// <c>PostProcessSuccessAsync</c> 与 <c>PrecheckFinalLayoutConflicts</c> 两处已经是
        /// "问不到就走保守档（自动重命名、绝不覆盖）"，所以这里不需要第二套逻辑。
        /// 其余几个框（多分支 / 缺卷补救 / 批次结束提示）各自在调用点上读 <c>oneClickRun</c>。
        /// </para>
        ///
        /// <para>
        /// ⚠ 2026-10-04：这一行过去写着"多分支不展开" —— 那是"一键档一律保守"的旧口径
        /// （用户当场推翻：一键档只决定"问不问"，展开策略按②页所选档位）。
        /// </para>
        /// </summary>
        private void SuppressDecisionPromptsForOneClickRun()
        {
            lock (_conflictDecisionLock)
            {
                _conflictPromptUnavailable = true;
            }

            AppendLog(
                "INFO",
                "一键处理：本次不弹任何确认框（多分支按②页所选档位办、冲突走自动重命名、缺卷不补救、结束提示只写日志）——"
                + "该问的事一律按设置里你选的那一档办，理由逐条写在日志里。");
        }

        private string GlobalPassword => _vm.GlobalPassword;
        private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;
        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string message) => _vm.AppendLog(message);

        /// <summary>
        /// 写一处**判据**（人读一行 + 机器读一条，字段固定；唯一收口 <see cref="MainViewModel.CriterionLog"/>）。
        ///
        /// <para>⚠ 刻意**不走任务级捕获缓冲**（那条缓冲会把成功任务的 INFO 丢掉）：判据是大测试"只看日志排查"的
        /// 唯一线索 ⇒ 它每一处都必须落进日志文件。⛔ 只是记录：判据与行为一个字都不改。</para>
        /// </summary>
        private void CriterionLog(
            string criterion,
            string inputs,
            string verdict,
            string action = "",
            string file = "",
            string identity = "",
            string level = "INFO")
            => _vm.CriterionLog(criterion, inputs, verdict, action, file, identity, level);

        /*
         * ==================== 每任务日志「攒着，成功就丢」（用户 2026-09-25 第 44 条）====================
         *
         * 他的原话："你看看一次导出 713KB……这个多吓人"，并给了三条建议：
         * ①进度别这么详细（"就比如说开始解压，A.rar 解压100%，解压成功"）；
         * ②每次不用汇报那么详细（"多相同文件，数量肯定会很多"）；③删文件别写那么多文字。
         *
         * 量化过那一次 4025 行 / 713 KB 的构成：**进度 1181 行（16%）**、**删除 412 行（16%）**、
         * 其余 2432 行里绝大多数是**每个任务十几条一样形状的样板**（落点 / 入仓 / 空间门 / 资源预算 /
         * 特定解压 / 结果校验 / 定稿完成 / 工作区清理 / 其余物…），68 个包就是 68 份。
         *
         * 所以这里的规矩是：**一个任务跑的时候，INFO 先攒着**；
         * · 任务**成功** → 只留**一行**（"解压 100% ｜ 解压成功 ｜ 校验通过 ｜ → 短地址 ｜ 用时 …"，
         *   开头那行"开始解压"在 2026-09-25 追加之二里并进了它），细节全丢（他明确说"成功就不用那么多"）；
         * · 任务**失败 / 取消 / 部分完成** → 攒下的细节**原样全吐出来**（他明确说"如果失败的话，
         *   你就可以多一点"）—— 排查要的就是那些数字与路径；
         * · **进度行永远是"噪声"**：成功时一律丢，失败时随细节一起吐出来；
         * · WARN / ERROR **立刻可见**（它们是信号，不许被攒住），并且第一次出现时把**之前**攒的那些
         *   一起吐出来 —— 否则错误行会孤零零地出现在日志里，前面发生了什么全看不到；
         * · 详细档（⑥设置 →「详细日志（排查用）」或测试口子）**根本没有缓冲**（capture == null），
         *   所有行直接出去 —— 上面这些"攒与丢"一条都不适用。
         */
        private sealed class TaskLogCapture
        {
            public List<(string Level, string Message)> Buffer { get; } = new();

            public bool DetailsVisible { get; set; }
        }

        /*
         * ⚠ 缓冲必须是**每个任务各一份**，而且得跟着 async 流走：批量解压是**并发**的
         * （用户选的并行档，实测 4 个任务同时跑）。第一版把它写成协调器上的普通字段 ——
         * 两个任务同时开工就互相清缓冲、互相吐摘要，`List.Add` 还会被两个线程同时改，
         * 结果任务直接炸成「未知错误」（两处既有用例当场变红，就是这条）。
         * AsyncLocal 每个任务的 await 链各拿一份，天然不串。
         */
        private readonly AsyncLocal<TaskLogCapture?> _currentTaskLog = new();

        /// <summary>进度类日志：成功时一律丢（量最大，信息量最低），失败时随细节一起吐。</summary>
        private static bool IsProgressNoise(string message) =>
            message.Contains("：进度 ", StringComparison.Ordinal)
            || message.Contains("：取出内嵌归档 ", StringComparison.Ordinal);

        /// <summary>
        /// **测试用**的显式口子：true = 成功时也把全部细节写进日志
        /// （默认 <c>false</c> = 产品行为：成功的任务在日志里只留一行摘要）。
        ///
        /// <para>为什么留这个口子：一大批既有用例把"日志里那一行"当作**行为证据**（跨盘搬运走了复制、
        /// 工作区被清理、空间依据写了"同卷只算一份"……），而第 44 条的瘦身改的是**默认呈现**、
        /// 不是行为本身。让那些用例显式声明"我这个用例要看细节"，比把它们一条条改成断言摘要更诚实
        /// —— 也让"默认现在是简洁的"这件事只由 <c>LogVolumePolicyTests</c> 一处钉住。</para>
        ///
        /// <para>⚠ 用户侧的入口是 ⑥设置 →「详细日志（排查用）」（<see cref="AppSettings.VerboseLog"/>，
        /// 默认关），二者在 <see cref="VerboseTaskLogEnabled"/> 里合成同一个行为；
        /// ⛔ 这里这个口子只给测试用，界面不绑它。</para>
        /// </summary>
        internal bool KeepTaskDetailInLog { get; set; }

        /// <summary>
        /// **测试缝**：收尾（定稿/归集/搬源）**已经开始、第一道取消闸门之后**回调一次。
        ///
        /// <para>为什么需要它（用户「残余缺口：收尾内部按取消无用例钉住」）：收尾内部有三处
        /// `ThrowIfCancellationRequested()`（本文件 `:2015` / `:2227` / `:2260` 附近），
        /// 而**没有任何用例能看见它们** —— 现有那两个钩子（`Engine.OnList/OnExtract`）都发生在收尾之前，
        /// 更早的闸门就先看见了取消（`ExtractionPipelineFixTests` 那条用例的注释自己写明了这一点）。
        /// ⇒ 只有在这里回调，测试才能把"取消"按在**收尾已经开始之后**。</para>
        ///
        /// <para>⛔ 默认 `null` = 一个字都不做（生产路径行为与改动前逐字相同）。</para>
        /// </summary>
        internal Action? PostProcessStartedForTests { get; set; }

        private void BeginTaskLogCapture(ArchiveTask task)
        {
            // 详细档（⑥设置 →「详细日志（排查用）」，默认关）或测试显式要求 → 不攒，全部原样写出去。
            _currentTaskLog.Value = VerboseTaskLogEnabled ? null : new TaskLogCapture();

            /*
             * ⚠ 这里**不再**单独写一行"开始解压"：它与收尾摘要合成一行（用户 2026-09-25 第 44 条追加：
             * "把开始解压 + 摘要合成一行"）—— 一个任务在日志里就**一行**，136 个任务的批次从 270 行降到 140 行以内。
             */
        }

        /// <summary>详细日志：设置里的开关（默认关）或测试/排查用的显式口子。</summary>
        private bool VerboseTaskLogEnabled => KeepTaskDetailInLog || Settings.VerboseLog;

        private void FlushTaskLogBuffer(TaskLogCapture capture)
        {
            if (capture.Buffer.Count == 0)
            {
                return;
            }

            foreach ((string level, string message) in capture.Buffer)
            {
                _vm.AppendLog(level, message);
            }

            capture.Buffer.Clear();
        }

        /// <summary>
        /// 任务收尾：按**机器终态**决定细节留不留（⛔ 不比对中文文案），再补一行"收尾摘要"。
        /// </summary>
        private void EndTaskLogCapture(ArchiveTask task)
        {
            TaskLogCapture? capture = _currentTaskLog.Value;

            _currentTaskLog.Value = null;

            /*
             * ⚠ 详细档（capture == null）也要写这一行：它是"开始解压 + 摘要合成一行"里的那一行，
             * 详细档只是**额外**把中间过程全写出去（用户 2026-09-25 第 44 条追加），
             * ⛔ 不是"详细档就没有收尾摘要"。
             */
            if (capture != null)
            {
                bool succeeded = task.Outcome == TaskOutcome.Succeeded;

                if (succeeded)
                {
                    capture.Buffer.Clear();   // 成功：样板与进度全丢，只留下面那一行
                }
                else
                {
                    FlushTaskLogBuffer(capture);   // 失败 / 取消 / 部分完成：细节全留
                }
            }

            _vm.AppendLog("INFO", BuildTaskSummaryLine(task));
        }

        /// <summary>一行说清这个任务的结果（成功时的**唯一**一行）。</summary>
        private static string BuildTaskSummaryLine(ArchiveTask task)
        {
            var parts = new List<string>();

            // 成功 = "解压 100%"（用户要的形状："开始解压，A.rar 解压100%，解压成功"合成一行）。
            if (task.Outcome == TaskOutcome.Succeeded)
            {
                parts.Add($"{StatusText.OpExtract} 100%");
            }

            parts.Add(task.Status);

            if (task.OutputVerification == OutputVerificationOutcome.Passed)
            {
                parts.Add("校验通过");
            }
            else if (task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                parts.Add("校验未通过");
            }

            string destination = DescribeShortDestination(task.CollectedPath, task.OutputPath);

            if (!string.IsNullOrWhiteSpace(destination))
            {
                parts.Add("→ " + destination);
            }

            if (task.IsContinuationTask && task.Outcome == TaskOutcome.Succeeded)
            {
                parts.Add("续解");
            }

            /*
             * 第 42 条：这一单在工作区里接过分卷（默认关的那个开关）—— 那是**动过盘**的动作，
             * 而默认档"成功就丢"会把过程细节丢掉，所以必须出现在这一行摘要里。
             */
            if (!string.IsNullOrWhiteSpace(task.SplitVolumeAssemblyNote))
            {
                parts.Add(task.SplitVolumeAssemblyNote);
            }

            if (!string.IsNullOrWhiteSpace(task.ElapsedText)
                && task.ElapsedText != "-"
                && !string.Equals(task.ElapsedText, "00:00:00", StringComparison.Ordinal))
            {
                // 秒级任务不写"用时 00:00:00"（68 个包就是 68 个零，纯噪声）。
                parts.Add("用时 " + task.ElapsedText);
            }

            return $"{task.LogName}：{string.Join(" ｜ ", parts)}";
        }

        /// <summary>
        /// 落点写成"短地址"（用户 2026-09-25 第 44 条：满屏几百字符的绝对路径最占地方）。
        /// 规则：从**输出根**往下保留；拿不到输出根就保留末两段，前面用 `…\` 省掉。
        /// 完整路径仍然在任务详情 / 失败清单 / ①页「输出位置」里（那里要求看到完整地址）。
        /// </summary>
        private static string DescribeShortDestination(string? collectedPath, string? outputPath)
        {
            string path = !string.IsNullOrWhiteSpace(collectedPath) ? collectedPath! : outputPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length <= MaxSummaryPathLength)
            {
                return trimmed;
            }

            string[] segments = trimmed.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length <= 3)
            {
                return trimmed;
            }

            string tail = string.Join(Path.DirectorySeparatorChar, segments[^2..]);

            return "…" + Path.DirectorySeparatorChar + tail;
        }

        /// <summary>收尾摘要里允许的路径长度上限（超过就只留末两段）。</summary>
        private const int MaxSummaryPathLength = 72;

        /// <summary>
        /// 只留路径的**末 N 段**（<c>H:\…\共用根\其余物\025</c> → <c>其余物\025</c>）。
        ///
        /// <para>其余物那一行用它（用户 2026-09-26 第 45 条：真机上逐任务一条 60 字符的完整路径 × 118 条，
        /// 而他要认的只是"哪一个包的其余物"）。完整路径在 ③页「工作区残留」与输出目录里都看得到，
        /// **失败**时也照旧全量打印。</para>
        /// </summary>
        private static string DescribeShortTail(string? path, int segments)
        {
            if (string.IsNullOrWhiteSpace(path) || segments <= 0)
            {
                return string.Empty;
            }

            string trimmed = path!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string[] parts = trimmed.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            return parts.Length <= segments
                ? trimmed
                : string.Join(Path.DirectorySeparatorChar, parts[^segments..]);
        }

        /// <summary>其余物目录里有几个顶层条目（只数，不递归 —— 只为一行日志说清"有多少东西"）。</summary>
        private static int CountRestEntries(string? directory)
        {
            try
            {
                return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                    ? 0
                    : Directory.GetFileSystemEntries(directory).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>本批"其余物已处理"的记账（逐任务只写一行，批末一条汇总 —— 第 44 条）。</summary>
        private int _batchPurgedTaskCount;
        private long _batchPurgedBytes;

        /// <summary>批级记账的锁（任务是并发跑的，普通字段会被两个线程同时改）。</summary>
        private readonly object _batchPurgeGate = new();

        private void NoteBatchPurge(long freedBytes, bool permanent)
        {
            lock (_batchPurgeGate)
            {
                _batchPurgedTaskCount++;
                _batchPurgedBytes += freedBytes;

                if (permanent)
                {
                    _batchPurgedPermanently = true;
                }
            }
        }

        private bool _batchPurgedPermanently;

        /// <summary>
        /// 「停止后续」的那条日志**本批只写一次**（2026-09-27 真机：他连点两次按钮，
        /// 日志里"已请求停止后续任务"出现两遍，跑完收尾又是第三句不同措辞）。
        /// 批开始处（<c>_operationCts = new CancellationTokenSource()</c> 那一段）重置。
        /// </summary>
        private bool _stopNoticeLogged;

        /// <summary>见 <see cref="StatusText.StopRequestedNotice"/>：措辞统一、只写一次。</summary>
        private void LogStopRequestOnce()
        {
            if (_stopNoticeLogged)
            {
                return;
            }

            _stopNoticeLogged = true;

            AppendLog("WARN", StatusText.StopRequestedNotice);
        }

        private (int Tasks, long Bytes, bool Permanent) ReadBatchPurge()
        {
            lock (_batchPurgeGate)
            {
                return (_batchPurgedTaskCount, _batchPurgedBytes, _batchPurgedPermanently);
            }
        }

        private void AppendLog(string level, string message)
        {
            TaskLogCapture? capture = _currentTaskLog.Value;
            if (capture != null && !string.IsNullOrEmpty(message))
            {
                bool isInfo = string.Equals(level, "INFO", StringComparison.OrdinalIgnoreCase);

                if (isInfo && IsProgressNoise(message))
                {
                    // 进度：攒着（失败时才有用），成功时随缓冲一起丢。
                    capture.Buffer.Add((level, message));
                    return;
                }

                if (isInfo && !capture.DetailsVisible)
                {
                    capture.Buffer.Add((level, message));
                    return;
                }

                if (!isInfo && !capture.DetailsVisible)
                {
                    // WARN / ERROR：先把"之前发生了什么"吐出来，再把这一行放出去（顺序才对得上）。
                    FlushTaskLogBuffer(capture);
                    capture.DetailsVisible = true;
                }
            }

            _vm.AppendLog(level, message);
        }
        private void UpdateSummary() => _vm.UpdateSummary();

        /// <summary>
        /// 批量解压入口 —— **地基路径**（界面「只解压（不改后缀）」走的就是它）。
        ///
        /// <para>
        /// 源包处理（决策 D-9；用户 2026-09-22 的最新规则）：
        /// </para>
        /// <list type="bullet">
        /// <item><description>「一键处理」= 整理路径 → 走 <see cref="StartExtractForOneClickAsync"/>；</description></item>
        /// <item><description>本方法 = 地基路径（单层「只解压」）。</description></item>
        /// </list>
        /// <para>
        /// ⚠ <b>两条路径的源包规则现在是同一个</b>：都读 <see cref="AppSettings.SourceHandling"/>，
        /// 都在"内容物已定稿 + 输出校验通过 + 未取消 + 分卷组完整"之后把整组源包移入 <c>其余物</c>。
        /// 用户原话：「如果成功了你就直接将源包放在其余物里面」，并明确"地基路径也照这条走"
        /// （这推翻了更早那版"地基路径永远不动源包"的约定）。<c>KeepInPlace</c> 档仍是"永不搬"的出口。
        /// </para>
        /// <para>
        /// 两条路径真正的差别只剩一处（回写 <c>oneClickRun</c> 一路传到底）：**一键处理有续解链**，
        /// 所以"第一层只出过程物"时它把源包搬运留到链结束后补做（<see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；
        /// 单层的「只解压」没有链可等，当场按"定稿 + 校验通过"处理。
        /// 定稿、校验、归集、工作区清理的行为两条路径完全一致。
        /// </para>
        /// </summary>
        /// <param name="extractIntoSourceFolder">
        /// 这一次按**「解压到当前文件夹」**办（用户 2026-09-27 新增的手动档；默认 false）。
        ///
        /// <para>
        /// 语义 = WinRAR 右键的同一句话：内容物**不建包名那一层**，直接落在源包所在的那个目录里
        /// （<c>111\222.rar</c> → <c>111\内容物</c>）。它只活在这一批里，⛔ 绝不写进设置；
        /// **一键处理 / 继续解永远不会传它**（用户红线：批量处理一律套包名那一层，不摊平）。
        /// 同名文件/文件夹按当前冲突档处理 —— 摊平会更容易撞名，这个代价用户 2026-09-27 明确接受。
        /// </para>
        /// </param>
        public Task StartExtractAsync(bool extractIntoSourceFolder = false) =>
            StartExtractCoreAsync(oneClickRun: false, runOptions: null, extractIntoSourceFolder: extractIntoSourceFolder);
        /// <summary>
        /// 「一键处理」的显式入口：与 <see cref="StartExtractAsync"/> 同一个本体，
        /// 差别只在"有续解链"这件事上（见 <see cref="StartExtractAsync"/> 的说明）。
        /// </summary>
        /// <param name="runOptions">
        /// 本次选项面板选出来的**运行期快照**（规格 §9；null = 没弹面板 / 无界面宿主，按设置走）。
        ///
        /// <para>
        /// 它只活在这一批里：进方法时记下、<c>finally</c> 里置空。落点经它翻译成
        /// <see cref="ExtractOptions"/> 上那三个既有字段（唯一实现仍是 <c>OutputPlacement</c>），
        /// 终端落法与源包处理则在收尾处优先取它的值 ——
        /// **不勾"存为默认"时设置文件一个字节都不会被写**，因为这条路径上没有任何写设置的代码。
        /// </para>
        /// </param>
        /// <param name="continuationRound">
        /// 这一次是**同一条续解链的后续轮次**（一键处理第 2 轮起）。
        ///
        /// <para>它只决定一件事：批首要不要清掉"缺卷留到最后再判"的那份名单（用户 2026-10-05 口径 2）。
        /// 第 1 轮没解出来的第 1 卷很可能在第 2 轮才出现 ⇒ 那份名单必须**跨轮活到链尾**；
        /// 而用户重新点一次就是新的一批 ⇒ 那时清空（旧结论不许留到新批次上）。
        /// ⛔ 不改任何解压行为、不改轮数（轮数只有一个出口 <c>OneClickCoordinator.RoundLimit</c>）。</para>
        /// </param>
        public Task StartExtractForOneClickAsync(OneClickRunOptions? runOptions = null, bool continuationRound = false) =>
            StartExtractCoreAsync(oneClickRun: true, runOptions, continueDeferredVolumeDeficits: continuationRound);

        private async Task StartExtractCoreAsync(
            bool oneClickRun,
            OneClickRunOptions? runOptions = null,
            bool extractIntoSourceFolder = false,
            bool continueDeferredVolumeDeficits = false)
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                /*
                 * 一律只认勾选（用户 2026-09-24 第 12 条）：一个都没勾 → 只提示、什么都不做。
                 * 提示语来自 StatusText 的唯一来源，与清理类命令、一键处理逐字相同 ——
                 * 以前这里各写一套，用户看到三种说法，还以为是三个不同的问题。
                 */
                _dialogService.ShowWarning(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.NoCheckedTaskPromptFormat,
                    oneClickRun ? "一键处理" : "只解压",
                    Tasks.Count));

                AppendLog("WARN", StatusText.NoCheckedTaskLogFormat);

                return;
            }

            if (!_archiveEngine.IsAvailable)
            {
                /*
                 * 提示由 ToolLocator 现算（体检报告 §4 第 1 条）：
                 * 这条判定的口径是"任一引擎可用"，而默认优先级是 WinRAR(UnRAR) → 7-Zip ——
                 * 写死 7z 路径会把"其实只缺 UnRAR"的用户引去修一个没问题的目录。
                 * 两条期望路径与当前优先级顺序都由 ToolLocator 给出（外部工具路径只有它一个来源）。
                 */
                _dialogService.ShowError(ToolLocator.Default.DescribeNoEngineAvailable());
                AppendLog("ERROR", StatusText.SevenZipMissing);
                AppendLog("ERROR", ToolLocator.Default.DescribeAvailability());
                return;
            }

            _runOptions = runOptions;

            /*
             * 手动档「解压到当前文件夹」：只认**手动那一条入口**传来的值。
             * 一键处理那条入口压根没有这个参数（永远 false）—— 这里再挡一道，防止以后有人顺手把它接过去：
             * 批量摊平会把几十个包的内容物混进同一层，是用户明令禁止的。
             */
            _extractIntoSourceFolderThisRun = extractIntoSourceFolder && !oneClickRun;

            if (extractIntoSourceFolder && oneClickRun)
            {
                AppendLog("WARN", "一键处理不接受「解压到当前文件夹」（批量一律套包名那一层）—— 本次按普通一键处理执行。");
            }

            if (_extractIntoSourceFolderThisRun)
            {
                AppendLog("INFO", StatusText.ExtractIntoSourceFolderReminderLog);
            }

            /*
             * ===== 一键处理期间**一个弹窗都不弹**（用户 2026-09-27 真机铁证）=====
             *
             * 原话："什么叫做一键解压，就是说用户选中你来，然后自己去忙了，根本就没有空管你，
             * 以后不要出现弹窗"。他那一批 13 个任务一共被打断 8–9 次（多分支确认框 ×6–7 +
             * 批次结束的提示 ×1）—— 每一次都要他回来点一下，而"一键"的全部意义就是不回来。
             *
             * 处置：凡是"需要用户回答"的框，一键档一律**不弹**（照旧做最不意外的那件事），
             * 并把"本来要问什么、按什么办了"写进日志：
             *   · 多分支要不要展开 → **按②页所选档位**（用户 2026-10-04 推翻"一键档一律保守不展开"：
             *     默认档「展开所有分支」下递归核心根本不会问；只有用户显式选了「单链自动展开」时
             *     才会停在这一层，见 RunRecursiveAsync 里 `expandAll` 那一段）；
             *   · 同名冲突怎么处理 → 自动重命名、绝不覆盖（走既有的 ConflictActions 保守档）；
             *   · 分卷缺失要不要指定目录 → 不补救，照旧落「分卷缺失」（不变量 7 不放松）；
             *   · 批次结束的密码提示 → 只写日志（含"再点一次可手动输密码"的出路）。
             * ⛔ 判定与日志一条都没少，少的只是弹窗；手动「只解压」仍然照旧弹（那时用户就在旁边）。
             *
             * ⚠ 抑制必须排在**本批状态清零之后**（2026-09-29 复核逮到）：`ResetBatchConflictState()`
             * 会把 `_conflictPromptUnavailable` 一起置回 false，老代码在它之前抑制 —— 于是设置里选
             * 「询问」的人，一键档批中间照样会撞上那个聚合询问框（违反"一键档批中间零弹窗"）。
             * 见下面 IsBusy = true 之后那一段。
             */

            if (runOptions != null)
            {
                /*
                 * 本次选项进日志（规格 §9.2 硬要求⑥）：用户事后要能回答"这次为什么解到这里"。
                 * 逐任务那一行在 ExtractSingleTaskAsync 里写（带实际落点），这一行是本批的总纲。
                 */
                AppendLog("INFO", $"本次一键处理按「本次选项」执行：{runOptions.Describe()}");

                if (!runOptions.IsPlacementValid)
                {
                    AppendLog(
                        "WARN",
                        "本次选项里选了「指定位置」但没填路径 —— 为了避免它被解释成「未指定位置」那一档，" +
                        "本次落点回落设置里的值（其余两项照常生效）。");
                }
            }

            /*
             * ===== 解压前的提醒（用户 2026-09-22 需求第 8 条）=====
             *
             * 位置：**引擎可用性检查之后**（一个引擎都没有时该报的是「没有可用的解压引擎」，
             * 而不是"源目录里有些说明文件"）、**IsBusy = true 之前**（这是"还没动手"的最后一步：
             * 用户在这里选「先不处理」，这一批就是一个字节都没动过的干净状态）。
             *
             * 整批只弹一次，两段合并成同一个框（项目先例：多任务清理合并成 1 次确认）；
             * 两段都为空时**不弹** —— 空对话框只会训练用户闭眼点确定。
             */
            if (!await ConfirmBatchRemindersAsync(selectedTasks))
            {
                return;
            }

            IsBusy = true;
            IsStopping = false;
            _isExtracting = true;

            // 本批的密码失败登记从零开始：上一批的残留不能让这一批多弹一次提示。
            ClearPasswordFailures();

            /*
             * "复用本批已成功的密码"这一档**只在本批内生效**（用户 2026-10-04：
             * "不同次文件一律严格按我列表顺序"）。
             *
             * ⚠ 2026-10-16 订正边界（用户当面澄清逐字：「**同一次就是指同一次导入的**」）：
             * 一键处理**一条导入会跑好几轮**（`OneClickCoordinator` 的 `round` 循环），
             * 轮与轮之间属于**同一次导入** ⇒ ⛔ 后续轮次不许清。
             * 原来这一句是**无条件**的，而 `OneClickCoordinator` 每轮都会调进来
             * （`:1067` 逐字 `StartExtractForOneClickAsync(runOptions, continuationRound: round > 1)`）
             * ⇒ 第 1 轮学到的密码在第 2 轮被忘掉、后面每一个包又从头把整张表试一遍。
             * **实测（红检态）**：把这一句改回无条件 ⇒
             * `PasswordReuseTests.一键处理的后续轮次不清复用清单_同一次导入跨轮照样复用` 当场红，
             * 逐字 `Assert.Single() Failure: The collection contained 6 items` +
             * `Collection: ["", "<示例密码1>", "<示例密码2>", "<示例密码3>", "<示例密码4>", ···]`。
             *
             * 判据与同一方法里的"缺卷名单"**同一个事实位**（`continueDeferredVolumeDeficits`）：
             * 后续轮次保留；**用户重新点一次 = 新的一次导入** ⇒ 那时清空（旧批次的结论不许活到新批次上）。
             */
            if (!continueDeferredVolumeDeficits)
            {
                _passwordService.BeginBatch();
            }

            // 同名冲突的记账同样从零开始：上一批答过的「全部覆盖」绝不许延续到这一批。
            ResetBatchConflictState();

            /*
             * 一键档的"零弹窗"抑制放在这里 —— **必须在 ResetBatchConflictState 之后**：
             * 那个方法会把 _conflictPromptUnavailable 置回 false，先抑制后清零等于没抑制
             * （2026-09-29 复核逮到：设置里选「询问」时一键档批中间仍会弹聚合询问框）。
             */
            if (oneClickRun)
            {
                SuppressDecisionPromptsForOneClickRun();
            }

            // 「完成后打开输出目录」整批只开一次；上一批开过不算这一批的（见 _outputFolderOpenedThisBatch）。
            ResetBatchOutputFolderState();

            // 手动密码只对**本次运行**有效（不变量 5：绝不落盘）：批开始时重新问一次、重新记一次。
            ResetManualBatchPassword();

            // 空间规划与本批记账清零（见 finally 里的说明：清在**批首**，自测才读得到这一批的证据）。
            ResetSpacePlanningState();

            /*
             * 「缺卷留到最后再判」那份名单的清空点（用户 2026-10-05 口径 2）。
             *
             * ⛔ 一键处理的**后续轮次不清**：第 1 轮没解出来的第 1 卷很可能第 2 轮才出现，
             * 清了就等于把"留到最后"变成"留到本轮末尾"（真机那条 `.002` 正是跨轮才补上的形状）。
             * 用户重新点一次 = 新的一批 ⇒ 清空（旧批次的缺口结论不许活到新批次上）。
             */
            if (!continueDeferredVolumeDeficits)
            {
                _volumeDeficitDeferred.Clear();
                _triedVolumeGroupSignatures.Clear();
                _gatheredUserSourcePieces.Clear();
                _deferredVolumeGroupMember.Clear();
                _byteBudgetLinkedSources.Clear();
                _scoredSlotAttempts.Clear();
                _scoredSlotLinks.Clear();
                _scoredSlotAttempts.Clear();
                _scoredSlotLinks.Clear();
                _consumedVolumeSources.Clear();
                _groupConsumerByName.Clear();
                _volumeGroupUnits.Clear();
                ClearGroupPieceProducers();
                _producerSourcesCollected.Clear();
                _adoptedLandedPieces.Clear();
                _groupLayerByBaseName.Clear();
                _volumeStepTasks.Clear();
                _volumeGroupEntriesThisBatch.Clear();
                _volumeGroupEntriesThisBatch.Clear();
                _volumeDeficitFinalPass.Clear();
            }

            try
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = new CancellationTokenSource();

                _batchPurgedTaskCount = 0;
                _batchPurgedBytes = 0;
                _batchPurgedPermanently = false;
                _stopNoticeLogged = false;

                AppendLog("INFO", "开始批量解压");

                /*
                 * 手动密码出口（WinRAR 参考 §2 H 组 / §3 附注采纳项）。
                 *
                 * 位置：**动手之前**问一次。理由很实在 —— 密码本没命中、统一密码也不对时，
                 * 用户以前只能"改设置 → 重跑整批"，而重跑意味着已经解过的包再解一遍。
                 * 现在这一步先问，答了就当成本批所有任务的候选（排在空密码之后、密码本之前：
                 * 它是用户刚刚给出的信息，比密码本里的历史条目更"新"）。
                 *
                 * 三条纪律：
                 * ① **整批一次性**（_manualPasswordPrompted）—— 不做成每个包问一次，那正是 D-4 要避免的；
                 * ② 手动「只解压」这条路上**只对本次运行有效、绝不落盘**（不变量 5）——
                 *    只是一个字段，不进设置、不进密码列表、不进日志；
                 * ③ **无 UI 宿主不弹窗、不死等** —— 判定见 PromptForManualBatchPasswordAsync。
                 *
                 * ⚠ **一键档不再走这个框**（用户 2026-09-29 拍板）：一键处理的红线是"批中间零弹窗"，
                 * 而这个框恰好卡在开工前，等于给一键档开了第二个口子。一键档**没有手动密码入口**
                 * （当时那个输入框是整块撤掉的，不是搬去了别处），只写一条"要手动给就去「密码」页
                 * 一键导入"的日志 —— 见 LogOneClickPasswordHint。
                 *
                 * ⇒ 由此可以断定：一键档下 `_manualBatchPasswords` **恒为空**，所以每轮开头那次
                 * `ResetManualBatchPassword()`（StartExtractForOneClickAsync 的批首）清掉的是"空表"，
                 * 不会丢任何输入。
                 * ⚠ 以后若真要把输入框加回一键档，**必须同时改那一处清空**：一键处理一轮 = 一次
                 * `StartExtractForOneClickAsync`，不清就等于用户在某一轮填的密码到下一轮就没了
                 * （而"每轮都问"又违反零弹窗）。
                 */
                if (oneClickRun)
                {
                    LogOneClickPasswordHint(selectedTasks);
                }
                else
                {
                    await PromptForManualBatchPasswordAsync(selectedTasks);
                }

                /*
                 * ===== 工作区根：本批的中间产物放哪（用户 2026-09-30：落在这一单的目标目录里）=====
                 *
                 * 位置刻意在**空间规划之前**：空间账面要按"工作区与成品是不是同一块卷"给口径，
                 * 而目标目录正是定工作区根时算出来的（两处必须看同一个事实，不能各算一遍）。
                 *
                 * ⛔ 定不下来（没有任何任务算得出目标目录）→ **整批停手并报错**：
                 * 老实现这里会悄悄回落到程序目录（可能就是 C 盘），用户 2026-09-30 点名否掉了那个方向。
                 * return 在 try 里面，所以 finally 照常收尾（_isExtracting / IsBusy 都会被复位）。
                 */
                /*
                 * ⚠ 2026-10-09（D 结案 + 用户「回上一步解决错误」）：**定工作区根之前**先给这一批每一单
                 * 补跑一次"按它所在目录里的真实文件归组"。
                 *
                 * 为什么必须在这一刻（出处 `ViewModels/OneClickCoordinator.cs:1539-1564` 的注释）：
                 * 续解层的内层任务是走 `AddPathsAsync(..., suppressAutoScan: true)` 加进来的，
                 * **那条路不跑扫描期的分卷归组** ⇒ 它们的 `IsVolumeGroup` 一直是 false，
                 * 而 `ApplyBatchWorkspaceRoot` 里那道"跳过入口包还没解出来的分卷组单"的守卫
                 * （`IsUnresolvedVolumeGroupMember`，它第一句就是 `if (!task.IsVolumeGroup) return false;`）
                 * 因此对它们一律放行 ⇒ 真机 EEEE 的整批工作区根被那一单的**占位落点**
                 * （`…\EEEE\111(4)\111`）挑走。
                 *
                 * ⇒ 这就是"上游没做完的事，别在下游补"：把归组补在**它该在的位置**（定锚点之前），
                 * 而不是放宽那道守卫的判据。唯一出口仍是 `ApplyVolumeGroupingFromDirectory`
                 * （它自己再转调 `VolumeGroupDetector.Group` + `VolumeGroupingService.ApplyGroupInfo`，
                 * 与扫描期、手动档完全同一条路）；归不上组 ⇒ 什么都不动（与以前一样）。
                 */
                foreach (ArchiveTask groupingTarget in selectedTasks)
                {
                    if (groupingTarget != null && !groupingTarget.IsVolumeGroup)
                    {
                        OneClickCoordinator.ApplyVolumeGroupingFromDirectory(groupingTarget);
                    }
                }

                WorkspaceRootResolution workspaceResolution = ApplyBatchWorkspaceRoot(selectedTasks);

                // 第二大步点名：批首只读组分析，把可证完整那一组的入口任务标成第二大步（只解一层）。
                MarkVolumeStepEntriesForBatch(selectedTasks);

                if (!workspaceResolution.Resolved)
                {
                    _dialogService.ShowError(workspaceResolution.Reason);
                    AppendLog("ERROR", "这一批已经停下：工作区位置没定下来，一个字节都没写、源包一个都没碰。");
                    return;
                }

                /*
                 * ===== 批级整组改名：把"名字被改坏的分卷组"改回标准名（在**任何任务开工之前**）=====
                 *
                 * 旧位置是"每个任务开工前各改各的"（2026-09-28 起，见 NormalizeDisguisedVolumeNamesAsync）。
                 * 那个位置在并发下不成立：一个包的两卷是**两个任务**，谁先开工不定 ——
                 * 先开工的那一单改名成功、把两卷都改成标准名，而另一单可能在同一瞬间已经按**旧名字**
                 * 判过归组（名字太烂，认不出组）并接着往下跑，于是它拿着旧名一路走到「分卷缺失」，
                 * 还把整组的链尾处理一起拦住（用户 2026-10-01 真机：三棵一模一样的产物树 +
                 * `111(1)` / `333-Rar4(1)`，源包一个都没删）。
                 *
                 * 挪到批首之后，"这一批文件叫什么名字"在开工前就是定稿的：归组、源文件快照、
                 * 落点、链尾闸门读到的都是同一份事实。开工时那一次照旧留着（幂等：名字已标准就快速返回），
                 * 手动档「只解压」也只有那一次（单任务，本来就没有竞争）。
                 */

                /*
                 * 落点这一档要能回答"入口包还压在包里时，它将来落在哪"（真机 CCCC 2026-10-06：
                 * 用户连着追问的"批的落点还是按任务表里这一组第一单选"）。
                 *
                 * 位置：**改名归一之后**（名字定了才谈得上推标准入口包名）、**工作区根之后**。
                 * ⛔ 挂的就是"唯一出口"那个方法本身，判据一个字都不在这里写。
                 *
                 * ⚠ **为什么"工作区根之后"这一步不影响工作区根**（2026-10-11 核对，原先这里写的是
                 * "工作区根之前"、与代码相反，害得它看起来像个活的缺陷）：`ApplyBatchWorkspaceRoot` 里
                 * 唯一消费落点的地方是挑锚点，而那条路有两个事实把它挡住了 ——
                 *   ① 未挂钩时 `PathService` 直接返回 null（`PathService.cs` 里 `resolver == null ⇒ null`），
                 *      落点退回老公式；
                 *   ② 挂钩之后，这一档的判据第一句就是"**产出这一份的那一单**在不在任务表里"
                 *      （`ResolveProducerEntryArchivePathCore` 里 `FindProducerTaskForPackage` 为 null ⇒ 返回空串）
                 *      —— 而"那一单"若在表里，它自己的文件就在盘上、落点一定算得出来 ⇒
                 *      挑锚点时它**先于**这一单被收进 `destinations`（未解出入口包的那一单由
                 *      `IsUnresolvedVolumeGroupMember` 挪到兜底位，见 `ApplyBatchWorkspaceRoot`）。
                 * ⇒ 两种情形下锚点都是同一个答案；顺序只影响可读性，不影响行为。
                 * 实测佐证：真机 EEE 形状里那句 `本批有 1 单是分卷组的成员…工作区根改用别的单来定`
                 * 后面跟的工作区根正是产出链那一层（`…\in\111\111\.ArchiveFixer.work`），
                 * 当年那个 `…\EEEE\111(4)\111\.ArchiveFixer.work` 没再现。
                 */
                _pathService.GroupProducerEntryResolver =
                    (task, options, archivePath) => ResolveProducerEntryArchivePathCore(task, options, archivePath, 0);

                _pathService.DiagnosticLog = message => AppendLog("WARN", message);

                /*
                 * 「空间不足」模式：**批首钉死**（用户 2026-09-27 拍板的手动开关，见 _spaceTightThisBatch）。
                 * 位置刻意在并发与空间规划之前 —— 排序、并发、源包处理三件事都要读它。
                 * 后面那一行是它的**安全档**（「不删原包」）—— 两档一起钉，同一批口径一致。
                 */
                _spaceTightThisBatch = _vm.SpaceTightMode;
                _spaceTightKeepSourceThisBatch = _spaceTightThisBatch && _vm.SpaceTightKeepSource;

                /*
                 * 本批是不是一键档（只用于"中途空间不足要不要弹那一次纯提示"）：
                 * 一键档的人常常已经走开，手动档他正坐在屏幕前看列表（任务状态会当场变红）。
                 */
                _oneClickThisBatch = oneClickRun;
                _spaceBlockedNoticeShown = false;

                int maxParallel = ResolveMaxParallel(selectedTasks, out bool fullSpeed);

                if (_spaceTightThisBatch && fullSpeed)
                {
                    /*
                     * 「全速」在这个模式下**不生效**（用户原话："忽略全速和并发档"；
                     * 2026-09-27 追加：并发档改成"只许往下压"，见 ResolveSpaceTightParallelForBatch）。
                     * 说清原因再关掉它：否则日志里"已开全速"与"同时最多跑 3 个"会自相矛盾。
                     */
                    AppendLog(
                        "WARN",
                        "「空间不足」模式已开：本批「全速」不生效，并发由空间自己定"
                        + "（若②页的「最大并发解压数」比它更低，就按更低的跑；设置里的值一个字节都没改）。");

                    fullSpeed = false;
                }

                /*
                 * ===== 空间规划 + 空间门（用户 2026-09-22 需求第 1 / 2 条）=====
                 *
                 * 顺序：
                 * ① 按空间需求**从小到大**排序（用户点名的反例：一开始就去解 5G+6G，两个都跑不动）；
                 * ② 算出"按当前可用空间，最多能并行几个"，与用户选的档位一起写进日志；
                 * ③ 每个任务**启动之前**再判一次空间（不是只在计划时判一次）——
                 *    空间是随跑随变的，计划时的数字只能当建议（见 SpaceReservationLedger 的说明）。
                 *
                 * ⚠ 排序只影响**执行顺序**，不动任务列表本身：用户在列表里看到的顺序、
                 * 任务的 Index 都不变（改列表顺序会让"我刚才是第 3 个"这种对照失效）。
                 */
                ExtractionSchedulePlan plan = BuildSchedulePlan(selectedTasks, maxParallel, _spaceTightThisBatch);

                if (_spaceTightThisBatch)
                {
                    /*
                     * 并发档在**计划之后**定（它要读计划里的净占用与空间建议）——
                     * 顺序与普通档相反，因为普通档读的是设置、这里读的是刚刚算出来的空间。
                     */
                    maxParallel = ResolveSpaceTightParallelForBatch(plan);
                }

                PrepareRestHandlingForBatch();

                foreach (string line in plan.DescribeLines())
                {
                    AppendLog("INFO", line);
                }

                LogRestHandlingForBatch();

                LogExtractionLimitsForBatch();

                /*
                 * 「多层 + 空间小 ⇒ 中途才报空间不足」那句提醒（用户 2026-09-29 第 2 条）：
                 * 必须在**动手之前**说，而且与确认框里那句用**同一份判据、同一段文案**
                 * （见 EvaluateMultiLayerSpaceRisk / StatusText.MultiLayerSpaceRiskFormat）。
                 */
                LogMultiLayerSpaceRisk(plan);

                _spaceLedger = new SpaceReservationLedger(
                    ProbeAvailableSpace(ResolveSpaceProbePath(selectedTasks)),
                    ReserveSpaceBytes);

                AppendLog("INFO", "空间账面：" + _spaceLedger.Describe());

                /*
                 * 空间侦察：从这一刻起"时刻"看着这块盘（每 5 秒一针 + 每个任务开工 / 收尾各一针），
                 * 批末写一条空间曲线。位置在账面之后、第一个任务之前 —— 起点的数字就是"开工前还剩多少"。
                 */
                StartSpaceTrendMonitor(ResolveSpaceProbePath(selectedTasks));

                var runningTasks = new List<Task>();

                /*
                 * 第 45 条（用户 2026-09-26）：等待并发位的提示**一批只说一次** + 批末给个计数。
                 *
                 * 真机日志里那一行出现了 **74 次**（76 个任务）：
                 * `并发已满（1/1 个任务正在跑），「018.7z」在队列里等一个空位。想让它立刻开跑：
                 *   勾上主界面的「全速」；或点「停止后续」……`
                 * 用户三条意见全中：①**主界面根本没有「全速」**（它在②页 →「并发与空间」）；
                 * ②他不知道"是要暂停下来开还是直接开"；③一条 60 多字的提示刷 74 遍，量与"简洁"背道而驰。
                 */
                int throttledTasks = 0;
                bool throttleExplained = false;

                /*
                 * ===== 空间门拒了 ≠ 这个任务没做成（用户 2026-10-02 真机，14 任务里 6 个白没做）=====
                 *
                 * 现场：14 个任务、并发档 8、盘上 35.86 GiB 可用。前 8 个一开跑，账本里"已经在跑的任务
                 * 预留"就顶到 35.11 GiB（那 8 个各自的全额需求之和本来就接近整块盘）—— 于是第 9 个包
                 * 哪怕自己只要新写 851.45 MiB，也被判「还差 9.77 MiB」**当场记成失败**。
                 * 那 8 个要跑 30 多分钟，其间陆续跑完、把预留一笔笔放出来（最大的那个一个人就放开 15.71 GiB），
                 * 可被拒的那 6 个**再也没人回头看它们**：批末报「失败 6」、诊断「磁盘空间不足：6 个」。
                 *
                 * 判据上的错在哪：**"此刻空间不够"是一个会变的瞬时事实，不是一个终态**。
                 * 所以拒下的任务进这张名单**等空间**，等有任务跑完（预留释放）再按原顺序（小→大）重试；
                 * 一直等到没人跑、空间不会再变多为止，剩下那些才如实报「空间不足」。
                 *
                 * ⛔ 两条不变：① 排队**不占并发位**（不会因为排队把盘跑满）；
                 * ② 真正开跑前仍然要过账本（`TryReserve`），**放行判据一个字没放宽**。
                 */
                var spaceWaiting = new List<(ArchiveTask Task, ScheduledExtractionItem Item, SpaceGateDecision Gate)>();

                foreach (ScheduledExtractionItem item in plan.Ordered)
                {
                    ArchiveTask task = item.Task;

                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        LogStopRequestOnce();
                        break;
                    }

                    // 每个任务各自的"已经在队列里等过"标记与开始等的时间（只有等得久才补一句，见下）。
                    bool queuedLogged = false;
                    DateTime queuedSince = DateTime.Now;

                    // 等待有空闲并发位；期间若用户点了“停止后续”，不再为后面的任务等位。
                    while (runningTasks.Count >= maxParallel)
                    {
                        /*
                         * 「等待状态必须可见」（WinRAR 参考 §2 D 组采纳项）。
                         *
                         * 没有这一行时，被节流挡住的任务在界面上**完全看不出在等** ——
                         * 用户看到的是"点了开始，一半任务纹丝不动"，然后怀疑程序卡死
                         * （这正是本机历史上被投诉过的那类现象）。
                         */
                        throttledTasks++;

                        if (!queuedLogged)
                        {
                            queuedLogged = true;
                            queuedSince = DateTime.Now;
                        }

                        if (!throttleExplained)
                        {
                            throttleExplained = true;

                            /*
                             * 一批只说一次（用户 2026-09-26 第 45 条：这条刷了 74 遍）。
                             * 三个必须写对的点：
                             * ①**位置**：「全速（本批不节流）」在**②页 →「并发与空间」**，不在主界面；
                             * ②**怎么用**：勾上**立刻**就放开本批（下面那个 if 会当场生效），不用停也不用重开；
                             * ③另一条出路是「停止后续」。
                             */
                            AppendLog(
                                "INFO",
                                $"并发已满（{runningTasks.Count}/{maxParallel} 个任务正在跑），后面的任务在队列里等空位。" +
                                (fullSpeed
                                    ? "（已开「全速」，本批不再节流。）"
                                    : "想立刻放开：②页 →「并发与空间」勾上「全速（本批不节流）」—— 勾上当场生效，" +
                                      "不用停止、也不用重开；或点①页「停止后续」不再启动后面的任务。" +
                                      "（这条只在第一次排队时说一遍，本批还有几个在等，跑完会在结尾汇总。）"));
                        }

                        /*
                         * 中途勾上「全速」→ **立刻放开**（用户问的正是"是要暂停下来开还是直接开"：直接开）。
                         * 这里重读一次那个开关（它是 MainViewModel 上的本次运行开关，不是落盘设置）。
                         *
                         * ⚠ 「空间不足」模式下这一条**不生效**（用户 2026-09-27："忽略全速和并发档"）：
                         * 那个模式的并发数是按空间算出来的，中途放开等于把"别把盘写满"这条自己拆掉。
                         */
                        if (!_spaceTightThisBatch && !fullSpeed && _vm.RunAtFullSpeed)
                        {
                            fullSpeed = true;
                            maxParallel = Math.Max(1, plan.Ordered.Count);

                            AppendLog("INFO", $"检测到「全速」已勾上，本批立刻放开并发（同时最多 {maxParallel} 个）。");

                            break;
                        }

                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    /*
                     * 关键修复（实测复现）：上面的 break 只跳出了"等并发位"的 while，
                     * 落到这里仍会把当前任务 Add 进去 —— 表现为"点了停止后续，另一个包照样跑成功了"，
                     * 违反不变量 9（停止后续只阻止**启动后续**，绝不能再启动新任务）。
                     * 所以在真正启动之前必须再查一次。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        LogStopRequestOnce();
                        break;
                    }

                    if (queuedLogged)
                    {
                        /*
                         * 等过空位的任务：只在**等得久**的时候补一句（用户 2026-09-26 第 45 条：
                         * 74 条"并发已满 / 等到空位"同义反复就是噪声）。30 秒以内的等待不值得单独占一行，
                         * 批末那条汇总会把"有几个等过"说清。
                         */
                        TimeSpan waited = DateTime.Now - queuedSince;

                        if (waited.TotalSeconds >= ThrottleNoticeThresholdSeconds)
                        {
                            AppendLog("INFO", $"「{task.FileName}」等了 {waited.TotalSeconds:F0} 秒才排上空位（本批同时最多跑 {maxParallel} 个）。");
                        }
                    }

                    /*
                     * 空间门（**启动之前**判，用户 2026-09-22 需求第 1 条）。
                     *
                     * 判据里含"已经在跑的任务预留了多少"（账本），所以 5G+6G 这种组合里
                     * 第二个大包会在启动前就被拦下，而不是等它写到一半才报磁盘满。
                     * 拦下时**跳过它、继续看后面的**（后面的包可能更小、正好塞得下）——
                     * 全部排完再一次性报告哪些被跳过、各需要多少。
                     *
                     * ⚠ 数字是 `RequiredBytes` = 内容物 + 过程物（`TaskSpaceEstimate.FreeSpaceDemandBytes`），
                     * **不是峰值**：源包已经在盘上、不在可用空间里，拿峰值比就是把源包算两遍
                     * （2026-09-29 真机：17.7 GiB 的三卷包在 33.45 GiB 可用的盘上被判"整盘都放不下"）。
                     */
                    ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);

                    SpaceGateDecision gate = _spaceLedger.TryReserve(
                        item.RequiredBytes,
                        item.Estimate.ReclaimableBytes);

                    if (!gate.Allowed)
                    {
                        /*
                         * 不记失败、不改任务状态 —— 先挂进"等空间"名单（见上面那段说明）。
                         * 只写一行 INFO 说清"它在等什么"，用户才不会以为这个包已经被判死刑了。
                         */
                        spaceWaiting.Add((task, item, gate));

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：现在空间不够（{gate.Reason}）—— 先排队等空间，"
                            + "等已经在跑的任务收尾把预留放出来就自动补跑（不是失败，也不会漏掉它）。");

                        /*
                         * 「中途撞上空间不足」那一次纯提示**照旧当场发**（用户 2026-09-29 第 2 条：
                         * 一键档弹一次、后面被拦下的不再弹；手动档只写日志）。
                         *
                         * 为什么不挪到批末"确实排不上"那一刻再发：那一刻整批已经结束了，
                         * 用户坐在屏幕前等着的时候**什么都不知道** —— 而这条提示的全部意义就是
                         * "现在告诉你一声，别干等"。排队不等于没空间不足，这一条该说还得说。
                         */
                        NotifySpaceBlockedOnce(task, item.RequiredBytes, gate.AvailableBytes, gate.ShortfallBytes);

                        continue;
                    }

                    runtime.ReservedBytes = gate.ProbeFailed ? 0L : item.RequiredBytes;
                    runtime.AvailableBeforeStart = _spaceLedger.AvailableBytes;

                    runningTasks.Add(RunScheduledTaskAsync(task, oneClickRun, runtime));
                }

                /*
                 * ===== 等待所有已启动的任务结束 + **等空间的补跑环**（用户 2026-10-02 真机）=====
                 *
                 * 两件事必须在**同一个环**里做，这是踩过一次才定下来的：补跑环如果写在"等所有任务结束"
                 * **之后**，轮到它时 `runningTasks` 已经空了 —— 那个 `while` 一次都不进，
                 * 被拒的任务照样在批末被记成「空间不足」（红检现场：用例里第二个包一直是"未处理"）。
                 *
                 * 规则：**每跑完一个任务就重试一遍等待名单**（它一收尾就把预留放出来了，空间是真变多了）；
                 * 只要还有人在跑，空间就还可能变多，就继续等。没人跑了 ⇒ 空间不会再变多 ⇒ 剩下的如实报。
                 *
                 * ⛔ 三点纪律：① 重试仍然走同一个 `TryReserve`（放行判据一个字没放宽）；
                 * ② 名单里的任务**不占并发位**，只有真排上了才进 `runningTasks`；
                 * ③ 用户点「停止后续」⇒ 立刻停（剩下的照旧什么都不做、如实落状态）。
                 *
                 * ⚠ 2026-10-05 外面又套了一层（同一个环，⛔ 不是第二套批末流程）：**批末补判「缺卷」**
                 * （用户口径 2）。它只能在"这一批的解压**都跑完**之后"判（那时缺的那几卷可能已经被解出来 /
                 * 被收拢到位）；判出来"补齐了"的那几组要**照常跑**，所以它们必须回到**这个环**里等收尾 ——
                 * 这也是"补跑环必须与等所有任务结束写在同一个环里"那条纪律的同一条道理。
                 */
                /*
                 * ⛔ 2026-10-15：**批末补判的重判次数护栏**（保命，不是性能）。
                 *
                 * 运行期实测到的死循环（全量测试里跑了 40 分钟、日志涨到 125 MB 都出不来）：
                 * 批末补判说"「set.7z.002」这一组现在齐了（共 2 卷），照常跑这一组" ⇒ 排进 `runningTasks` ⇒
                 * 它一开工，预检又说"这一组缺的是**第 1 卷** … 判不出该怎么收，整组一个字节都没动" ⇒
                 * 回到本环再判 ⇒ 又是"齐了" ⇒ ……**两个判据对同一组给出相反结论，这一环永远出不去**。
                 *
                 * 上面那段注释写的出口（"撞上轮数上限"）在代码里**根本不存在** —— 这个 `while (true)`
                 * 原来只有"批末补判返回空"一个出口。⇒ 补一道**硬上限**：重判超过 `MaxVolumeRecheckPasses`
                 * 次就停下，把那几单**如实留在「未处理」那一档**（与既有那份"没有结论"的口径一致），
                 * 并写一行点名它们。⛔ 判据一个字没放宽 —— 缺卷照旧不开始解。
                 *
                 * ⚠ 为什么是"趟数硬上限"而不是"看名单有没有变少"：实测的形状是**缩了又涨**
                 * （这一趟补判把一单挪出名单 ⇒ 它跑起来撞缺卷 ⇒ 下一趟又有单进来）⇒
                 * "连续不缩小才计数"那种写法在振荡下永远到不了阈值，等于没修。
                 * 这里要的是**保证终止**，所以数的是总趟数。
                 */
                const int MaxVolumeRecheckPasses = 20;
                int volumeRecheckPasses = 0;

                while (true)
                {
                    while (runningTasks.Count > 0)
                    {
                        Task finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        await finished;

                        if (IsStopping || _operationCts.IsCancellationRequested)
                        {
                            break;
                        }

                        if (spaceWaiting.Count == 0)
                        {
                            continue;
                        }

                        /*
                         * 刚跑完一个任务 ⇒ **当场再探一次可用空间**，再拿它重试等待名单。
                         *
                         * 账本平时由空间侦察每 5 秒刷一针；可"任务收尾把预留放出来"这件事是**立刻**发生的，
                         * 如果这里不重探，补跑环会拿着 5 秒前的旧数字白等一轮（真机上就是"明明跑完了一个大包、
                         * 空间已经回来了，那个小包还在等"）。
                         */
                        _spaceLedger.RefreshAvailable(ProbeAvailableSpace(ResolveSpaceProbePath(selectedTasks)));

                        for (int i = 0; i < spaceWaiting.Count;)
                        {
                            if (runningTasks.Count >= maxParallel)
                            {
                                break;   // 并发位又满了：等下一轮（跑完一个再来看）
                            }

                            (ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) = spaceWaiting[i];

                            SpaceGateDecision retry = _spaceLedger.TryReserve(
                                waitingItem.RequiredBytes,
                                waitingItem.Estimate.ReclaimableBytes);

                            if (!retry.Allowed)
                            {
                                i++;
                                continue;
                            }

                            ScheduledTaskRuntime waitingRuntime = GetOrCreateRuntime(waitingTask);
                            waitingRuntime.ReservedBytes = retry.ProbeFailed ? 0L : waitingItem.RequiredBytes;
                            waitingRuntime.AvailableBeforeStart = _spaceLedger.AvailableBytes;

                            spaceWaiting.RemoveAt(i);

                            AppendLog(
                                "INFO",
                                $"{waitingTask.LogName}：腾出空间了，现在补跑它（它刚才被拒的原因：{waitingGate.Reason}）");

                            runningTasks.Add(RunScheduledTaskAsync(waitingTask, oneClickRun, waitingRuntime));
                        }
                    }

                    /*
                     * 用户点了「停止后续」⇒ 批末补判整段不做（不变量 9：停止后续只阻止**启动后续**，
                     * 不许变成"强杀当前"，也不许在停下之后又去起新的任务）。
                     * 名单里那几单照旧什么都不做、状态留在「未处理」—— 如实反映"这一批没轮到它"。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        break;
                    }

                    /*
                     * ===== 批末补判「缺卷」（用户 2026-10-05 口径 2）=====
                     *
                     * 判据与执行体都在 `RecheckDeferredVolumeDeficits` 里：再收一次卷 → 现归一次组
                     * → 齐了就返回该补跑的那几单；仍然缺 ⇒ 一键档留给链尾（还有后续轮次）、
                     * 手动档**到这里**才如实报缺卷。
                     *
                     * ⛔ 兜底：这个方法要么给出"该跑的那几单"，要么把状态落定 —— 绝不会让名单里的任务
                     * 停在"没人管"那一档（唯一例外是整条链被用户停下，那时它如实留在「未处理」）。
                     */
                    List<ArchiveTask> deferredVolumeTasks = await RecheckDeferredVolumeDeficitsAsync(
                        finalPass: !oneClickRun,
                        // 这一站没有 CancellationToken 在手（老签名就没有）⇒ 传 None，与改成 async 之前等价。
                        CancellationToken.None).ConfigureAwait(false);

                    /*
                     * 「7-Zip 打不开」的那一份若看着就是本批某一组的一片 ⇒ 到这里才按**严格判据**
                     * 升级成「分卷缺失」（用户 2026-10-09 口径：未识别的先跳过、到了最后再重新检验一步）。
                     * ⚠ 2026-10-17：这两个成员从 2026-10-16 加进来就一直**没有调用点**（名单只写不读）⇒ 本处补上。
                     * 幂等：只动机器终态还是「已跳过」的那几单。
                     */
                    UpgradeUnrecognizedMaybePieceVerdicts();

                    if (deferredVolumeTasks.Count == 0)
                    {
                        break;
                    }

                    /*
                     * 保命护栏（见上面那段说明）：**总趟数硬上限**。
                     *
                     * 正常情形下一两趟就清空名单（每一趟会把名单里所有能补上的组一次性补齐）；
                     * 到了上限还在打转，说明批末补判与开工前预检对同一组给出的结论相反 ——
                     * ⛔ 再判下去只会把日志写爆、把这一批永远挂住 ⇒ 停下 + 点名 + 如实留在「未处理」。
                     */
                    volumeRecheckPasses++;

                    if (volumeRecheckPasses > MaxVolumeRecheckPasses)
                    {
                        int stillDeferred = _volumeDeficitDeferred.Count;

                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeRecheckPassLimitReachedFormat,
                                volumeRecheckPasses,
                                stillDeferred,
                                string.Join(
                                    "、",
                                    _volumeDeficitDeferred.Take(5).Select(item => item.LogName))));

                        break;
                    }

                    foreach (ArchiveTask deferredTask in deferredVolumeTasks)
                    {
                        runningTasks.Add(RunScheduledTaskAsync(
                            deferredTask,
                            oneClickRun,
                            GetOrCreateRuntime(deferredTask)));
                    }
                }

                /*
                 * 等到最后还是排不上的：**这时候才**如实记「空间不足」（状态 / 错误信息 / 日志 / 批末清单
                 * 走同一个落点 `MarkSpaceBlocked`）。⛔ 这些任务从头到尾一个字节都没动。
                 */
                foreach ((ArchiveTask waitingTask, ScheduledExtractionItem waitingItem, SpaceGateDecision waitingGate) in spaceWaiting)
                {
                    MarkSpaceBlocked(waitingTask, waitingItem, waitingGate);
                }

                AppendLog("INFO", "批量解压完成");

                /*
                 * **批末把"改过名的行"那三列重算一遍**（检测格式 / 建议后缀 / 后缀状态）——
                 * 用户 2026-10-07 点名的三列过期：扫描那一刻写的是**旧名字**下的结论
                 * （真机 `111(2)_.zi删除p` 写着「后缀不匹配」，而我们随后把它改回了 `111(2)_.zip`）。
                 * ⛔ 只重算这几个纯事实字段、走扫描那边唯一的出口；⛔ 不碰 Status / 进度 / 落点 / 归组。
                 */
                await _vm.RefreshRenamedRowFactsAsync().ConfigureAwait(true);

                /*
                 * 并发排队的批末汇总（用户 2026-09-26 第 45 条）：过程里只在第一次排队时说一句，
                 * 这里把"到底有几个等过"一次说清 —— 比 74 行同义反复有用得多。
                 */
                if (throttledTasks > 0)
                {
                    AppendLog(
                        "INFO",
                        $"并发排队汇总：本批 {throttledTasks} 个任务等过空位（同时最多跑 {maxParallel} 个）。"
                        + (fullSpeed
                            ? "本批已开「全速」。"
                            : "下次想全程不排队：②页 →「并发与空间」勾上「全速（本批不节流）」（勾上当场生效）。"));
                }

                /*
                 * **把①页每一行实际显示的那几格写进日志**（用户 2026-10-07：「你日志里面能否看清楚列表里面
                 * 显示什么内容」）。显示层（列绑定 / 行刷新）本来不进日志，于是"列表显示对不对"只能靠截图；
                 * 这一行把名称 / 大小 / 当前后缀 / 完整路径 / 状态 / 错误信息**按显示口径**原样打出来，
                 * 用户导出的日志就能与界面逐格对齐（⛔ 取的是同一批计算属性，不另算一套）。
                 *
                 * ⚠ 2026-10-08（EEEE 真机）：快照**必须取在"事实定稿之后"**。老写法只有这一处，
                 * 而链尾补判（`FinalizeDeferredVolumeDeficits`，由一键档在整批跑完之后调）还会改写几行的
                 * 状态 ⇒ 导出的日志里最后一份快照停在批首的中性档（真机那两行写着「等待解压」，
                 * 用户据此判定"显示不对"）。⇒ 收成唯一出口 `AppendTaskRowSnapshot`，两处都调它。
                 */
                AppendTaskRowSnapshot();


                AppendBatchSummary(selectedTasks);

                /*
                 * ⛔ **批末重试"过路层其余物按档删"**（真机 2026-10-06 20:07 的 48.35 MB）：
                 * 收场那一刻半套分卷闸门拦得对（同组的片还在成品树里），可那一组随后就解完了
                 * ⇒ 到这里障碍已经消失；不补这一次，那份其余物就永远没人再看一眼。
                 */
                RetryPendingPassThroughRestPurges();

                // 其余物处理的批末汇总（第 44 条：逐任务一行 + 这里一条，替掉以前几十行长文案）。
                (int purgedTasks, long purgedBytes, bool purgedPermanently) = ReadBatchPurge();

                if (purgedTasks > 0)
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RestPurgedBatchSummaryFormat,
                            purgedTasks,
                            WorkspaceCleanupService.FormatSize(purgedBytes))
                        + (purgedPermanently
                            ? "（彻底删除，不进回收站、不可恢复）"
                            : "（已进回收站，可还原）"));
                }

                /*
                 * **如实报告**被空间门跳过的任务（用户 2026-09-22 需求第 2 条：
                 * "全部排不下时如实报告哪些包因为空间不足被跳过、各自需要多少，不要静默跳过"）。
                 *
                 * 每一条都带具体数字与建议动作 —— 这一行往往是用户唯一能拿到的行动线索。
                 */
                ReportSpaceBlockedTasks();

                /*
                 * 询问次数写进日志：决策 D-4 要求"本批只问一次"，这一行就是它的证据 ——
                 * 用户与排障都能看到"这一批到底打断了几次"（选了「全部 X」之后不该再涨）。
                 */
                int conflictPrompts;

                lock (_conflictDecisionLock)
                {
                    conflictPrompts = _conflictPromptCount;
                }

                if (conflictPrompts > 0)
                {
                    AppendLog(
                        "INFO",
                        $"本批同名冲突询问共 {conflictPrompts} 次（选了「全部 X」之后不再询问）。");
                }

                /*
                 * 密码错误**合并成一次提示**（P0）。
                 * 逐个任务弹模态框时，50 个错包就是 50 次阻塞点击 —— 批量处理根本跑不动。
                 * 放在批次结束后：此时才谈得上"本批 N 个包"，也不会挡住正在跑的任务。
                 *
                 * ⚠ 一键处理期间这条**只写日志、不弹框**（用户 2026-09-27：一键解压不许弹窗，
                 * 用户走开时没人点它，反而把"批已跑完"这件事卡在最后一个模态框上）。
                 * 日志那一行照旧写全（含"再点一次可以手动输密码"的出路），失败清单也能导出。
                 */
                await ShowPasswordFailuresSummaryAsync(showDialog: !oneClickRun);
            }
            finally
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = null;

                _isExtracting = false;
                IsBusy = false;
                IsStopping = false;

                // 本次选项**只活这一批**：批结束就丢掉，下一批（哪怕是同一次运行里的下一轮续解）
                // 由调用方重新给一份 —— 这样"覆盖"永远不会悄悄延续到别的批次上。
                _runOptions = null;

                /*
                 * 「解压到当前文件夹」与"本次选项"同一个寿命，而且**必须**在批尾清掉：
                 * 它是"这次点击"的语义，留着就会让下一次**一键处理**也悄悄摊平 ——
                 * 那正是用户 2026-09-27 划的红线（批量绝不摊平）。
                 */
                _extractIntoSourceFolderThisRun = false;

                /*
                 * 空间规划与本批记账同样"只活这一批"：
                 * 上一批的账本必须丢掉（下一批的可用空间与任务集都变了）。
                 */
                _spaceLedger = null;

                /*
                 * ⚠ 这里**刻意不**把 `_restHandlingThisBatch` 落回 Keep（2026-09-25 第 32 条被测试逮住）。
                 *
                 * 一键处理的「删除操作」是在**整条续解链跑完之后**做的，而那个链尾钩子
                 * （`CompleteRootSourcePackagesAfterChainAsync`，由 `OneClickCoordinator` 在本次解压批次
                 * 返回之后调用）读的就是这个字段 —— 在这里清掉，等于把用户选的「彻底删除 / 移入回收站」
                 * 静默降级成「不动其余物」：真机表现就是"我明明选了彻底删除，其余物还在"。
                 *
                 * 下一批开工时 `PrepareRestHandlingForBatch()` 会重新按当时的设置写一次，
                 * 所以留着的值只服务于"这一批自己的链尾"，不会影响任何后续批次。
                 */

                /*
                 * 「空间不足」模式**与上面那个字段相反：批尾必须清掉**。
                 *
                 * 理由：它读的是①页那个运行期勾（<see cref="MainViewModel.SpaceTightMode"/>），
                 * 而用户随时可能把它关掉 —— 留着 true 会让**批尾之后**的代码（链尾补搬、
                 * 单包重试、界面上的空间建议）继续按"删源包"那一套思考。
                 * 下一批开工时会重新 `= _vm.SpaceTightMode`，所以清掉不会丢任何东西。
                 */
                _spaceTightThisBatch = false;
                _spaceTightKeepSourceThisBatch = false;

                /*
                 * 「本批是不是一键档」同样只活这一批（它只服务于"中途空间不足要不要弹那一次纯提示"）：
                 * 留着 true 会让**批尾之后**的单包路径（重试、链尾、清理）也以为自己在一键批里。
                 * 下一次开工时重新按参数钉死。
                 */
                _oneClickThisBatch = false;

                // 空间侦察：停循环 + 把这一批的空间曲线写进日志（用户 2026-09-27 要求"时刻侦察空间变化"）。
                StopSpaceTrendMonitor();

                /*
                 * 顺手把**空的工作区壳**收掉（用户 2026-09-27：真机跑完 `…\.ArchiveFixer.work\recursive`
                 * 会留一个空壳在那儿）。
                 *
                 * 为什么要在这里做：任务是各清各的（每单自己的目录、递归逐层），但 `recursive`
                 * 这种"父壳"没人负责 —— 文件都没了，壳还在，用户看着就是残留。
                 *
                 * 三条安全边界（⛔ 一条都不许松）：
                 * ① **根下面只要还有一个文件就一个字节都不动**（宁可留着壳，也不许误删东西）；
                 * ② 只删工作区根**直接子目录**，绝不递归着往外走（不会碰到源包 / 成品目录）；
                 * ③ 全部包在 try/catch 里 —— 收尾的顺手活，失败只写日志，不许影响批结论。
                 */
                RemoveEmptyWorkspaceShells();

                /*
                 * 再收一层：**我们自己造出来的那个目标目录壳**（用户 2026-09-30 的新收尾）。
                 *
                 * 顺序不能反：先收 `<目标目录>\.ArchiveFixer.work`（上一步），这一层才可能变空。
                 * 判据两条同时成立才删 —— ① 它确实是这一次建出来的（批首记的事实）；
                 * ② 现在一个条目都没有（现枚举）。少了任何一条都会去碰用户自己的目录。
                 */
                RemoveEmptyTargetShellIfWeCreatedIt();

                // 批尾清掉这条批内状态（与 _spaceTightThisBatch / _oneClickThisBatch 同一口径）。
                _batchCreatedTargetDirectory = string.Empty;

                UpdateSummary();
            }
        }

        // ================================================================ 空间规划 + 其余物处理

    }
}
