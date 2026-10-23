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
        /// <summary>第一大步的交接事实表（第二大步的组分析从这里取候选）。</summary>
        private readonly List<ArchiveFixer.Detection.VolumeHandoffEntry> _volumeHandoffEntries = new();

        /// <summary>本批的交接事实（只读给第二大步/日志用）。</summary>
        internal IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry> VolumeHandoffEntries => _volumeHandoffEntries;

        private void RegisterVolumeHandoff(ArchiveTask task, ArchiveFixer.Detection.FileNatureCard card)
        {
            string path = task.CurrentPath ?? string.Empty;

            foreach (ArchiveFixer.Detection.VolumeHandoffEntry existing in _volumeHandoffEntries)
            {
                if (string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _volumeHandoffEntries.Add(new ArchiveFixer.Detection.VolumeHandoffEntry
            {
                Path = path,
                BaseName = FileNameHelper.SecondStepBaseName(path),
                Format = card.Format,
                Size = card.Size,
                BodyKind = card.BodyKind,
                VolumeNumber = card.VolumeNumber,
                FirstVolumeFlag = card.FirstVolumeFlag,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = task.IsContinuationTask
                    ? ArchiveFixer.Detection.VolumeHandoffSource.ChainProduced
                    : ArchiveFixer.Detection.VolumeHandoffSource.UserImported
            });
        }

        /// <summary>
        /// 「其余物」目录名（契约 §3.2，2026-09-22 由「过程物」改名）：为得到内容物而产生、
        /// 用户不需要的东西 —— 内层归档、分卷、抠出来的中间 ZIP、纯壳文件夹；**以及源包本身**
        /// （决策 D-9：一键处理默认把源包也移进来，用户在那个目录里一次删掉就干净了）。
        ///
        /// 名字的**唯一来源**是 <see cref="ProcessArtifactLayout.ArtifactDirectoryName"/>，
        /// 这里只是给本类内部一个短名字用（别在别处再写一遍字面量）。
        /// </summary>
        internal const string ProcessArtifactDirectoryName = ProcessArtifactLayout.ArtifactDirectoryName;

        private readonly MainViewModel _vm;
        private readonly IArchiveEngine _archiveEngine;
        private readonly PasswordService _passwordService;
        private readonly PathService _pathService;
        private readonly DialogService _dialogService;

        /// <summary>
        /// 源包搬运要碰的文件系统（决策 D-12）。
        ///
        /// 默认走真实文件系统；单测注入假实现来验证**跨盘那条路**（真机上很难临时造第二个卷）：
        /// "复制成功后再删原件、复制失败源包一个字节都不动"是红线，不能只靠读代码。
        /// </summary>
        private readonly ISourceMoveFileSystem _sourceMoveFileSystem;

        /// <summary>
        /// 递归解压（M4）。引擎、探测器、密码来源全部注入，递归层自己不碰密码本。
        ///
        /// 注意：**不在这里留一个构造时就固定上限的实例** ——
        /// <see cref="RecursiveExtractor"/> 的上限是构造参数，构造一次就再也改不了，
        /// 而这里是 ViewModel 层的单例：用户改完设置（层数 / 每层密码上限）不重启程序就不生效。
        /// 所以每次任务现建一个（见 <see cref="CreateRecursiveExtractor"/>），上限当场从设置里取。
        ///
        /// 日志出口也接在这里：递归核心要把它清工作区（删目录，**不可逆**）的"删什么、为什么"
        /// 写出来，而它自己不认识 GUI、也不引用 LogService（与引擎、探测器同一套注入方式，
        /// AGENTS.md §4 分层铁律）。<see cref="MainViewModel.AppendLog(string, string)"/> 走的是
        /// BeginInvoke，从后台线程调用是安全的。
        ///
        /// ⚠ 一个任务一个实例：递归核心把"本次任务的工作区"记在实例上（<c>CurrentWorkspace</c>），
        /// 清理只认那一个目录。共用实例会让并发任务互相覆盖这个字段。
        /// </summary>
        private RecursiveExtractor CreateRecursiveExtractor()
        {
            /*
             * 这一趟递归的「候选值 → 来源」映射：递归层只拿得到值，来源说明只能由**算候选时**
             * 顺手记下来（见 BuildRecursionPasswordCandidates 与 ResolveCandidateSourceForLog）。
             *
             * ⚠ 必须**每个递归实例一份**（即每个任务一份）：协调器是 ViewModel 层的单例，
             * 并发跑两个任务时共用一份映射会让 A 任务的候选来源印到 B 任务的日志上；
             * 而且旁路说明文件是按归档路径算的 —— 跨任务共用等于按别人的包说话。
             */
            var candidateSources = new Dictionary<string, string>(StringComparer.Ordinal);

            var extractor = new RecursiveExtractor(
                _archiveEngine,
                new MagicArchiveProber(),
                archivePath => BuildRecursionPasswordCandidates(archivePath, candidateSources),
                BuildRecursionLimits(),
                AppendLog,
                // 不变量 11 在**第 0 层**上的落点：递归核心每解一层之前都会问一次
                // "这一层要解的那个源文件还是原来那一份吗"。只有第 0 层（用户给的源包）
                // 会真的比对 —— 第 1 层起解的是工作区里我们自己产出的过程物，
                // 它们本来就不在快照里（详见 CheckRootSourceUnchangedAsync）。
                CheckRootSourceUnchangedAsync)
            {
            };

            /*
             * 「内容物保留关键词」（用户 2026-10-04 的新功能「内容物压缩文件不解压」）：
             * 命中关键词的内层归档**不当内层归档**（不展开、也不改名）。
             *
             * ⛔ 判据只有一处（ContentKeepRules.FromSettings → ContentKeepRules），递归核心只拿到结果；
             * 空列表 ⇒ 判据是 Empty ⇒ 行为与加这条功能之前逐字相同。
             * 与上面那一档同一个理由：**每次任务现建一个实例、当场从设置里取**（改完设置不重启也要生效）。
             */
            extractor.KeepRules = ContentKeepRules.FromSettings(Settings);

            /*
             * 详细日志档与"候选来源描述器"（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）：
             *
             * 递归路径以前**连一条候选日志都没有**，而真机那次 13 分钟走的正是这条路 ——
             * 打开详细日志之后，它要跟单层路径一样把每个候选写出来。
             *
             * ⚠ 描述器用的是**单层路径那一份** BuildTryPasswordLogText：两处的措辞必须逐字一致，
             * 否则同一件事在日志里长成两句话（§9.5）。它只输出占位符，绝不出现明文。
             *
             * ⚠ 2026-10-05：来源必须取自**这一趟算候选时记下的映射**（candidateSources）——
             * 只按值回查密码列表的话，「本批已成功 / 密码本命中 / 密码本整行 / 说明文件旁路 /
             * 手动输入的密码」全都会被印成"尝试密码列表第 N 项"（说话不实，排障时看错方向）。
             */
            extractor.VerboseLog = VerboseTaskLogEnabled;
            extractor.DescribeCandidate = (value, index) =>
                _passwordService.BuildTryPasswordLogText(
                    new PasswordItem { Value = value, Source = ResolveCandidateSourceForLog(value, candidateSources) },
                    index);

            /*
             * 「停止后续」在**递归**候选循环里的落点（2026-10-05 只读审计）。
             *
             * 单层路径在候选之间看 IsStopping（语义：正在解的那一次不打断，下一个密码不再试），
             * 而递归这条路过去拿不到这个信号 —— 用户点了「停止后续」，这一层剩下的候选照样一个个试完
             * （大包就是几十分钟）。这里只把同一个信号接过去：判据与措辞都在递归核心那一处，
             * ⛔ 协调器不再自己判一遍。
             */
            extractor.StopRequested = () => IsStopping;

            /*
             * 「可疑条目提示」的开关（2026-10-05 只读审计）：递归层拿到本层清单之后要统计
             * 可执行 / 脚本类条目，而"要不要统计"是用户设置（注入方式照 VerboseLog：当场取值）。
             * 结论怎么显示由 RunRecursiveAsync 那一边决定（写的是单层路径同一个字段）。
             */
            extractor.ReportDangerousEntries = Settings.ReportDangerousEntries;

            /*
             * ===== 递归路「逐层回收」的落点（用户 2026-10-05：「这个我不是说了要同步吗」）=====
             *
             * AGENTS.md §11.3 那条口径（每一层「定稿 + 输出校验通过 + 未取消 + 可证完整」之后当场回收
             * 这一层的过程物）以前只接在**轮次续解**那条路上；「展开所有分支」这条链是**一个任务**内
             * 由 RecursiveExtractor 展开的（层是工作区目录、不是任务），过去整条链的过程物一直攒到
             * 定稿那一刻才释放（真机第六批：空间曲线 13:39:32 最低 62.48 GiB → 13:40:57 回到 82.63 GiB，
             * 峰值 26.94 GiB 而结果只有 7.28 GB）。
             *
             * ⛔ **判据只有既有那一个事实位**（`_layerReclaimThisBatch`，写入点唯一
             * `PrepareRestHandlingForBatch`）—— 递归路**不另算一遍**：
             * 「空间不足」模式照旧走它自己那一支（每层各删各的、含源包），普通档恒为 true。
             * 挂钩子这一行读的就是它。
             */
            /*
             * ⚠ 2026-10-12 三条实测（保留，因为它们是"为什么这个钩子只删、绝不在这里搬"的依据）：
             * ① 接到源包搬运（`ExecuteSourcePackageMove`）上 ⇒ 被那条红线拒搬（"源在工作区目录里…
             *    一个字节都不搬"），只留一句假 INFO；
             * ② 换成过程物那条出口（往其余物里**搬**）⇒ 片确实集中进其余物了（日志逐字
             *    `逐层回收（删除操作 = 不动其余物） —— pack111-L2.z01 已集中进其余物`），**但同一轮的内容物
             *    被重复解出来**（残留出现 `pack111-L4.rarzz\pack111-L0\444(1).mp4`、`444(2).mp4`）
             *    ⇒ 逐层"搬"会让后续那一层的产物跟踪按路径找不到原件；
             * ③ 而"用完了就没了"（永久删）逐层做没问题 ⇒ 钩子的动作面**只有删**，
             *    记账那一半（`_chainInnerLayerInputs`）交给链尾。
             *
             * ⛔ 2026-10-15：这一位现在**不再跟档位联动**（成功链的过程物一律当场永久删，
             * 见 `PrepareRestHandlingForBatch`）。它仍然是"要不要挂钩子"的唯一判据：
             * 「空间不足」模式有它自己那一支（置 false），普通档恒 true。
             */
            if (_layerReclaimThisBatch)
            {
                extractor.LayerReclaim = OnRecursiveLayerReclaim;
            }

            return extractor;
        }

        /// <summary>
        /// **递归路「逐层回收」的执行口**（每次回调 = 一条递归链里某一层**跑成之后**、
        /// 该把上一层交出来的那个内层包还回去的时候）。
        ///
        /// <para>判据与执行体**全部转调既有出口**，这里只做三件接线的事：</para>
        /// <list type="number">
        /// <item><description>把"要还的那几个文件"拼成一个只用于**取清单**的任务对象
        /// （<c>SourceCleanupService.BuildTargetList</c> 是"要删哪些文件"的唯一出口，
        /// 而它读的是任务的 <c>VolumePaths</c> / <c>CurrentPath</c> —— 那份清单同时喂给
        /// 「半套分卷」闸门，⛔ 判据与实际删除必须看同一份清单）；</description></item>
        /// <item><description>把上一层的说法与"这一层可证完整吗"的结论原样喂给
        /// <see cref="PurgeLayerSourcePackage"/>（它自己那几道门槛一条都不绕过，只是**多**了一档
        /// 由调用方给出的完整性结论）；</description></item>
        /// <item><description>按事实说话：删了才写「已回收」，没删就写清为什么（⛔ 不静默 return）。</description></item>
        /// </list>
        ///
        /// <para><b>⛔ 最外层源包永远不在这条路上</b>：<paramref name="request"/>.GroupFiles 里装的全是
        /// 递归工作区里的内层包（入队时算好的），而 <c>reclaimRootSource: false</c> 是第二道保险。</para>
        /// </summary>
        private void OnRecursiveLayerReclaim(RecursiveLayerReclaimRequest request)
        {
            if (request == null)
            {
                return;
            }

            /*
             * ⛔ 逐层回收只删**过程物**：任何一单的 `OriginalPath`（用户最初导入的那一份）永远不进
             * 删除清单。2026-10-18 `28282828` ProcMon 现场：源 `.001` 被硬链接到输出后，递归层的
             * “内层包（这一层的源）3 个”把它一起删了 ⇒ `KeepInPlace` 的样本树少一份源。
             * ⛔ 用户源包该不该删只由链尾「源包操作」那一档回答，不由逐层回收回答。
             */
            var reclaimableFiles = request.GroupFiles
                .Where(file => !string.IsNullOrWhiteSpace(file)
                    && !IsAnyTaskOriginalPath(file)
                    && !IsAnyTaskOriginalIdentity(file)
                    && !IsUserImportedSourcePath(file))
                .ToList();

            if (reclaimableFiles.Count != request.GroupFiles.Count)
            {
                AppendLog(
                    "WARN",
                    $"{request.ChildLayerLabel}：逐层回收清单里有用户源包（{request.GroupFiles.Count - reclaimableFiles.Count} 份）"
                    + "—— 这一层只回收过程物，源包按链尾「源包操作」处理");

                request = new RecursiveLayerReclaimRequest
                {
                    ChildLayerLabel = request.ChildLayerLabel,
                    GroupFiles = reclaimableFiles,
                    Verdict = request.Verdict,
                    WorkspaceTaskDirectory = request.WorkspaceTaskDirectory
                };
            }

            if (request.GroupFiles.Count == 0)
            {
                return;
            }

            /*
             * **只记账**（⛔ 不动盘）：这一层用过的那几个文件（入口 + 同组各卷）。
             * 链尾那张"集中进其余物"的名单要用它 —— 实测依据（2026-10-12，`run-4444`）：
             * 挂在"逐层"那一刻去搬会把后续那一层的产物跟踪弄丢（内容物被重复解出来，`444(1)/(2).mp4`），
             * 而**逐层删掉**没问题（"用完了就没了"）⇒ 所以"搬"必须等链尾。
             */
            foreach (string used in request.GroupFiles)
            {
                if (!string.IsNullOrWhiteSpace(used))
                {
                    _chainInnerLayerInputs.Add(used);
                }
            }

            /*
             * ⛔ 2026-10-15：这里原来有一支**「删除操作 = 不动其余物」档 ⇒ 只有记账、一个字节都不动**的提前返回。
             * 它随"逐层回收与档位解绑"一起删除（用户 2026-10-14 口径：成功链上的过程物**统一彻底删除**，
             * 不看档位；见 `PrepareRestHandlingForBatch` 里那一段）。⇒ 走到这里的每一层都按下面那套判据删。
             */
            if (!request.Verdict.AllowsSourceRemoval)
            {
                /*
                 * L4 判不出 / 判否 ⇒ **什么都不做**，但必须留一行（AGENTS.md §9.5：判不出 ⇒ 什么都不做
                 * **+ 如实写一行**）。写的是 `Blocker`（唯一出口拼的那句"为什么没删"），
                 * 这里不另拼一套措辞。
                 */
                AppendLog(
                    "WARN",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.RecursionLayerNotReclaimedFormat,
                        request.ChildLayerLabel,
                        request.Verdict.Blocker.Length > 0
                            ? request.Verdict.Blocker
                            : StatusText.RecursionLayerReclaimNotVerifiedReason));

                return;
            }

            var logEntries = new List<(string Level, string Message)>();

            string? failure = PurgeLayerSourcePackage(
                BuildInnerPackageReclaimTask(request),
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.LayerReclaim,
                reclaimVerdict: request.Verdict,
                reclaimRootSource: false,
                reclaimLabel: request.ChildLayerLabel,
                reclaimWorkspaceDirectory: request.WorkspaceTaskDirectory);

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }

            /*
             * ⛔ 递归路**不**把"过程物没删掉"升级成任务结论（与续解那条路刻意不同）：
             * 续解层那一份 `sourceMoveFailure` 会落到任务上的「部分完成」，而递归路是一条链里的一层，
             * 删不掉只是"这一份过程物多留一会儿"，工作区收尾照样会整份清掉 —— 拿它把已经跑通的
             * 内容物标成"部分完成"只会误导用户（不变量 6 讲的是**内容物**没做完的情形）。
             */
            if (failure != null)
            {
                AppendLog("WARN", $"{request.ChildLayerLabel}：{failure}");
            }
        }

        /// <summary>
        /// **把一个过程物文件搬进其余物**（唯一出口：让位 + 同卷改名 / 跨卷复制 + 绝不覆盖）。
        ///
        /// <para>三处消费点共用它（⛔ 不许各写一份）：① <c>CollectChainInnerPackagesIntoRestAsync</c>
        /// （链尾把"内层包"集中进其余物）；② <c>CollectChainInnerLayerInputsIntoRest</c>
        /// （链内那一族的片在链尾集中）；③ <c>PurgeGroupLandedPieces</c>
        /// （被整组接走的那份副本集中进其余物）。</para>
        ///
        /// <para>判据与源包搬运（<c>SourcePackageMover</c>）**同一口径**：目标名被占 ⇒
        /// <see cref="ProcessArtifactLayout.MakeUniqueTarget"/> 让位（连"本计划里前面那几条"一起算），
        /// 同卷直接改名、跨卷/被占用才回落到"先复制成功再删原件" —— 复制失败就一个字节都不动。</para>
        /// </summary>
        /// <param name="from">源（必须在盘上）。</param>
        /// <param name="to">目标（最终名可能因让位而变，见 <paramref name="target"/>）。</param>
        /// <param name="movedTargets">本趟已经落下去的目标名（让位时要看它）。</param>
        /// <param name="target">真的搬到的那个名字（失败时为空串）。</param>
        /// <param name="failure">失败原因（成功时为 null）。</param>
        internal static bool TryMoveIntoRest(
            string from,
            string to,
            HashSet<string> movedTargets,
            out string target,
            out string? failure)
        {
            target = to;
            failure = null;

            try
            {
                if (movedTargets.Contains(target) || File.Exists(target) || Directory.Exists(target))
                {
                    target = ProcessArtifactLayout.MakeUniqueTarget(
                        target,
                        isDirectory: false,
                        movedTargets,
                        FileSystemArtifactTargetProbe.Instance);
                }

                movedTargets.Add(target);

                SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(target) ?? string.Empty);

                try
                {
                    File.Move(from, target, overwrite: false);
                }
                catch (IOException)
                {
                    File.Copy(from, target, overwrite: false);
                    File.Delete(from);
                }
                catch (NotSupportedException)
                {
                    File.Copy(from, target, overwrite: false);
                    File.Delete(from);
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;

                return false;
            }
        }

        /// <summary>
        /// 把"递归层要还回去的那几个文件"包成一个**只用来取清单**的任务对象。
        ///
        /// <para>为什么非要一个任务对象：要删哪些文件这件事**只有一个出口**
        /// （<see cref="SourceCleanupService.BuildTargetList"/>，它读 <c>VolumePaths</c> / <c>CurrentPath</c>），
        /// 而同一份清单还要喂给「半套分卷」闸门 —— 判据与实际删除必须看同一份清单（AGENTS.md §9.5）。
        /// 这个对象不登记进任务列表、不进界面、不参与统计，用完即弃。</para>
        ///
        /// <para>一条以上 ⇒ 按**分卷组**算（整组一起判、一起删）；只有一条 ⇒ 单文件任务。</para>
        /// </summary>
        private static ArchiveTask BuildInnerPackageReclaimTask(RecursiveLayerReclaimRequest request)
        {
            IReadOnlyList<string> files = request.GroupFiles;

            string first = files.Count > 0 ? files[0] : string.Empty;

            var task = new ArchiveTask(first)
            {
                FileName = Path.GetFileName(first),
                IsArchive = true
            };

            if (files.Count > 1)
            {
                task.IsVolumeGroup = true;
                task.VolumePaths.AddRange(files);
            }

            return task;
        }

        /// <summary>
        /// 递归层拿到的候选**值**要能映射回"这条密码是从哪儿来的"，日志才看得懂顺序为什么是这样。
        ///
        /// <para>递归那条路只拿到值（<c>Func&lt;string, IReadOnlyList&lt;string&gt;&gt;</c>），
        /// 而来源说明在 <see cref="PasswordService.BuildTryPasswordLogText"/> 里。</para>
        ///
        /// <para><b>先查"算这一层候选时顺手记下来的映射"</b>（<paramref name="candidateSources"/>）：
        /// 按值回查密码列表**答不了**好几档 —— 同一个值可能既是"密码本命中"又是"本批已成功"
        /// （密码列表里的同一条），而"说明文件旁路 / 手动输入 / 密码本整行"根本不在密码列表里，
        /// 一律落兜底 ⇒ 日志把它们印成"尝试密码列表第 N 项"（说话不实）。
        /// 查不到才退回按值回查（不变量 5：⛔ 绝不猜来源，更不写明文）。</para>
        /// </summary>
        /// <param name="candidateSources">算候选时记下的「值 → 来源」；null = 只按值回查（老路）。</param>
        internal string ResolveCandidateSourceForLog(
            string value,
            IReadOnlyDictionary<string, string>? candidateSources = null)
        {
            string exact = value ?? string.Empty;

            if (candidateSources != null &&
                candidateSources.TryGetValue(exact, out string? recorded) &&
                !string.IsNullOrWhiteSpace(recorded))
            {
                return recorded;
            }

            if (string.IsNullOrEmpty(exact))
            {
                return "Empty";
            }

            if (!string.IsNullOrEmpty(GlobalPassword) &&
                Settings.UseGlobalPasswordForAllTasks &&
                string.Equals(GlobalPassword, exact, StringComparison.Ordinal))
            {
                return "GlobalPassword";
            }

            foreach (PasswordItem item in _passwordService.Passwords)
            {
                if (string.Equals(item.Value ?? string.Empty, exact, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(item.Source) ? "ImportedList" : item.Source;
                }
            }

            return "ImportedList";
        }

        /// <summary>
        /// 递归的硬上限（不变量 8）：层数与每层密码尝试次数都取用户的设置项。
        ///
        /// 旧实现的坑：调用方 new 的时候没传 limits，递归核心一直用 <c>RecursionLimits.Default</c> ——
        /// 于是"最大嵌套层数"改成 5 也照样只解 3 层，而每层密码上限被悄悄压到 8
        /// （设置项缺省是 10，用户调大更是不生效）。设置项必须真的管用，不能只是界面上的数字。
        ///
        /// ⚠ <c>internal</c>（2026-10-05）：单测要钉住"四条上限一条都不许漏映"——
        /// 单文件那一条正是这么漏掉的（递归核心过去一处都没引用 ResourceBudget）。
        /// </summary>
        internal RecursionLimits BuildRecursionLimits() => new()
        {
            MaxDepth = Math.Clamp(Settings.MaxRecursionDepth, 1, 10),
            MaxPasswordAttemptsPerLayer = MaxPasswordAttemptsPerLayer,

            /*
             * 累计总量与展开比也取用户那一套上限（第 36 条）：递归核心以前写死 50 GiB / 500 倍，
             * 于是"我在设置里把总大小上限调大了"在递归档下不生效 —— 又是一处"改了没反应"。
             * 文件数同理（预算那一档默认 20 万，递归那一档原来是 10 万）。
             */
            MaxTotalSize = BudgetLimits.MaxTotalSize,
            MaxTotalFiles = BudgetLimits.MaxFileCount,
            MaxExpansionRatio = BudgetLimits.MaxExpansionRatio,

            /*
             * 单文件那一条（2026-10-05 只读审计）：递归核心过去**一处都没映** ——
             * 出厂默认档（展开所有分支）下，一个含 200 GiB 单文件的包不会被这道闸门拦下。
             * 上限只有 BudgetLimits 这一个出口（与上面三条同一个理由：改了设置不重启也要生效）。
             */
            MaxSingleFileSize = BudgetLimits.MaxSingleFileSize
        };

        /// <summary>解压前预检最多试几个密码候选去列表：试太多次会让"一键"变成等待。</summary>
        private const int MaxPreflightPasswordAttempts = 3;

        // ================================================================
        // 不变量 11：源文件变化（AGENTS.md §6 第 11 条）
        // ================================================================

        /// <summary>
        /// **开工前**保证手上有一份基准：没有就现在补拍一次。
        ///
        /// <para>
        /// 为什么是补拍而不是拦下（"判据缺失 ≠ 判据不通过"）：老任务（这个功能落地之前建的任务）、
        /// 测试直接 <c>new</c> 出来的任务、以及用户手改过列表的情形，都可能没有快照。
        /// 把"我们没记录"说成"你的文件变了"，会把一个本来能正常跑的任务拦死，
        /// 而用户完全无从理解（他什么都没做过）。正确做法是把基准补上，从这一刻起开始保护。
        /// </para>
        /// </summary>
        public void EnsureSourceSnapshot(ArchiveTask task)
        {
            if (task == null || task.HasSourceSnapshot)
            {
                return;
            }

            task.CaptureSourceSnapshot();

            AppendLog(
                "INFO",
                $"{task.LogName}：开工前补拍源文件快照（这份任务过去没有基准），"
                + "从这一刻起按源文件大小 / 修改时间判断它有没有被改动。");
        }

        /// <summary>
        /// **开工前就拦下这一单**的统一收尾（唯一出口）。
        ///
        /// <para>谁走它：不变量 11 的"源文件已变化"、不变量 7 的"缺卷预检"、以及"源文件不在了 /
        /// 落点无效"这三条 —— 它们的共同点是**引擎一次都没被调用、这一单一个字节都没产出**，
        /// 所以状态、原因、结束时间必须一次落全。</para>
        ///
        /// <para><b>为什么必须收口成一个出口</b>：机器终态的唯一收口（<c>ProcessExtractTaskAsync</c>
        /// 的 finally，判据 <c>Outcome == Pending &amp;&amp; EndTime 有值 &amp;&amp; 校验没通过</c>）
        /// 只在"这一单已经结束"时才敢补结论。少写一个 <c>EndTime</c>，任务就永远停在 <c>Pending</c>：
        /// 批末汇总按终态把它算进「未处理」，而批末诊断按状态说「分卷缺失」—— 同一件事两处口径打架
        /// （2026-09-30 真机 `222.z01`：`失败 4 / 未处理 1` vs `分卷缺失：1 个`）。</para>
        ///
        /// <para>⛔ 只落"这一单已结束 + 失败"这套事实（走 <see cref="ArchiveTask.MarkFailed"/>），
        /// <b>不在这里替任何一档下别的结论</b>：状态与原因由调用方给（各档自己的判据），
        /// 中文文案一个字都不改。</para>
        /// </summary>
        /// <summary>
        /// **"7-Zip 打不开"的那一份，是不是某个分卷组的一片？**（唯一出口，判据 2026-10-14 定、2026-10-16 落地）
        ///
        /// <para>现场（官方环台 `10101010` / `19191919`）：一份名为 `pack111-L2.7z` 的文件 ——
        /// 它其实是分卷 7z 的**第 3 片**，只被伪装法吃掉了卷标记 ⇒ <b>内容判不出</b>
        /// （7z 族的中间片/末片按定义没有魔数，卡上 `格式=Unknown 分卷身份=Unknown`）、
        /// <b>名字也不像后续卷</b>。老口径把它落成「已跳过 ⇒ 不是压缩包」，于是整组永远凑不齐。</para>
        ///
        /// <para>判据（⛔ 只用既有事实，⛔ 不猜卷号、⛔ 不动盘上任何东西）：<br/>
        /// ① 同批任务里有另一单的名字读得出"第 1 卷"（<see cref="VolumeGroupDetector.TryGetVolumeIndex"/> = 1）；<br/>
        /// ② 它的基名（唯一出口 <see cref="FileNameHelper.GetArchiveBaseName"/>）与这一份**相同或互为前缀**；<br/>
        /// ③ 这一份的体积 **≤** 那一份的体积（"除末卷外等大" ⇒ 任何一片都不会比满片还大）。<br/>
        /// 三条同时成立 ⇒ 判"疑似这一组的一片" ⇒ 结论是**缺卷**，交给批末/跨链那一套去凑。</para>
        ///
        /// <para>⛔ 判不出 ⇒ 返回 <c>false</c>（照旧落「已跳过」）—— 兜底永远是"什么都不做"。</para>
        /// </summary>
        /// <summary>
        /// 批末：把 <see cref="_unrecognizedMaybePiece"/> 里那些**升级**成「分卷缺失」（严格判据成立时）。
        ///
        /// <para>⛔ 只升级、不改别的：判据不成立的那些**保持「已跳过」**（它们是真的"不是压缩包"）；
        /// 机器终态与结束时间由 <see cref="MarkStoppedBeforeExtract"/> 一次性收口（那一支的注释里有理由）。</para>
        /// </summary>
        private void UpgradeUnrecognizedMaybePieceVerdicts()
        {
            if (_unrecognizedMaybePiece.Count == 0)
            {
                return;
            }

            foreach (ArchiveTask task in _unrecognizedMaybePiece.ToList())
            {
                if (task.Outcome != TaskOutcome.Skipped)
                {
                    continue;
                }

                if (!LooksLikePieceOfBatchVolumeGroup(task, out string diagnosis))
                {
                    continue;
                }

                MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);

                /*
                 * ⛔ 状态改了、终态也必须落：`MarkStoppedBeforeExtract` 先把 `Outcome` 清成 `Pending`，
                 * 而**终态由谁落**只有 `FinalizeOutcomeIfPending` 一个出口（缺卷那一档在里面就是
                 * 「部分完成」）。少了这一句，这一单会以「分卷缺失」的**状态** + `Pending` 的
                 * **终态**收场 ⇒ 批末汇总把它算进「未处理」（2026-10-17 新用例当场逮到）。
                 */
                FinalizeOutcomeIfPending(task);

                AppendLog("WARN", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");
            }
        }
        private bool LooksLikePieceOfBatchVolumeGroup(ArchiveTask task, out string diagnosis)
        {
            diagnosis = string.Empty;

            string ownName = FileNameHelper.GetFileName(task.CurrentPath);

            if (ownName.Length == 0)
            {
                return false;
            }

            long ownSize;

            try
            {
                ownSize = new FileInfo(task.CurrentPath).Length;
            }
            catch
            {
                return false;
            }

            string ownBase = FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            if (string.IsNullOrWhiteSpace(ownBase))
            {
                return false;
            }

            foreach (ArchiveTask? other in SnapshotTaskTable(Tasks))
            {
                if (other == null || ReferenceEquals(other, task) || string.IsNullOrWhiteSpace(other.CurrentPath))
                {
                    continue;
                }

                string otherName = FileNameHelper.GetFileName(other.CurrentPath);

                // ① 那一单的名字必须读得出"第 1 卷"。
                if (VolumeGroupDetector.TryGetVolumeIndex(otherName) != 1)
                {
                    continue;
                }

                string otherBase = FileNameHelper.GetArchiveBaseName(other.CurrentPath);

                // ② 基名相同，或互为前缀（`pack111-L2` vs `pack111-L2.7z` 这种"归档后缀差一段"）。
                bool sameBase = string.Equals(ownBase, otherBase, StringComparison.OrdinalIgnoreCase)
                    || ownBase.StartsWith(otherBase, StringComparison.OrdinalIgnoreCase)
                    || otherBase.StartsWith(ownBase, StringComparison.OrdinalIgnoreCase);

                if (!sameBase)
                {
                    continue;
                }

                long otherSize;

                try
                {
                    otherSize = new FileInfo(other.CurrentPath).Length;
                }
                catch
                {
                    continue;
                }

                // ③ "除末卷外等大" ⇒ 这一份不会比满片还大。
                if (ownSize > otherSize)
                {
                    continue;
                }

                diagnosis = $"它可能是「{otherName}」那一组的一片（同族同基名、体积 {ownSize} ≤ {otherSize}），"
                    + "但这一组现在凑不齐 —— 先不判它，等这一批的解压都跑完再判一次。";

                return true;
            }

            return false;
        }

        /// <summary>
        /// **这一批已经用「完全同一组片」试过一遍的组签名**（批末补判去重用的那本账）。
        ///
        /// <para>签名 = 组的 `GroupKey` + 每一卷「完整路径 + 字节数」。**只要有新片进来签名就会变**
        /// ⇒ 补判"后来又补齐了、再跑一次"那条合法路径**一个字都不受影响**；
        /// 只有"这一批已经拿完全同样的几片试过、结论也就那样"才拒。</para>
        ///
        /// <para>2026-10-17 真机现场（`10101010`，逐行计数）：批末补判把同一组排了 **21 趟**，
        /// 每趟候选序号恒为 `1/10`（第一趟第一枚候选就撞结构性失败）、`分卷收不到一起` 21 次。</para>
        /// </summary>
        private readonly HashSet<string> _triedVolumeGroupSignatures =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 组的"试过一遍"签名（唯一出口）：`GroupKey` + 按卷序每一卷的「路径|字节数」。
        /// 读不到字节数记 `-`（⛔ 不因此判"不同组"——判不出就照旧算同一组，宁可不重跑）。
        /// </summary>
        /// <summary>
        /// **被「收拢」搬离用户原目录的那几片**（原路径 ↔ 搬走后的路径）。
        ///
        /// <para>收拢本身是用户 2026-10-05 拍板的（「各分卷在不同的目录，你就将其全部移动到头文件 .001
        /// 同级目录里面去」）；但「源包操作 = 留下」要求用户源文件**一个字节都不许少** ——
        /// 2026-10-17 真机 `10101010` 的判据 ④ 就是这么红的（第三片 10,490,213 字节被搬走后
        /// 当过程物永久删除，日志逐字「逐层回收…已立刻永久删除内层包（这一层的源）3 个」）。</para>
        ///
        /// <para>用法：逐层回收**动删除之前**先把这几份放回原目录；放不回就如实写一行，
        /// ⛔ 绝不因为这本账把别人的删除整段拦下。</para>
        /// </summary>
        private readonly List<(string OriginalPath, string CurrentPath)> _gatheredUserSourcePieces = new();

        /// <summary>记下"这几片是从用户自己的源目录里收拢过来的"（只记搬离过的那几份）。</summary>
        private void RememberGatheredUserSourcePieces(IReadOnlyList<VolumeRepairItem> items)
        {
            foreach (VolumeRepairItem item in items)
            {
                if (string.IsNullOrWhiteSpace(item.CurrentPath) || string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(item.CurrentPath, item.TargetPath))
                {
                    continue;
                }

                if (!IsUserImportedSourcePath(item.CurrentPath))
                {
                    continue;
                }

                _gatheredUserSourcePieces.Add((item.CurrentPath, item.TargetPath));
            }
        }

        /// <summary>
        /// **把收拢搬走的用户源片放回原目录**（逐层回收动删除之前唯一该做的补偿）。
        /// 返回放回的份数；放不回（原位置又被占 / 出错）⇒ 如实写 WARN，⛔ 不拦别人的删除。
        /// </summary>
        private int RestoreGatheredUserSourcePieces(List<(string Level, string Message)>? logEntries)
        {
            if (_gatheredUserSourcePieces.Count == 0)
            {
                return 0;
            }

            int restored = 0;

            foreach ((string originalPath, string currentPath) in _gatheredUserSourcePieces.ToList())
            {
                try
                {
                    if (!File.Exists(currentPath))
                    {
                        _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                        continue;
                    }

                    if (File.Exists(originalPath))
                    {
                        logEntries?.Add((
                            "WARN",
                            $"{Path.GetFileName(originalPath)}：原目录里已经有一份同名的了 —— "
                            + "不覆盖，收拢搬走的那一份仍留在这边。"));

                        continue;
                    }

                    string? directory = Path.GetDirectoryName(originalPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);
                    File.Move(currentPath, originalPath);

                    logEntries?.Add((
                        "INFO",
                        $"用户源片放回原目录：{Path.GetFileName(originalPath)}（它是被「收拢」搬走的，"
                        + "按「源包操作 = 留下」一个字节都不许少）。"));

                    _gatheredUserSourcePieces.Remove((originalPath, currentPath));
                    restored++;
                }
                catch (Exception ex)
                {
                    logEntries?.Add((
                        "WARN",
                        $"{Path.GetFileName(originalPath)}：没能放回原目录（{ex.Message}）—— "
                        + "这一份仍是收拢过后的位置。"));
                }
            }

            return restored;
        }
        private static string BuildVolumeGroupAttemptSignature(VolumeGroup group)
        {
            var builder = new System.Text.StringBuilder(group.GroupKey);

            foreach (VolumeCandidate volume in group.Volumes)
            {
                builder.Append('|').Append(volume.Path).Append('|');

                try
                {
                    builder.Append(new FileInfo(volume.Path).Length);
                }
                catch
                {
                    builder.Append('-');
                }
            }

            return builder.ToString();
        }
        private void MarkStoppedBeforeExtract(ArchiveTask task, string status, string errorMessage)
        {
            /*
             * 这一轮没有任何产物、也没有任何校验：上一轮留下的**校验结论与机器终态**必须清干净
             * （任务对象是复用的：重试 / 再点一次）。
             *
             * 不清的话两头都会错：①收口那条判据（"校验没通过"才补 Failed）被上一轮的 `Passed`
             * 挡在门外；②上一轮的 `Succeeded` 会活到这一轮，汇总把一次**没做成**的处置算成成功
             * （不变量 6 的反面）。清成 `Pending` 而不是就地写 `Failed`，是为了让"终态由谁落"
             * 仍然只有收口一处（这里只说"这一单还没有结论"）。
             */
            task.IsOutputVerified = false;
            task.Outcome = TaskOutcome.Pending;

            task.MarkFailed(StatusText.OpWaiting, status, errorMessage);
        }

        /// <summary>
        /// <b>机器终态收口</b>（第 44 条追加；⚠ 唯一出口 —— 单任务收尾与批末补判都转调它）：
        /// 任务已经结束（<c>EndTime</c> 有值）而 <see cref="TaskOutcome"/> 还停在 <c>Pending</c>，
        /// 说明它走的是那些"只写了中文状态、没写终态"的失败分支（实测：识别不出格式的包
        /// 状态是「解压失败」，<c>Outcome</c> 却是 <c>Pending</c>，于是批末汇总把它算进"未处理"）。
        ///
        /// <para>汇总 / 失败清单 / 四道删除门读的都是这一位，⛔ 一律不许靠比对中文。
        /// 判据用"没通过输出校验"这条事实，只有真正的失败才会落进来。</para>
        ///
        /// <para>⚠ 2026-10-05：批末补判那一档（缺卷留到最后再判）是**在任务收尾之后**才落状态的，
        /// 它走不到原来那个 <c>finally</c> 里 ⇒ 少了这一句，真机那种"批末才报缺卷"的任务
        /// 会一直停在「未处理」（机器终态没落，汇总与诊断当场打架）。所以判据抽到这里共用。</para>
        /// </summary>
        /// <summary>
        /// 产物越界（不变量 4）：递归层已把它记成结构化事实（<see cref="RecursionLayerReport.LandingViolation"/>）
        /// ⇒ 结论必须写成「越出目标根目录」，⛔ 不许折成泛泛的"引擎解不开"（措辞与单层路逐字对齐）。
        /// </summary>
        private void ApplyLandingViolationNote(ArchiveTask task, RecursionResult result)
        {
            // 判据与措辞都搬进唯一出口 `Extraction/LandingViolationNote`（一次只搬这一个方法）。
            if (task == null || !LandingViolationNote.HasLandingViolation(result))
            {
                return;
            }

            task.ErrorMessage = LandingViolationNote.Compose(task.ErrorMessage);

            AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
        }

        private static void FinalizeOutcomeIfPending(ArchiveTask task, bool producedContent = false)
        {
            // 判据与落点都搬进唯一出口 `Extraction/OutcomeFinalizer`（一次只搬这一个方法）。
            OutcomeFinalizer.FinalizeIfPending(task, producedContent);
        }

        /// <summary>
        /// **真正调引擎之前**比一次：变了就当场停下（不变量 11）。
        ///
        /// <para><b>为什么落在这里</b>：<see cref="ExtractSingleTaskAsync"/> 是**所有**解压入口的
        /// 唯一收口 —— 一键处理、手动「只解压」、单包重试、危险模式自测走的都是它；
        /// 而"引擎"这条路只有它下游会走。放在这里，等于任何一个入口都不可能绕过去。</para>
        ///
        /// <para><b>停下来意味着什么（不变量 1 的红线照旧）</b>：引擎**一次都不会被调用**、
        /// 源包一个字节都不动、<c>其余物</c> 不生成、不发布任何产物、不留任何中间品。
        /// 状态落「源文件已变化」，<c>ErrorMessage</c> 里点名"哪一个文件、哪一项变了"并给出出路。</para>
        /// </summary>
        /// <returns>true = 已经拦下（调用方必须立刻 return，什么都不许做）。</returns>
        private bool StopIfSourceChanged(ArchiveTask task) => StopIfSourceChanged(task, out _);

        /// <summary>
        /// 与上面同一个判据，只是把"为什么"也交回给调用方（递归核心要拿它当停止理由）。
        /// <paramref name="reason"/> 为空串 = 没有变化，任务照常继续。
        /// </summary>
        private bool StopIfSourceChanged(ArchiveTask task, out string reason)
        {
            reason = string.Empty;

            if (task == null || !task.HasSourceSnapshot)
            {
                // 没有基准：不拦（调用方会先 EnsureSourceSnapshot 补上）。
                return false;
            }

            SourceChangeResult? comparison = task.CompareWithSourceSnapshot();

            if (comparison == null || !comparison.Changed)
            {
                return false;
            }

            reason = SourceFileSnapshot.DescribeChange(comparison);

            /*
             * 状态 / 进度 / 原因 / 结束时间四件事一起落（不变量 6 的反面同样成立：
             * 一件没做成的事不许看起来像做成了）。这里刻意**不碰** task.OutputPath：
             * 它可能还停在上一次的值，但对一个"压根没开工"的任务来说没有意义，
             * 而失败清单里的"位置"一行读的是 CurrentPath（源包在哪），那才是用户要去找的东西。
             */
            MarkStoppedBeforeExtract(task, StatusText.SourceChanged, reason);

            AppendLog("ERROR", $"源文件已变化，未开始解压：{task.FileName} —— {reason}");

            return true;
        }

        /// <summary>
        /// 递归核心那一侧的检查口（不变量 11 覆盖到每一层）。
        ///
        /// <para><b>只认第 0 层</b>，也就是用户给的源包：第 1 层起解的是**我们自己**从内层抠出来的
        /// 过程物（工作区里的文件），它们既不在源文件快照里、也不该被源文件的变化牵连 ——
        /// 拿源包的快照去比内层包，只会得出一句必然错误的"源文件已变化"。</para>
        ///
        /// <para>返回非 null = 已经拦下，递归核心会停止展开并把这句话当成本次的结论。</para>
        /// </summary>
        private Task<string?> CheckRootSourceUnchangedAsync(ArchiveTask task)
        {
            return Task.FromResult(StopIfSourceChanged(task, out string reason) ? reason : null);
        }

        /// <summary>
        /// 每个归档（**每一层**）最多真的试几个密码候选（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
        ///
        /// 为什么必须有：解压模式下一个候选 = **一次完整解压**。加密分卷的第二层，
        /// 几百条密码本的包会被逐个候选整包重解一遍（几百 MB 起、磁盘一直在写），
        /// 表现就是"几十分钟不动"，最后还可能把"没试完"报成"密码错误"。
        ///
        /// 到上限时的状态是 <see cref="StatusText.PasswordAttemptLimitReached"/>，**不是**"密码错误"。
        ///
        /// 值取自设置项 <see cref="AppSettings.MaxPasswordAttemptsPerLayer"/>（界面可改、能落盘）。
        /// 这里再夹一次只是兜底：设置对象不一定走过 <see cref="AppSettings.Normalize"/>（例如测试里直接
        /// <c>new AppSettings()</c>），而 0 或负数会让"一个候选都不试"直接变成密码错误 —— 那是最误导人的结论。
        /// （旧写法：这里是个常量 10，用户在设置界面改了不生效。）
        /// </summary>
        private int MaxPasswordAttemptsPerLayer =>
            Math.Clamp(Settings.MaxPasswordAttemptsPerLayer, 1, 1000);

        /// <summary>
        /// 解压前的资源预算上限（不变量 8），四条全部来自设置（用户 2026-09-25 第 36 条）。
        ///
        /// <para>为什么收口成一个属性：这套上限有**四个**用它的地方 —— ①解压前预检
        /// （<see cref="ResourceBudget.CheckBeforeExtract"/>）②产物事后核算 ③内嵌 ZIP 直读
        /// ④递归核心的累计上限。以前①③④各写各的默认值（预检与直读是 <c>ResourceBudgetOptions.Default</c>、
        /// 递归是 <c>RecursionLimits.Default</c> 的 50 GiB），用户在设置里改一处并不会同时生效 ——
        /// 现在只有这一个出口，口径不可能再分叉。</para>
        ///
        /// <para>旧写法：解压前预检直接用 <c>new ResourceBudget()</c>（= 硬编码 4 GiB 单文件 / 20 GiB 总量），
        /// 界面上一个字都没有 —— 他 12 GiB 的分卷包被拦下时看到的就是那句判决。</para>
        /// </summary>
        private ResourceBudgetOptions BudgetLimits => ResourceBudgetOptions.FromSettings(Settings);

        /// <summary>合并提示里最多列几个文件名（再多就让用户去看失败清单），别弹一个占满屏幕的框。</summary>
        private const int MaxPasswordFailureNamesInDialog = 10;

        private CancellationTokenSource? _operationCts;
        /// <summary>
        /// 正在跑的任务各自的取消源。
        ///
        /// <para>⚠ <b>必须加锁访问</b>（2026-09-22：默认并发由 1 提到 4 之后暴露的真缺陷）：
        /// 每个任务在**自己的线程**上加/删这个列表，而 <see cref="CancelCurrentTask"/> 在 UI 线程上读它。
        /// 旧的裸 <c>List</c> 写法在并发下会直接把内部数组搞坏 ——
        /// 实测到的现象是收尾时抛 <c>ArgumentOutOfRangeException</c>（<c>List.RemoveAt</c> 越界），
        /// 一个任务因此变成「未知错误」，而不是它本来的结论。</para>
        /// </summary>
        private readonly List<CancellationTokenSource> _runningTaskCts = new();

        private readonly object _runningTaskCtsLock = new();

        private void TrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Add(cts);
            }
        }

        private void UntrackRunningTask(CancellationTokenSource cts)
        {
            lock (_runningTaskCtsLock)
            {
                _runningTaskCts.Remove(cts);
            }
        }

        /// <summary>取一份快照（取消时**在锁外**逐个 Cancel：Cancel 会触发回调，不能拿着锁做）。</summary>
        private List<CancellationTokenSource> SnapshotRunningTasks()
        {
            lock (_runningTaskCtsLock)
            {
                return _runningTaskCts.ToList();
            }
        }

        private bool _isExtracting;

        /*
         * 本批因密码没通过而失败的任务（密码错误 / 达到密码尝试上限）。
         *
         * 旧逻辑在每个任务的循环体里各弹一次模态框，而且用的是**同步** Dispatcher.Invoke：
         * 50 个错包 = 50 次阻塞点击，批量跑不动。现在只在这里登记，
         * 批次结束后由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
         * 并发解压时多个任务会同时写它，所以必须加锁（而不是靠"反正都在 UI 线程上"）。
         */
        private readonly List<(string FileName, string Status)> _passwordFailures = new();
        private readonly object _passwordFailuresLock = new();

        /*
         * ===== 同名冲突（ConflictAction = Ask）的批内状态 =====
         *
         * 决策 D-4：用户的真实场景是 50–200+ 个包，逐个弹窗等于不可用。所以"询问"这一档必须做到
         * **本批只问一次**：用户答了「覆盖全部 / 跳过全部 / 全部自动重命名」之后，后面的冲突一律照办，
         * 一个字都不许再问。这三条状态就是这件事的全部记账（批内重置、并发下加锁）。
         *
         * · _batchConflictDecision —— "全部 X"：本批后续所有冲突的答案；
         * · _taskConflictDecision  —— "这一个"：同一个任务内后面的冲突沿用（一次聚合询问管整任务）；
         * · _conflictPromptUnavailable —— 无界面宿主（单元测试 / 控制台宿主）或问不出答案：
         *   记下来，后面的冲突不再尝试弹窗，直接走保守档并只写一次日志。
         */
        private readonly object _conflictDecisionLock = new();
        private ConflictDecision? _batchConflictDecision;
        private ConflictDecision? _taskConflictDecision;
        private bool _conflictPromptUnavailable;

        /// <summary>本批已经真的弹过几次询问（日志与排障用；正常情况下 ≤ 1）。</summary>
        private int _conflictPromptCount;

        /*
         * ===== 本批的"完成后打开输出目录"记账（设置项 OpenOutputFolderWhenDone，默认关）=====
         *
         * 为什么必须记账：设置开着的时候，如果每个任务成功都开一次资源管理器，
         * 一批 50–200 个包就会在用户桌面上炸出几十个窗口 —— 那不是"看一眼结果"，是骚扰。
         * 所以整批**只开一次**（第一个成功收尾的最外层任务），批开始时清零。
         *
         * 并发下多个任务会同时走到判断处，所以必须加锁（与 _passwordFailures 同一个理由）。
         */
        private readonly object _outputFolderLock = new();
        private bool _outputFolderOpenedThisBatch;

        /*
         * ===== 手动输入的密码（WinRAR 参考 §3 附注 / §2 H 组采纳项）=====
         *
         * 不变量 5 的红线：**只对本次运行有效、绝不落盘**。所以它只是一个字段 ——
         * 不进 AppSettings、不进密码列表、不进日志、不进报告，进程一退就没了。
         *
         * 为什么需要它：密码本没命中、统一密码也不对时，用户以前只能"改设置再重跑整批"。
         * 现在整批**一次性**问一次，输入的值当成本批所有任务的候选（排在空密码之后、
         * 密码本之前 —— 它比密码本更"新"，是用户刚给出的信息）。
         *
         * ⚠ <c>internal</c>（2026-10-05）：单测要钉住"它必须同时进**递归**那条路的候选表"——
         * 只靠公开入口造不出"本批手动输过密码"这个状态（无界面宿主不会弹那个框）。
         */
        internal readonly List<string> _manualBatchPasswords = new();

        /// <summary>本批是否已经问过手动密码（一次性：问过就不再问，无论用户填没填）。</summary>
        private bool _manualPasswordPrompted;

        /*
         * ===== 「一键处理 · 本次选项」的运行期覆盖（规格 docs/输出与整理模型.md §9）=====
         *
         * 面板选的落点 / 终端落法 / 源包处理**只对这一次一键处理有效**：
         * · 它是一个**每批一份、批结束就置空**的字段（不是静态、不进设置、不落盘）——
         *   「不勾存为默认就不许改设置文件」这条硬要求靠的就是"这里根本没有写设置的路径"；
         * · 同时只有一批能在跑（_isExtracting 守着），所以字段不会被两批同时用到；
         * · 手动「只解压」（地基路径）**不带**它：那条路径照旧完全按设置走。
         */
        private OneClickRunOptions? _runOptions;

        /// <summary>本批的"本次选项"（没有就是 null —— 地基路径、或没弹面板时的降级）。</summary>
        private OneClickRunOptions? RunOptions => _runOptions;

        /*
         * ===== 手动档「解压到当前文件夹」的运行期开关（用户 2026-09-27）=====
         *
         * 语义 = WinRAR 右键的「解压到当前文件夹」：`111\222.rar` → `111\内容物`（**不建包名那一层**）。
         * 三条边界写死在这里，别的路径一律不许打开它：
         * ① 只有①页那个显式按钮会传 true（一键处理 / 批量 / 续解一律 false）—— 用户红线：批量绝不摊平；
         * ② 与「本次选项」同寿：批首置入、`finally` 里连同 _runOptions 一起清掉，不进设置、不落盘；
         * ③ 同名冲突照走既定冲突档（用户拍板接受这个代价：摊平本来就更容易撞名）。
         */
        private bool _extractIntoSourceFolderThisRun;

        /*
         * ⛔ 原来这里有一个 `SourceDeleteFileSystemOverride`（"清理源包"的删除执行体，只给测试注入）。
         * 2026-09-25 第 32 条之后**管线里再没有任何一处调用 `SourceCleanupService`**
         * （删除统一走 `Storage/RestItemPurger`，它自己的执行体接缝在 `RecycleBinService` 那一层），
         * 所以这个接缝成了"看起来能影响删除、其实什么都不影响"的假接口 —— 一并删掉，
         * 免得以后有人拿它当"删除没发生"的证据。
         */

        /// <summary>
        /// 本批是否出现过密码类失败（密码错误 / 达到尝试上限）。
        /// 批次结束后的合并提示据此换一句话：出过密码问题时指引"再点一次、把密码输进去"，
        /// 没出过就不提（免得每次跑完都念一遍）。
        /// </summary>
        private bool _batchHadPasswordFailures;

        /*
         * ===== 空间规划 + 危险模式（用户 2026-09-22 需求）=====
         *
         * 三件互相咬合的东西，都在这一批里活着：
         * · _ledger         —— 空间预留账本：并发时"已经许出去多少空间"的唯一记账处。
         *                      没有它，5G 与 6G 两个包会各自看到"还剩 10G"然后一起开跑。
         * · _runtime        —— 逐任务的运行期记录（预留了多少、开工前的可用空间、危险模式删了多少）。
         *                      自测证据与"跳过报告"都从这里取数；key 是任务路径。
         * · _refinedEstimates —— 解压前那一遍 list 算出来的**精确**空间需求（按任务路径存）。
         *                      自测的空间曲线要用它的"内容物"那一项，而任务对象上没有这个字段
         *                      （Models\ArchiveTask.cs 不在本批授权范围内，也就不去动它）。
         */

        /// <summary>本批的空间预留账本（串行时也建，只是永远只有一个任务在账上）。</summary>
        private SpaceReservationLedger? _spaceLedger;

        private readonly object _spaceRuntimeLock = new();

        /*
         * ⚠ 两张表都用**任务对象本身**做键，不用路径 —— 这一点有实际后果：
         * 源包搬进其余物之后，协调器会把 task.CurrentPath 改写到新位置（决策 D-12 要求的回写）。
         * 用路径当键的话，同一个任务在"搬之前"与"搬之后"会落进两个不同的格子：
         * 自测读证据时拿到的是全新的一份（RestPurged=false、可用空间 -1），于是"明明删掉了却报没删"。
         * ArchiveTask 没有重写 Equals，字典默认就是引用相等 —— 正是这里要的语义。
         */
        private readonly Dictionary<ArchiveTask, ScheduledTaskRuntime> _spaceRuntime = new();

        /// <summary>
        /// 本次运行里每个任务**真正用的**工作区目录（<c>&lt;work&gt;\&lt;taskId&gt;</c>，由暂存目录反推）。
        ///
        /// <para>为什么必须记、不能事后重算：<see cref="PathService.BuildTaskWorkDirectory"/> 的 taskId 含
        /// **源路径哈希**，而任务收尾会把源包搬进 `其余物` 并回写 <c>task.CurrentPath</c> ——
        /// 重算出来的是**另一个**目录（<see cref="CleanupTaskWorkspaceDirectory"/> 的注释里记着这个坑：
        /// 实测近 1 GB 过程物因为重算而"清理"了个空）。空壳清理同样只能在**当时那个**目录上做。</para>
        ///
        /// <para>键用任务对象本身（理由与 <see cref="_spaceRuntime"/> 一样），条目在任务收尾时移除。</para>
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, string> _taskWorkspaceDirectories = new();

        /// <summary>
        /// 部分完成发布的候选（**失败路径**填、收尾处发布；见 <see cref="TryPublishPartialProductsAsync"/>）。
        ///
        /// <para>键用任务对象本身（理由与上面两张表一样）；条目在收尾时移除 ——
        /// 不清掉的话同一个任务对象重跑时会把上一轮的清单当成这一轮的（那是"拿旧识别结果解新文件"的翻版）。</para>
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, PartialPublishCandidate> _partialPublishCandidates = new();

        /// <summary>
        /// 本次运行里每个任务用过的**递归核心**（<see cref="RecursiveExtractor"/> 实例）。
        ///
        /// <para>为什么要在收尾处留着它：递归核心的逐层工作区不在任务工作区里，而是它自己的
        /// <c>&lt;work&gt;\recursive\&lt;taskId&gt;</c>（<see cref="RecursiveExtractor.ConfiguredWorkspaceRoot"/>
        /// 之下再挂一层 <c>recursive</c>）—— 那才是双层包里最占地方的一份（用户原话：
        /// "如果解压 40G，两层，解压失败有 80G 的卸载残留"）。它按结论清理的时机有两处：
        /// 成功那一支由递归核心自己在 <c>FinalizeRun</c> 里清；**失败 / 取消 / 部分完成**那一支
        /// 原本一律保留，现在按 <see cref="AppSettings.KeepFailedWorkspace"/> 决定清不清 ——
        /// 而那个决定只能在任务收尾时做（要等"这一批到底算不算成功"落定），
        /// 取消那一条路更是连 <c>ExtractAsync</c> 都没正常返回（抛 OperationCanceledException）。</para>
        ///
        /// <para>⚠ 一个任务一个实例（递归核心把"本次任务的工作区"记在实例上），键用任务对象本身
        /// （ArchiveTask 没重写 Equals，字典默认引用相等）；条目在收尾时移除。</para>
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, RecursiveExtractor> _taskRecursiveExtractors = new();

        /// <summary>
        /// 空间不足：本批因此被跳过（没启动）的任务。
        /// 批末要**如实报告**它们各自需要多少 —— 静默跳过是明令禁止的。
        /// </summary>
        private readonly List<(string Name, long RequiredBytes, long AvailableBytes, long ShortfallBytes)> _spaceBlockedTasks = new();

        /// <summary>
        /// 本批是不是**一键处理**（批首钉一次；<c>oneClickRun</c> 只在批首那个参数里，
        /// 而"中途撞上空间不足该不该弹提示"这件事发生在很深的地方，需要它当判据）。
        ///
        /// <para>为什么需要：一键档的批中间是**零弹窗**红线，而那条"空间不足"的纯提示恰恰是
        /// 唯一被允许的例外之一（纯提示 / 一个按钮 / 非模态 / 同一批一次）——
        /// 手动档保持原样（只写日志），所以要能把两档分开。</para>
        /// </summary>
        private bool _oneClickThisBatch;

        /// <summary>
        /// 本批**我们自己建出来的那个目标目录**（工作区要落在它里面，所以批首必须先建它）。
        ///
        /// <para>为什么要记：工作区改到目标目录里面之后（用户 2026-09-30），"最终目录连建都不该建"
        /// 这条老口径在实现上不可能再成立 —— 不先建它就没地方放工作区。但**用户看得见的残留**
        /// 一条都不许多：批尾如果这个目录里什么都没有（这一批什么都没解出来 / 全失败 / 被取消），
        /// 它就是**我们自己造的一个空壳**，必须自己收掉。</para>
        ///
        /// <para>⛔ 只在"确实是这一次建出来的"并且"现在一个条目都没有"这两条同时成立时才删
        /// （前者靠 <see cref="WorkspaceRootResolution.TargetDirectoryCreated"/>，
        /// 后者靠现枚举）—— 用户本来就有的目录、或者里面有我们删不掉的东西（目录联接点），
        /// 一律一个字节都不动。空串 = 这一批没建过任何目标目录。</para>
        /// </summary>
        private string _batchCreatedTargetDirectory = string.Empty;

        /// <summary>
        /// 本批那条"中途空间不足"的纯提示**弹过没有**（用户 2026-09-29 第 2 条：
        /// "同一批只弹一次（合并计数）"）。
        ///
        /// <para>后面的同类情况只写日志 —— 一批几十个包时逐个弹等于把批卡死在他面前。</para>
        /// </summary>
        private bool _spaceBlockedNoticeShown;

        /// <summary>
        /// 本批生效的「删除操作」档（<see cref="RestHandlingModes"/>；批首定一次）。
        ///
        /// <para>⚠ 批首定一次而不是每任务现读设置：同一批里不许"前半段按一个档、后半段按另一个"
        /// （用户可能中途去③页改）。<see cref="RestHandlingModes.Keep"/> 是默认档 ——
        /// 没经过批首的路径（例如单独解压一个包）永远按 Keep 走，不会顺手删东西。</para>
        /// </summary>
        private string _restHandlingThisBatch = RestHandlingModes.Keep;

        /// <summary>
        /// 本批走不走**「逐层回收」**（用户 2026-10-03 拍板；批首定一次）。
        ///
        /// <para><b>⛔ 2026-10-15 口径变更：与③页「删除操作」档位彻底解绑</b> —— 用户 2026-10-14 口径逐字
        /// 「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**（因为他们都是成功的情况下），
        /// 统一彻底删除」（见 <c>docs\大改动-2026-10-14-口径与冲突.md</c> §2.1 / §2.2 / §18.1）。
        /// 老口径（"只有彻底删除档才逐层回收"）已废：**成功链上的过程物一律当场永久删**，
        /// 不管用户选的是留下还是彻底删除 —— 它们是"成功过的那几层"，可以从留下的那一层重做
        /// （用户 2026-10-14 的论证）。</para>
        ///
        /// <para><b>⛔ 只在这里写一次</b>（AGENTS.md §9.5：同一件事的真值只允许有一个出口）——
        /// 续解层的每层回收（<c>PostProcessSuccessAsync</c>）、递归路的钩子
        /// （<see cref="OnRecursiveLayerReclaim"/>，挂载点读它）、链尾"只处理源包"
        /// （<c>CompleteRootSourcePackagesAfterChainAsync</c>）都只读这一个字段，
        /// 别处⛔ 不许再拿设置 / <see cref="RunOptions"/> 判一遍。</para>
        ///
        /// <para>⚠ 唯一的例外仍是**「空间不足」模式**：那一档有它自己的一支（连源包都在每层当场删），
        /// 批首把它置 false 避免抢活（见 <c>PrepareRestHandlingForBatch</c> 里那一支）。</para>
        ///
        /// <para>⚠ 与 <see cref="_restHandlingThisBatch"/> 同样的寿命：批尾**不清**（链尾钩子在批次返回之后
        /// 才跑，清掉会让链尾以为"这一批没有逐层回收"而再去搬已经不存在的内层包）；
        /// 下一批开工时 <c>PrepareRestHandlingForBatch</c> 会重新写一次。</para>
        /// </summary>
        private bool _layerReclaimThisBatch;

        /// <summary>
        /// **链内每一层"被用掉的那几个输入文件"**（递归钩子每层报上来的 `GroupFiles`）——
        /// 只记账、不动盘（用户 2026-10-12 口径：搬不能挂在"逐层"那一刻，见
        /// <see cref="CollectChainInnerPackagesIntoRestAsync"/> 末尾那一段"链尾集中"）。
        /// 写入点唯一 = <see cref="OnRecursiveLayerReclaim"/>；清空点 = 批首 `PrepareRestHandlingForBatch`。
        /// </summary>
        private readonly List<string> _chainInnerLayerInputs = new();

        /// <summary>
        /// 本批是不是按**「空间不足」模式**跑（批首定一次；用户 2026-09-27 拍板的模式）。
        ///
        /// <para>它是一个**运行期**开关（<see cref="MainViewModel.SpaceTightMode"/>，⛔ 不写设置、不记忆），
        /// 但一经开跑就**钉死在这一批上**：同一批里不许"前半段边解边删源包、后半段又不删了" ——
        /// 那会让空间账面与用户看到的行为前后矛盾。批首定一次、批内只读它。</para>
        ///
        /// <para>它一共改四件事（每一件都写在下面对应位置的注释里）：
        /// ① 并发档（忽略设置里的档与「全速」，按 <c>ExtractionScheduler.ResolveSpaceTightParallelCount</c>）；
        /// ② 排序（按净占用从小到大，先解"解完占地最少"的）；
        /// ③ 源包处理（✅ 定稿 + 校验通过 → **当场永久删除源包**，不进其余物）；
        /// ④ 删除操作（其余物 = 过程物，任务成功后彻底删除）。
        /// ⛔ 它**不动**任何设置：用户设置里的三档在这次运行里一个字节都不会被改写。</para>
        /// </summary>
        private bool _spaceTightThisBatch;

        /// <summary>
        /// 本批是不是按「空间不足」的**安全档**跑（「不删原包」勾着时；见
        /// <see cref="MainViewModel.SpaceTightKeepSource"/>）。
        ///
        /// <para>安全档与默认档的差别**只有一条**：源包一个字节都不动（不搬进其余物、也不删除）。
        /// 并发、排序、其余物删除这三件事两档完全一致 —— 用户要的正是"先看清空间怎么变，
        /// 再决定要不要开那个会删源包的档"（他的原话：怕"没成功而且原包也没有了"）。</para>
        /// </summary>
        private bool _spaceTightKeepSourceThisBatch;

        /// <summary>逐任务的运行期记账（并发下多个任务同时写，所以全部走锁）。</summary>
        private sealed class ScheduledTaskRuntime
        {
            /// <summary>当前账上给这个任务预留的字节数（开工时是粗估，拿到清单后可能被调整）。</summary>
            public long ReservedBytes;

            /// <summary>它开工前的可用空间（-1 = 取不到）。</summary>
            public long AvailableBeforeStart = -1;

            /// <summary>它收尾（含其余物处理）之后的可用空间（-1 = 取不到）。</summary>
            public long AvailableAfterFinish = -1;

            /// <summary>其余物是不是被彻底删掉了。</summary>
            public bool RestPurged;

            /// <summary>其余物处理（回收站 / 彻底删除）释放或腾出的字节数。</summary>
            public long PurgedBytes;

            /// <summary>其余物处理没做成时的原因（空 = 没出问题）。</summary>
            public string PurgeNote = string.Empty;
        }

        public ExtractionCoordinator(
            MainViewModel vm,
            IArchiveEngine archiveEngine,
            PasswordService passwordService,
            PathService pathService,
            DialogService dialogService,
            ISourceMoveFileSystem? sourceMoveFileSystem = null)
        {
            _vm = vm;
            _archiveEngine = archiveEngine;
            _passwordService = passwordService;
            _pathService = pathService;
            _dialogService = dialogService;
            _sourceMoveFileSystem = sourceMoveFileSystem ?? FileSystemSourceMoveFileSystem.Instance;
        }

        /// <summary>源包搬运用的文件系统（真实实现或测试注入的假实现）。</summary>
        private ISourceMoveFileSystem SourceMoveFileSystem => _sourceMoveFileSystem;

        private AppSettings Settings => _vm.Settings;

        /// <summary>
        /// 收尾重活的产物：后台线程只负责碰文件系统，结论与日志都通过它回传给 UI 线程，
        /// 免得后台线程去动界面集合 / 任务状态。
        /// </summary>
        private sealed class PostProcessWorkResult
        {
            public OutputVerificationResult Verification { get; init; } = new();

            public List<(string Level, string Message)> LogEntries { get; init; } = new();

            public bool BudgetExceeded { get; init; }

            public string BudgetMessage { get; init; } = string.Empty;

            /// <summary>
            /// 产物越出目标根目录的说明（非 null = **不承认**这次解压：不归集、不清理源包）。
            /// 它必须走"结论"这条路回传，而不是像旧实现那样只往日志里塞一行 ERROR。
            /// </summary>
            public string? LandingViolation { get; init; }

            public CollectResult? Collected { get; init; }

            /// <summary>定稿（暂存区 → 最终目录）的结果；没走到定稿这一步时为 null。</summary>
            public StageCommitResult? Commit { get; init; }

            /// <summary>
            /// 定稿**整体失败**（一个内容物都没能搬进最终目录）：与越界同一口径 —— 结论不成立。
            /// 与 <see cref="LandingViolation"/> 分开写，是为了让状态与原因各自说自己的事，
            /// 别让"越界"这个词出现在一句讲搬运失败的提示里。
            /// </summary>
            public bool CommitFailed { get; init; }

            public string CommitFailureMessage { get; init; } = string.Empty;

            /// <summary>
            /// 源包没能移入其余物（非 null = **任务要标成"部分完成"**）。
            ///
            /// 与越界/预算同一口径地走"结论"这条路回传：内容物已经好了，这件事只影响任务的完成度，
            /// 所以它**不**顶掉"解压成功"，但也绝不允许被静默吞掉（D-12：移动失败要写明原因）。
            /// </summary>
            public string? SourceMoveFailure { get; init; }
        }

        /// <summary>定稿搬运的一句话结论（给日志与任务字段用）。</summary>
        internal sealed class StageCommitResult
        {
            public bool Attempted { get; init; }

            /// <summary>搬运过程中被取消（已把搬过去的搬回来，最终目录不留半成品）。</summary>
            public bool Cancelled { get; init; }

            /// <summary>计划里的内容物搬运条数（一条可能是一整个目录）。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>真的搬过去的内容物条数。</summary>
            public int MovedContentCount { get; init; }

            /// <summary>真的搬过去的其余物条数。</summary>
            public int MovedProcessCount { get; init; }

            /// <summary>
            /// 因为同名冲突被**跳过**的条数（用户选了「跳过」/「跳过全部」）。
            ///
            /// 与"没能搬运"分开记：跳过是用户的选择，不是失败；但"一条内容物都没落位"时
            /// 必须靠它把任务落成「已跳过」而不是「解压成功」（不变量 6 的反面同样成立）。
            /// </summary>
            public int SkippedCount { get; init; }

            /// <summary>其中被跳过的是内容物的条数。</summary>
            public int SkippedContentCount { get; init; }

            /// <summary>因为用户选了「覆盖」而被顶掉的同名条目数。</summary>
            public int OverwrittenCount { get; init; }

            /// <summary>被顶掉的落点（写日志用：**哪些文件被覆盖**必须留痕）。</summary>
            public IReadOnlyList<string> OverwrittenPaths { get; init; } = Array.Empty<string>();

            /// <summary>内容物文件数（来自定稿计划，不是"搬了几条"）。</summary>
            public int ContentFileCount { get; init; }

            /// <summary>其余物目录（定稿计划算出来的那个）。源包搬运要用它当目标根（决策 D-10/D-12）。</summary>
            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            /// <summary>
            /// 内容物实际落地的那一层目录（＝定稿计划的 <c>DestinationDirectory</c>）。
            ///
            /// <para>第 35 条：续解产物必须落进**父任务内容物所在的那一层**，而不是父任务的 OutputPath ——
            /// 共用输出根模式下两者不是一个目录（前者是 <c>BBB\111\2222\</c>，后者是 <c>BBB\111</c>）。</para>
            /// </summary>
            public string ContentDirectory { get; init; } = string.Empty;

            public long ProcessArtifactBytes { get; init; }

            public int RenamedCount { get; init; }

            public int FailedCount { get; init; }

            public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

            public string Message { get; init; } = string.Empty;

            /// <summary>要带回 UI 线程写日志的行（后台线程不许直接写界面集合）。</summary>
            public List<(string Level, string Message)> LogEntries { get; init; } = new();
        }

        /// <summary>
        /// 定稿布局计划：直接包住 <see cref="ResultFinalizer"/> 的结论，只加三样执行阶段要用的东西 ——
        /// 其余物源路径的集合（执行时判断一条移动属于哪一类）与"这是不是一次失败的规划"。
        ///
        /// 为什么不再自己算一套相对路径：判定表（契约 §3.1：终端单文件直接放 / 多文件套一层 /
        /// 多重空目录提上来 / 单链塌缩）**只有一份实现**，就在 <see cref="ResultFinalizer"/> 里。
        /// 本类只负责"把暂存树描述给它、把它的计划执行掉"。
        /// </summary>
        internal sealed class FinalLayoutPlan
        {
            public bool Failed { get; init; }

            public string FailureReason { get; init; } = string.Empty;

            public FinalizeLayoutKind Layout { get; init; } = FinalizeLayoutKind.Empty;

            /// <summary>全部移动（**内容物在前、其余物在后**，按这个顺序执行）。</summary>
            public IReadOnlyList<PlannedMove> Moves { get; init; } = Array.Empty<PlannedMove>();

            /// <summary>计划里的内容物条数。</summary>
            public int PlannedContentCount { get; init; }

            /// <summary>哪几条是其余物（按 From 查，执行时用）。</summary>
            public HashSet<string> ProcessArtifactSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            public string ProcessArtifactDirectory { get; init; } = string.Empty;

            public long ProcessArtifactTotalSize { get; init; }

            public int ContentFileCount { get; init; }

            public string Summary { get; init; } = string.Empty;

            /// <summary>
            /// 这一单真的按**特定解压**跑了（判定表里"要套的那一层"没有套，规格 §3.5）。
            /// 日志与任务详情据此说清"这次为什么少了一层"（用户最恨"我以为它按默认跑的"）。
            /// </summary>
            public bool SpecialExtractionApplied { get; init; }

            /// <summary>
            /// 暂存区里**只有 0 字节产物**（用户 2026-09-24 铁证：密码不对时 7z 会写出 0 字节的桩文件）。
            ///
            /// <para>
            /// 这一档下 <see cref="Moves"/> 一定是空的（0 字节的东西不算产物，见
            /// <see cref="PlanFinalLayout"/>），所以"不定稿 / 不生成其余物 / 不搬源包"由控制流保证；
            /// 这个标志只用来把日志与结论说得更准（别让人读成"这个包本来就是空的"）。
            /// </para>
            /// </summary>
            public bool ZeroByteProductsOnly { get; init; }

            /// <summary>
            /// 这一层的计划作废，是因为**有一组分卷证明不了完整**（缺卷 / 疑缺卷 / 名字不标准）。
            ///
            /// <para>⚠ 2026-10-09：这一档**不是"失败"** —— 用户口径「连完整的都没有，这就不是程序的错误」、
            /// 「在最后一步之前碰到分卷缺失的就先跳过」⇒ 调用方据此把日志落成 **WARN + 先跳过**
            /// （原来一律写 ERROR「定稿布局规划失败」，用户点名那是红字误报）。
            /// ⛔ "什么都不动"这条判据一个字没放松：它照旧不定稿、不搬、不删。</para>
            /// </summary>
            public bool BlockedByIncompleteVolumeGroup { get; init; }

            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 解压成功之后的收尾：**定稿搬运** → 校验 → 归集 → 视开关清理源包（M3/契约 §2）。
        ///
        /// 顺序不能换：
        /// 0. 先定稿 —— 暂存区里的产物只有搬到最终目录才算"结果"，校验也必须以搬完之后的落点为对象
        ///    （契约 §2.2：定稿是搬运，不是"原地解压"）；
        /// 1. 再校验 —— 没有校验就没有"删除源包"的资格（AGENTS.md §9.5）；
        /// 2. 再归集 —— 归集是移动；先归集再校验会算不准（文件已经不在原输出目录）；
        /// 3. 最后清理源包 —— 它依赖前两步的结论。
        ///
        /// 任何一步失败都**不改变**"解压成功"这个结论，只是把结论写进日志与任务字段；
        /// 不要因为归集或清理失败就把任务标成失败 —— 用户的文件确实解出来了。
        ///
        /// 三个例外（它们不是"收尾失败"，而是**解压结论本身不成立**，必须顶掉"解压成功"）：
        /// ① 产物越出目标根目录（不变量 4）—— 不归集、不清理，状态落成失败；
        /// ② 产物超出资源预算 —— 同样是失败，并停止后续任务；
        /// ③ 定稿时被取消 —— 已经搬过去的会搬回来，最终目录不留半成品，状态落成"已取消"。
        ///
        /// 返回值就是这个语义：<c>true</c> = "解压成功"仍然成立；<c>false</c> = 已被上面三条否掉，
        /// 调用方**不许**再补一句"解压成功：xxx"（那会与任务状态自相矛盾，等于骗人）。
        ///
        /// 线程规则（这是 P0 修复的关键）：
        /// 收尾里的"全目录枚举 / 二次遍历 / 定稿搬运 / 归集移动 / 删除源包"**全是同步磁盘活**，
        /// 之前整段跑在 UI 线程上（整条管线没有 ConfigureAwait(false)，await 的续体全回 Dispatcher），
        /// 780MB 的包解出几千个文件时窗口彻底无响应 —— 与"抠出内嵌归档"是同一个坑，
        /// 那边已经改成 Task.Run 并写了注释，收尾这段是漏改。
        /// 现在重活统一进 <see cref="RunPostProcessWork"/> 交给 Task.Run，
        /// await 回来（没有 ConfigureAwait(false)，续体仍在 UI 上下文）才写任务状态与日志。
        ///
        /// 取消规则（P1）：收尾每一步之前都查令牌。用户按了「取消当前」就不许再移动产物、更不许删源包
        /// （AGENTS.md §9.5：取消、部分完成、校验失败一律不删）。取消由上层 catch 落成"已取消"，不得显示成功。
        /// </summary>
        /// <param name="stageDirectory">
        /// 本任务的暂存目录（入仓阶段的产物在这里，最终目录此时还是干净的）。
        /// </param>
        /// <param name="oneClickRun">
        /// 这一批是不是「一键处理」发起的整理路径（决策 D-9）。
        ///
        /// <para>
        /// ⚠ <b>它已经不再决定"要不要动源包"</b>（用户 2026-09-22 的新规则：两条路径一致，
        /// 成功 + 校验通过就把源包移入 <c>其余物</c>；见下方源包处理那一段）。
        /// 现在它只剩一个用处：**一键处理有续解链、手动「只解压」没有** ——
        /// 真实文件常常"第一层只出过程物"，那时一键处理要把源包搬运留到链结束后补做，
        /// 而单层的「只解压」当场就能按"定稿 + 校验通过"处理掉。
        /// </para>
        /// </param>
        /// <param name="knownList">
        /// 已经拿在手上的条目清单（可选）。给定时**不再问引擎列目录** ——
        /// 内嵌 ZIP 直读那条路就是这样：清单是解析出来的，而源文件 7z 根本打不开
        /// （前缀远超 8 MiB 的容忍上限）。留 null 时行为与以前逐字节一致。
        /// </param>
        /// <param name="recursion">
        /// 这一单是不是**递归展开**出来的（可选；一键处理那条路走 <see cref="RunRecursiveAsync"/>）。
        ///
        /// <para>
        /// ⚠ 它只影响一件事：**结果校验拿什么当预期**。递归展开超过一层时，
        /// "外层归档的清单"（第 0 层声明的东西）与"暂存区里最终的内容物"（叶子层解出来的东西）
        /// 本来就不是一回事 —— 拿前者核对后者**必然对不上**。
        /// 真机铁证（用户 2026-09-27 CCC，13 个包全部"解压失败"、内容其实完好）：
        /// `预期 2 个文件 / 157348161 字节，实际 7 个 / 157347461 字节`，
        /// 而日志同一段里写着"已完成 2 层递归解压…已发布 7 个文件"—— 7 个才是对的。
        /// </para>
        /// <para>
        /// 所以这一档改为"**没有可用的预期清单**"：只做非空 / 落点 / 预算三道校验
        /// （<see cref="OutputVerifier"/> 的规则 1–3），并在日志里说清为什么不算清单。
        /// 单层递归（只展开一层）仍然照旧拿外层清单核对 —— 那时两者确实是一回事。
        /// </para>
        /// </param>
        private async Task<bool> PostProcessSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string stageDirectory,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            CancellationToken cancellationToken,
            ArchiveListResult? knownList = null,
            RecursionResult? recursion = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * 递归展开超过一层 → 外层清单不再描述最终产物（见 recursion 参数说明）。
             *
             * ⚠ 2026-09-30 修（用户真机报的 bug）：老写法一见"展开 > 1 层"就把预期整份丢掉
             * （塞一份 `Success = false` 的空预期），于是 `ManifestCrossChecked = false` ⇒
             * L4「判不出完整性」⇒ 用户设的"源包放入其余物 + 其余物彻底删除"**整条失效**，
             * 源包与其余物一个字节都没动 —— 可两层**都**列得出清单。
             *
             * 现在改成：**第 0 层的清单只对第 0 层**（这条口径不变），但最终产物由**叶子层**产出，
             * 所以换用叶子层的清单去核对它。哪一层、为什么取不到，全由
             * <see cref="ChainManifestResolver"/> 这一个出口回答（⛔ 这里不许自己再判一遍层数）。
             */
            /*
             * **产物来源目录**：链停在某一层时，用**最后一个成功层**的产物目录
             * （用户口径：停在第 k 层时，上面已成功那一层的内容物要按「部分完成」发布；
             * 停下那一层按③页「删除操作」档处理）。
             * ⛔ 单层路（recursion == null）与跑完的链照旧用 stageDirectory，行为一个字不改。
             */
            // 判据与"取哪一层"都在唯一出口 `Extraction/ChainProductSource`（一次只搬这一个方法）。
            string productDirectory = ChainProductSource.Resolve(recursion, stageDirectory);
            ManifestExpectation manifestExpectation = ChainManifestResolver.Resolve(recursion, knownList);

            // 记账：L4 的分类器与所有删除闸门都从任务上读它（"哪一层 + 为什么没清单"要能一路带到日志）。
            task.ManifestExpectation = manifestExpectation;

            // 1) 校验：拿到引擎声明的条目数与总大小，和落盘结果对一遍。
            // 清单同样取自**真正解开的那份归档**：内嵌归档要拿抠出来的文件去列，源文件 7z 根本打不开。
            // 密码照旧传进去：加密头（-mhe）的包不给密码根本列不出清单，校验会直接退化成"没法比"。
            ArchiveListResult expected;

            if (manifestExpectation.HasManifest)
            {
                expected = manifestExpectation.Expected!;
            }
            else if (manifestExpectation.Source == ManifestExpectationSource.QueryArchive)
            {
                // 展开 0~1 层且手上没有清单：**行为与以前逐字节一致**（现问引擎列一份）。
                ArchiveListResult queried = await _archiveEngine.ListAsync(
                    ArchiveRequest.For(engineArchivePath, password),
                    cancellationToken);

                if (queried.Success)
                {
                    expected = queried;
                }
                else
                {
                    /*
                     * 连这一份都列不出来 ⇒ 预期确实取不到（L4 会落"判不出"，源包不动）。
                     * 记账换成"点名这一份归档 + 为什么 + 用户能做什么"，
                     * 于是后面那一行「为什么没删源包」能说清是哪一份、什么原因。
                     */
                    task.ManifestExpectation = ChainManifestResolver.DescribeOuterUnavailable(engineArchivePath, queried);

                    expected = new ArchiveListResult
                    {
                        Success = false,
                        ErrorType = queried.ErrorType,
                        Message = task.ManifestExpectation.UnavailableReason
                    };
                }
            }
            else
            {
                /*
                 * 真的取不到清单（叶子层列不出来 / 没有任何一层留下清单）：
                 * 给一份 Success = false 的"空预期"，OutputVerifier 会走"只做非空校验"那一支，
                 * L4 落「判不出」⇒ 源包一个字节都不动。
                 *
                 * Message 带上"哪一层 + 为什么" —— 它就是 L4 那句"无法确认完整性"后面的细节，
                 * 用户按这句话就能知道下一步该做什么（用户 2026-09-30：日志不许再让人猜）。
                 */
                expected = new ArchiveListResult
                {
                    Success = false,
                    Message = manifestExpectation.UnavailableReason
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 开关在进后台之前读一次：设置是用户可改的，别让后台线程读到半路改掉的值。
            bool collectResults = Settings.CollectResultsToDirectory;
            string collectTargetDirectory = Settings.CollectTargetDirectory;

            /*
             * 源包处理（决策 D-9，一次读清、三档互斥）。
             *
             * **两条路径一致**（用户 2026-09-22 拍板，推翻上一版"地基路径永远不动源包"）：
             * 不论是一键处理还是手动「只解压」，源包档位都取自 SourceHandling，
             * 三条前提（内容物已定稿 + 输出校验通过 + 未取消 + 属于本任务分卷组）由
             * MoveSourcePackageIntoRest 统一把关。用户原话：
             * 「如果成功了你就直接将源包放在其余物里面」「地基路径也照这条走」。
             *
             * 档位语义（**2026-09-25 第 32 条收敛成两档**）：
             * · MoveToRest（默认）—— 成功 + 校验通过就把整组源包移入其余物；
             * · KeepInPlace —— 一个字节都不搬（"传统解压器"语义的出口）。
             *
             * ⛔ 原来还有第三档 DeleteAfterVerify（校验通过后删源包），已按用户要求删掉：
             * 要删源包就选"放入其余物" + ③页「删除操作」= 回收站 / 彻底删除 ——
             * 那条路由 RunRestHandlingAsync 在收尾之后统一处理（回收站可还原，彻底删除直接省空间）。
             * 手动档那条更老的开关 DeleteSourceAfterExtract 也一并退役（用户原话：
             * "手动档的操作就和选项卡里面的一致"）：两条路径现在读**同一套**设置。
             *
             * ⚠ 「本次选项」里选过源包处理时**以它为准**（规格 §9.2 硬要求①：覆盖只对本次有效）：
             * RunOptions 只在"一键处理这一批"里非空，地基路径永远是 null → 照旧读设置。
             */
            SourceHandlingMode sourceHandling = RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            /*
             * 终端落法（内容物最后一层）**已退役**（用户 2026-09-27："这个可以删除掉，默认就是这样的情况"）：
             * 现在固定成"保留归档自带的那一层 + 外面永远裹一层包名目录"，
             * 也就是原来那一档的 KeepLastFolder —— 参数仍然传下去（`ResultFinalizer.Plan` 的契约没变），
             * 但设置项与界面入口都删掉了。想塌掉"包自带的那一层"只能用**特定解压**。
             */
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder;

            /*
             * 特定解压（规格 §3.5，用户 2026-09-24 拍板）：与终端落法同一处、同一时机读一次。
             *
             * 快照只读一次的原因与上面那条一样：一次一键处理会跨很多任务、很多轮（续解最多 10 层），
             * 用户跑到一半去改设置**不许**影响这一批（否则同一个包的前后两层会按两套规则落）。
             * 总开关关着时快照就是 Off —— 规则清单里写什么都不参与运算，
             * 行为与加这条功能之前逐字相同（这是用户点名要的那条保证）。
             */
            SpecialExtractionPlan specialExtraction =
                SpecialExtractionPlan.FromSettings(Settings);

            if (specialExtraction.IsActive)
            {
                // 可追溯（用户 2026-09-24）：这一单按哪条特定规则跑，日志里必须看得见。
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.SpecialExtractionAppliedLogFormat,
                        task.FileName,
                        specialExtraction.RuleNames));
            }

            /*
             * 同名冲突的决定：Ask 档**必须在动最终目录之前**拿到答案（"暂停该任务"就发生在这里）。
             *
             * 位置刻意放在收尾重活之前、并且只在本任务第一次撞上冲突时问：
             * · 预检是磁盘活（要算一遍定稿计划 + 判每个落点是否存在）→ 走 Task.Run，UI 线程不碰盘；
             * · 询问本身是 UI 活 → 走 DialogService 的异步重载（可取消、无界面宿主不弹窗、不用同步 Invoke）；
             * · 非 Ask 档一个字都不问，直接由档位推出结论（默认 AutoRename，与既有行为一致）。
             *
             * 已经答过"全部 X"或是本任务已经答过"这一个"时（GetEffectiveConflictDecision 非空），
             * 连预检都不做 —— 决策 D-4：50–200+ 个包逐个问等于不可用。
             */
            string conflictAction = ConflictActionValue;
            ConflictDecision? conflictDecision = null;

            if (ConflictActions.IsAsk(conflictAction))
            {
                ConflictDecision? decided = GetEffectiveConflictDecision();

                if (decided == null && !IsConflictPromptUnavailable)
                {
                    ConflictPrecheck precheck = await Task.Run(
                        () => PrecheckFinalLayoutConflicts(
                            task,
                            productDirectory,
                            task.OutputPath,
                            sharedOutputRoot,
                            terminalLayout,
                            specialExtraction,
                            recursion),
                        cancellationToken);

                    if (precheck.TotalCount > 0)
                    {
                        string message =
                            $"定稿时发现 {precheck.TotalCount} 个同名冲突（目标已经存在），" +
                            (precheck.ContentCount > 0
                                ? $"其中 {precheck.ContentCount} 个是内容物。"
                                : "都在其余物里。") +
                            "请选择同名时怎么处理。";

                        decided = await AskConflictAsync(
                            task,
                            message,
                            BuildConflictDetail(precheck),
                            cancellationToken);
                    }
                }

                // 问不到答案（无界面宿主 / 超时）→ 保守档：自动重命名落位，绝不覆盖（不变量 3）。
                conflictDecision = decided ?? ConflictDecision.Conservative;
            }

            PostProcessWorkResult work = await Task.Run(
                () => RunPostProcessWork(
                    task,
                    productDirectory,
                    expected,
                    collectResults,
                    collectTargetDirectory,
                    sourceHandling,
                    sharedOutputRoot,
                    terminalLayout,
                    oneClickRun,
                    conflictAction,
                    conflictDecision,
                    cancellationToken,
                    specialExtraction,
                    recursion),
                cancellationToken);

            // 回到 UI 线程：只做状态与日志，不再碰大盘。
            foreach ((string level, string message) in work.LogEntries)
            {
                AppendLog(level, message);
            }

            /*
             * 定稿被取消：产物已经搬回暂存区，最终目录里没有半成品。
             * 这里必须抛出去让上层落成"已取消"（不变量 6：取消不得显示成功），
             * 而不是 return false —— 那会被上层当成"结论不成立"，任务变成"解压失败"，
             * 用户看到的原因就成了"失败"，但他的操作其实是取消。
             */
            if (work.Commit?.Cancelled == true)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (work.BudgetExceeded)
            {
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = work.BudgetMessage;
                task.Outcome = TaskOutcome.Failed;

                // 不发布、不清理：产物留在原地让用户自己判断，源文件更不能删。
                StopAfterCurrent();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(work.LandingViolation))
            {
                /*
                 * 产物越界（不变量 4）：这不是"收尾出的小问题"，而是**这次解压的结果不能承认**。
                 *
                 * 旧实现只写了一行 ERROR 日志：任务照样是"解压成功"，产物照样被归集走、源包照样被删 ——
                 * 用户的文件已经写到目标根之外了，程序却报告一切正常。现在与递归路径同一口径：
                 * 状态落成失败、原因写进 ErrorMessage，归集与清理在 RunPostProcessWork 里就已经不做了
                 * （它在越界处直接 return，这里也**不允许**补做任何一步）。
                 */
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.LandingViolation;

                // 结论不成立时不许在详情里显示"输出校验通过"。
                task.IsOutputVerified = false;
                task.Outcome = TaskOutcome.Failed;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            if (work.CommitFailed)
            {
                // 定稿一个内容物都没搬过去：产物还在暂存区，最终目录是空的 —— 不能报成功（不变量 6）。
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.CommitFailureMessage;
                task.IsOutputVerified = false;
                task.Outcome = TaskOutcome.Failed;
                task.LastUpdatedTime = DateTime.Now;

                /*
                 * 这一类失败的事实位（唯一写入点）：批末诊断靠它把"定稿搬运失败"单独点出来 ——
                 * 它的原因**不是引擎报的**，⛔ 不许被指去"看引擎原话"（用户 2026-10-04 真机）。
                 */
                task.CommitMoveFailed = true;

                return false;
            }

            /*
             * 同名冲突全部选了「跳过」：一个内容物都没落位。
             *
             * 这是"用户的选择"，不是失败，但**绝不能报成功**（不变量 6 的反面同样成立：跑完了没产物，
             * 说成"解压成功"就是骗人 —— 用户会去那个目录里找一份根本不存在的产物）。
             * 同时也不能清理工作区：产物还在暂存目录里，那是用户唯一的一份。
             */
            if (work.Commit is { Attempted: true, MovedContentCount: 0, FailedCount: 0, SkippedContentCount: > 0 } skipped
                && skipped.PlannedContentCount > 0)
            {
                task.Status = StatusText.Skipped;
                task.Operation = StatusText.OpSkip;
                task.ProgressText = StatusText.ProgressSkipped;
                task.ErrorMessage = "同名冲突按你的选择跳过：产物仍留在暂存目录，未写入输出目录。";
                task.IsOutputVerified = false;
                task.Outcome = TaskOutcome.Skipped;
                task.LastUpdatedTime = DateTime.Now;

                return false;
            }

            task.IsOutputVerified = work.Verification.Verified;

            // L4 的第二个事实：这一次"通过"是**核对过清单**还是**只做了非空底线校验**（判不出完整性）。
            task.OutputManifestCrossChecked = work.Verification.ManifestCrossChecked;

            /*
             * ===== 产物校验未通过 = **结论本身不成立**（用户 2026-09-24 铁证，不变量 6）=====
             *
             * 这是本次修复的第二道闸（第一道在候选循环里：判否就地换下一个候选密码）。
             * 它管的是**绕不过去的那几条路**：递归、内嵌 ZIP 直读、以及"先测试再解压"那一支 ——
             * 那些路都不会经过候选循环，所以判否必须在这里落地。
             *
             * 旧行为（本轮修掉）：校验判否只写进 `IsOutputVerified` 与 `VerifyMessage`，
             * 任务状态**照样是「解压成功」**，日志照样打印"解压成功：xxx"，
             * 汇总照样把它算进成功数 —— 用户看到的是一句彻头彻尾的假话。
             *
             * 现在的口径与"越界 / 超预算 / 定稿失败"完全一致：状态落失败、原因带上预期与实际的三个数字、
             * 不定稿之外的任何一步都不补做（源包在 RunPostProcessWork 里已经因为校验没过没被动过），
             * 机器终态落 Failed（删源 / 搬源 / 续解 / 危险模式删除四条路都会读它）。
             */
            if (!work.Verification.Verified)
            {
                /*
                 * ⚠ 2026-10-15（**机器终态缺口**，用户测试逮到）：用户按了取消的时候，这一档不许落「解压失败」。
                 *
                 * 现场（取证逐字，`WorkspaceRootTests.取消且一个文件都没有_空壳工作区被删掉`）：
                 * 测试在"列目录"那一步按下「取消当前」、引擎一个文件都没写 ⇒ 收尾读到空目录 ⇒
                 * 日志只有 `产物校验未通过：输出目录是空目录，没有产物` ⇒ `解压失败 ｜ 校验未通过`，
                 * **整段日志一次都没提"取消"** ⇒ 用户按的是取消，①页与批末汇总却把它算成**失败**。
                 *
                 * 口径（AGENTS.md 不变量 1 + 用户 2026-10-14）：「失败 / 部分完成 / 取消 ⇒ 源包原地不动、
                 * 其余物不生成」—— 取消是**用户的选择**，不是失败；它必须和
                 * `RecursiveExtractor` 的 `RecursionStopReason.UserCancelled` 那一支（本文件 `:7575`）
                 * 落**同一套**机器终态。
                 *
                 * ⛔ 只改"这一次跑该落什么结论"：校验事实（`VerifyMessage` / `IsOutputVerified`）照旧写进去，
                 * 源包与产物一个字节都不动（下面那三行以后的分支本来也不会跑到），
                 * ⛔ 不动判据、不放宽任何闸门。
                 */
                bool cancelled = IsStopping
                                 || _operationCts?.IsCancellationRequested == true
                                 || cancellationToken.IsCancellationRequested;

                if (cancelled)
                {
                    // 取消支整族搬进唯一出口 `Extraction/PostProcessCancelledWriter`（一次只搬这一个功能族）。
                    PostProcessCancelledWriter.Write(task, work.Verification.FailureMessage, AppendLog);
                    return false;
                }

                // 这一支整族搬进唯一出口 `Extraction/PostProcessFailureWriter`（一次只搬这一个功能族）。
                PostProcessFailureWriter.Write(task, work.Verification.FailureMessage, AppendLog);
                return false;
            }

            // 把"实际输出到别处"这件事写进任务对象：输出目录被自动改名时，用户必须在任务上看得见，
            // 不能只在日志里留一句就过去（AGENTS.md 不变量 6 的同一精神：结论不能静默）。
            string verifyMessage = string.IsNullOrWhiteSpace(outputRedirectNote)
                ? work.Verification.Message
                : $"{work.Verification.Message}；{outputRedirectNote}";

            /*
             * L4 的那一行证据（用户 2026-09-30：日志不许再让人猜）。
             * 位置刻意在"校验通过"这条分支之内：判否那一支上面已经带着原因 return 了。
             */
            AppendCompletenessEvidence(task, work.Verification, sourceHandling);

            // 定稿里搬不动的文件也必须让用户看得见：产物还在暂存区，不在他打开的那个目录里。
            if (work.Commit is { FailedCount: > 0 })
            {
                verifyMessage += $"；定稿时有 {work.Commit.FailedCount} 个文件没能搬到最终目录" +
                                 $"（{string.Join("、", work.Commit.Failures.Take(5))}），它们仍在暂存区。";
            }

            /*
             * 覆盖了哪些落点也要写进任务结论（不只写日志）。
             *
             * 覆盖是不可逆的（旧文件按两阶段落位删掉了），用户回头找"我原来那份去哪了"时，
             * 日志与任务详情必须都能给出答案 —— 这就是"真正覆盖前要留痕"的落点。
             */
            if (work.Commit is { OverwrittenCount: > 0 } overwriteCommit)
            {
                verifyMessage += $"；同名冲突按你的选择覆盖了 {overwriteCommit.OverwrittenCount} 项" +
                                 $"（{string.Join("、", overwriteCommit.OverwrittenPaths.Take(5))}）";
            }

            task.VerifyMessage = verifyMessage;

            /*
             * 源包没能移入其余物（决策 D-12）：内容物已经好了，所以**不**改"解压成功"这个结论，
             * 但任务整体没做完 —— 按用户明确要求标成「部分完成」并写明原因。
             * 不变量 6 的反面同样成立：跑了一半的事不许只报成功。
             */
            if (!string.IsNullOrWhiteSpace(work.SourceMoveFailure))
            {
                task.Status = StatusText.PartiallyCompleted;
                task.ErrorMessage = work.SourceMoveFailure;
                task.VerifyMessage = $"{verifyMessage}；{work.SourceMoveFailure}";
                task.Outcome = TaskOutcome.PartiallyCompleted;
            }

            if (work.Collected != null && work.Collected.Success)
            {
                task.CollectedPath = work.Collected.DestinationPath;
            }

            /*
             * 记下**这一轮定稿实际使用的其余物目录**。
             *
             * 为什么必须记：真实文件（222.mp4 = 假 MP4 头 + 尾部 ZIP + 内层加密分卷）第一层只出过程物，
             * 源包搬运要留到整条续解链跑完之后补做 —— 那一刻已经不在这一轮的收尾里了，
             * 而"其余物到底在哪"这个事实只有定稿计划知道（归集之后还会整体平移一次）。
             * 现场重算路径会在三处出错：内容物层正好也叫「其余物」时的 `其余物(1)`、
             * 共用输出根模式下按包名分的那一层、以及归集把整个目录搬走之后的落点。
             */
            if (work.Commit != null)
            {
                string restDirectory = ResolveRestDirectoryAfterCollect(work.Commit, work.Collected);

                if (!string.IsNullOrWhiteSpace(restDirectory))
                {
                    task.RestDirectoryPath = restDirectory;
                }

                /*
                 * 内容物实际落地的那一层目录（第 35 条）：续解产物的落点要跟着它走。
                 * 同样要按归集后的位置换算 —— 归集把整个产物目录搬走了，旧路径已经不在了。
                 */
                string contentDirectory = ResolveContentDirectoryAfterCollect(work.Commit, work.Collected);

                if (!string.IsNullOrWhiteSpace(contentDirectory))
                {
                    task.ContentDirectoryPath = contentDirectory;
                }

                /*
                 * "这一层到底产出了内容物没有"（2026-09-27 落点模型 v2）：续解层"该不该建包名目录"的
                 * 判据里要用它 —— 只出过程物的过路层在简洁档下不建层，而出了内容物就说明是分支、照建。
                 *
                 * ⚠ 必须记**文件数**，不能拿 `ContentDirectoryPath` 非空当证据：那一格是"内容物层在哪"，
                 * 只出过程物的层也会被算成落点目录本身（真机形状：destDir 里只剩一个 `其余物\`）——
                 * 实测拿它当判据时，简洁档被判成了忠实档（端到端用例当场红）。
                 */
                task.ContentFileCount = work.Commit.ContentFileCount;
            }

            /*
             * ===== 跨链收卷：**单层路也要做**（用户 2026-10-05：「赶紧同步」）=====
             *
             * 单层路（②页「只解当前这一层」，以及一键处理的轮次续解**每一轮**）解出来的东西是**成品**、
             * 直接落在用户目录里 —— 真机 CCCC 的形状下，`111(2)_.zip` 解出来的正是这一组的第 1 片
             * （名字还脏，真机是 `111.z0删除1`），末片则由同批另一个包解出来。
             * 这一段把它们按规范卷名接进"这一组"的目录，于是批末那一站
             * （<see cref="RecheckDeferredVolumeDeficits"/>）能照常把整组收齐、解开。
             *
             * ⛔ 只扫**这一单刚发布的那一层**（不递归：§8 不替用户满盘找）；⛔ 只硬链接
             * （零字节、不改名、不搬、不覆盖）；判据与执行体仍然只有一份
             * （<see cref="TryAdoptUnresolvedVolumePiece"/>，与递归路**同一份**）。
             */
            if (work.Verification.Verified)
            {
                AdoptPublishedVolumePieces(task);
            }

            /*
             * 中间工作区清理（P1，端到端验收实测一次成功就留下近 1 GB 垃圾）。
             *
             * 位置与条件都在这里定死：**解压成功 + 输出校验通过 + 没被取消**。
             * 越界 / 超预算在上面已经 return（结论不成立）；取消会让上面的 await 抛 OperationCanceledException；
             * 校验没过时产物可能不全，工作区里的过程物是用户唯一的线索 —— 一律不删。
             *
             * 入仓（stage）之后这里更要紧了：暂存目录就在本任务的工作区里，产物已经定稿搬走，
             * 剩下的全是可再生的过程物。
             */
            if (work.Verification.Verified && !cancellationToken.IsCancellationRequested)
            {
                CleanupTaskWorkspaceDirectory(task, stageDirectory);
            }

            /*
             * 「定稿完成后打开输出目录」（设置项 OpenOutputFolderWhenDone，**默认关**）。
             *
             * 触发条件刻意收得最紧（四条全中才开）：
             * ① 这一批还没开过（TryOpenOutputFolderOnce 里的批记账 —— 50–200 个包每个都开一次是骚扰）；
             * ② 不是续解出来的内层包（它们与父任务共用同一个目录，反复打开就是刷屏）；
             * ③ 内容物已定稿（走到这一行说明定稿分支都没 return，见上面的越界/超预算/定稿失败早退）；
             * ④ 输出校验通过 + 没被取消。
             *
             * 失败 / 取消 / 部分完成一律不开：打开一个空目录只会误导用户（他以为东西在里面）。
             * 打开动作本身**只开文件夹，不置前、不最大化、不抢焦点**（AGENTS.md §13）。
             *
             * 整段包着 try/catch：这是收尾的最后一步，一个"打开资源管理器失败"绝不允许
             * 把已经成功的解压拖成异常（结论已经写在任务上了）。
             */
            try
            {
                if (work.Verification.Verified &&
                    !cancellationToken.IsCancellationRequested &&
                    task.Status == StatusText.ExtractSuccess)
                {
                    TryOpenOutputFolderOnce(task);
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.LogName}：打开输出目录失败（不影响解压结论）：{ex.Message}");
            }

            // 走到这里说明"解压成功"这个结论没有被越界 / 超预算 / 校验未通过顶掉。
            //
            // 机器终态在这里定稿：`Succeeded` 是**唯一**允许"删源 / 搬源 / 续解 / 危险模式删其余物"的档，
            // 而它只在"产物已定稿 + 输出校验通过 + 没有被上面任何一条顶掉"时才写 ——
            // 也就是说那四条路从此读的是**事实**，不是后来可能被人改掉的 `Status` 字符串。
            //
            // 判据是**上面的结论字段**本身（源包没搬成就不是完成），不是拿 Status 去比字符串。
            task.Outcome = string.IsNullOrWhiteSpace(work.SourceMoveFailure)
                ? TaskOutcome.Succeeded
                : TaskOutcome.PartiallyCompleted;

            /*
             * 收尾时把进度**补到 100% 并停表**（用户 2026-09-27 真机："上面怎么都显示解压成功 99%，
             * 连一个 100% 的都没有"）。
             *
             * 两个原因叠在一起：
             * ① 引擎报的最后一帧常常是 99（`Everything is Ok` 之前的那一次），任务其实已经成功；
             * ② 成功这条路以前**不写 EndTime**，于是 HasLiveProgress 一直为真 ——
             *    列表里那一格永远停在"解压成功 99%"，而"耗时"还会一直往上涨（UpdateElapsedText 拿 Now 兜底）。
             * 现在：成功的任务进度补成 100、EndTime 落定 → 那一格显示"解压成功 100%"、耗时不再变。
             * ⛔ 只对**成功**补 100：部分完成 / 失败 / 取消保持原样，免得出现"解压失败 100%"这种自相矛盾。
             */
            if (task.Outcome == TaskOutcome.Succeeded)
            {
                task.ApplyProgress(100, null);
            }

            if (!task.EndTime.HasValue)
            {
                task.EndTime = DateTime.Now;
                task.UpdateElapsedText();
            }

            return true;
        }

        /// <summary>
        /// 收尾重活本体：落点校验 → 结果校验 → 预算事后判定 → **定稿搬运** → 归集 → 处理源包。
        ///
        /// 校验与预算量的都是**暂存区**：那才是引擎真正写盘的地方（契约 §2.1）。
        /// 最终目录此时还是干净的，只有定稿那一步会往里面写东西。
        ///
        /// **只允许在后台线程上跑**（见 <see cref="PostProcessSuccessAsync"/> 的线程规则）：
        /// 这里只碰文件系统，不写任务状态、不写界面日志集合，要说的都放进 LogEntries 回传。
        /// </summary>
        /// <param name="oneClickRun">
        /// 是不是「一键处理」发起的整理路径。**只影响一件事**：一键处理有续解链，
        /// 所以"本轮没有内容物"时源包搬运要记成"留到链结束后补搬"；
        /// 手动「只解压」是单层路径，没有链可等，当场按"定稿 + 校验通过"处理。
        /// </param>
        private PostProcessWorkResult RunPostProcessWork(
            ArchiveTask task,
            string stageDirectory,
            ArchiveListResult expected,
            bool collectResults,
            string collectTargetDirectory,
            SourceHandlingMode sourceHandling,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            bool oneClickRun,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var logEntries = new List<(string Level, string Message)>();

            cancellationToken.ThrowIfCancellationRequested();

            PostProcessStartedForTests?.Invoke();

            /*
             * ⚠ 2026-09-30（用户真机报的 bug）：**不再把"取不到清单"折成 null**。
             * null 会把"为什么取不到"（<see cref="ChainManifestResolver"/> 点名的那一层与原因）
             * 一起丢掉，用户只能看到一句"未取得预期条目数"。
             * <see cref="OutputVerifier.Verify"/> 内部对 null 与 Success=false 的处理逐字相同
             * （两条规则都先判 Success），所以这一改**只多带一句话，不改任何判定**。
             */
            OutputVerificationResult verification = OutputVerifier.Verify(stageDirectory, expected);

            /*
             * ⛔ 「只出了源包自己那一个垃圾」—— 收尾这一步的最后一道闸门（用户 2026-09-28 红检实测）。
             *
             * 现场：**单个**被改烂名字的 7z 第一卷（同目录里没有能配对的后续卷）会被 7-Zip 当成
             * 「通用分片流（Split）」解掉：退出码 0，清单里只有一条，**那一条就是文件自己**，
             * 于是产物 = 一个与源包等大的垃圾文件，名字正好是源包名去掉末尾那段分片号。
             * 老口径下"预期 1 条 / 实际 1 个文件、字节数也对得上" → 判**通过** → 报「解压成功」，
             * 而用户的输出目录里根本没有包里的内容物 —— 这正是"部分成功不得显示为成功"被违反的那一次。
             *
             * 为什么写盘之前那道闸门（RawSplitStreamDetector.IsBrokenVolumeChain）不够：
             * 它要引擎**列得出清单**才生效，而列出清单失败 / 直读 / 递归这些岔路上引擎照样会这么干。
             * 这里量的是**产物形状**，与清单无关，所以是最后能拦住它的地方。
             *
             * 判据只有一处（RawSplitStreamDetector.LooksLikeSplitStreamEcho，纯函数、可单独测），
             * 这一支**直接 return**：不定稿（输出目录里不会多出一个垃圾内容物）、不归集、
             * 不生成其余物、更不动源包 —— 那几条不可逆动作全部以"校验通过"为前提，这里把前提掐掉。
             */
            if (verification.Verified
                && RawSplitStreamDetector.LooksLikeSplitStreamEcho(
                    task.CurrentPath,
                    task.IsArchive
                        && !string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase),
                    stageDirectory))
            {
                string echoMessage =
                    "校验未通过：解出来的是一个与源包等大的「通用分片」垃圾 —— 它的名字与字节数都和源包本身一样"
                    + "（引擎没找到这一组的后续卷，只把这一卷当成一段分片拼了一遍）。"
                    + "这不是包里的内容物：任务按失败结案，不落「解压成功」；"
                    + "不定稿（输出目录里不会多出这个垃圾文件）、不生成其余物、源包一个字节都不动。"
                    + "请把这一组的后续卷放回同目录（或先按内容级卷号把整组名字改回标准名）再重试。";

                logEntries.Add(("ERROR", $"{task.LogName}：{echoMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = new OutputVerificationResult
                    {
                        Verified = false,
                        Message = echoMessage
                    },
                    LogEntries = logEntries
                };
            }

            /*
             * 解压后落点校验（第二道防线，不变量 4）：产物必须都落在目标根目录之内。
             *
             * 量的是**暂存区**：引擎写盘的地方就是它（契约 §2.1）。落点校验必须在定稿**之前**做，
             * 否则越界的产物已经被搬进最终目录，再报"拒绝承认"就晚了 —— 用户目录里已经多了东西。
             *
             * 旧写法（本轮修掉）：越界时只写一行 ERROR 日志，**结论照样是"解压成功"**，
             * 而且照样归集产物、照样清理源包 —— 等于把不变量 4 降级成一条没人看的提示：
             * 越界的产物被搬进结果目录、源包被删掉，程序却报一切正常。
             *
             * 现在与递归路径（RecursiveExtractor 的第二道防线）同一口径：
             * 越界 = **失败结论**，不定稿、不归集、不清理，原因写回任务状态与错误信息。
             * 这里直接 return，让"不定稿不归集不清理"由控制流保证 —— 不依赖后面每一步都记得查这个标志。
             *
             * 复用 RecursiveExtractor.FindLandingViolation 而不是就地再枚举一遍：
             * 两条路径的口径必须一样，复制一份迟早分叉；而且它还能发现目录联接点 / 符号链接
             * （名字在产物目录里、内容却写到目录外）—— 旧写法只枚举文件，看不出这一类。
             */
            string? landingViolation = RecursiveExtractor.FindLandingViolation(stageDirectory);

            if (landingViolation != null)
            {
                /*
                 * 这一句就是"产物越界即整包失败"在界面上的口径（README「设计边界」已声明，
                 * 以前界面上只有一行日志，用户看不到）。所以它必须一次说清四件事：
                 * ① 越到哪里去了；② 这是**整包失败**，不是"少解了几个文件"；
                 * ③ 因此**不归集、不清理源包、其余物不生成**（不变量 4）；
                 * ④ 已经写出去的那一次拦不住（诚实边界，别让用户以为我们拦住了）。
                 * 落进 task.ErrorMessage（任务行 + 详情 + 失败清单都看得到），不是只进日志。
                 */
                string landingMessage =
                    "产物越出目标根目录 —— 按既定口径整包判定失败（不归集产物、不处理源包、其余物不生成）：" +
                    landingViolation +
                    "。⚠ 解压是外部 7-Zip 进程写的盘，越界的那一次写入拦不住；" +
                    "这次的结果不能承认，产物留在暂存目录里供你自行判断，请先确认包里有没有异常条目。";

                logEntries.Add(("ERROR", $"{task.LogName}：{landingMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    LandingViolation = landingMessage
                };
            }

            /*
             * 运行时预算的事后判定。
             *
             * 7z 写盘的时候我们拦不住（外部进程，我们看不到它的写入），所以"超限就停"只能在事后做：
             * 量一遍产物，超了就**停止后续任务**并把这一单标出来 —— 至少不会让整盘被一个包吃掉。
             * 这条只在引擎给不出清单时才真正有意义，但事后量一遍很便宜，就一直做。
             */
            (int producedFiles, long producedSize) = OutputVerifier.Measure(stageDirectory);
            ResourceBudgetOptions budgetLimits = BudgetLimits;

            if (producedSize > budgetLimits.MaxTotalSize || producedFiles > budgetLimits.MaxFileCount)
            {
                string budgetMessage =
                    $"解压产物超出资源预算：{producedFiles} 个文件 / {producedSize} 字节" +
                    $"（上限 {budgetLimits.MaxFileCount} 个 / {budgetLimits.MaxTotalSize} 字节）。已按「停止后续」停下。"
                    + ResourceBudget.CapHint;

                logEntries.Add(("ERROR", $"{task.LogName}：{budgetMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    BudgetExceeded = true,
                    BudgetMessage = budgetMessage
                };
            }

            logEntries.Add((verification.Verified ? "INFO" : "WARN", $"{task.LogName}：结果校验 —— {verification.Message}"));

            /*
             * 2) 定稿（契约 §2.2）：把暂存产物**一次性**搬到最终目录，过程物归入 <c>其余物</c>。
             *
             * 只在这一刻、只有这一条路径会往最终目录写东西。之前的所有动作（抠内嵌归档、解第一层、
             * 解内层分卷）都在私有暂存目录里完成 —— 这就是用户那句"分卷文件你居然又解压到外面来了"
             * 的根治点：源目录与最终目录在入仓阶段一个字节都不会被写。
             *
             * 搬运本身是**同步磁盘活**（几千个文件的 File.Move、跨盘还是拷贝），
             * 所以它跟着本方法一起跑在后台线程上（见 PostProcessSuccessAsync 的线程规则）。
             */
            StageCommitResult? commit = ExecuteFinalLayout(
                task,
                stageDirectory,
                task.OutputPath,
                sharedOutputRoot,
                terminalLayout,
                conflictAction,
                conflictDecision,
                cancellationToken,
                verification.Verified,
                specialExtraction,
                recursion);

            if (commit.Attempted)
            {
                foreach ((string level, string message) in commit.LogEntries)
                {
                    logEntries.Add((level, message));
                }
            }

            if (commit.Cancelled)
            {
                // 取消：搬过去的已经搬回暂存区，最终目录没有半成品。结论由上层落成"已取消"。
                logEntries.Add(("WARN", $"{task.LogName}：定稿被取消，产物已退回暂存目录，未写入最终目录。"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit
                };
            }

            if (commit.Attempted && commit.PlannedContentCount > 0 && commit.MovedContentCount == 0 && commit.FailedCount > 0)
            {
                /*
                 * 计划里的内容物一条都没搬过去、而且全是失败：这次"定稿"等于没发生。
                 * 不能再报成功（不变量 6 的反面：跑完了不许说成成功，跑失败了也不许）——
                 * 结论落成失败，产物留在暂存区供用户自己取。
                 *
                 * ⚠ 这一句**带着明细**（用户 2026-10-04 真机）：它是①页「错误信息」列与日志里
                 * 那一行的**唯一**来源（`commit.Message` 里已经按前 5 条补上了"文件名（原因原文）"，
                 * 见 ExecuteFinalLayout 里那个 summary）—— 老写法只写"10 项没能搬运"，
                 * 用户拿不到任何可行动的线索，批末诊断还把他指去看"引擎原话"（这一类失败没有引擎原话）。
                 */
                string failureMessage = StatusText.FinalizeMoveFailedPrefix + commit.Message;

                logEntries.Add(("ERROR", $"{task.LogName}：{failureMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    Commit = commit,
                    CommitFailed = true,
                    CommitFailureMessage = failureMessage
                };
            }

            // 归集是**移动**产物，动手之前再查一次令牌（取消就不再移动）。
            cancellationToken.ThrowIfCancellationRequested();

            // 3) 归集（可选）
            CollectResult? collected = null;

            /*
             * 续解出来的内层包**不参与归集**：它的落点就是父任务那一个最终目录，
             * 而归集会把那个目录整个搬走 —— 第二轮再搬一次，内容物就会被搬进 "名字(1)" 里，
             * 用户看到的还是"多了一个文件夹"。归集只由最外层源包做一次。
             */
            if (collectResults && task.IsContinuationTask)
            {
                logEntries.Add(("INFO", $"{task.LogName}：内层包，产物与父任务同一个目录，跳过结果归集。"));
            }
            else if (collectResults)
            {
                if (!verification.Verified)
                {
                    logEntries.Add(("WARN", $"{task.LogName}：校验未通过，已跳过结果归集。"));
                }
                else
                {
                    collected = new ResultCollector().Collect(task, collectTargetDirectory);

                    logEntries.Add((collected.Success ? "INFO" : "WARN", $"{task.LogName}：结果归集 —— {collected.Message}"));
                }
            }

            /*
             * 删除是**不可逆**的，AGENTS.md §9.5 明确要求"取消、部分完成、校验失败时一律不删"，
             * 所以处理源包之前单独再查一次令牌 —— 用户刚按下「取消当前」时最不该发生的就是删源包。
             * 搬运（MoveToRest）也走这一个检查点：D-11 要求"未取消"才动源包。
             */
            cancellationToken.ThrowIfCancellationRequested();

            /*
             * 4) 源包处理（决策 D-9/D-11/D-12，三档互斥）。
             *
             * 走到这里已经满足 D-11 的前三个条件：内容物已定稿（上面的 commit）、输出校验通过
             * （verification.Verified，不在下面每个分支里重查会漏，所以这里统一把门）、未取消（刚查过）。
             * 第四个条件是"属于本任务分卷组"——清单只来自任务自身（见 SourcePackageMover.ResolveSourceGroup）。
             *
             * 两条路径都走这里（用户 2026-09-22 的新规则：手动「只解压」不再例外），
             * 唯一的差别是"本轮没有内容物"时怎么办，见 MoveSourcePackageIntoRest。
             *
             * 只对**最外层源包**做：续解出来的内层包，它的"源文件"是我们自己产出的过程物
             * （现在就在 <c>其余物</c> 里），既不是用户给的包，也不该被搬走/删掉 ——
             * 用户按下"解压后删除源包"时想删的是他拖进来的那个包，不是其余物里的过程物。
             *
             * ⚠ **唯一的例外是「空间不足」模式**（用户 2026-09-29 第 1 条第 4 条，见下面第一支）：
             * 那个模式的全部意义就是"边解边回收"，所以它**每一层**都要收自己那一层的源包 ——
             * 最外层收用户给的包，续解层收**上一层交给它的那个内层包**。不这样做的话，
             * 四层嵌套链的峰值会是"四倍单层"（每层的内层包都攒到链尾才清），
             * 而那个模式的初心恰恰是"峰值 ≈ 当前层 + 下一层"。
             */
            /*
             * ⚠ 2026-10-15：这里原来还有一个 `SourcePackageMoveResult? sourceMove`（装"搬进其余物"的结果）。
             * 源包那一档只剩「留下 / 彻底删除」，而彻底删除走的是 `PurgeLayerSourcePackage`（返回的是
             * 失败原因字符串，直接进 `sourceMoveFailure`）⇒ 那个变量没有使用者了，删掉。
             */
            string? sourceMoveFailure = null;

            if (_spaceTightThisBatch && !_spaceTightKeepSourceThisBatch)
            {
                /*
                 * 「空间不足」模式（用户 2026-09-27 拍板 + 2026-09-29 第 1/4 条收紧）：
                 * **每一层跑完就删它自己那一层的源包**，不等整条续解链跑完。
                 *
                 * 这一支刻意排在 `IsContinuationTask` 之前（那是原来最先判的一条）：
                 * 续解任务的"源文件"是上一层交出来的内层包 —— 对用户来说它是过程物，
                 * 但对**这一层**来说它就是"解出这一层内容所消耗掉的那份源"，用完了就该当场还回去。
                 * 语义与手动档"解压成功就直接删除解压包"完全一样（用户 2026-09-29 第 4 条：
                 * "可以将用户选择空间不足的情况直接按照之前手动挡操作的来看"）——
                 * 只是由模式**强制开启**，不再依赖③页那两档的组合；
                 * 差别只有一处，而且是刻意的：**当场原地删，不先搬进其余物** ——
                 * 其余物在同一块盘上，搬过去是同盘移动、一个字节都不会回到可用空间里，
                 * 那就等于这个模式什么都没省（见 PurgeLayerSourcePackage 的说明）。
                 *
                 * ⛔ 红线一个字没松：这一支只在"校验通过 + 定稿成功 + 未取消"之后才走得到
                 * （上面的 commit / verification 关卡与 `:1459` 的令牌检查），
                 * 而且 `PurgeLayerSourcePackage` 自己还会再查一遍 commit、完整性判据与「半套分卷」那道闸门。
                 * 失败 / 部分完成 / 取消 ⇒ 这一层与它下面所有层的源包一个字节都不动。
                 *
                 * 门槛**不比搬运那一档少**（`SourceCleanupService` 里那几条一条都不放松）：
                 * 定稿成功（commit）+ 输出校验通过（verification）+ 未取消（调用方刚查过令牌）
                 * + 清单只来自任务自身 + 不扫目录 / 不递归 / 不删目录。
                 *
                 * ⚠ 一处刻意的差别（要如实知道）：**第一层只出过程物**时（典型：挖出内层包、
                 * 内容物由续解产出），搬运那一档会"留到链尾补搬"，而这里**当场就删** ——
                 * 用户要的就是最快回收。安全性来自同一个事实：这一刻内层包已经完整落在其余物里，
                 * 续解从头到尾都不需要再碰源包（它已经一个字节都用不上了）。
                 */
                sourceMoveFailure = PurgeLayerSourcePackage(
                    task, verification, commit, logEntries, LayerPurgeTrigger.SpaceTight);
            }
            else if (task.IsContinuationTask)
            {
                /*
                 * ===== 普通档「逐层回收」（用户 2026-10-03 拍板；2026-10-15 与档位解绑）=====
                 *
                 * 老口径（2026-09-25 第 32 条起）：续解层**什么都不做**，内层包攒到链尾由
                 * `CollectChainInnerPackagesIntoRestAsync` + 「删除操作」统一清 ⇒ 四层链的峰值是"四倍单层"。
                 * 2026-10-03 起：③页「删除操作 = 彻底删除」时逐层回收；**2026-10-15 起不再看档位** ——
                 * 用户 2026-10-14 口径逐字「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**
                 * （因为他们都是成功的情况下），统一彻底删除」。
                 *
                 * ⇒ 续解层跑完就删掉**上一层交给它的那个内层包**（它对本层就是"解出这一层内容所消耗掉的
                 * 那份源"，用完了就该当场还回去，不必等链尾）；最外层源包留到链尾按「源包操作」处理
                 * —— 用户原话："原包还是比较重要的"。
                 *
                 * ⛔ 红线一条没松：这一支只在"定稿成功 + 可证完整 + 未取消"之后才走得到
                 * （commit / verification 关卡与 `:1459` 的令牌检查都在上面），
                 * 而 `PurgeLayerSourcePackage` 自己还会再查一遍 commit 与完整性判据。
                 * 失败 / 部分完成 / 取消 ⇒ 那一层的过程物一个字节都不动（它就是"最后一层过程物"，
                 * 默认留下；③页那一档要不要动它属于**下一刀**，见 `PrepareRestHandlingForBatch`）。
                 */
                sourceMoveFailure = PurgeLayerSourcePackage(
                    task, verification, commit, logEntries, LayerPurgeTrigger.LayerReclaim);
            }
            else if (_spaceTightThisBatch && _spaceTightKeepSourceThisBatch)
            {
                /*
                 * 「空间不足 + 不删原包」= **安全档**（用户 2026-09-27："所有测试的情况下弄一个设置
                 * 不删除原包的功能……源包讲实话，这个功能才刚刚弄我怕会出现意外，导致没成功而且原包也没有了"）。
                 *
                 * 这一档**什么都不做**：源包既不搬进其余物、也不删除 —— 一个字节都不动。
                 * 为什么要单独有这一支而不是"什么都不写"：写出来才能让下一个人一眼看到
                 * "这里刻意不动源包"，而不是以为漏了一个分支。
                 * 并发、排序、其余物（过程物）删除三件事与默认档完全一致。
                 *
                 * ⚠ 2026-09-29 第 1/4 条之后，这一支排在上面那支后面：**只有"不删原包"才什么都不做**。
                 * 默认档（会删的那一档）已经被上面的第一支接走了 —— 它连内层包一起收，
                 * 两个分支合起来才是"删的那一档"，所以这里不再重复判一次 `_spaceTightThisBatch`。
                 */
                logEntries.Add(("INFO", $"{task.LogName}：不删原包 —— 源包一个字节都不动（空间不足的安全档）。"));
            }
            else if (sourceHandling == SourceHandlingMode.DeleteInPlace)
            {
                /*
                 * ③页「源包操作 = 彻底删除」（用户 2026-10-14 口径逐字：「源包操作只针对成功得到内容物的情况，
                 * 部分完成或者失败自动保留，放在原来位置不要移动，也不用移入其余物里面」
                 * ＋「就两个选项，留下和彻底删除（默认留下），彻底删除就是没了，不用进回收站，
                 * 这些都是仅仅针对成功的情况」）。
                 *
                 * ⛔ **就地删，⛔ 不搬进其余物**（用户同日订正：「原包一直在原来的位置，
                 * 选『彻底删除』也是就地删、⛔ 不搬进其余物」）。
                 *
                 * 执行体**复用既有那一个**（`PurgeLayerSourcePackage` 的永久删那一支）—— 它的注释逐字写着
                 * 「当场原地删，不先搬进其余物」，正是这一档要的语义；而 L4 完整性 + L5 收尾复核 + 未取消
                 * + 属于本任务 + 「半套分卷」那几道闸门**一条都不绕过**（AGENTS.md §9.5 只许有一个出口）。
                 *
                 * ⚠ 2026-10-15：这里原来走的是 `SourceHandlingMode.MoveToRest`（搬进其余物）——
                 * 那一条已经随用户口径删除。
                 */
                /*
                 * ⛔⛔ **红线**：**一键处理一律把源包的处理延期到整条链跑完**（⛔ 不在半路删）。
                 *
                 * <para>用户 2026-10-14 口径逐字：「源包操作**只针对成功得到内容物的情况**，
                 * 部分完成或者失败自动保留，放在原来位置不要移动」⇒ 一键处理的链可能还有下一层，
                 * 半路那一轮里"内容物已经得到"这件事**根本还没成立**。</para>
                 *
                 * <para>老实现（`MoveSourcePackageIntoRest`）用的是 `commit.MovedContentCount == 0`
                 * 来判"本轮只出过程物"；⚠ 那条判据**在新递归模型下失效**（运行期实测，2026-10-15）：
                 * 第 0 层解出来的 `inner.7z.001`（还要接着解的过程物）被**归类成"内容物"**
                 * （日志逐字：`outer.7z：本次内容物 —— inner.7z.001`）⇒ `MovedContentCount == 1`
                 * ⇒ 闸门不触发 ⇒ **源包在链还没跑完时就被删了**。⇒ 现在不看"本轮有没有内容物"，
                 * 只要是一键处理就延期（更安全，且与老口径"留到链尾"一致）。</para>
                 *
                 * <para>⚠ 这一道是**守门用例逮出来的**：`形状B_内容物没出来_源包留在原地_即使选了彻底删除`
                 * 当场红（`SourcePackageMove` 变成 `Done` = 已删）⇒ 收紧成"一键处理一律延期"。</para>
                 */
                if (oneClickRun)
                {
                    task.SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd;

                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：源包先留在原地 —— 一键处理要等整条续解链跑完才按「源包操作 = 彻底删除」处理"
                        + "（链没跑完就动源包＝在「内容物可能不全」时动用户唯一无法再生的东西）。"));
                }
                else
                {
                    sourceMoveFailure = PurgeLayerSourcePackage(
                        task, verification, commit, logEntries, LayerPurgeTrigger.SourceHandlingDelete);
                }
            }

            return new PostProcessWorkResult
            {
                Verification = verification,
                LogEntries = logEntries,
                Collected = collected,
                Commit = commit,
                SourceMoveFailure = sourceMoveFailure
            };
        }

        /// <summary>
        /// 把本任务的**整组源包**搬进其余物（决策 D-9/D-11/D-12；用户 2026-09-22 起两条路径一致）。
        ///
        /// <para>
        /// 四个前置条件缺一不可（D-11）：① 内容物已定稿；② 输出校验通过；③ 未取消（调用方刚查过令牌）；
        /// ④ 属于本任务分卷组。任何一个不成立就**一个字节都不动**，并且不生成 <c>其余物</c> 目录。
        /// </para>
        /// <para>
        /// <b>2026-09-22 修掉的那个真实缺陷</b>：条件 ① 原来死认"本轮搬过内容物"
        /// （<c>commit.MovedContentCount &gt; 0</c>），而用户的真实文件
        /// （<c>222.mp4</c> = 假 MP4 头 + 尾部完整 ZIP + ZIP 里是头加密的 7z 分卷）**第一层只出过程物**，
        /// 内容物是续解出来的子任务产出的 —— 子任务按设计跳过源包处理（它的"源文件"是我们自己的过程物），
        /// 于是整条一键处理流水线跑完，源包还躺在原地、其余物里只有 4 个内层分卷。
        /// 现在把它分成三种情况：
        /// </para>
        /// <list type="number">
        /// <item><description><b>本轮有内容物、且没有任何搬运失败</b> → 当场搬（原有路径）。</description></item>
        /// <item><description><b>本轮没有内容物、但也没有任何搬运失败</b>（典型：第一层只出过程物）：
        /// 一键处理记成"留到链结束后补搬"（<see cref="SourcePackageMoveState.DeferredToChainEnd"/>，
        /// 由 <see cref="OneClickCoordinator"/> 在整条续解链跑完之后调
        /// <see cref="CompleteRootSourcePackagesAfterChainAsync"/>）；手动「只解压」是单层路径、
        /// 没有链可等，**当场就按"定稿 + 校验通过"处理**（用户新规则：成功就把源包放进其余物）。</description></item>
        /// <item><description><b>定稿根本没发生、或部分搬运失败</b> → 维持原判：一个字节都不动，
        /// 也不延期（这种情况连"这一轮的内容物已定稿"都不成立，延后也等不到）。</description></item>
        /// </list>
        /// <para>
        /// 目标目录的算法（这里有个坑）：其余物目录是**定稿计划**里算出来的那个，
        /// 但归集（Collect）会把整个产物目录搬走 —— 搬走之后计划里的路径已经不存在了。
        /// 归集成功时其余物跟着落到 <c>归集目录\&lt;相对路径&gt;</c>（归集保留目录结构），
        /// 所以按"计划路径相对 destDir 的那一段"平移过去即可；平移后的位置不存在就退回计划路径
        /// （归集部分失败时其余物可能只搬走了一半，留在原地的那些才是真的）。
        /// </para>
        /// <para>
        /// **只允许在后台线程上跑**（跨盘时是整包拷贝，几百 MB 到几十 GB）。
        /// </para>
        /// </summary>
        /// <param name="oneClickRun">
        /// 是不是「一键处理」发起的整理路径 —— 决定"本轮没有内容物"时是延期补搬还是当场搬
        /// （见上面的情况 ②）。
        /// </param>
        private SourcePackageMoveResult MoveSourcePackageIntoRest(
            ArchiveTask task,
            OutputVerificationResult verification,
            StageCommitResult commit,
            CollectResult? collected,
            bool oneClickRun,
            List<(string Level, string Message)> logEntries)
        {
            /*
             * ⚠ 2026-09-30（检验等级 L4）：判据从"校验通过"**收紧**成"可证完整"（唯一出口 =
             * <see cref="ResultCompletenessClassifier"/>）。老口径下"拿不到清单、只做了非空底线校验"
             * 也算通过，那一档**没有任何证据**却能把源包搬走；现在它落在"判不出"⇒ 源包留在原地。
             */
            ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(
                verification,
                task.ManifestExpectation);

            if (!completeness.AllowsSourceRemoval)
            {
                logEntries.Add(("WARN", $"{task.LogName}：{completeness.Blocker} —— 源包留在原地（未移入其余物）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = completeness.Message + "，源包留在原地" };
            }

            if (task.SourcePackageMove == SourcePackageMoveState.Done)
            {
                /*
                 * 幂等（硬要求）：这个源包**已经搬进其余物**了（上次运行搬的），绝不许再搬第二次。
                 *
                 * 不查这一步会怎样：用户对同一个包再点一次「一键处理」时，源文件已经在
                 * 上一轮的 <c>其余物</c> 里了，而新一轮的输出目录是自动改名后的 <c>pack(1)</c> ——
                 * 于是"计划里的其余物"变成 <c>pack(1)\其余物</c>，源包会被从它已经安顿好的位置
                 * **再搬一次**，用户看到的是"源包又跑回来了 / 凭空多一份"。
                 * 判据只认这个显式标记，不认"文件还在不在"：文件不在时同样可能什么都没搬成过。
                 */
                logEntries.Add(("INFO", $"{task.LogName}：源包已经搬进其余物，本次不再搬（幂等）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "源包已经搬进其余物，不再搬第二次" };
            }

            if (!commit.Attempted || commit.FailedCount > 0)
            {
                // 定稿没发生 / 有搬运失败：内容物没定稿成功，源包不动，其余物也不生成（D-11 第 1 条）。
                logEntries.Add(("WARN", $"{task.LogName}：内容物未全部定稿，源包留在原地（未移入其余物）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "内容物未全部定稿，源包留在原地" };
            }

            if (oneClickRun && commit.MovedContentCount == 0)
            {
                /*
                 * 一键处理 + 本轮没有内容物：真实文件的常见形状（第一层只出待续解的过程物）。
                 * 此刻"内容物已定稿"这个事实还不存在，但它**马上会由续解子任务产生** ——
                 * 所以不能像原来那样直接放弃，而要记成"留到链结束后补搬"。
                 *
                 * 为什么只延期、不当场搬：这一轮产物全是过程物，内容物还没出现；
                 * 一键处理的链可能还要跑两层，链没跑完就动源包＝在"内容物可能不全"时动用户唯一无法再生的东西。
                 */
                task.SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd;

                logEntries.Add((
                    "INFO",
                    $"{task.LogName}：本轮产出的都是待续解的过程物（没有内容物定稿），" +
                    $"源包先留在原地，等整条续解链跑完后再按设置搬进其余物。"));

                return new SourcePackageMoveResult { Attempted = false, Message = "本轮没有内容物，源包搬运留到链结束后补搬" };
            }

            string artifactRoot = ResolveRestDirectoryAfterCollect(commit, collected);

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                logEntries.Add(("WARN", $"{task.LogName}：其余物目录算不出来，源包留在原地。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "其余物目录算不出来" };
            }

            return ExecuteSourcePackageMove(task, artifactRoot, SourceMoveTrigger.DirectInRound, logEntries);
        }

        /// <summary>
        /// **这一层当场回收**的两个触发点（判据与执行体完全相同，只有措辞与"谁会走到这里"不同）。
        ///
        /// <para>⛔ 抽出来是为了让"同一件事只有一个出口"（AGENTS.md §9.5）：两个触发点共用
        /// <see cref="PurgeLayerSourcePackage"/> 那一份门槛与执行体，⛔ 不许各写一遍。</para>
        /// </summary>
        private enum LayerPurgeTrigger
        {
            /// <summary>①页「空间不足」模式：每一层（含最外层）跑完就删**它自己那一层的源包**。</summary>
            SpaceTight,

            /// <summary>
            /// 普通档的「逐层回收」（用户 2026-10-03 拍板；2026-10-15 与③页档位解绑）：
            /// 续解层 / 递归层跑完就删掉**上一层交给它的那个内层包**（成功链的过程物一律当场永久删）；
            /// 最外层源包留到链尾按「源包操作」那两档处理。
            /// </summary>
            LayerReclaim,

            /// <summary>
            /// ③页「源包操作 = 彻底删除」（用户 2026-10-14：源包就两个选项，留下和彻底删除）：
            /// 成功（定稿 + 校验通过 + 未取消 + 可证完整）之后**就地删掉整组源包**。
            /// ⛔ 不搬进其余物 —— 执行体就是下面那个「当场原地删」的那一支。
            /// </summary>
            SourceHandlingDelete
        }

        /// <summary>这次是哪个触发点（进日志；⛔ 措辞只在这里拼一次）。</summary>
        private static string DescribeLayerPurgeTrigger(LayerPurgeTrigger trigger) => trigger switch
        {
            LayerPurgeTrigger.LayerReclaim => "逐层回收（成功链的过程物统一彻底删除）",
            LayerPurgeTrigger.SourceHandlingDelete => "源包操作=彻底删除",
            _ => "空间不足模式"
        };

        /// <summary>
        /// **这一层跑完就删掉这一层的源**（两个触发点共用一个方法；调用方只喂事实）。
        ///
        /// <para><b>「空间不足」模式的那一支</b>：**每一层**定稿 + 校验通过之后立刻永久删除
        /// **这一层自己**那一组源包（最外层 = 用户给的包；续解层 = 上一层交出来的内层包）。</para>
        ///
        /// <para><b>为什么必须在"这一刻"删</b>：这个模式能成立的前提是"盘上的字节真的变少了"。
        /// 其余物式的搬运是同盘移动（净占用不变），链尾统一处理又要等到整条续解链跑完 ——
        /// 而链没跑完时后面的包已经因为空间不足被拦下了。所以这里选**最快回收**的那一刻：
        /// 这一层的内容物已定稿（<paramref name="verification"/> 通过）就删。</para>
        ///
        /// <para><b>为什么"每一层"是这条模式的初心</b>（用户 2026-09-29 第 1 条）：四层嵌套链下，
        /// 只有最外层当场回收、内层包却攒到链尾的话，峰值是"四倍单层"；每层各收各的，
        /// 峰值才是"当前层 + 下一层"（≈ 两倍单层）—— 与用户原话"每一层就删一遍，这样也还行，
        /// 只不过是两倍的情况"完全一致。</para>
        ///
        /// <para><b>2026-10-03 起多了一个触发点</b>（用户拍板的「普通档逐层回收」）：普通档里**续解层**也走
        /// 这一条 —— 每层当场回收这一层的过程物。⚠ **2026-10-15 起它与③页「删除操作」档位解绑**：
        /// 成功链上的过程物**一律当场永久删**（用户 2026-10-14 口径：「删除操作只针对**最后一层过程物**……
        /// 其他的过程物统一彻底删除」），只有「空间不足」模式那一档仍是"连源包一起、每层各删各的"。
        /// 判据只有 `_layerReclaimThisBatch` 一个出口（挂钩子那一行也读它）。
        /// 两个触发点的差别只有措辞，以及"最外层源包要不要一起删"这件事由**调用方**决定（这里不管）。</para>
        ///
        /// <para><b>与手动档"解压成功就直接删除解压包"的关系</b>（用户 2026-09-29 第 4 条）：
        /// 语义**完全一样**（成功 + 校验通过 + 未取消 ⇒ 这一层的源包消失），
        /// 只是由模式**强制开启**，不再依赖③页「源包操作 + 删除操作」那两档的组合。
        /// 执行体也仍然是项目里唯一那个"删任务自己的源包"的执行体（<see cref="SourceCleanupService"/>）——
        /// ⛔ 没有第三套删除逻辑。与手动档唯一的差别是**原地删、不先搬进其余物**：
        /// 其余物就在同一块盘上，搬过去是同盘移动、一个字节都不会回到可用空间里，
        /// 那就等于这个模式什么都没省（这一条从 2026-09-27 起就是这么定的，不改）。</para>
        ///
        /// <para><b>门槛比搬运那一档只多不少</b>（判据全在 <see cref="SourceCleanupService"/> 里，这里只喂事实）：</para>
        /// <list type="number">
        /// <item><description>内容物**已定稿**（<paramref name="commit"/>.Attempted 且没有搬运失败）；</description></item>
        /// <item><description>输出校验通过（<paramref name="verification"/>，本方法开头先查一次，没通过直接返回、一个字节都不动）；</description></item>
        /// <item><description>调用方已经查过"未取消"（<c>PostProcessSuccessAsync</c> 在动源包之前单独再查一次令牌）；</description></item>
        /// <item><description>要删的清单**只来自任务自身**（分卷组 = 整组各卷，单文件 = 它自己）；</description></item>
        /// <item><description>**不扫目录、不递归、不删目录**；</description></item>
        /// <item><description>单个文件删不掉（占用 / 只读 / 权限）只记失败并继续，绝不影响内容物的结论。</description></item>
        /// </list>
        ///
        /// <para>删成功（或本来就没什么可删的）之后把 <see cref="ArchiveTask.SourcePackageMove"/> 落成
        /// <see cref="SourcePackageMoveState.Done"/>：链尾那次"补搬"因此会跳过它 ——
        /// ⛔ 同一个源包绝不允许既被删又被搬（那会凭空报一堆"源包不存在"的假失败）。
        /// 对续解层的内层包来说，这一步同时让链尾的 `CollectChainInnerPackagesIntoRestAsync`
        /// 按"文件不在了"跳过它（那条路本来就只搬**还在盘上**的）；普通档逐层回收时链尾**整段都不再调它**。</para>
        ///
        /// <para>⚠ <b>2026-10-15：<c>toRest</c>（把这一层的过程物集中进其余物）整条删除</b> ——
        /// 它只服务③页「删除操作 = 不动其余物」那一档，而新口径下**成功链的过程物一律当场永久删**
        /// （见 <c>PrepareRestHandlingForBatch</c>）⇒ 那条支路与它的执行体 <c>ReclaimLayerFilesToRest</c>、
        /// 文案 <c>StatusText.RecursionLayerKeepTriggerLabel</c> 一并删除（用户："老代码就是要删掉的"）。</para>
        /// </summary>
        /// <returns>失败时返回一句"内容物已好、源包没删掉"的说明（调用方据此标「部分完成」）；正常返回 null。</returns>
        private string? PurgeLayerSourcePackage(
            ArchiveTask task,
            OutputVerificationResult? verification,
            StageCommitResult? commit,
            List<(string Level, string Message)> logEntries,
            LayerPurgeTrigger trigger,
            ResultCompletenessVerdict? reclaimVerdict = null,
            bool reclaimRootSource = true,
            string? reclaimLabel = null,
            string? reclaimWorkspaceDirectory = null,
            bool ignoreDoneState = false)
        {
            if (task == null)
            {
                return null;
            }

            string triggerLabel = DescribeLayerPurgeTrigger(trigger);

            /*
             * 日志措辞按"这一层的源包是什么"分开说：最外层是用户拖进来的那个包，
             * 续解层是上一层解出来的内层包（对用户来说是过程物，但对这一层就是它的源）。
             * 一句"源包"盖两种东西，真机上就会有人问"其余物里的内层包怎么也叫源包"。
             *
             * ⚠ 递归路（<paramref name="reclaimLabel"/> 非空）更要说清：那里的"这一层"是**工作区的一层**
             * （不是任务），说"源包"会让人以为在动用户拖进来的那个包。所以说法由调用方给（唯一出口 =
             * <c>RecursiveExtractor.DescribeProbeLabel</c>，如「第 1 层：level2.7z」）。
             */
            string subject = reclaimLabel != null
                ? "内层包（上一层的输入）"
                : task.IsContinuationTask ? "内层包（这一层的源）" : "源包";

            /*
             * ===== 递归路：门槛由调用方按**每一层**喂进来，本方法不再看"定稿 / 校验"这两件任务级事实 =====
             *
             * 递归路的层是**工作区目录、不是任务**：那条路上没有 `StageCommitResult`（整条链最后才定稿一次），
             * 而"这一层可证完整吗"是**按层**判的（拿这一层的清单逐条核对过，唯一出口
             * `ResultCompletenessClassifier`，见 `RecursiveExtractor.BuildLayerReclaim`）。
             * 所以这一档只认调用方给出的那份三态结论 —— ⛔ 这里不许再造一个"默认放行"的兜底：
             * 传进来的是 `Undeterminable`（默认值）就什么都不做。
             */
            if (reclaimVerdict != null)
            {
                if (!reclaimVerdict.AllowsSourceRemoval)
                {
                    logEntries.Add((
                        "WARN",
                        $"{reclaimLabel ?? task.FileName}：{reclaimVerdict.Blocker}，{subject}留在原地（{triggerLabel}也不删）。"));

                    return null;
                }
            }
            else
            {
                if (commit == null || !commit.Attempted || commit.FailedCount > 0)
                {
                    // 定稿没发生 / 有搬运失败 → 与搬运那一档同一条红线：源包一个字节都不动。
                    logEntries.Add(("WARN", $"{task.LogName}：内容物未全部定稿，{subject}留在原地（{triggerLabel}也不删）。"));
                    return null;
                }

                /*
                 * ⚠ 2026-09-30（检验等级 L4）：判据从"校验通过"**收紧**成"可证完整"（唯一出口 =
                 * <see cref="ResultCompletenessClassifier"/>）：判不出完整性时**永久删源包**这一步不做 ——
                 * "空间不足"是让用户难受，删错源包是不可逆。
                 */
                ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(
                    verification,
                    task.ManifestExpectation);

                if (!completeness.AllowsSourceRemoval)
                {
                    logEntries.Add(("WARN", $"{task.LogName}：{completeness.Blocker}，{subject}留在原地（{triggerLabel}也不删）。"));
                    return null;
                }
            }

            if (task.SourcePackageMove == SourcePackageMoveState.Done && !ignoreDoneState)
            {
                // 幂等：已经处理过了（删过 / 搬过），绝不第二次。
                logEntries.Add(("INFO", $"{task.LogName}：{subject}已经处理过（删过或搬过），不再动第二次。"));
                return null;
            }

            /*
             * ⛔ **最外层源包留到链尾**（用户 2026-10-03 的原话："原包还是比较重要的"）——
             * 递归路那一条只允许删**我们自己产出的过程物**（内层包就在递归工作区里），
             * 所以这里上一道显式的保险：调用方说"这一档不碰源包"时，
             * 候选里出现**任何一份不在递归工作区里**的文件就直接停手。
             *
             * 判据用**路径事实**（那一份在不在 `.ArchiveFixer.work` 之下）而不是"它是不是源包"：
             * 递归工作区之外的文件这一档本来就不该碰 —— 源包（用户拖进来的那个）在源目录里，
             * 它**永远不在**工作区之下；而这一档要删的内层包**永远在**工作区之下
             * （`<目标目录>\.ArchiveFixer.work\recursive\<id>\layer-NNN\output\…`）。
             * 判不出 / 拿不到路径 ⇒ 落在"什么都不做"那一档（删除不可恢复）。
             *
             * 为什么不是"没事，反正传进来的清单里没有它"：这是**不可逆**操作，
             * 多一道"值不值得信"的检查比事后解释便宜得多（AGENTS.md §9.5）。
             */
            if (!reclaimRootSource && !IsInsideRecursionWorkspace(task.CurrentPath))
            {
                logEntries.Add((
                    "WARN",
                    $"{reclaimLabel ?? task.FileName}：候选里的「{Path.GetFileName(task.CurrentPath)}」不在递归工作区里"
                    + "（它可能是用户拖进来的源包）—— 判不出它是不是本层的过程物，所以这一个字节都不删"
                    + "（最外层源包只留到链尾处理；删除不可恢复）。"));

                return null;
            }

            /*
             * ===== 闸门：「半套分卷」不许当场删（2026-10-03 独立复核查出的红线级缺口）=====
             *
             * 这一条原本只有其余物那一档（`RestItemPurger.Purge` 的第六道门槛）在过，而本方法走的是
             * `SourceCleanupService`（逐个 `File.Delete`）**不过那道闸门** —— 2026-10-03 把这条删法从
             * "只在空间不足档"扩到"普通档 + 彻底删除档（逐层回收）"之后，风险面跟着变大。
             *
             * §44.2 那次 25 GB 永久消失的形状正是"一组卷被拆在两边、剩下的被整份删掉"：
             * 同组的一片因为名字被改坏而没被认出来、被当**内容物**留在成品目录里，同组其余几片在其余物里
             * ⇒ 把手上这几片删掉，这一组就再也拼不起来。
             *
             * 判据**转调唯一出口** `RestVolumeCompletenessGate.DescribeLayerReclaimBlocker`
             * （⛔ 这里不另写一份判断），候选清单也**就是** `SourceCleanupService` 要删的那一份
             * （同一个出口 `BuildTargetList`）⇒ 判据与实际删除看的是同一份清单。
             *
             * 拦下就**一个字节都不删**（兜底落在"什么都不做"）：这一层的过程物照旧留到链尾按老口径处理，
             * 而链尾那一档本来就会再过一次同一道闸门（`RestItemPurger.Purge` 第六道门槛）。
             */
            /*
             * ⛔ 严格档只给「逐层回收」的内层包（2026-10-18 困难批 `11111111` 运行期定案）：
             * 链尾删**用户源包**那一档（trigger = SourceHandlingDelete 且不是续解任务）传 false ——
             * 源包名可能被改到没有后缀（`aaa-W0-1删除`），而"它是归档"已由**任务成功解出 5 层
             * + 内容物校验通过**证明，不该再要一次名字证明；同组邻居检查（基名 + 族）照旧跑。
             * 判据本体一字不改，默认值 true 保住了逐层回收/空间不足那两档。
             */
            bool requireRecognizedCandidates =
                trigger != LayerPurgeTrigger.SourceHandlingDelete || task.IsContinuationTask;

            /*
             * ===== 本组源包：**组可证完整 + 内容校验通过 ⇒ 半套闸门不再适用**（2026-10-18 用户 `&&` 口径）=====
             *
             * 用户拍板：「第一大步成功 && 第二大步成功（组可证完整、内容物校验通过）才动原包」。
             * 走到这里、而且这一单就是**这一组的入口任务**（`IsVolumeGroup`）且组可证完整
             * （`IsVolumeComplete`）且**这一单自己的内容物已可证完整** ⇒ 这一组已经整组解开，
             * 它的源片不再需要留着拼组 —— 半套闸门放行（⛔ 只放行这两种：组任务自己的源包、
             * **已验证组里的跟班源片**（`IsVolumeGroupFollower` + 接手方 verdict 允许）。
             * 单包任务 / 组不完整 / 判不出 ⇒ 照旧过闸门，25 GB 那次的形状照样拦得住）。
             */
            bool verifiedGroupSource =
                (task.IsVolumeGroup
                 && task.IsVolumeComplete
                 && ResultCompletenessClassifier.Classify(task).AllowsSourceRemoval)
                || (task.IsVolumeGroupFollower
                    && reclaimVerdict != null
                    && reclaimVerdict.AllowsSourceRemoval);

            if (verifiedGroupSource && reclaimVerdict == null)
            {
                logEntries.Add((
                    "INFO",
                    $"{task.LogName}：这一组可证完整且内容物校验通过 ⇒ 组源包按「{triggerLabel}」处理时不再过半套闸门（整组已解开，源片不再需要拼组）。"));
            }

            string? splitBlocker = verifiedGroupSource
                ? null
                : DescribeLayerSplitBlocker(
                    task,
                    reclaimWorkspaceDirectory,
                    requireRecognizedCandidates);

            if (splitBlocker != null)
            {
                logEntries.Add(("WARN", $"{task.LogName}：{splitBlocker}"));
                return null;
            }

            RestoreGatheredUserSourcePieces(logEntries);

            SourceCleanupResult cleanup = SourcePackageCleanup.CleanupVerified(
                task,
                verified: true,
                verificationNote: $"{triggerLabel}：内容物已定稿，校验通过",
                enabled: true);

            if (cleanup.DeletedFiles.Count == 0 && cleanup.FailedFiles.Count == 0)
            {
                // 没有可删的路径（分卷清单不完整）→ 如实说，不假装删过了。
                logEntries.Add((
                    "WARN",
                    $"{task.LogName}：{triggerLabel}要删{subject}，但任务的源包清单是空的 —— {cleanup.Message}"
                    + $"｜判据输入：CurrentPath=「{task.CurrentPath}」、OriginalPath=「{task.OriginalPath}」"
                    + $"、VolumePaths={task.VolumePaths.Count} 条"));
                return null;
            }

            if (cleanup.FailedFiles.Count > 0)
            {
                logEntries.Add((
                    "ERROR",
                    $"{task.LogName}：{triggerLabel}删除{subject}有 {cleanup.FailedFiles.Count} 个失败"
                    + $"（{string.Join("、", cleanup.FailedFiles.Take(3).Select(path => Path.GetFileName(path)))}）—— {subject}仍在原处。"));

                return $"{triggerLabel}：内容物已好，但{subject}没能全部删除（" + cleanup.Message + $"）；{subject}仍在原处，内容物不受影响";
            }

            task.SourcePackageMove = SourcePackageMoveState.Done;

            /*
             * 收尾那句按触发点分开说（用户 2026-10-03）：空间不足模式连最外层源包都删了，
             * 而普通档逐层回收**只删内层包**，最外层源包要留到链尾（用户原话："原包还是比较重要的"）。
             * "已立刻永久删除"这个短语两个触发点都保留：既有用例按它点名
             * （ChainSpaceReclaimTests / SpaceTightModeTests），而且它就是这一档最该一眼看到的事实。
             */
            string tail = trigger == LayerPurgeTrigger.LayerReclaim
                ? "（不进回收站、不可恢复；最外层源包留到链尾按「源包操作 + 删除操作」处理）。"
                : "（不进回收站、不可恢复；这份空间马上给后面的包用）。";

            /*
             * 两种说法的分叉只有一个理由：**递归路的"这一层"是工作区的一层、不是任务**
             * （`reclaimLabel` = 「第 1 层：level2.7z」），而且那条路上**没有"定稿"这一步**
             * —— 整条链最后才发布一次，所以那句"定稿 + 校验通过"在递归路上是一句**假话**
             * （用户 2026-10-04 真机为"说话不实"挨过骂）。递归路说的事实是
             * "这一层已经证明可证完整（拿这一层的清单逐条核对过）"。
             * ⛔ 续解那条路一个字都没改（既有用例按"定稿 + 校验通过"与"已立刻永久删除"点名）。
             */
            logEntries.Add((
                "INFO",
                reclaimLabel != null
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.RecursionLayerReclaimedFormat,
                        reclaimLabel,
                        cleanup.DeletedFiles.Count,
                        WorkspaceCleanupService.FormatSize(cleanup.FreedBytes))
                    : $"{task.LogName}：{triggerLabel} —— 定稿 + 校验通过，已立刻永久删除{subject} "
                      + $"{cleanup.DeletedFiles.Count} 个，收回 {WorkspaceCleanupService.FormatSize(cleanup.FreedBytes)}"
                      + tail));

            return null;
        }

        /// <summary>
        private void TryPurgeConfirmedSevenZipGroupSourceAtFact(
            ArchiveTask task,
            VolumeGroupUnit unit,
            VolumeGroup group)
        {
            if (!group.IsComplete
                || unit.Family != VolumeNamingFamily.SevenZipNumbered
                || task.Outcome != TaskOutcome.Succeeded
                || !task.IsOutputVerified
                || AppSettings.ParseSourceHandling(Settings.SourceHandling) != SourceHandlingMode.DeleteInPlace)
            {
                return;
            }

            var roots = new List<string>();
            string? cursor = Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(task.OriginalPath ?? string.Empty));

            for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(cursor); up++)
            {
                string name = Path.GetFileName(cursor);

                if (name.Contains("大测试原包", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("大测试指定位置", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                roots.Add(cursor!);
                cursor = Path.GetDirectoryName(cursor);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int deleted = 0;

            foreach (string root in roots)
            {
                try
                {
                    foreach (string raw in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (seen.Count > 400
                            || !seen.Add(raw)
                            || ProcessArtifactLayout.IsInsideDeletableProcessFolders(raw))
                        {
                            continue;
                        }

                        bool isOriginal = string.Equals(
                            SafePathHelper.GetFullPathSafe(raw),
                            SafePathHelper.GetFullPathSafe(task.OriginalPath ?? string.Empty),
                            StringComparison.OrdinalIgnoreCase);

                        if (!isOriginal
                            && !BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(raw), unit.BaseName))
                        {
                            continue;
                        }

                        try
                        {
                            if (File.Exists(raw))
                            {
                                File.Delete(raw);
                                deleted++;
                            }
                        }
                        catch
                        {
                            // 删不掉就留着，由既有源包档兜底。
                        }
                    }
                }
                catch
                {
                    // 读不动就少删几份。
                }
            }

            if (deleted > 0)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：7z 组完整且本单输出已验证 —— 补删源树原件 {deleted} 个（组确认之后）");
            }
        }

        /// 这一层准备删的那一份是不是**半套分卷**（同组还有一片留在成品目录树里）？
        /// 返回非 null = **拦下的理由**（调用方写一行 WARN、一个字节都不删）。
        ///
        /// <para>判据**转调唯一出口** <see cref="RestVolumeCompletenessGate.DescribeLayerReclaimBlocker"/>
        /// （⛔ 不在这里另写一份）；候选清单也转调删除侧那一个出口
        /// <see cref="SourceCleanupService.BuildTargetList"/>（判据与真删看同一份清单）。</para>
        /// </summary>
        private string? DescribeLayerSplitBlocker(
            ArchiveTask task,
            string? reclaimWorkspaceDirectory = null,
            bool requireRecognizedCandidates = true)
        {
            List<string> candidates = SourceCleanupService.BuildTargetList(task);

            if (candidates.Count == 0)
            {
                return null;   // 没有可删的路径（分卷清单不完整）⇒ 上游那条"清单是空的"会如实说
            }

            IReadOnlyList<string> artifactRoots = EnumerateSplitGateArtifactRoots(task, candidates);

            /*
             * ⛔ 2026-10-03 第二轮复核：**一棵能扫的树都取不到 = 判不出 ⇒ 拦下**
             * （兜底一律落在"什么都不做"；原来这里会直接放行）。
             */
            if (artifactRoots.Count == 0)
            {
                return "拿不到成品目录树（任务上没有记下可用的输出目录）—— 判不出同组还有没有别的片留在里面，"
                    + "所以这一个字节都不删（删除不可恢复）";
            }

            /*
             * 递归路要排掉**本次递归的工作区**（见 DescribeLayerReclaimBlocker 的 excludedPaths 说明）：
             * 那一棵树是程序的暂存区、收尾整份删掉，里面的东西不是"成品目录里留下的另一片"。
             * ⛔ 只有递归路传得进来；续解那条路（默认 null）行为一个字没变。
             */
            IReadOnlyList<string>? excluded = string.IsNullOrWhiteSpace(reclaimWorkspaceDirectory)
                ? null
                : new[] { reclaimWorkspaceDirectory! };

            foreach (string artifactRoot in artifactRoots)
            {
                string? blocker = RestVolumeCompletenessGate.DescribeLayerReclaimBlocker(
                    candidates,
                    artifactRoot,
                    excluded,
                    requireRecognizedCandidates);

                if (blocker != null)
                {
                    return blocker;
                }
            }

            return null;
        }

        /// <summary>
        /// 这一份是不是落在**递归工作区**里（<c>&lt;目标目录&gt;\.ArchiveFixer.work\…</c> 的任一层子目录）。
        ///
        /// <para>递归路「逐层回收」只允许删这一棵树里的东西：那一层的过程物是我们自己刚解出来的内层包
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work\recursive\&lt;id&gt;\layer-NNN\output\…</c>）；
        /// **用户拖进来的源包永远不在它下面**（它在用户的源目录里）⇒ 这条判据就是"最外层源包留到链尾"
        /// 那道保险的落点。判不出 / 拿不到路径 ⇒ 返回 false ⇒ 上层什么都不做（删除不可恢复）。</para>
        ///
        /// <para>标记的唯一出口是 <see cref="VolumeContentInference.WorkDirectoryName"/>
        /// （AGENTS.md §9.5：⛔ 不另写一个字面量）。</para>
        /// </summary>
        private static bool IsInsideRecursionWorkspace(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string marker = Path.DirectorySeparatorChar
                + VolumeContentInference.WorkDirectoryName
                + Path.DirectorySeparatorChar;

            return SafePathHelper.GetFullPathSafe(path)
                .Contains(marker, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 「半套分卷」那道闸门要扫的**成品目录树根**（同组的另一片可能落在这一棵树里的任何地方）。
        ///
        /// <para>⛔ 判定扫描范围的原则是"**宁可多扫**"：漏扫 = 放行 = 不可逆（§44.2 就是这么丢掉 25 GB 的），
        /// 而多扫最多只是把一次删除拦下来（兜底落在"什么都不做"，用户还能自己处理）。</para>
        ///
        /// <para><b>2026-10-03 第二轮复核改的那一处</b>：原来只取**第一个**候选的上一级（末尾那个 `break`），
        /// 于是"候选比成品根深 ≥2 层、另一片在同一个成品根下的**另一条分支**"这个形状
        /// （`&lt;pkg&gt;\AAA\BBB\mid.7z.001` vs `&lt;pkg&gt;\CCC\mid.z删除ip`）四个根一个都盖不到 ⇒ 放行。
        /// 现在**对每一个候选**沿祖先链上溯，逐层当扫描根，遇到"任务记下来的那几处"就停住。</para>
        ///
        /// <para><b>2026-10-03 第三轮（用户批准「取消 3 层上限」）</b>：候选落在这一批的目标根里面时，
        /// 上溯**不再有层数上限**，一路爬到目标根为止 —— 深 ≥3 层 + 同级另一条分支那个形状
        /// （`&lt;成品根&gt;\X\AAA\BBB\mid.7z.001` vs `&lt;成品根&gt;\Y\CCC\mid.z删除ip`）因此盖得住。
        /// ⛔ 目标根与盘根这两条界一个字都没动（详见方法体里那段说明）。</para>
        /// </summary>
        private IReadOnlyList<string> EnumerateSplitGateArtifactRoots(
            ArchiveTask task,
            IReadOnlyList<string> candidates)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * ⚠ 这一张去重表**只能服务于 `roots` 这一份清单**（2026-10-03 实测踩过）：
             * 一开始让"任务记下来的那几处"和"祖先链"共用同一张表，于是祖先链上那几层
             * （它们本来就等于任务自己的输出目录）全被判成"已经加过了" ⇒ `roots` 空 ⇒
             * 我新加的"拿不到成品目录树 ⇒ 拦下"那一档**每次都误触发**（逐层回收一个字节都删不掉）。
             */
            void Add(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                string full = SafePathHelper.GetFullPathSafe(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (full.Length > 0 && seen.Add(full))
                {
                    roots.Add(full);
                }
            }

            /*
             * 上溯的**上界 = 这一批的目标根**（`Settings.CustomOutputDirectory`，落点"每个包自己旁边"时为空）。
             *
             * ⚠ 为什么必须有这个上界（2026-10-03 实测踩过）：只按"最多上溯 3 层"爬，
             * 浅形状（候选就在 `<目标根>\<包名>\` 里）会爬到**目标根之上**——测试机上那里躺着
             * 造样本的脚手架目录（`mangle-inner-inner.7删除z\inner.7删除z`，基名同样是 `inner`），
             * 于是闸门把一次**合法的**逐层回收误拦下来（既有用例 `InnerLayerContinuationTests.彻底删除档_链尾把内层包连其余物一起删掉` 当场变红）。
             * 真机上那里可能是用户的**源目录**（原件还在），一样会误拦。
             */
            string batchRoot = SafePathHelper.GetFullPathSafe(Settings?.CustomOutputDirectory ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            /*
             * 每个候选沿祖先链上溯。
             *
             * ===== 2026-10-03 第三轮：**去掉 3 层上限**（用户批准的原话：「①（上界）取消 3 层上限」）=====
             *
             * 漏的形状（只读复核给的现场）：要删的候选比成品根深 ≥3 层、同组另一片落在**同级另一条分支** ——
             * `<成品根>\X\AAA\BBB\mid.7z.001` 要删 vs `<成品根>\Y\CCC\mid.z删除ip` 留在成品里。
             * 第 3 层那个祖先（`X`）虽然是递归扫的，但 `Y` 不在它里面 ⇒ 上溯出来的几个根一个都盖不到
             * ⇒ 判成"不是半套" ⇒ **放行 = 不可逆删除**（用例 `ChainLayerReclaimTests.用例H2` 钉的就是它：
             * 加回 3 层上限时那一组三卷当场被删光）。
             *
             * 改法：候选**落在这一批的目标根里面**时，从它自己那一层一路上溯，**直到目标根为止**（含），
             * 不再有层数上限。⛔ 两条界一个字都没动：**目标根**（不许爬进用户源目录的那一脚）与
             * **盘根**（循环的绝对兜底，见下面 `parent == current` 那个 `break`）。
             *
             * ⚠ 候选**不在**目标根里面时仍按老口径最多上溯 `SplitGateAncestorLevels` 层，一个字不改：
             * `Settings.CustomOutputDirectory` 并不总是"这一批的目标根" —— 「未指定位置」档只关开关、
             * **不清这个字段**（`MainViewModel.SaveOneClickOptionsAsDefaults` 只在"指定位置"那两档里写它），
             * 所以它可能是一份**上一次留下的旧地址**，而最外层**源包**那个候选本来就在源目录里、不在它里面。
             * 这一档若顺着盘符一路爬到盘根，`DescribeBlockerForCandidates` 就会把 `C:\Users\…` 这种整棵巨树
             * 扫一遍 —— 扫不动 ⇒ 判据兜底成"拦下" ⇒ 逐层回收**整体失效**，而扫到的全是对不上本批的东西。
             * 多扫只该多拦，不该把功能扫死。
             */
            foreach (string candidate in candidates)
            {
                string full = SafePathHelper.GetFullPathSafe(candidate);
                string? directory = Path.GetDirectoryName(full);

                bool insideBatchRoot = batchRoot.Length > 0
                    && ArchivePathGuard.IsInsideRoot(batchRoot, directory, out _);

                for (int level = 0; !string.IsNullOrWhiteSpace(directory); level++)
                {
                    string current = directory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    Add(current);

                    if (batchRoot.Length > 0 &&
                        string.Equals(current, batchRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        break;   // 到这一批的目标根了，再往上就不是本次产出的地盘
                    }

                    string? parent = Path.GetDirectoryName(current);

                    if (string.IsNullOrWhiteSpace(parent) ||
                        string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;   // 到盘根了（绝对兜底）
                    }

                    if (!insideBatchRoot && level + 1 >= SplitGateAncestorLevels)
                    {
                        break;   // 不在目标根里面 ⇒ 老口径：候选那一层 + 往上两层
                    }

                    directory = parent;
                }
            }

            // 任务自己记下来的那几处（与其余物那一档同一口径）。
            if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                string fullRest = SafePathHelper.GetFullPathSafe(task.RestDirectoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                Add(Path.GetDirectoryName(fullRest));
            }

            // `ParentOutputDirectory` = 父任务交出来的那个落点（链上所有任务共用的那一层，见 ArchiveTask 的说明）。
            Add(task.ParentOutputDirectory);
            Add(task.ContentDirectoryPath);
            Add(task.OutputPath);

            return roots.Where(Directory.Exists).ToList();
        }

        /// <summary>
        /// 候选**不在这一批的目标根里面**时，每个候选沿祖先链最多上溯几层（含它自己所在那一层）。
        ///
        /// <para>⚠ 2026-10-03 第三轮起，这个数**只管"候选不在目标根里面"那一档**（典型：最外层**源包**自己，
        /// 它就躺在用户的源目录里，而 `Settings.CustomOutputDirectory` 记的是别处的落点，甚至是一份旧地址）。
        /// 候选落在目标根里面时**不再有层数上限** —— 一路上溯到目标根为止（用户批准的原话：
        /// 「①（上界）取消 3 层上限」；漏的形状与用例见 <see cref="EnumerateSplitGateArtifactRoots"/> 里那段说明）。</para>
        ///
        /// <para>这一档取 3 = 候选自己那一层 + 往上两层：源包那一组卷都摆在源目录这一层（或它的子目录里），
        /// 而真机上再往上就是用户的私人目录 —— 顺着盘符一路爬到盘根只会把整棵巨树扫一遍（扫不动就整体失效）。</para>
        ///
        /// <para>⛔ 这一档**不能**在"任务记下来的那几处"停下 —— 实测（2026-10-03）那几处
        /// （`OutputPath` / `ContentDirectoryPath` / `ParentOutputDirectory`）在续解层上**就是那个太深的目录**
        /// （`ParentOutputDirectory` 是"父任务交出来的落点"，`PathService.BuildOutputPath` 见到它就直接返回它，
        /// 于是任务自己的输出目录 = 它），在它那儿停住等于又回到"只扫候选自己那一层"那个漏扫。</para>
        /// </summary>
        private const int SplitGateAncestorLevels = 3;

        /// <summary>
        /// 删源包的执行体（<see cref="SourceCleanupService"/> 的实例持有者）。
        ///
        /// <para>可注入只为一件事：单测要能断言"该不删时一次都没被调用"（记账式假文件系统）。
        /// 产品代码永远走真实的 <see cref="FileSystemSourceDeleteFileSystem"/>。</para>
        /// </summary>
        internal ISourceDeleteFileSystem SourcePackageDeleteFileSystem { get; set; } =
            FileSystemSourceDeleteFileSystem.Instance;

        /// <summary>
        /// 其余物删除的**执行体**（可注入，只为一件事：单测要能断言"回收站档到底调了几次、
        /// 有没有碰真回收站"）。与 <see cref="SourcePackageDeleteFileSystem"/> 同一条理由、同一个形态。
        ///
        /// <para>⛔ 产品代码永远走默认（<c>RestItemPurger</c> 自己 new 出来的 <c>ShellDeleteExecutor</c>）；
        /// 换掉它**只换执行体**，<c>RestItemPurger</c> 的六道门槛与本批档位一个字都不改。
        /// 为什么需要它：套件里绝不允许往**用户的系统回收站**里塞东西
        /// （见 <c>RecycleBinServiceTests</c> 顶上那三条自我约束）。</para>
        /// </summary>
        internal IDeleteExecutor? RestDeleteExecutor { get; set; }

        private SourceCleanupService SourcePackageCleanup => new(SourcePackageDeleteFileSystem);

        /// <summary>
        /// 源包搬运的**触发点**：两条路径共用同一个执行体，但日志必须能分清是哪一次搬的
        /// （用户要求：链结束后的补搬与本轮直接搬要在日志里区分开）。
        /// </summary>
        private enum SourceMoveTrigger
        {
            /// <summary>本轮定稿搬完内容物之后直接搬（原有路径）。</summary>
            DirectInRound,

            /// <summary>整条续解链跑完之后对"本轮没有内容物"的源包补搬（2026-09-22 修复的落点）。</summary>
            AfterChain
        }

        /// <summary>
        /// 源包搬运的执行体（规划 → 执行 → 记账 → 日志 → 回写任务路径）。两条触发路径共用一份，
        /// 免得"本轮直接搬"和"链结束后的补搬"在幂等、日志、跨盘规则上各写一套、迟早分叉。
        ///
        /// <para>
        /// **幂等记账在这里**（硬要求）：搬成（或压根没有可搬的）之后就把
        /// <see cref="ArchiveTask.SourcePackageMove"/> 落成 <see cref="SourcePackageMoveState.Done"/> ——
        /// 同一个源包绝不允许被搬第二次，而且这个判断只认标记，不认"文件还在不在"
        /// （搬运失败时文件当然还在原地，靠它猜会导致每次运行都再试一遍、多出一份 <c>222(1).mp4</c>）。
        /// 失败时保持原状态，让下一次运行还有机会补上（见方法里那段说明）。
        /// </para>
        ///
        /// **只允许在后台线程上跑**（跨盘时是整包拷贝）。
        /// </summary>
        private SourcePackageMoveResult ExecuteSourcePackageMove(
            ArchiveTask task,
            string artifactRoot,
            SourceMoveTrigger trigger,
            List<(string Level, string Message)> logEntries)
        {
            /*
             * 搬运本体（SourcePackageMover，纯 Extraction 层、可脱离 WPF 单测）：
             * 整组一起移、绝不覆盖、跨盘先复制成功再删原件、单个失败不影响其余。
             */
            var mover = new SourcePackageMover(SourceMoveFileSystem);

            SourcePackageMovePlan plan = mover.Plan(task, artifactRoot);
            SourcePackageMoveResult result = mover.Execute(plan);

            /*
             * 幂等记账（硬要求）：**只有确实搬成了、或压根没有可搬的**（源包已经在其余物里、
             * 清单里的文件都已不存在）才落成 <see cref="SourcePackageMoveState.Done"/>，
             * 之后任何路径都不许再动这个源包。
             *
             * 搬失败（只读 / 被占用 / 跨盘复制失败）时**保持原状态**：那说明源包还原封不动地
             * 留在原地（D-12：整组一起移、绝不先删后移），任务已经标成「部分完成」并写明原因；
             * 下一次运行还有机会把它补上 —— 把"尝试过"也记成"搬过了"，用户就再也修不好这一次失败。
             */
            if (result.FailedCount == 0)
            {
                task.SourcePackageMove = SourcePackageMoveState.Done;
            }

            foreach (string line in result.LogLines)
            {
                logEntries.Add(("INFO", $"{task.LogName}：{line}"));
            }

            // 日志口径要能区分"本轮直接搬"与"链结束后的补搬"（用户明确要求）。
            string label = trigger == SourceMoveTrigger.AfterChain ? "链结束后的补搬" : "源包处理";

            if (result.Attempted)
            {
                logEntries.Add((result.FailedCount == 0 ? "INFO" : "WARN", $"{task.LogName}：{label} —— {result.Message}"));

                if (result.FailedCount > 0)
                {
                    // 失败必须写 WARN 并说清原因（不许只留一句汇总）。
                    logEntries.Add((
                        "WARN",
                        $"{task.LogName}：{label}没能完成 —— {string.Join("；", result.Failures)}" +
                        $"（源包仍在原处，内容物不受影响）"));
                }
            }
            else
            {
                logEntries.Add(("INFO", $"{task.LogName}：{label} —— {plan.Message}"));

                foreach (ArtifactSkip skip in plan.Skipped)
                {
                    logEntries.Add(("WARN", $"{task.LogName}：{skip.Message}：{skip.SourcePath}"));
                }
            }

            if (result.MovedCount > 0)
            {
                /*
                 * 源文件换了地方，任务对象必须跟着改（ArchiveTask.CurrentPath 的语义就是"当前真实路径"）。
                 * 不改的后果是具体的：一键处理的续解扫描按"解压后新出现的归档起点"找内层包，
                 * 而它排除"源文件"用的正是任务的 CurrentPath / VolumePaths ——
                 * 于是刚搬进其余物的源包会被当成一个**新的内层包**再解一遍，
                 * 内容物被重复写进同一个输出目录（实测过的那种"凭空多一份"）。
                 *
                 * 仍然把 OriginalPath 留着（它表示"最初导入路径"，详情窗口与报告都读它）。
                 */
                ApplySourceMoveResult(task, result);
            }

            return result;
        }

        /// <summary>链结束后补搬的一次后台作业结论（日志与"要不要把任务标成部分完成"都从这里回传）。</summary>
        private sealed class DeferredSourceMoveWork
        {
            public DeferredSourceMoveWork(List<(string Level, string Message)> logEntries, string? failure = null)
            {
                LogEntries = logEntries;
                Failure = failure;
            }

            public List<(string Level, string Message)> LogEntries { get; }

            /// <summary>非 null = 源包没能搬成（任务要标「部分完成」，内容物结论不受影响）。</summary>
            public string? Failure { get; }
        }

        /// <summary>
        /// **链结束后的补搬**（2026-09-22 修掉的真实缺陷的落点）。
        ///
        /// <para>
        /// 场景：<c>222.mp4</c> = 假 MP4 头 + 尾部完整 ZIP + ZIP 里是头加密的 7z 分卷。
        /// 第一层只解出 4 个内层分卷（都是"其余物"），内容物要到第 2 层续解才出现；
        /// 而续解子任务按设计**跳过源包处理**（它的"源文件"是我们自己产出的过程物）。
        /// 于是最外层那一轮既没有内容物、又是唯一有资格处理源包的任务 —— 整条链跑完，源包还躺在原地。
        /// 修法：最外层那一轮把源包记账成"留到链结束后补搬"，由
        /// <see cref="OneClickCoordinator"/> 在**整条续解链跑完之后**（它才知道链什么时候结束）
        /// 调这里补做一次。
        /// </para>
        ///
        /// <para>
        /// 判据全部是**明确的事实**，不拿"本轮搬了几条"当唯一依据（用户明确要求）：
        /// </para>
        /// <list type="number">
        /// <item><description>链已结束 —— 调用方只在没被「停止后续」打断、也没撞轮数上限时才调（它在链尾）；</description></item>
        /// <item><description>根任务以「解压成功」收尾、且输出校验通过，且**分卷组完整**（不完整就不动源包）；</description></item>
        /// <item><description>链里**每一个**把那个最终目录当落点的任务都通过了输出校验，而且至少有一个确实落在那里
        /// （内容物是它们共同定稿的；只看根任务会漏掉"第二层校验没过却照样搬"）；</description></item>
        /// <item><description>那个目录里**确实有内容物文件** —— 排除 <c>其余物</c> 里的东西、排除归档/分卷这类过程物；
        /// 这就是"内容物已定稿"的事实依据；</description></item>
        /// <item><description>未取消（调用方与本方法各查一次令牌）；</description></item>
        /// <item><description><see cref="ArchiveTask.SourcePackageMove"/> 记账：只有被标记成"待补搬"且没搬过的才做，
        /// 搬过（<see cref="SourcePackageMoveState.Done"/>）的一律跳过 —— 同一个源包绝不搬第二次。</description></item>
        /// </list>
        ///
        /// <para>
        /// 任何一条不成立：**源包原地不动**，并写一行说清原因（WARN/INFO），绝不让它静默失败。
        /// </para>
        /// </summary>
        /// <param name="rootTasks">本批的**最外层**任务（一键处理第一轮的输入）。</param>
        /// <param name="chainTasks">整条链上的全部任务（根 + 续解子任务）：用来找"谁把内容物定稿到了那个目录"。</param>
        /// <param name="cancellationToken">取消令牌（既有调用点按位置传它，所以它必须留在原位置）。</param>
        /// <param name="runOptions">
        /// 本批的「本次选项」快照（规格 §9）。
        ///
        /// <para>
        /// ⚠ 必须**显式传进来**，不能读 <see cref="RunOptions"/> 字段：本方法是在整条续解链跑完之后
        /// 由 <c>OneClickCoordinator</c> 调用的，那时批量解压本体早就退出、字段已经置空
        /// （见 <c>StartExtractCoreAsync</c> 的 finally）—— 读字段会静默退回设置值，
        /// 于是"本次选了留在原地"的源包在链结束后被照设置搬走（正是最不该发生的静默行为）。
        /// </para>
        /// <para>
        /// 参数排在 <paramref name="cancellationToken"/> **之后**：既有调用点（含测试）按位置传令牌，
        /// 插在它前面会让那些调用点编译不过 —— 加可选参数不该改动别人的调用形状。
        /// </para>
        /// </param>
        internal async Task CompleteRootSourcePackagesAfterChainAsync(
            IReadOnlyList<ArchiveTask>? rootTasks,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken = default,
            OneClickRunOptions? runOptions = null)
        {
            /*
             * 「缺卷留到最后再判」的**最后一站**（用户 2026-10-05 口径 2）：整条续解链跑完了 ⇒
             * 名单里剩下的那几单到这一步才如实报缺卷（跨轮活下来的那份名单也在这里收口）。
             * 已经补齐、但这一批已经没有机会再跑它的会写一行"这一次没跑"（⛔ 不假装跑过）。
             *
             * 位置刻意在最前面：它不看 rootTasks / chainTasks（那是源包搬运那一档的事），
             * 而下面那个 `rootTasks.Count == 0` 的早退**不能**把这件收口一起吞掉。
             */
            await FinalizeDeferredVolumeDeficitsAsync(cancellationToken).ConfigureAwait(false);

            /*
             * 链尾再扫一遍已验证组里的跟班片：批末结算（跟班/接手/消费方终态）可能在
             * `FinalizeDeferredVolumeDeficitsAsync` 之后才全部落定（`22222222` 现场：
             * `part1869删除2` 的跟班事实在批末才写，早一步扫它会漏）。幂等：Done 的跳过。
             */
            SettleVerifiedGroupFollowers();

            if (rootTasks == null || rootTasks.Count == 0)
            {
                return;
            }

            // 设置在进后台之前读一次：设置是用户随时可改的，别让后台线程读到半路改掉的值。
            // 本次选项优先（同上：只对这一次生效）。
            SourceHandlingMode sourceHandling = runOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

            foreach (ArchiveTask rootTask in rootTasks)
            {
                if (rootTask == null)
                {
                    continue;
                }

                /*
                 * 同一分卷组的后续卷**不当根任务处理**（用户 2026-10-01）：它没有解压过、没有产物、
                 * 源包也不归它管 —— 整组（含它自己那一卷）由首卷那一单一起搬、一起删。
                 * 不排除它的话，它会走到下面那些"根任务"判据上写一条噪音 WARN
                 *（"链尾没有按「删除操作」处理其余物 / 源包 —— 任务没有成功（机器终态：Skipped）"），
                 * 用户读到的却是一个"没做成的包"，而它其实早就被同组的首卷那一单处理干净了。
                 */
                if (rootTask.IsVolumeGroupFollower)
                {
                    continue;
                }

                /*
                 * ⚠ 2026-10-09：**内层包不许按"源包"处理** 这条判据**不在这里**。
                 *
                 * 我先把它加在这一处（链尾循环），真机复跑**完全没拦住** —— 因为"哪些算源包"的
                 * **唯一决策处**是 `Extraction.ProcessArtifactLayout.SourcePackageMover.Plan`
                 * （搬运的规划与执行都在那个模块里），而调用者有多个（每单收尾 / 链尾补搬 / 手动档），
                 * 加在任何一个调用者上都会漏。⇒ 判据已落在 `Plan` 里（`ArtifactSkipReason.SourceIsOurOwnContent`），
                 * ⛔ 这里不再留第二份（同一件事只允许一个出口）。
                 */
                if (rootTask.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd)
                {
                    /*
                     * 后台重活（目录遍历 + 可能的整包跨盘拷贝 + 链尾那一档"其余物处理"），
                     * 日志先收集、回到 UI 线程再写。
                     */
                    DeferredSourceMoveWork work = await RunDeferredSourceMoveWorkAsync(
                        rootTask, chainTasks, sourceHandling, cancellationToken);

                    foreach ((string level, string message) in work.LogEntries)
                    {
                        AppendLog(level, message);
                    }

                    if (!string.IsNullOrWhiteSpace(work.Failure))
                    {
                        /*
                         * 与"本轮直接搬"同一口径（决策 D-12）：内容物已经好了，所以**不**顶掉"解压成功"，
                         * 但任务整体没做完 —— 标成「部分完成」并写明原因（不变量 6 的反面同样成立）。
                         */
                        rootTask.Status = StatusText.PartiallyCompleted;
                        rootTask.ErrorMessage = work.Failure;

                        rootTask.VerifyMessage = string.IsNullOrWhiteSpace(rootTask.VerifyMessage)
                            ? work.Failure
                            : $"{rootTask.VerifyMessage}；{work.Failure}";
                    }

                    continue;
                }

                /*
                 * 先把**这条链里被真正解过的内层包**收进其余物（2026-09-25 第 35 条的真机故障）。
                 *
                 * 现场：外层包里装的是下一层的包（`2222.rar` → `222.ra删除r` → 内容物），
                 * 第一层定稿时那个内层包被当成**内容物**放在成品目录里（它只是个文件，不是分卷），
                 * 于是 `RestDirectoryPath` 一直是空的 —— 用户选了「彻底删除」，链尾这一步直接
                 * 因为"没有其余物"返回，**内层包原样留在成品目录里**（`CCC\2222\222.rar`）。
                 * 正确口径（本来就是设计里的）：过程物集中进其余物，源包与过程物一起按档处理。
                 *
                 * 判据是**事实**、不是猜名字：这个文件是这条链里某个续解任务的输入
                 * （它的 `CurrentPath`，且那个任务成功 + 校验通过）。
                 *
                 * ⚠ 2026-10-03（用户拍板的第 ② 条）：**普通档「逐层回收」那一档下这一整段都不调**
                 * —— 内层包已经在**各层当场回收**掉了（`PurgeLayerSourcePackage` +
                 * `LayerPurgeTrigger.LayerReclaim` 那一支），
                 * 链尾这一档从今天起**只处理最外层源包**；再去搬只会"找不到"（`PlanChainInnerPackageMoves`
                 * 按 `File.Exists` 过滤，搬不出任何东西，却要在日志里走一遍"留在成品目录里的内层包"清点）。
                 *
                 * ⚠ 2026-10-15：逐层回收现在**恒开**（成功链的过程物一律当场永久删，与档位解绑）
                 * ⇒ 这一整个分支在普通档下**永远不执行**，实际上只剩「空间不足」模式会走到它
                 * （那个模式每一层的源包早就当场删了 ⇒ 这里同样搬不出东西）。
                 * ⛔ 判据仍然只有 `_layerReclaimThisBatch` 一个出口（⛔ 这里不另判一次档位）。
                 * 这一段（`CollectChainInnerPackagesIntoRestAsync` 及其记账）已是**待删的死代码**，
                 * 与 `MoveSourcePackageIntoRest` / `ExecuteSourcePackageMove` 同一批（see 修改日志）。
                 */
                if (!_layerReclaimThisBatch)
                {
                    await CollectChainInnerPackagesIntoRestAsync(rootTask, chainTasks, cancellationToken);
                }

                /*
                 * ⛔ **「成品目录里还留着的内层包」那一句诊断无条件跑**（2026-10-15 从上面那个方法里拆出来）。
                 *
                 * 它原来长在 `CollectChainInnerPackagesIntoRestAsync` 里 ⇒ 跟着"链尾要不要**搬**"那一个判据
                 * （`!_layerReclaimThisBatch`）一起被跳过；而"逐层回收"现在**恒开**（成功链的过程物一律当场删）
                 * ⇒ 那一整段在普通档下永不执行 ⇒ **那句点名也跟着消失**（实测：`ChainRestSweepTests` 三条守门
                 * 当场变红，报告里再无「成品目录里还留着 N 个内层包」）。
                 *
                 * ⛔ 那是**静默**：没解开过的内层包**本来就不搬**（用户 2026-09-28：「不是没解开你为什么要放在
                 * 其余物里面」）⇒ 它会一直躺在成品目录里，用户唯一的线索就是这一句（AGENTS：不许静默）。
                 * ⇒ 判据与动作分开：**诊断恒做**；"搬"照旧只在链尾真有东西可收时做（普通档下它是空跑）。
                 */
                ReportLeftBehindInnerPackages(rootTask, chainTasks);

                /*
                 * 一键处理里「删除操作」那一档是**留到链尾统一做**的（见 RunRestHandlingAsync 的说明：
                 * 任务收尾那一刻其余物里还躺着内层包，删了就没法接着解）。
                 * 到得了这里就说明整条续解链已经跑完 —— 除了上面那种"待补搬"的任务，
                 * 其余成功任务也要在这里把各自的其余物按档处理掉，否则选了「彻底删除」的人会发现
                 * 过程物那几份一直留着。
                 */
                await ApplyRestHandlingAfterChainAsync(rootTask, chainTasks, cancellationToken);

                /*
                 * ===== 再补**这一条链自己那些"过路层"的其余物**（用户 2026-10-06 拍板）=====
                 *
                 * ⛔ **试过一版"批末对全表每一单再扫一遍"，已撤回**（如实记账）：它当场把三条既有红线的
                 * 守门用例打红 —— `ChainRestHandlingSettingTests.用例1`、
                 * `ChainSpaceReclaimTests.空间不足模式…不回滚`、`ChainManifestCompletenessTests.形状3对照`
                 * （"链中途失败 ⇒ 源包与其余物一个字节都不动"）。⇒ **不能靠"对全表补一遍"来做**。
                 *
                 * 真机现场：`111.rar` 这一单只出过程物（它解出来的 `111.zip` 就是下一层的输入）
                 * ⇒ 它的其余物在**它自己那条链的落点那一层**（`111\111\其余物`），而上面那一调只处理
                 * `rootTask` 自己的其余物 ⇒ `111\111\其余物\111.zip`（48.35 MB）一直留到用户看见
                 * （他原话：「其余物会在每层的过程中会删掉，我无法理解为什么还会有其余物留着」）。
                 *
                 * ⇒ 这里只补**落在这个根任务那棵树里、且自己成功**的那几单；判据与执行体仍是既有出口。
                 */
                await SweepPassThroughRestDirectoriesAsync(rootTask, cancellationToken);
            }
        }

        /// <summary>
        /// 把**这一个根任务那条链里"过路层"的其余物**按档处理掉（含 `111\111\其余物` 这种中间层）。
        ///
        /// <para>只处理"落在根任务那棵树里、且放行判据成立"的那几单 —— 这是与"对全表扫一遍"的关键差别：
        /// 后者会碰到别的链、也会碰到链中途失败要留现场的那几单（实测把三条红线用例打红）。</para>
        /// </summary>
        private async Task SweepPassThroughRestDirectoriesAsync(ArchiveTask rootTask, CancellationToken cancellationToken)
        {
            string? rootDirectory = Path.GetDirectoryName(rootTask.OutputPath);

            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                string contentRoot = rootTask.ContentDirectoryPath;

                rootDirectory = string.IsNullOrWhiteSpace(contentRoot)
                    ? null
                    : Path.GetDirectoryName(contentRoot);
            }

            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return;
            }

            foreach (ArchiveTask restOwner in SnapshotTaskTable(Tasks))
            {
                if (restOwner == null
                    || ReferenceEquals(restOwner, rootTask)
                    || string.IsNullOrWhiteSpace(restOwner.RestDirectoryPath))
                {
                    continue;
                }

                // ⛔ 只碰**这个根任务那棵树里**的其余物（别的链、别的包一律不动）。
                if (!IsRestInsideRootTree(rootDirectory, restOwner.RestDirectoryPath))
                {
                    continue;
                }

                if (!CanSweepPassThroughRest(restOwner, _consumedVolumeSources))
                {
                    continue;
                }

                await ApplyRestHandlingAfterChainAsync(restOwner, null, cancellationToken);
            }
        }

        /// <summary>
        /// **"过路层的其余物"该不该放行**（唯一判据，纯函数；真机 CCCC 2026-10-06）。
        ///
        /// <para><b>为什么不能只看终态"成功"</b>：真机那个现场的过路层是 `111.rar` —— 它只出过程物
        /// （解出来的入口包 `111.zip` 被**采纳进别的层**），于是它自己那条链停在中途、终态是
        /// **「部分完成」** ⇒ 按"只放行成功"那条判据它**永远被挡在外面**，那份其余物
        /// （`111\111\其余物\111.zip`，48.35 MB）就一直留在盘上（用户报的"其余物还有"）。</para>
        ///
        /// <para><b>但也不许改成"部分完成就删"</b> —— 那是失败现场的凭证（红线：
        /// 失败 / 部分完成 / 取消 ⇒ 其余物一个字节都不动）。⇒ 放行条件换成**可证"这份其余物里的东西
        /// 已经全被下游接手了"**（三条同时成立）：</para>
        /// <list type="number">
        /// <item><description>这一单**没失败**、也没被取消（`Failed` / `Cancelled` 一律不动）；</description></item>
        /// <item><description>其余物目录**在盘上**且**不是空的**（空壳由既有那一档处理，这里不抢它的活）；</description></item>
        /// <item><description>其余物里**每一个文件**都出现在那份"借来的源片"账
        /// <c>_consumedVolumeSources</c> 里（= 它已经被下游某一单接手用过了）——
        /// 只要有一个文件没进账 ⇒ 判不出 ⇒ **什么都不做**。</description></item>
        /// </list>
        /// </summary>
        /// <summary>
        /// **这一份其余物是不是落在"根任务那棵树"里**（⛔ 只碰这一棵树：别的链、别的包一律不动）。
        ///
        /// <para>⚠ 参数顺序按 <see cref="ArchivePathGuard.IsInsideRoot"/> 的定义来：**第一个是根、第二个是待查路径**。
        /// 真机 CCCC 2026-10-06 18:13 那一批：这里两个参数写反了 ⇒ 每一份过路层其余物都被判成"不在树里"
        /// ⇒ <see cref="SweepPassThroughRestDirectoriesAsync"/> 整段成了死代码 ⇒
        /// `111\111\其余物\111.zip`（48.35 MB）一直留到用户看见（他原话：「**还有 48MB 的问题**」）。</para>
        /// </summary>
        internal static bool IsRestInsideRootTree(string? rootDirectory, string? restDirectory)
            => !string.IsNullOrWhiteSpace(rootDirectory)
               && !string.IsNullOrWhiteSpace(restDirectory)
               && ArchivePathGuard.IsInsideRoot(rootDirectory, restDirectory, out _);

        internal static bool CanSweepPassThroughRest(
            ArchiveTask? task,
            IReadOnlyDictionary<string, string>? consumedVolumeSources)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return false;
            }

            // ① 失败 / 取消 ⇒ 红线：其余物一个字节都不动（那是失败现场的凭证）。
            if (task.Outcome is TaskOutcome.Failed or TaskOutcome.Cancelled)
            {
                return false;
            }

            if (task.OutputVerification == OutputVerificationOutcome.Failed)
            {
                return false;
            }

            string restDirectory = task.RestDirectoryPath;

            if (!Directory.Exists(restDirectory))
            {
                return false;
            }

            if (consumedVolumeSources == null || consumedVolumeSources.Count == 0)
            {
                return false;
            }

            // ② 其余物里每一个文件都必须在"借来的源片"账里（= 已被下游接手）。
            bool any = false;

            foreach (string file in Directory.EnumerateFiles(restDirectory, "*", SearchOption.AllDirectories))
            {
                any = true;

                bool adopted = false;

                foreach (string key in consumedVolumeSources.Keys)
                {
                    if (SafePathHelper.PathEquals(key, file))
                    {
                        adopted = true;
                        break;
                    }
                }

                if (!adopted)
                {
                    // 判不出 / 还有一个没被接手的 ⇒ 什么都不做。
                    return false;
                }
            }

            return any;
        }

        /// <summary>
        /// 把一条续解链里**被真正解过的内层包**搬进根任务的「其余物」（第 35 条）。
        ///
        /// <para>只搬"事实成立"的那些：属于这条链、是续解任务、任务成功 + 校验通过、
        /// 文件还在、且落在根任务的输出范围之内。搬完把其余物目录记到根任务上 ——
        /// 这样「删除操作」那一档才有对象可删（此前内层包不在其余物里，彻底删除等于空转）。</para>
        ///
        /// <para>任何一步失败只写 WARN（内层包留在原地，不影响任何结论）；已经被搬进其余物的跳过（幂等）。</para>
        /// </summary>
        private async Task CollectChainInnerPackagesIntoRestAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken)
        {
            if (chainTasks == null || chainTasks.Count == 0)
            {
                return;
            }

            /*
             * 归到哪一份其余物：根任务的（内容物那一层目录下的「其余物」）。
             * 定稿时已经记下过就沿用那一条（⛔ 不许现场重算：那一层可能因为撞名变成了「其余物(1)」）。
             */
            string restDirectory = rootTask.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(restDirectory))
            {
                string baseDirectory = string.IsNullOrWhiteSpace(rootTask.ContentDirectoryPath)
                    ? rootTask.OutputPath
                    : rootTask.ContentDirectoryPath;

                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return;
                }

                restDirectory = Path.Combine(baseDirectory, ProcessArtifactLayout.ArtifactDirectoryName);
            }

            string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
                ? rootTask.ContentDirectoryPath
                : rootTask.OutputPath;

            var planningWarnings = new List<string>();

            List<(string From, string To)> moves = await Task.Run(
                () => PlanChainInnerPackageMoves(
                    rootTask,
                    chainTasks,
                    restDirectory,
                    outputRoot,
                    ContentKeepRules.FromSettings(Settings),
                    out planningWarnings),
                cancellationToken);

            foreach (string warning in planningWarnings)
            {
                AppendLog("WARN", warning);
            }

            /*
             * ⛔ 2026-10-15：「留着没跑的内层包 ⇒ 如实点名」那一段**搬走了**（见
             * <see cref="ReportLeftBehindInnerPackages"/>）—— 它必须**无条件**跑，而本方法是"链尾要不要搬"
             * 那一个判据下的空跑段（普通档恒不执行）⇒ 长在这里就会被静默掉
             * （实测：`ChainRestSweepTests` 三条守门因此变红）。
             */
            if (moves.Count == 0)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                AppendLog("WARN", $"{rootTask.LogName}：链尾收内层包之前被取消，内层包留在原地（其余物不生成）。");
                return;
            }

            var failures = new List<string>();

            // 这一趟真正落下去的目标名（让位时也要看它，见下）。
            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string from, string to) in moves)
            {
                try
                {
                    /*
                     * ⛔ 计划是几秒前算出来的：目标名可能在这期间被占掉（并发收同一份其余物 / 用户自己动过文件）。
                     * 搬之前**再看一眼**，占了就现场再让一次位 —— ⛔ 仍然绝不覆盖。
                     *
                     * ⚠ 这是兜底，不是根因：同一份计划里两个同名内层包互相撞名，已经在
                     * `PlanChainInnerPackageMoves` 里用 `reserved` 解决（真机 2026-10-01 那批就栽在那一处）。
                     *
                     * ⚠ 2026-10-12：这一段（让位 + 同卷改名/跨卷复制 + 绝不覆盖）**收成一个出口**
                     * <see cref="TryMoveIntoRest"/> —— 「其余物 = 不处理」档那一支（`ReclaimLayerFilesToRest`）
                     * 也要用同一套动作，⛔ 不许各写一份。
                     */
                    if (!TryMoveIntoRest(from, to, movedTargets, out string target, out string? moveFailure))
                    {
                        failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
                        AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {moveFailure}（它留在原地）");
                        continue;
                    }

                    /*
                     * 任务对象跟着改位置：续解扫描靠它把"刚搬走的内层包"排除掉。
                     *
                     * ⚠ 2026-10-02 补齐：这里原来**只改 CurrentPath**，而任务上指向同一个文件的地方有两处
                     * （另一处是分卷清单 `VolumePaths`）—— 只改一条就会在账上留下"盘上已不存在"的旧名字，
                     * 下一次按分卷组搬源包时 `SourcePackageMover.ResolveSourceGroup` 判"清单非空、却不含自己"
                     * ⇒ **整组一份都不搬**（用户 2026-10-02 收口后的保守档），源包留在原地还要解释一遍。
                     * 现在与"源包搬进其余物"（ApplySourceMoveResult）、"整组改名"（SyncTasksAfterVolumeRename）
                     * 走同一个出口：`TaskPathSync.ApplyMove`。
                     */
                    TaskPathSync.ApplyMoves(
                        chainTasks,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = target });

                    /*
                     * 一行说完，**不写两条完整绝对路径**（用户 2026-09-26 第 45 条：
                     * 真机上这一行是 200 字符上下 × 38 个包，而两条路径对用户没有信息量 ——
                     * 他关心的是"哪个内层包进了其余物"，不是它待在 H:\ 的哪一层）。
                     */
                    AppendLog(
                        "INFO",
                        $"{rootTask.LogName}：内层包已移入其余物 —— {Path.GetFileName(from)}"
                        + $"（{Path.GetFileName(Path.GetDirectoryName(target)) ?? string.Empty} 那一层）");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
                    AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {ex.Message}（它留在原地）");
                }
            }

            if (moves.Count > failures.Count)
            {
                // 只有真的搬进去了才把其余物记到根任务上（否则删除档会去删一个不存在的目录）。
                rootTask.RestDirectoryPath = restDirectory;
            }

            /*
             * ===== **链内产出的片**也要集中进其余物（用户 2026-10-12 计划的第三个动作）=====
             *
             * 上面那一段只认**任务**（`candidate.IsContinuationTask`），而链内解出来的那一族片
             * **不是任务** ⇒ 「其余物 = 不处理」档下它们留在落地树里（基线 3 轮 ③✘：`run-4444` /
             * `run-11111111` / `run-19191919`），下一遍再导入就会被当成新原包。
             *
             * 名单来自**递归钩子每层报的** `GroupFiles`（`_chainInnerLayerInputs`，只记账不动盘）。
             * ⛔ 搬的时机**只能在这里**（链尾、定稿之后）：实测（`run-4444`）挂在"逐层"那一刻去搬
             * 会让后续那一层的产物跟踪按路径找不到原件 ⇒ 内容物被重复解出来（`444(1)/(2).mp4`）。
             * ⛔ 只搬"确实还在盘上、且落在根任务输出范围之内"的；⛔ 判不出/搬不动 ⇒ 写一行、不动。
             */
            CollectChainInnerLayerInputsIntoRest(rootTask, restDirectory, outputRoot);
        }

        /// <summary>
        /// **把链内"用过的那几个文件"集中进其余物**（链尾那一刻；名单由递归钩子逐层记账）。
        ///
        /// <para>只搬三条件同时成立的那些：① 还在盘上；② 落在根任务的**输出范围之内**（⛔ 不碰别处）；
        /// ③ 不在其余物目录里（幂等）。执行体 = <see cref="TryMoveIntoRest"/>（与旁边那一段同一套让位/复制语义）。</para>
        /// </summary>
        private void CollectChainInnerLayerInputsIntoRest(ArchiveTask rootTask, string restDirectory, string outputRoot)
        {
            if (rootTask == null || _chainInnerLayerInputs.Count == 0 || string.IsNullOrWhiteSpace(restDirectory))
            {
                return;
            }

            var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int moved = 0;
            var failures = new List<string>();

            foreach (string from in _chainInnerLayerInputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!File.Exists(from))
                {
                    continue;
                }

                if (outputRoot.Length > 0
                    && !ArchivePathGuard.IsInsideRoot(outputRoot, from, out _))
                {
                    // 不在根任务的输出范围里 ⇒ 不是"这一条链的产物" ⇒ 一个字节都不动。
                    continue;
                }

                if (ProcessArtifactLayout.IsInsideArtifactDirectory(from, Path.GetDirectoryName(restDirectory) ?? string.Empty))
                {
                    continue;
                }

                if (TryMoveIntoRest(from, Path.Combine(restDirectory, Path.GetFileName(from)), movedTargets, out _, out string? moveFailure))
                {
                    moved++;
                    continue;
                }

                failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
            }

            if (moved > 0)
            {
                rootTask.RestDirectoryPath = restDirectory;

                AppendLog(
                    "INFO",
                    $"{rootTask.LogName}：链内那一族的片已集中进其余物 {moved} 个 —— {ProcessArtifactLayout.ArtifactDirectoryName}"
                    + (failures.Count > 0 ? $"（{failures.Count} 个没搬成，仍在原处）" : string.Empty));
            }
        }

        /// <summary>
        /// **如实点名"成品目录里还留着的内层包"**（唯一出口；链尾那一站**无条件**调它）。
        ///
        /// <para><b>为什么不能当过程物清掉</b>（用户 2026-09-28 真机：`amb909\amb909\amb.7z.001..004`
        /// 一直留在成品目录里，他以为是"忘了搬进其余物"）：过程物（内层包）能清的前提是"它的内容物已经
        /// 解出来了" —— 这一单跑成功、内层包被判**已完成**才成立。而"这一轮根本没跑"的内层包，内容还在它
        /// 里面，清掉（其余物在删除档下是**彻底删除**）等于把唯一一份内容删了 ⇒ 只做一件事：**如实点名 +
        /// 告诉他怎么处理**（判据与搬运用同一套：分卷组取 `VolumePaths` 全卷，单文件取自己；只认在本单输出目录里的）。</para>
        ///
        /// <para>⚠ 2026-10-15：这一段原来长在 <see cref="CollectChainInnerPackagesIntoRestAsync"/> 里，
        /// 跟着"链尾要不要搬"那一个判据（`!_layerReclaimThisBatch`）一起被跳过；而逐层回收现在**恒开**
        /// ⇒ 那一整段在普通档下永不执行 ⇒ 这句点名被**静默**掉（实测 `ChainRestSweepTests` 三条守门变红）。
        /// 判据与动作于是拆开：**诊断恒做**，搬照旧只在该搬的时候搬。</para>
        /// </summary>
        private void ReportLeftBehindInnerPackages(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
        {
            if (rootTask == null || chainTasks == null || chainTasks.Count == 0)
            {
                return;
            }

            string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
                ? rootTask.ContentDirectoryPath
                : rootTask.OutputPath;

            try
            {
                var leftBehind = new List<string>();

                /*
                 * ⛔ 判据与搬运那一边**同一套**（`PlanChainInnerPackageMoves`）：只有"自己这一单成功且校验通过"
                 * 的内层包才会被收进其余物 —— 在这里再点名它们就是自相矛盾的假警报。
                 *
                 * ⚠ 2026-10-04 起"根任务成功 ⇒ 链上内层包都会被收走"这半句**不再成立**：
                 * 未解开过的内层包一条都不搬（用户当场推翻旧口径，见 PlanChainInnerPackageMoves）。
                 * 两边必须同时改（§9.5：同一件事只能有一个判据），否则又会出现
                 * "警告说还留着、下一行就搬走删掉"那种自相矛盾。
                 *
                 * 真机 2026-10-01：这一行写着「成品目录里还留着 2 个内层包没被清理（它们的内容还没解出来，
                 * 清掉就等于删内容）」，紧接着两行就把那两个搬进其余物并彻底删掉了 ——
                 * 用户读到的是"程序自己跟自己打架"，而真正没被清掉的那两个（撞名搬失败的）它反倒没说是为什么。
                 */
                foreach (ArchiveTask? candidate in chainTasks)
                {
                    if (candidate == null ||
                        ReferenceEquals(candidate, rootTask) ||
                        !candidate.IsContinuationTask)
                    {
                        continue;
                    }

                    bool candidateSucceeded =
                        candidate.Outcome == TaskOutcome.Succeeded &&
                        candidate.OutputVerification == OutputVerificationOutcome.Passed;

                    // 会被收走的（**只有它自己成功那一种**）：不进"还留着"的名单。
                    if (candidateSucceeded)
                    {
                        continue;
                    }

                    IEnumerable<string> candidatePaths = candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0
                        ? candidate.VolumePaths
                        : new[] { candidate.CurrentPath };

                    foreach (string candidatePath in candidatePaths)
                    {
                        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(outputRoot) &&
                            ArchivePathGuard.IsInsideRoot(outputRoot, candidatePath, out _))
                        {
                            leftBehind.Add(Path.GetFileName(candidatePath));
                        }
                    }
                }

                if (leftBehind.Count > 0)
                {
                    AppendLog(
                        "WARN",
                        $"{rootTask.LogName}：成品目录里还留着 {leftBehind.Count} 个内层包没被清理（它们的内容还没解出来，"
                        + $"清掉就等于删内容）：{string.Join("、", leftBehind.Take(5))}"
                        + $"{(leftBehind.Count > 5 ? " 等" : string.Empty)}"
                        + " —— 要解就把它们加进任务列表；删除档只清「内容物已经解出来」的过程物。");
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{rootTask.LogName}：清点留在成品目录里的内层包失败（不影响这一单）：{ex.Message}");
            }
        }

        /// <summary>
        /// 一条续解链里"该收进其余物的内层包"的搬运计划（**纯计划：一个字节都不动**）。
        ///
        /// <para>抽成静态方法是为了能被用例直接钉住（真机 2026-10-01 那一类缺陷全在"名字怎么算"上，
        /// 而算名字这一步以前埋在 <c>Task.Run</c> 的匿名体里，谁都测不到）。</para>
        ///
        /// <para><b>⛔ 撞名让位必须把"本计划里前面那几条"也算进去</b>（<c>reserved</c>）：
        /// 只问文件系统的话，**两条同名内层包会算出同一个目标** —— 第一条搬成功、第二条撞上
        /// <c>already exists</c> 而**留在原地**。用户 2026-10-01 真机 BBBB / CCCC 两批的现场就是它：
        /// <c>…\111\111\111\111.part1.rar</c> 与 <c>…\222\222\222\222.zip</c> 卡在内容物里没被收走，
        /// 日志写着「内层包没能移入其余物：… The file '…\其余物\111.part1(1).rar' already exists.（它留在原地）」。
        /// 口径与源包搬运（<c>SourcePackageMover.Plan</c>）**逐字相同**：reserved + <c>MakeUniqueTarget</c>。</para>
        /// </summary>
        /// <param name="rootTask">这一条链的根任务（其余物挂在它身上）。</param>
        /// <param name="chainTasks">整条链的任务（含根任务自己，会被跳过）。</param>
        /// <param name="restDirectory">其余物目录（定稿时算好的那一个，⛔ 不现场重算）。</param>
        /// <param name="outputRoot">根任务的输出根（只搬这个范围里的内层包）。</param>
        /// <param name="keepRules">
        /// 「内容物保留关键词」判据（用户 2026-10-04 的新功能；唯一出口 <see cref="ContentKeepRules"/>）。
        /// 命中它的内层包**不搬进其余物**（"碰都不碰"），整组一起留；空判据 ⇒ 一个都不拦（老行为）。
        /// 传 <c>null</c> 等价于 <see cref="ContentKeepRules.Empty"/>。
        /// </param>
        internal static List<(string From, string To)> PlanChainInnerPackageMoves(
            ArchiveTask? rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            string? restDirectory,
            string? outputRoot,
            ContentKeepRules? keepRules,
            out List<string> warnings)
        {
            var planned = new List<(string, string)>();
            warnings = new List<string>();

            ContentKeepRules rules = keepRules ?? ContentKeepRules.Empty;

            if (rootTask == null ||
                chainTasks == null ||
                chainTasks.Count == 0 ||
                string.IsNullOrWhiteSpace(restDirectory))
            {
                return planned;
            }

            string artifactRoot = restDirectory!;

            // 本计划**已经排出去的名字**（见方法注释：不问它就必然撞名）。
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /*
             * ⛔ 谁的"过程物"要收：**只有真的被解开过的**续解任务（内容已经落盘）。
             *
             * <para>用户 2026-10-04 当场推翻 2026-09-28 的旧口径 —— 原话：
             * 「链上那个从未解开过的内层包（它躺在其余物里）—— **不是没解开你为什么要放在其余物里面，啊**」。
             * 老写法是 `if (!candidateSucceeded && !rootSucceeded) continue;`：根任务一成功，
             * 链上**所有**续解任务（失败的 / 这一轮没跑的 / 跳过的）都被当过程物收走，
             * 而链尾「其余物按设置处理」那一档在「彻底删除」下是**永久删除**
             * ⇒ 还没解出来的内容连包一起没了（不可逆）。
             *
             * 新口径（与"内层包按有没有被解开过分类"同一句话）：
             * · **解开过的**（自己这一单成功 + 输出校验通过 ⇒ 内容已落盘）= 过程物 ⇒ 收进其余物、照旧按档处理；
             * · **从未解开过的**（停在半路留下的分支 / 命中「内容物保留关键词」的 / 到上限没展开的 /
             *   失败与没跑成的）= **还没处理的内容** ⇒ 原样留在成品目录里，⛔ 一条都不进其余物。
             *
             * 判据**不新造**：就是本方法一直在用的那条既有事实（`Succeeded` + `Passed`），
             * 以及上面「内容物保留关键词」那一档；⛔ 绝不按"名字像不像归档"去猜。
             * ⚠ 与 `CollectChainInnerPackagesIntoRestAsync` 里那份"还留着"名单**同一套判据**（§9.5）。
             */
            foreach (ArchiveTask? candidate in chainTasks)
            {
                if (candidate == null ||
                    ReferenceEquals(candidate, rootTask) ||
                    !candidate.IsContinuationTask)
                {
                    continue;
                }

                bool candidateSucceeded =
                    candidate.Outcome == TaskOutcome.Succeeded &&
                    candidate.OutputVerification == OutputVerificationOutcome.Passed;

                if (!candidateSucceeded)
                {
                    continue;
                }

                string path = candidate.CurrentPath;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                /*
                 * ⛔ 分卷组必须**整组一起搬**（2026-09-28 真机：内层包是 4 卷分卷组时只搬走了
                 * `amb.7z.001` 并把它彻底删掉，`.002/.003/.004` 留在成品目录里 —— 用户原话
                 * "同样犯了 winrar 会犯的问题：只删除 .001 为首的分卷头文件，其他分卷还残留着"）。
                 * 搬一半等于把一套完整的包拆成废件，比不搬糟得多。
                 *
                 * 判据与"源包整组一起移"**同一套**：分卷组取 VolumePaths 全卷，单文件取它自己。
                 */
                var groupPaths = new List<string>();

                if (candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0)
                {
                    foreach (string volumePath in candidate.VolumePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(volumePath) && File.Exists(volumePath))
                        {
                            groupPaths.Add(volumePath);
                        }
                    }
                }

                if (groupPaths.Count == 0)
                {
                    groupPaths.Add(path);
                }

                /*
                 * 兜底（**宁可不动，也不搬一半**）：分组信息没拿到（IsVolumeGroup=false），
                 * 但名字明显是分卷、同目录里还躺着同组的别的卷 —— 这一卷不搬，写清为什么。
                 * 留下的是一套完整的包，用户还能自己接着处理。
                 */
                if (!candidate.IsVolumeGroup && HasSiblingVolumeBeside(path))
                {
                    // 日志由调用方写（纯计划里不写盘、不记日志 —— 这样它才测得动）。
                    warnings.Add(
                        $"{rootTask.LogName}：{Path.GetFileName(path)} 看着是分卷组的一卷，"
                        + "但没拿到整组清单 —— 这次不搬它（搬一半会把一套包拆成废件）。");
                    continue;
                }

                /*
                 * ===== 「内容物保留关键词」：命中就**整组都不搬**（用户 2026-10-04 的新功能）=====
                 *
                 * 用户原话：「只要文件名里面包含着这个字符就不能动……这些压缩文件碰都不要碰」。
                 * 链尾把内层包收进其余物，是为了让「删除操作」那一档把它们清掉；命中关键词的既然
                 * "碰都不碰"，那就**一步都不动**：不搬、不改名、后面的删除也删不到它（它不在其余物里）。
                 *
                 * ⛔ 判据唯一出口 <see cref="ContentKeepRules"/>（包含即命中 / 大小写不敏感 / 不做通配正则）。
                 * ⛔ 分卷组**整组一起判**：一卷命中就整组留着，绝不只留一半。
                 * ⛔ 空关键词列表 ⇒ 这里一个都不拦（行为与以前逐字相同）。
                 */
                string? keepKeyword = groupPaths
                    .Select(groupPath => rules.FindMatch(groupPath))
                    .FirstOrDefault(keyword => keyword != null);

                if (keepKeyword != null)
                {
                    warnings.Add(
                        $"{rootTask.LogName}：{Path.GetFileName(path)} 命中「内容物保留关键词」（{keepKeyword}）"
                        + " —— 按设置碰都不碰，这次不搬进其余物（它留在原处，也不参与后面的删除）。");
                    continue;
                }

                foreach (string groupPath in groupPaths)
                {
                    // 已经在其余物里了（重复调用 / 上一轮搬过）：跳过。
                    if (SafePathHelper.GetFullPathSafe(Path.GetDirectoryName(groupPath) ?? string.Empty)
                            .EndsWith(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(outputRoot) &&
                        !ArchivePathGuard.IsInsideRoot(outputRoot, groupPath, out _))
                    {
                        // 不在根任务的输出范围内（理论上不该发生）：不动它。
                        continue;
                    }

                    /*
                     * 撞名（同一条链里两个同名内层包）自动让位，绝不覆盖 ——
                     * **本计划里已经排出去的名字也算撞名**（reserved，见方法注释）。
                     */
                    string target = Path.Combine(
                        artifactRoot,
                        FileNameHelper.SanitizeFileName(Path.GetFileName(groupPath)));

                    if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
                    {
                        target = ProcessArtifactLayout.MakeUniqueTarget(
                            target,
                            isDirectory: false,
                            reserved,
                            FileSystemArtifactTargetProbe.Instance);
                    }

                    reserved.Add(target);
                    planned.Add((groupPath, target));
                }
            }

            return planned;
        }

        /// <summary>
        /// 把"这段活干了多快"说成人话（"21.0 MB/s"）——直读那一条日志用它（用户 2026-09-25 第 38 条）。
        /// 速度按**写出字节 ÷ 用时**算：直读的真实工作量就是把这些字节从源读到成品。
        /// </summary>
        private static string DescribeThroughput(long writtenBytes, TimeSpan elapsed)
        {
            if (writtenBytes <= 0 || elapsed.TotalSeconds <= 0.05)
            {
                return "速度算不出来（太小或太快）";
            }

            double mibPerSecond = writtenBytes / 1024d / 1024d / elapsed.TotalSeconds;

            return $"{mibPerSecond:0.0} MB/s";
        }

        /// <summary>
        /// 「一组分卷的第一卷名字被改坏」时，把**我们自己产出的**那一卷改回标准命名（用户 2026-09-25 第 37 条）。
        ///
        /// <para>真机现场：他那个 12.22 GiB 的容器解出来的是三卷 7z，第一卷
        /// <c>Code Complete-BZ.7z(删掉.001</c>（打包者塞了字、吃掉右括号），后两卷名字正常。
        /// 7-Zip 按名字找不到后续卷 → 把它当"通用分片" → 解出来是一个等大的垃圾文件；
        /// 于是那 12 GiB 的内容永远出不来（用户看到的是"20 G 的素材只出来 8 G"）。</para>
        ///
        /// <para>⛔ 只对**续解任务**（文件是我们自己从上一层解出来的过程物）改名：
        /// 那是把我们的产物摆正，不是动用户的源文件（不变量 1）。
        /// 用户自己添加的坏名字分卷走不了这一条 —— 会由后面的「通用分片」闸门如实报「分卷缺失」并给出改名建议。</para>
        ///
        /// <para>改完之后**重拍源文件快照**（不变量 11）：名字换了、路径换了，旧快照对不上会让这一单
        /// 被"源文件已变化"拦下（与"分卷补齐"那条路同一处理）。</para>
        /// </summary>
        private string TryRepairBrokenVolumeChainName(ArchiveTask task, string engineArchivePath)
        {
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return engineArchivePath;
            }

            // 只有"从别的包里解出来的"才允许改名（见方法注释）。
            if (!task.IsContinuationTask)
            {
                return engineArchivePath;
            }

            IReadOnlyList<string?> names = TryListFileNames(Path.GetDirectoryName(task.CurrentPath));

            if (names.Count == 0)
            {
                return engineArchivePath;
            }

            BrokenVolumeChainRepair.RenamePlan? plan = BrokenVolumeChainRepair.TryPlan(task.CurrentPath, names);

            // 名字本来就标准（零条改名）= 什么都不用做，也**不许**记那句"被改坏"的日志。
            if (plan == null || BrokenVolumeChainRepair.IsNoOp(plan))
            {
                return engineArchivePath;
            }

            if (!BrokenVolumeChainRepair.TryApply(plan, out string failure))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一卷的名字看着被改坏了（{plan.Describe()}），但改名没成功：{failure}"
                    + " —— 保持原样继续，下面会如实报结论。");

                return engineArchivePath;
            }

            AppendLog(
                "INFO",
                $"{task.LogName}：分卷名字被改坏，已在自己的产物里摆正 —— {plan.Describe()}"
                + $"（源包一个字节都没动；改的是上一层解出来的过程物）。原位置：{task.CurrentPath}");

            string previousPath = task.CurrentPath;
            task.CurrentPath = string.IsNullOrWhiteSpace(plan.FirstVolumePathAfterRename)
                ? previousPath
                : plan.FirstVolumePathAfterRename;

            // 路径换了就必须重拍快照（不变量 11：按"位"比对，旧路径会立刻判成"文件不见了"）。
            task.CaptureSourceSnapshot();

            return task.CurrentPath;
        }

        /// <summary>列一个目录里的**文件名**（读不了就返回空集合，调用方按"没有线索"处理）。</summary>
        private static IReadOnlyList<string?> TryListFileNames(string? directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return Array.Empty<string?>();
                }

                return Directory.EnumerateFiles(directory!, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string?>();
            }
        }

        /// <summary>
        /// 链尾对**单个**成功任务补做「删除操作」那一档（一键处理专用；幂等：已处理过的直接跳过）。
        /// </summary>
        /// <param name="chainTasks">
        /// 整条链的全部任务（根 + 续解子任务）。**调用方照旧按位置传**（同一批的链信息在别处还要用），
        /// 但本档 2026-10-04 起**不再读它** —— 判据只问"这一单自己的根源包那一层"，
        /// 不再要求链上每个内层包都成功（用户原话：「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」，
        /// 见下面那一段长注释）。
        /// </param>
        /// <param name="cancellationToken">取消令牌（取消那一档由执行体 <c>RestItemPurger</c> 第 3 道门槛判）。</param>
        private async Task ApplyRestHandlingAfterChainAsync(
            ArchiveTask task,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken)
        {
            _ = chainTasks;
            _ = cancellationToken;

            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * ⚠ 2026-10-13（困难批：6 份样本 ①✔ 而只差判据 ③，日志 + 运行期定案）：
                 * 「其余物 = 不动」这一档过去在这里**整档早退** ⇒ 「程序自己产出、已被整组接走」的那份副本
                 * （接片建的链接 / 产出方落点里那一份）**永远没人认领** ⇒ 判据 ③「盘上无残留」红。
                 *
                 * <para><b>用户口径（2026-10-13 拍板选 A + 当天再次点名）</b>：「不动其余物」的"不动"
                 * = **不删用户的东西**；而**程序自己产出、且已被整组接走**的那份多余副本**允许挪位置**
                 * （搬进同层的 `其余物\`，⛔ 一个字节都不删）；原话还有一句
                 * 「连"程序自己产的、已经没用的那份副本" 这个不就是**过程物**吗」。</para>
                 *
                 * <para>⇒ 这一档只补调**"产出方那一份片"那一个出口**（它自己按档分派：Keep ⇒ 搬进同层其余物、
                 * 一个字节都不删，见 <see cref="PurgeGroupLandedPieces"/>）。⛔ **用户源片那一半照旧一步都不动**
                 * —— 下面那两段（`SettleOneConsumedPieceTask` 的搬运与"借来用过的用户源片"账）**一个都不调**，
                 * 所以"不动其余物"对用户自己的东西仍然是一个字节都不碰。</para>
                 */
                if (task.Outcome == TaskOutcome.Succeeded)
                {
                    /*
                     * ⛔ 组基名**不许拿这一单自己的包基名猜**（断点实测 `1111`：链尾那一单是 `111-W0-0.zip`，
                     * 它自己的基名与那一组 `111-L2` 毫无关系 ⇒ 拿它去问等于什么都没问），
                     * ⛔ 也**不许拿 `_adoptedLandedPieces` 当来源**（同一次断点实测：那一本账 **0 条** ——
                     * 这两片是"引擎报缺卷 ⇒ 发布"那条路留下了，根本没走"接片"那一档）。
                     * ⇒ 只认既有那本"**谁吐出过哪一组的片**"的账（<see cref="_groupPieceProducers"/>，
                     * 写点 = `PublishUnresolvedVolumePieces` → `RememberGroupPieceProducer`），
                     * 问法 = **这一单在哪些组里当过我产出的那一份**，逐组按档处理它自己那一份。
                     */
                    foreach (string groupBaseName in SnapshotGroupPieceProducerBaseNames())
                    {
                        if (TrySnapshotGroupPieceProducers(
                                groupBaseName,
                                out List<(ArchiveTask Task, string PieceName)> producers)
                            && producers.Any(item => item.Task != null && ReferenceEquals(item.Task, task)))
                        {
                            PurgeGroupLandedPieces(task, groupBaseName);
                        }
                    }

                    /*
                     * ⚠ 2026-10-13（困难批 `1111` 判据 ③，插桩实测那一轮定案）：**产出方自己那一档**。
                     *
                     * <para>实测事实（`[排障·Keep档]` / `[排障·收片]` 两组行，夹具 `dbg-b6a4ee4a`）：
                     * 这一批里走进本档的只有 `recipe.json`(Skipped)、`111-W2-0.rar`、`111-W1-0.zip`
                     * 三单，而它们**每一单都拿自己的包基名**去问（`111-W2-0` / `111-W1-0`）——
                     * 那一组真正叫 `111-L2` ⇒ `targets.Count` 恒 0 ⇒ 「产出方那一份已经落地的片」没人认领。</para>
                     *
                     * <para>为什么会这样：那一组是被**批末补判**交给"续解出来的那一片"那一单去解的
                     * （入口那一单 `111-W0-0.zip` 的源包处理档是"链尾补搬"⇒ 走的是 `DeferredToChainEnd`
                     * 那一支，**根本走不到本档**），所以"谁在解"那本账里记的是**别人**；
                     * 而**我亲手从自己这棵树里链接走了一片**这件事只有这本账知道
                     * （<see cref="_byteBudgetLinkedSources"/>，写点 = <see cref="TryLinkMissingPiecesByByteBudget"/>）
                     * ⇒ 就按它问：这一单是不是**给这一组供过片**。</para>
                     *
                     * <para>⛔ 判据没放宽：下面那一档的每一条闸门（终态成功 / 可证完整 / 在盘上 / 在落点根内 /
                     * 不是任何一单的源 / 名字仍属于这一组）一条都不绕过；⛔ 用户自己的源片一个字节都不碰。</para>
                     */
                    foreach (string groupBaseName in _byteBudgetLinkedSources
                                 .Where(item => ReferenceEquals(item.Producer, task))
                                 .Select(item => item.GroupBaseName)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        PurgeGroupLandedPieces(task, groupBaseName);
                    }

                    // 我亲手链接过来源的那几组 —— 只处理**这一单正是那一组的接手方**的那些
                    // （`_groupConsumerByName` 是既有账：批末那一组齐了、交给哪一单去解时写的）。
                    foreach (string groupBaseName in _byteBudgetLinkedSources
                                 .Select(item => item.GroupBaseName)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (_groupConsumerByName.TryGetValue(groupBaseName, out string? consumerName)
                            && string.Equals(consumerName, task.FileName, StringComparison.Ordinal))
                        {
                            PurgeGroupLandedPieces(task, groupBaseName);
                        }
                    }

                    // 另外：这一单自己就是"入口包那一单"时（它 CurrentPath 已是第 1 卷）照旧问一次它自己的基名。
                    string ownBaseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath ?? string.Empty);

                    if (ownBaseName.Length > 0)
                    {
                        PurgeGroupLandedPieces(task, ownBaseName);
                    }
                }

                return;
            }

            /*
             * ===== 判据逐条查、**每一条拦下都要说清是哪一条**（用户 2026-09-30 真机报的 bug）=====
             *
             * 现场：用户设了"源包放入其余物 + 其余物彻底删除"，链跑完（日志"没有发现可继续解压的内层包"），
             * 然后**什么都没有**：源包原地不动、其余物没生成、日志里一个字都没说为什么 ——
             * 老写法在下面那一支直接 `return`（一句注释"什么都不做"），用户只能猜。
             *
             * 现在：判据一条一条查出来，拦下时写一行 WARN，点名**是哪一条不成立**；
             * 其中 L4 那一档由唯一出口 <see cref="ResultCompletenessClassifier"/> 给出
             * 「哪一层 + 什么原因」（`verdict.Blocker`）。
             */
            if (task.Outcome != TaskOutcome.Succeeded)
            {
                AppendRemovalBlocked(
                    task,
                    $"{StatusText.ChainRestBlockedTaskOutcomeFormat}（机器终态：{task.Outcome}）");

                return;
            }

            /*
             * ⚠ 2026-09-30（检验等级 L4）：第二条判据从"校验通过"**收紧**成"可证完整"——
             * 唯一出口 <see cref="ResultCompletenessClassifier"/>。老口径把"拿不到清单、
             * 只做了非空底线校验"也算成通过，这一档**没有任何证据**却能走到链尾删其余物。
             * （下游 <see cref="RestItemPurger"/> 还会再挡一次，但**同一件事只许有一个判据**：
             * 这里留一个更弱的版本，等于给下一个人两条互相矛盾的口径。）
             */
            ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(task);

            if (!completeness.AllowsSourceRemoval)
            {
                // 红线：判不出 / 可证不完整 ⇒ 什么都不做，但**必须写清是哪一条判据拦下的**。
                AppendRemovalBlocked(task, completeness.Blocker);

                return;
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                AppendRemovalBlocked(task, StatusText.ChainRestBlockedNoRestDirectoryFormat);

                return;
            }

            if (!Directory.Exists(task.RestDirectoryPath))
            {
                AppendRemovalBlocked(
                    task,
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.ChainRestBlockedRestMissingFormat,
                        task.RestDirectoryPath));

                return;
            }

            /*
             * ===== 借来用过的用户源片**也算源包**：跟这一组一起进其余物、一起按删除档处理 =====
             * （用户 2026-10-05 拍板原话：「那肯定啊，这个算原包，最后放到其余物里面一同删除」）
             *
             * 现场：一组跨盘 ZIP 的末片压在包里、其余三片散在三个源目录里 —— 递归层把三片硬链接过来
             * 凑齐、整组解开、结果发布成功。那三片就是**这一组的源包**，不能因为"它们自己那一单是跟班卷"
             * 就永远留在用户目录里。
             *
             * 位置刻意排在上面那几道判据**之后**（消费方成功 + 可证完整 + 其余物目录在）：判据一条都不放宽；
             * 搬运本体仍是既有出口 `ExecuteSourcePackageMove`（整组一起移、绝不覆盖、跨盘先复制再删原件、
             * 单个失败不影响其余），⛔ 不另写一套搬运动作。
             */
            CollectConsumedSourcePiecesIntoRest(task);

            /*
             * ===== 「整条链都得成功」那道门槛 2026-10-04 **被用户推翻**（原第 37 条）=====
             *
             * 老口径（2026-09-25 第 37 条那次 12.22 GiB 事故之后加的）：链上**每一个**续解任务都成功
             * 且可证完整，链尾才按「删除操作」处理其余物 —— 理由是"链没跑完时过程物是唯一的产物线索"。
             *
             * 用户 2026-10-04 真机（日志 271/272）否掉了它，原话：
             * 「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」——
             * 他选了「其余物 = 彻底删除」，只因为**内层包**失败（根源包自己那一层是成功的、
             * 输出校验也过了）就整份留着，在他眼里就是"设置没生效"。
             *
             * 现在的口径：**判据只问"这一单自己的根源包那一层"** ——
             * ① 根源包成功（<see cref="TaskOutcome.Succeeded"/>，上面已查）；
             * ② 输出校验通过且**完整性可证**（L4 唯一出口，上面已查）；
             * ③ 未取消（<c>RestItemPurger</c> 第 3 道门槛，执行体里查）；
             * ④ 其余物目录是定稿那一刻记下来的那一个（上面已查 + 执行体第 4/5 道门槛）。
             * ⛔ **不再要求链上每个内层包都成功**。
             *
             * 红线一个字没动（全部由下面这些闸门继续钉着，与哪一档无关）：
             * · 失败 / 部分完成 / 取消的**那一单** ⇒ 源包原地不动、其余物不生成（上面两条早退 + 执行体）；
             * · 其余物删除只有 <c>RestItemPurger</c> 一个执行体，六道门槛（含「半套分卷」
             *   <c>RestVolumeCompletenessGate</c>）照旧全过；
             * · 「空间不足」模式与其余物处理的口径不变；
             * · 判据只读事实（枚举终态 / 校验结果 / 取消令牌），⛔ 不比中文。
             *
             * 确实不满足上面四条而没处理其余物时，仍然**逐链点名**说清原因 + 路径
             * （<see cref="AppendRemovalBlocked"/> / <see cref="RecordRestKept"/>，三处消费）。
             */
            ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);

            if (runtime.PurgedBytes > 0 || runtime.RestPurged)
            {
                return;
            }

            await RunRestHandlingAsync(task, runtime, oneClickRun: false, force: true);
        }

        /// <summary>
        /// **其余物为什么原封不动地留着** —— 那一句话的唯一出口（用户 2026-10-04 真机）。
        ///
        /// <para>现场：日志 271/272 里写着「<c>Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）</c>」、
        /// 「<c>第6集.7z：链尾没有按「删除操作」处理其余物 / 源包 —— 任务没有成功…</c>」，
        /// 可①页、批末诊断、失败清单里**一个字都没有** —— 用户看到的是"解压成功、其余物还在"，
        /// 只能自己去猜。这一句把那条日志里的事实搬到他能看见的三个地方（同一份文字，⛔ 不各写一遍）。</para>
        /// </summary>
        private static string DescribeRestKeptNote(ArchiveTask task, string reason)
        {
            if (task == null)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                ? string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteNoPathFormat, reason)
                : string.Format(CultureInfo.CurrentCulture, StatusText.RestKeptNoteFormat, task.RestDirectoryPath, reason);
        }

        /// <summary>
        /// 把"其余物没处理 + 为什么"记到任务上（①页「错误信息」列读 <c>ErrorMessage</c>，
        /// 批末诊断与失败清单读 <c>RestKeptReason</c> —— 三处同一份文字）。
        ///
        /// <para>⚠ 刻意**不改**任务的终态 / 状态：内容物是好的（这一单确实成功），
        /// 只是"其余物这一档没做" —— 改终态会连带改掉批末计数与四条删除裁决，那不是措辞的事。
        /// 幂等：同一句只写一次（链尾可能两条路都判到）。</para>
        /// </summary>
        private static void RecordRestKept(ArchiveTask task, string reason)
        {
            if (task == null || string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            string note = DescribeRestKeptNote(task, reason);

            task.RestKeptReason = note;

            if (!task.ErrorMessage.Contains(note, StringComparison.Ordinal))
            {
                task.ErrorMessage = string.IsNullOrWhiteSpace(task.ErrorMessage)
                    ? note
                    : task.ErrorMessage + "；" + note;
            }
        }

        /// <summary>
        /// 链尾"按档处理其余物 / 源包"被某一条判据拦下时的**唯一**一行日志
        /// （用户 2026-09-30：源包被留下时必须说清是哪一条判据 —— ⛔ 不许静默 return）。
        ///
        /// <para>措辞与源包搬运动那几条 WARN 分开：这里说的是"链尾这一档没做"，
        /// 而 <paramref name="why"/> 里已经带了具体判据（含"哪一层 + 什么原因"）。</para>
        /// </summary>
        private void AppendRemovalBlocked(ArchiveTask task, string why)
        {
            // 这条 WARN 就是"其余物为什么还在"的答案 ⇒ 同一句话也要落到①页与报告里（用户 2026-10-04 真机）。
            RecordRestKept(task, why);

            AppendLog(
                "WARN",
                $"{task.LogName}：{StatusText.ChainRestBlockedPrefix} —— {why}；"
                + "一个字节都没动（失败 / 取消 / 判不出完整性一律不动）。");
        }

        /// <summary>
        /// L4（完整性分类）的**那一行证据** —— 全仓唯一一处写它。
        ///
        /// <para>为什么要写：这一条结论就是"敢不敢动用户源包"的唯一依据。
        /// 2026-09-30 的真机日志里，源包被留下了、可**为什么**只有一个笼统的"没有可用的归档清单可核对"，
        /// 用户根本不知道是哪一层、下一步该做什么。</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>可证完整</b>：只在"这一次真的要按设置动源包"时才写（留在原地档没什么要交代的）。</description></item>
        /// <item><description><b>判不出 / 可证不完整</b>：**一律写**，而且写的是"为什么没删源包：判据 + 哪一层 + 原因"。</description></item>
        /// </list>
        /// </summary>
        private void AppendCompletenessEvidence(
            ArchiveTask task,
            OutputVerificationResult verification,
            SourceHandlingMode sourceHandling)
        {
            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(
                verification,
                task.ManifestExpectation);

            if (verdict.AllowsSourceRemoval)
            {
                if (sourceHandling == SourceHandlingMode.KeepInPlace)
                {
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：{verdict.Message}（L3 核对依据：{DescribeManifestBasis(task.ManifestExpectation)}；"
                    + $"机器结论：{verdict.Evidence}）");

                return;
            }

            AppendLog(
                "WARN",
                $"{task.LogName}：{verdict.Blocker}（机器结论：{verdict.Evidence}）");
        }

        /// <summary>L3 的预期清单是从哪一层来的（给人看的一句话；取不到时说清是哪一层、为什么）。</summary>
        private static string DescribeManifestBasis(ManifestExpectation expectation)
        {
            return expectation.Source switch
            {
                ManifestExpectationSource.LeafLayer =>
                    $"{expectation.LayerLabel}（产出最终内容物的那一层）的清单 {expectation.Expected!.FileCount} 个文件 / "
                    + $"{expectation.Expected.TotalUncompressedSize} 字节",
                ManifestExpectationSource.OuterList =>
                    $"归档自己的清单 {expectation.Expected!.FileCount} 个文件 / {expectation.Expected.TotalUncompressedSize} 字节",
                ManifestExpectationSource.QueryArchive =>
                    $"引擎对这份归档现列出来的清单",
                _ => expectation.UnavailableReason
            };
        }

        /// <summary>
        /// 链结束后补搬的后台本体：判据 → 执行 → 结论。**只允许在后台线程上跑**。
        /// </summary>
        private async Task<DeferredSourceMoveWork> RunDeferredSourceMoveWorkAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            SourceHandlingMode sourceHandling,
            CancellationToken cancellationToken)
        {
            var logEntries = new List<(string Level, string Message)>();

            // 幂等（硬要求）：已经搬成的一律跳过 —— 不靠"文件还在不在"猜。
            if (rootTask.SourcePackageMove == SourcePackageMoveState.Done)
            {
                return new DeferredSourceMoveWork(logEntries);
            }

            if (sourceHandling == SourceHandlingMode.KeepInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{rootTask.LogName}：源包处理档是「留在原地」，链结束后的补做按该档不动源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsContinuationTask)
            {
                // 只处理最外层源包：内层包的"源文件"是其余物里的过程物（与本轮直接搬同一口径）。
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * ===== 判据一律读**事实**，⛔ 不读状态字符串（用户 2026-09-24 明确要求）=====
             *
             * 老写法是 `OneClickCoordinator.IsSuccessStatus(rootTask)`，也就是拿
             * `task.Status == "解压成功"` 去决定"要不要动用户唯一无法再生的那份源包"。
             * 那个字符串**证明不了**产物是好的 —— 真机日志里恰好出现过"状态写着解压成功、校验却已判否"，
             * 万一那条路走到这儿，用户的源包就会被删掉。
             *
             * 现在读的是**校验那一刻的事实**，而且用的是 L4 的**唯一出口**
             * <see cref="ResultCompletenessClassifier"/>（= 可证完整）：它要求"产物非空 + 与引擎清单逐条对得上"
             * （`ManifestCrossChecked = true`）—— **拿不到清单那一档算"判不出"，同样不许动源包**
             * （2026-09-30 检验等级），加上"机器终态不是部分完成/失败/取消"（<see cref="TaskOutcome.Succeeded"/>）。
             * 这些字段都由管线在结论成立的那一刻写、之后没有任何地方会为了显示去改它们。
             */
            /*
             * ⚠ 2026-09-30（检验等级 L4）：从"校验通过"**收紧**成"可证完整"
             * （唯一出口 <see cref="ResultCompletenessClassifier"/>，与链尾其余物那两道门同一口径）。
             *
             * 漏掉这一处会怎样：链尾补搬是**移动用户唯一无法再生的源包**。老口径下"没拿到清单、
             * 只做了非空底线校验"也算校验通过 —— 链尾照样把源包搬进其余物，而接下来的
             * "删除操作"档一起来就把它删了。判不出就是判不出：一个字节都不许动。
             *
             * ⚠ 与下面 `DescribeChainVerificationGap` 那一处**判据相同、刻意查两遍**：根任务总会
             * 落进那个目录，所以严格说这一处是冗余的早退；留着的理由是它给出的是"根任务自己判不出"
             * 这句更准的话，而且它排在分卷 / 内容物那几道门之前（红检实测：把下面那一处退回旧判据，
             * 用例 `形状B_链上有一层判不出完整性_链尾不补搬源包` 变红；只退这一处则被下面那一处接住）。
             * ⛔ 别为了"去重"删掉任何一处 —— 它们挡的是同一个不可逆动作，而两处的调用场景并不完全相同。
             */
            ResultCompletenessVerdict rootCompleteness = ResultCompletenessClassifier.Classify(rootTask);

            if (!rootCompleteness.AllowsSourceRemoval)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：{rootCompleteness.Blocker}" +
                    $"（机器结论：{rootCompleteness.Evidence}），链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.Outcome != TaskOutcome.Succeeded)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：根任务的机器终态不是「完成」（当前：{rootTask.Outcome}），" +
                    $"链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsVolumeGroup && !rootTask.IsVolumeComplete)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：分卷组不完整" +
                    (string.IsNullOrWhiteSpace(rootTask.VolumeInfoText) ? string.Empty : $"（{rootTask.VolumeInfoText}）") +
                    "，链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string destinationDirectory = ResolveContinuationOutputDirectory(rootTask);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：算不出根任务的最终输出目录，链结束后不补搬源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string? verificationGap = DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

            if (verificationGap != null)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：链里的输出校验没有全部通过（{verificationGap}），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            int contentFiles = CountContentFiles(destinationDirectory);

            if (contentFiles == 0)
            {
                // 内容物压根没出现（例如第二层解压失败 / 密码不对）：源包留在原地，其余物不为它生成。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：{destinationDirectory} 里没有内容物（只有过程物），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * ===== 按档位收尾：删 or 搬（**这一支以前只实现了"搬"**）=====
             *
             * 用户 2026-09-24 的第 19.4 条：设置=「校验通过后删除」时，选中**文件夹**里那些包一个都没被删，
             * 而单个文件会被删。诊断结论就落在这里 ——
             * 文件夹里的包绝大多数是"第一层只出内层包"的形状（假 MP4 + 尾部 ZIP + 内层加密分卷），
             * 于是它们全被记账成"留到链结束后补做"；而链结束后的这一段老代码写着
             * `if (sourceHandling != MoveToRest) { 写一句"按该档不搬源包"; return; }` ——
             * **DeleteAfterVerify 走到这里等于什么都没做**，源包自然而然留在了原地。
             *
             * 现在两种档位共用上面那同一套判据（内容物已定稿 + 全链校验通过 + 那个目录里确实有内容物 +
             * 未取消 + 不是内层包），只是最后的动作不同：一个搬进其余物、一个按 §9.5 删掉。
             * 失败 / 取消 / 校验未通过 → 上面每一条都已经 return，一个字节都不会动（红线不变）。
             */
            string artifactRoot = rootTask.RestDirectoryPath;

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                // 定稿计划没留下其余物目录：宁可不搬，也不要把用户的源包扔到一个猜出来的位置。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.LogName}：这一轮的其余物目录没有被记下来，为避免把源包搬到别处，" +
                    $"链结束后不补搬（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.LogName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            logEntries.Add((
                "INFO",
                $"{rootTask.LogName}：链结束后的补搬 —— 整条续解链已跑完，{destinationDirectory} 里有 " +
                $"{contentFiles} 个内容物且输出校验通过；本轮没有内容物可定稿、留到现在的源包按设置移入其余物。"));

            /*
             * ⛔ 2026-10-15（用户 2026-10-14 口径）：源包那一档只剩「留下 / 彻底删除」，而**两者都不搬** ——
             * 留下 = 一个字节都不动；彻底删除 = **就地删**（⛔ 不搬进其余物，用户逐字：
             * 「原包一直在原来的位置，选『彻底删除』也是就地删、不用移入其余物」）。
             * ⇒ 这里原来那次 `ExecuteSourcePackageMove(…, SourceMoveTrigger.AfterChain, …)`
             * （把源包搬进其余物 + 随后按「删除操作」清那一份）**整段删除**，
             * 改走与别处**同一条闸门链**（唯一出口 `PurgeLayerSourcePackage`）。
             */
            SourceHandlingMode deferredSourceHandling = RunOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(Settings?.SourceHandling);

            if (deferredSourceHandling != SourceHandlingMode.DeleteInPlace)
            {
                logEntries.Add((
                    "INFO",
                    $"{rootTask.LogName}：按「源包留下」这一档，链结束后源包一个字节都不动"
                    + $"（留在原处，随时可手工处理）。"));

                return new DeferredSourceMoveWork(logEntries);
            }

            /*
             * 「彻底删除」：走**同一条闸门链**（L4 完整性 + L5 收尾复核 + 未取消 + 属于本任务 + 半套分卷）。
             *
             * ⚠ 2026-10-15：这里**喂的是真证据**，⛔ 不是放宽闸门 —— 上面刚算出来的
             * <paramref name="rootCompleteness"/>（L4 唯一出口 <see cref="ResultCompletenessClassifier"/>）
             * 就是 `PurgeLayerSourcePackage` 那一档要的那份事实；机器终态、分卷完整性、内容物存在与否
             * 也都在上面逐条查过（任一条不过就在那一条 return 了，走不到这里）。
             * ⇒ 传 `reclaimVerdict` 之后，执行体不再要求 `commit`/`verification` 那两份"本轮定稿"事实
             * （链尾本来就没有它们），而 半套分卷闸门 / 幂等 / 路径形状 那几道**照旧全过**。
             */
            PurgeLayerSourcePackage(
                rootTask,
                verification: null,
                commit: null,
                logEntries,
                LayerPurgeTrigger.SourceHandlingDelete,
                reclaimVerdict: rootCompleteness);

            return new DeferredSourceMoveWork(logEntries);
        }

        /// <summary>
        /// 本任务的**最终输出目录**：归集开启时产物整棵被搬到归集目录，权威落点就是它。
        ///
        /// 转发到 <see cref="OneClickCoordinator.ResolveContinuationOutputDirectory"/>，
        /// 不在这里重写一遍 —— 续解、归集、源包补搬必须用同一个"内容物最终在哪"的口径。
        /// </summary>
        private static string ResolveContinuationOutputDirectory(ArchiveTask task) =>
            OneClickCoordinator.ResolveContinuationOutputDirectory(task);

        /// <summary>
        /// 链里的输出校验有没有缺口：**每一个**把这个目录当落点的任务都必须**可证完整**
        /// （L4 唯一出口 <see cref="ResultCompletenessClassifier"/>，2026-09-30 检验等级）。
        ///
        /// <para>
        /// 为什么要看**整条链**而不是只看根任务：用户那个形状里内容物是**续解子任务**产出的，
        /// 根任务那一轮的校验只覆盖了"过程物都搬进去了"。只看根任务，就会出现
        /// "第二层校验没过（比如声明的条目数与落盘不符）却照样把源包搬走"。
        /// </para>
        /// <para>
        /// 为什么是"每一个都必须过"而不是"有一个过就行"：同一个最终目录是链上所有任务共同产出的，
        /// 其中任何一个没通过校验，就没有"内容物已定稿并校验通过"这个事实 ——
        /// 这与"本轮直接搬"那条路径的口径一致（它也要求 L4 = 可证完整）。
        /// </para>
        /// <para>
        /// ⚠ 判据刻意**不是** <c>IsOutputVerified</c>：那个字段在"拿不到清单、只做了非空底线校验"时
        /// 也是 true（老口径下两者长得一模一样），而这一处的下游是**移动 / 删除用户的源包**。
        /// </para>
        /// </summary>
        /// <returns>
        /// null = 这条链在这个目录上校验齐了（且至少有一个任务确实落在这里）；
        /// 非 null = 说给用户听的原因（哪个任务没通过 / 压根没有任务落在这里）。
        /// </returns>
        internal static string? DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            bool anyLanding = false;

            foreach (ArchiveTask? candidate in EnumerateChainTasks(rootTask, chainTasks))
            {
                /*
                 * 同一分卷组的后续卷**不算这一条链上的产物**（用户 2026-10-01）：它没有解压过、
                 * 没有产物、也没有自己的落点目录 —— 它的落点是用同一套路径算法**重算**出来的，
                 * 于是必然与首卷那一单重合，拿它去比"链上的输出校验"必然得出"链里有一层没过"，
                 * 整组的源包就一个都删不掉了（真机现象）。
                 */
                if (candidate.IsVolumeGroupFollower)
                {
                    continue;
                }

                if (!SafePathHelper.PathEquals(
                        ResolveContinuationOutputDirectory(candidate), destinationDirectory))
                {
                    continue;
                }

                anyLanding = true;

                /*
                 * ⚠ 2026-09-30（检验等级 L4）：判据从 <c>IsOutputVerified</c>（= "校验通过"，
                 * 含"拿不到清单只做了底线校验"那一档）**收紧**成"可证完整"——
                 * 唯一出口 <see cref="ResultCompletenessClassifier"/>。这一处是**链尾补搬/删源**的最后一道
                 * 前置判据，判不出 ⇒ 源包一个字节都不许动。
                 */
                ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(candidate);

                if (!completeness.AllowsSourceRemoval)
                {
                    return $"{candidate.LogName}：{completeness.Message}"
                           + $"（机器结论：{completeness.Evidence}）";
                }
            }

            return anyLanding
                ? null
                : $"链里没有一个任务把 {destinationDirectory} 当落点（没有产物被定稿到那里）";
        }

        /// <summary>
        /// 链上的任务集合（调用方传进来的续解子任务 + 根任务自己），去重后逐个产出。
        /// 根任务单独兜一层：调用方可能只传了子任务，而"根任务也是链上的一员"这件事不能漏。
        /// </summary>
        private static IEnumerable<ArchiveTask> EnumerateChainTasks(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            var seen = new HashSet<ArchiveTask>();

            if (chainTasks != null)
            {
                foreach (ArchiveTask? candidate in chainTasks)
                {
                    if (candidate != null && seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }

            if (seen.Add(rootTask))
            {
                yield return rootTask;
            }
        }

        /// <summary>
        /// 数一个最终目录里**真正的内容物文件**：排除 <c>其余物</c>（含旧名 <c>过程物</c>）里的东西，
        /// 排除归档与分卷这类"待续解的过程物"，**并排除工作区自己那棵树**（用户 2026-09-30）。
        ///
        /// <para>这是"内容物确实已定稿"的事实依据 —— 不用"本轮搬了几条"这种过程数字
        /// （用户明确要求：别拿 move 计数当唯一判据）。
        /// 读不了目录时返回 0：宁可判定"没有内容物"而不动源包。</para>
        ///
        /// <para>⛔ 为什么必须排除工作区：工作区默认就建在目标目录里面（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），
        /// 而这一支是**删/搬源包（不可逆）之前的闸门** —— 把工作区里的暂存产物数成"内容物"，
        /// 等于用一个假事实去放行不可逆动作。判据唯一出口 <see cref="WorkspaceTree"/>。</para>
        /// </summary>
        private int CountContentFiles(string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            {
                return 0;
            }

            string workRoot = _pathService.WorkDirectory;

            try
            {
                int count = 0;

                // 「内容物保留关键词」判据：命中它的那些**就是内容物**（用户 2026-10-04 的新功能），
                // 所以"这一层有没有内容物"这件事要把它们数进去（判据唯一出口 ContentKeepRules）。
                ContentKeepRules keepRules = ContentKeepRules.FromSettings(Settings);

                foreach (string file in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
                {
                    if (WorkspaceTree.ShouldSkipEntry(file, workRoot))
                    {
                        continue;
                    }

                    if (ProcessArtifactLayout.IsInsideArtifactDirectory(file, destinationDirectory))
                    {
                        continue;
                    }

                    if (IsProcessArtifactFile(file) && !keepRules.ShouldKeep(file))
                    {
                        continue;
                    }

                    count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 归集之后其余物目录到底在哪。
        ///
        /// 归集把 <c>destDir</c> 整棵搬到 <c>归集目标\包名\</c>（**保留目录结构**），
        /// 所以"计划路径相对 destDir 的那一段"就是它在新位置下的相对路径。三种情况：
        /// ① 归集之后的位置已经存在（其余物真的被搬过去了）→ 用它；
        /// ② 它还不存在、而计划路径还在（归集只搬走了一部分）→ 用计划路径；
        /// ③ 两边都不存在（本次根本没有过程物）→ **跟着内容物走**，在归集目录里新建。
        ///
        /// ③ 是最容易写错的一档：没有过程物时 <c>destDir\其余物</c> 压根不存在，
        /// 只看"存在与否"就会退回那个**已经不存在**的旧位置，源包被扔进一个空目录（用户找不到）。
        /// </summary>
        private static string ResolveRestDirectoryAfterCollect(
            StageCommitResult commit,
            CollectResult? collected)
        {
            string planned = commit.ProcessArtifactDirectory;

            if (string.IsNullOrWhiteSpace(planned))
            {
                return string.Empty;
            }

            if (collected is not { Success: true } || string.IsNullOrWhiteSpace(collected.DestinationPath))
            {
                return planned;
            }

            try
            {
                // destDir 从计划里的路径反推：其余物目录永远在它下面（计划就是这么算的）。
                string destDir = Path.GetDirectoryName(planned) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(destDir))
                {
                    return planned;
                }

                string moved = Path.Combine(
                    collected.DestinationPath,
                    Path.GetRelativePath(destDir, planned));

                if (SafePathHelper.DirectoryExists(moved))
                {
                    return moved;
                }

                return SafePathHelper.DirectoryExists(planned) ? planned : moved;
            }
            catch
            {
                return planned;
            }
        }

        private static string ResolveContentDirectoryAfterCollect(
            StageCommitResult commit,
            CollectResult? collected)
        {
            string planned = commit.ContentDirectory;

            if (string.IsNullOrWhiteSpace(planned))
            {
                return string.Empty;
            }

            if (collected is not { Success: true } || string.IsNullOrWhiteSpace(collected.DestinationPath))
            {
                return planned;
            }

            try
            {
                // 内容目录相对**输出目录**的那一段在归集之后原样保留（归集是整棵搬走）。
                string outputDirectory = commit.ContentDirectory;

                if (!string.IsNullOrWhiteSpace(commit.ProcessArtifactDirectory))
                {
                    outputDirectory = Path.GetDirectoryName(commit.ProcessArtifactDirectory) ?? outputDirectory;
                }

                string moved = Path.Combine(
                    collected.DestinationPath,
                    Path.GetRelativePath(outputDirectory, planned));

                if (SafePathHelper.DirectoryExists(moved))
                {
                    return moved;
                }

                return SafePathHelper.DirectoryExists(planned) ? planned : moved;
            }
            catch
            {
                return planned;
            }
        }

        /// <summary>
        /// 内容物**实际所在的那一层目录**（第 35 条）。
        ///
        /// <para>定稿计划的 <c>DestinationDirectory</c> 是"落点目录"，但内容物往往被套了一层
        /// （判定表：多文件套一层 / 包里自带一个同名文件夹）—— 真机现场就是这样：
        /// 共用输出根是 <c>BBB\111</c>，而定稿把那**一个**文件夹 <c>2222</c> 搬了进去，
        /// 于是内容物真正所在的那一层是 <c>BBB\111\2222</c>。</para>
        ///
        /// <para>判据：落点目录里**除了「其余物」和工作区自己那棵树之外**只剩一个条目、而且它是目录
        /// → 那一层就是内容物层；否则就是落点目录本身（内容物是散文件，或本来就有多项）。
        /// 续解产物按它落位，才能做到"一个源包 = 一个目录"（用户第 35 条原话：
        /// "你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"）。</para>
        ///
        /// <para>⛔ <b>必须排除工作区那棵树</b>（用户 2026-09-30）：工作区默认就建在目标目录里面
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），不排除的话它会被当成"唯一的那一个子目录"，
        /// 于是"内容物层"被解析成工作区 —— 续解产物会直接落进工作区，然后被收尾清理一起删掉。</para>
        /// </summary>
        private string ResolveContentLayerDirectory(string destinationDirectory, string processArtifactDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                return string.Empty;
            }

            try
            {
                if (!Directory.Exists(destinationDirectory))
                {
                    return destinationDirectory;
                }

                string workRoot = _pathService.WorkDirectory;

                List<string> entries = Directory
                    .GetFileSystemEntries(destinationDirectory)
                    .Where(path => !ProcessArtifactLayout.IsArtifactDirectoryName(path))
                    .Where(path => !WorkspaceTree.ShouldSkipEntry(path, workRoot))
                    .ToList();

                if (entries.Count == 1 && Directory.Exists(entries[0]))
                {
                    return entries[0];
                }
            }
            catch
            {
                // 读不了目录（权限 / 刚好被删）：按落点目录回落，绝不因此让定稿结论出问题。
            }

            return destinationDirectory;
        }

        /// <summary>
        /// 搬运成功后把任务对象上的源路径改到新位置（CurrentPath 与分卷清单）。
        ///
        /// <c>task.FileName</c> 不变（只是换目录），所以界面上的名字照旧，路径列会显示新位置 ——
        /// 这是诚实的：源包确实已经在其余物里了。
        ///
        /// <para>⚠ 判据只有一处：<see cref="TaskPathSync.ApplyMove"/>（⛔ 别再自己写一遍"只改 CurrentPath"
        /// —— 那样账上会留下盘上已不存在的旧名字，见 §38 与内层包那一处 2026-10-02 的补齐）。</para>
        /// </summary>
        private static void ApplySourceMoveResult(ArchiveTask task, SourcePackageMoveResult result)
        {
            var byOldPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (SourcePackageMove move in result.Moved)
            {
                byOldPath[move.SourcePath] = move.TargetPath;
            }

            TaskPathSync.ApplyMove(task, byOldPath);
        }

        /// <summary>
        /// 抠出来的内嵌归档（双面文件尾部那一段）在定稿后是否搬进 <c>其余物</c>。
        ///
        /// 契约 §3.2 把"抠出来的中间 ZIP"列为其余物，本来是**该搬**的 —— 但实测下来不能搬，
        /// 因为它不是"解出来的产物"，而是**解压的输入**（用户那个源文件的一段副本）：
        ///
        /// · 双面文件 <c>user.mp4</c> 的 ZIP 里就是内层分卷 <c>inner.7z.001/.002</c>；
        ///   把它搬进 <c>其余物\user.zip</c> 之后，一键处理的续解扫描（它只看"本轮新出现的归档起点"）
        ///   会把这个 ZIP 也当成一个**新的内层包**，于是又解一遍、又产出 <c>inner.7z.001</c>、
        ///   再多续解一轮 —— 实测就是这个结果（回归测试"双面文件真实现场"从 2 轮变成 3 轮）。
        /// · 而且它往往很大（双面文件能到 760MB 量级），搬进用户目录等于凭空多一份大副本。
        ///
        /// 所以默认 **false**：它留在工作区（<c>&lt;work&gt;\&lt;taskId&gt;\</c>），
        /// 成功且校验通过后随工作区一起清掉，失败/取消时留着供排查。
        /// 如果将来判定表/ResultFinalizer 能把它标注成"输入而非产物"（续解扫描据此跳过），
        /// 把这里改成 true 即可，其余代码不用动。
        /// </summary>
        private const bool MoveCarvedArtifactIntoProcessDirectory = false;

        /// <summary>
        /// 定稿布局规划：把暂存产物树描述成 <see cref="StagedEntry"/>，交给
        /// <see cref="ResultFinalizer.Plan"/>（契约 §3.1 判定表的**唯一实现处**），
        /// 再把它的话翻译成本类执行阶段用的形状。
        ///
        /// 分工要说清楚：
        /// · 本方法负责"哪些是其余物" —— 这一步只有跑过暂存阶段的人知道。
        ///   判据用后缀（分卷段 + 归档本体）：解压出来的归档就是**待续解的内层包**，
        ///   按流水线语义它属于其余物（一键处理下一轮会去解它，成功的话它的内容物自会落进内容物一层）；
        ///   判定失败/到轮数上限时它留在 <c>其余物</c> 里可回收，比混在内容物里强。
        ///   （ResultFinalizer 的注释建议"由调用方显式标"，这里就是那个调用方。）
        /// · <see cref="ResultFinalizer"/> 负责"摆成什么样" —— 终端单文件直接放、多文件套一层、
        ///   多重空目录提上来、单链塌缩，以及 D-2 的 <c>&lt;源目录&gt;\其余物\&lt;包基名&gt;\</c>。
        ///
        /// 纯函数：只读目录、只返回计划，一个字节都不动，所以能脱离管线被测。
        /// </summary>
        /// <param name="stageDirectory">暂存目录（入仓阶段的产物树）。</param>
        /// <param name="destinationDirectory">最终目录（落点由 <see cref="PathService.BuildOutputPath"/> 算）。</param>
        /// <param name="sharedOutputRoot">
        /// 这个落点目录是不是同一次导入里多个包共用的（落点解析给出的唯一事实，
        /// 见 <see cref="OutputPlacementResult.SharesDestinationWithOtherPackages"/>）；
        /// 只影响其余物集中到哪（决策 D-10 依赖它）。
        /// </param>
        /// <param name="archiveBaseName">
        /// 终端归档基名（"没有最外层文件夹名"时给那一层取名用）。传空则退回 destDir 自己的末段名。
        /// </param>
        /// <param name="terminalLayout">
        /// 终端落法。⚠ 用户 2026-09-27 把这档选择**从界面上删掉了**（落点固定，不再让用户选），
        /// 所以生产路径现在一律传 <see cref="TerminalLayoutMode.KeepLastFolder"/>：
        /// 保留压缩包自带的那一层内层文件夹，外面再套一层以包名命名的文件夹。
        /// 参数本身留着 —— 它是 <see cref="ResultFinalizer.Plan"/> 的既有契约（其余物布局等路径也在用），
        /// 删掉只会白白动一大片与本次需求无关的代码。
        /// </param>
        /// <param name="specialExtraction">
        /// 本批生效的特定解压（规格 §3.5；默认 <see cref="SpecialExtractionPlan.Off"/> = 与以前逐字相同）。
        /// 规则生效时判定表里"要套的那一层"不再套（<c>222\1111\内容物</c>）；包内有并列的多个文件夹、
        /// 或几个包共用同一个成品目录时不塌，按原判定表套一层并写一条 WARN（见 <see cref="ResultFinalizer.Plan"/>）。
        /// </param>
        /// <param name="innermostPackageBaseName">
        /// **最后一个被展开的内层包**的包基名（没有内层包时传空）。
        /// 唯一来源 = <see cref="PackageLayerRules.ResolveBaseName"/>（读递归结果，不自己数层数）。
        /// 它非空 ⇒ 落点最少两层（用户 2026-09-30 红线）：destDir 里面必须还有"最后一个压缩包"那一层。
        /// </param>
        /// <param name="engineOutput">
        /// 这一单的递归结果。**只用来回答一件事**：暂存目录里某个文件是不是"引擎这一趟自己写出来的"
        /// —— 是 ⇒ 它是内容物，⛔ 不许当成待续解的过程物（用户 2026-09-27 真机：外层跨盘 zip 解出来的
        /// <c>222.zip</c> + <c>222.zi删除p</c> 本身就是一份完整的两卷 split zip，老判据把
        /// <c>222.zip</c> 当过程物、把另一卷当"名字不标准的残组"⇒ 整层定稿作废，678 MB 产物没落地）。
        /// 传 null（预检那一档）⇒ 退回老判据，一个字不变。
        /// </param>
        internal static FinalLayoutPlan PlanFinalLayout(
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            string? archiveBaseName,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
            SpecialExtractionPlan? specialExtraction = null,
            bool suppressPackageFolderLayer = false,
            string? innermostPackageBaseName = null,
            RecursionResult? engineOutput = null,
            ContentKeepRules? contentKeepRules = null,
            IReadOnlyCollection<string>? assembledVolumeBaseNames = null)
        {
            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new FinalLayoutPlan();
            }

            string stageRoot = SafePathHelper.GetFullPathSafe(stageDirectory);

            /*
             * 「内容物保留关键词」判据（用户 2026-10-04 的新功能）：整个循环只算一次；
             * 没传（null）/ 空列表 ⇒ 一个都不拦（行为与加这条功能之前逐字相同）。
             */
            ContentKeepRules keepRules = contentKeepRules ?? ContentKeepRules.Empty;

            /*
             * ===== 整组改名也要覆盖"引擎刚解出来的那一层"（用户 2026-10-01 真机）=====
             *
             * 现场：外层跨盘 zip 解出来的两个条目本身**又是一份完整的两卷 split zip**，可它们的名字
             * 是归档内部记的原名（`222.zi删除p` + `222.z0sc1`，网盘把中文/缩写塞进了后缀）。
             * 整组改名的两个调用点（批首 / 手动修复）**都只对"已经在任务表里"的文件动手**，
             * 这一对是递归产物、从没进过任务表 ⇒ 名字原样落地 ⇒ 7-Zip 按标准卷名找不到兄弟 ⇒ 打不开。
             *
             * 判据一个字不放宽（唯一出口仍是 VolumeGroupResolver + IsCanonicalVolumeName）：
             *   · 这一组必须**结论完整**、而且**每一卷都能唯一推出规范名**（判不出 / 缺卷 / 推不出 ⇒ 一个字都不改）；
             *   · 只改名字、内容一个字节不动；目标名被占 ⇒ 这一卷跳过（绝不覆盖）；
             *   · 全部改完再复查一遍（中途任何一卷不到位 ⇒ 倒序改回原名，⛔ 绝不留下半改状态）；
             *   · ⛔ **刚改过名的那一组不许进"可删的其余物"**（见下面 `renamedVolumeMembers`）——
             *     它是**内容物**（用户要拿去打开的那一份），不是待清理的过程物。
             */
            var volumeRenameChanges = new List<(string Level, string Message)>();
            var renamedVolumeMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            NormalizeEngineOutputVolumeNames(stageRoot, volumeRenameChanges, renamedVolumeMembers);

            var staged = new List<StagedEntry>();
            var warnings = new List<string>();
            long totalStageBytes = 0;
            int zeroByteArtifacts = 0;

            /* 分卷组不完整的现场（见下面那道闸门）：非空 ⇒ 整份计划作废，一个字节都不动。 */
            var incompleteVolumeGroups = new List<string>();

            /*
             * ===== 分卷组装判定（唯一出口 VolumeGroupResolver）=====
             *
             * 暂存目录里出现的每一组分卷**先判一次**，再决定谁能进"可删的其余物"。
             * 判据不再看"扩展名像不像分卷"，而是走同一份证据模型（基名 / 卷号 / 体积 / 位置 / 物理同一性；
             * 试开那一档在定稿这台同步机器上不做 —— 它在解前预检那边做，结果一样收进同一份结论里）。
             *
             * ⛔ 凡是被判定"属于某个分卷组"的文件，**永远不许进可删残留名单**：
             * 只有"整组完整 + 每一卷名字都标准"这一种情形才放行；其余一律让整份计划作废。
             */
            StageVolumeScan stageVolumeScan = ResolveStageVolumeGroups(stageRoot);

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                long size = 0;

                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 量不出大小只影响"其余物总共多大"这个数字，不影响布局。
                }

                totalStageBytes += size;

                /*
                 * ===== ⛔ 引擎这一趟自己写出来的文件**不是过程物**（用户 2026-09-27 真机：222 那一单）=====
                 *
                 * 现场：外层跨盘 zip（`222.zscip.zip` + `222.zscip.z01`）解出来的两个条目叫
                 * `222.zip` 与 `222.zi删除p`（网盘把中文塞进了内层后缀）。这两个**本身就是一份完整的
                 * 两卷 split zip**（7-Zip 自己认：「Multivolume = + / Volumes = 2」）。
                 *
                 * 老判据只看"后缀像不像归档" ⇒ `222.zip` 被算成"待续解的过程物"，随后下面那道
                 * 分卷组闸门看见同目录还有名字不标准的一卷 ⇒ 判"不完整" ⇒ **整份计划作废**：
                 * 内容物一个字节没搬、源包没进其余物、链尾的其余物一个都删不掉（日志还写着
                 * "解压成功 ｜ 校验通过"，用户却什么也没拿到）。
                 *
                 * 判据只有一条：**这个文件在不在这一趟引擎的产物清单里**（唯一事实来源 = 递归结果
                 * 指向的那一层产物目录）。在 ⇒ 它是内容物：既不是过程物，**也不进分卷组那两道闸门**
                 * —— 那两道问的是"这一堆是不是待续解的分卷"，引擎自己刚写出来的东西根本不是。
                 */
                /*
                 * ⚠ 2026-10-09 真机 `EEEE`（14:11 那一趟）**实测**：原来的三条判据
                 * （本层产物清单① / 每一层的产物目录② / 每一层解压前的逐条清单③）在
                 * **"链中途停在缺卷"**这一档**全都不成立**（排障行原文：
                 * `在产物清单里=False ｜ 在某层清单里=False ｜ 最终算过程物=True`）：
                 * ① `FinalOutputPath` 指向停住的那一层、文件却在 stage；② 层目录里那份已被发布**搬走**；
                 * ③ 那一层**列不出目录**（缺分卷 ⇒ 清单失败，真机行 217/219「没能列出归档内容」）
                 * ⇒ `ManifestEntries` **空表**。
                 * ⇒ 于是 `111.rar` 自己解出来的内容物 `111.zip` 又一次进了其余物 ⇒ 用户看到的"两个 `111.zip`"。
                 *
                 * ⇒ 判据不再"逐层找证据"，改问一个**当场成立**的事实：**这一趟到底跑过递归没有**。
                 * 跑过（`engineOutput != null`）⇒ 这个暂存区里每一个字节都出自**我们这条链**
                 * （本层引擎写出的 + 上一层产物发布进来的），而链内中间物早由"逐层回收"删掉了
                 * ⇒ **一律是内容物、要落地**（用户原话：「他就是最初 111.rar 经过两轮解压产出的内容物而已，
                 * 为什么会进其余物里面」）。
                 * ⛔ **没跑过递归**（`engineOutput == null`：预检那一档，以及 `PlanFinalLayout` 的直调用例）
                 * ⇒ 来路**判不出** ⇒ 退回原来的保守判据 + 下面那两道分卷组完整性闸门
                 * （`VolumeGroupDeletionSafetyTests` 那一族钉的就是这条兜底 —— 实测 136 条全绿）。
                 */
                bool isProcessArtifact = engineOutput == null
                    && (IsProcessArtifactFile(file) || CardSaysItIsOneOfTheGroup(file))
                    /*
                     * ⛔ 命中「内容物保留关键词」的**不是过程物**（用户 2026-10-04 的新功能
                     * 「内容物压缩文件不解压」）：它是用户点名"碰都不碰"的内容物 ⇒
                     * 跟着内容物一起定稿落盘，**不进其余物**（进了就会被链尾那一档按设置删掉）。
                     *
                     * 现场形状：外层包里就装着一个 `小明.zip`（用户要留的那一份）—— 老判据只看
                     * "像不像归档"，把它当"待续解的过程物"收进其余物 ⇒ 选了「彻底删除」就没了。
                     * 判据唯一出口 <see cref="ContentKeepRules"/>（包含即命中 / 大小写不敏感 / 不通配正则）。
                     */
                    && !keepRules.ShouldKeep(file);

                /*
                 * ===== ⛔ 分卷组的完整性闸门（用户 2026-09-30 真机：25 GB 被当"其余物"永久删除）=====
                 *
                 * 现场：外层 RAR 解出来的是一组 **PKZIP 跨盘**（`一只顶美.z01…z05` + 末卷 `一只顶美.zip`），
                 * 而末卷的名字被改坏成 `一只顶美.z删除ip` ⇒ 认不出是归档。
                 * 老判据只看**扩展名像不像分卷**，于是剩下 5 卷"每一卷单看都像待续解的过程物"，
                 * 全被收进可删的其余物；再叠加「空间不足」模式的永久删除 ⇒ **25 GB 当场没了**，
                 * 日志还写着"解压成功 ｜ 校验通过"。
                 *
                 * 现在：**分卷要进"可删的其余物"，必须先由判定器证明整组完整且名字标准**
                 * （判据见 <see cref="VolumeGroupResolver"/>，只认盘上的事实）。证明不了 ⇒
                 * 这一份既不进其余物、也不当内容物，**整份计划作废**（下面的 Failed 分支），
                 * 于是源包与其余物的删除一个都不会发生（它们都挂在"定稿 + 校验通过"之后）。
                 */
                StageVolumeGroupView? stageGroup = FindStageVolumeGroup(stageVolumeScan.Groups, file);

                /*
                 * ⛔ 这一道同样只问**过程物**（引擎这一趟自己写出来的东西已经在上面的 isProcessArtifact
                 * 里被摘掉了）：那两道闸门要回答的是"这一堆是不是待续解的分卷"，引擎刚解出来的产物
                 * 根本不是 —— 真机 222 那一单就是被这里拦下的（`222\222.zip` 与 `222\222.zi删除p`
                 * 是一份完整的两卷 split zip，判定器说"名字不标准"⇒ 整层作废、产物没落地）。
                 */
                if (isProcessArtifact && stageGroup != null)
                {
                    if (!stageGroup.Resolution.CanEnterDeletableRestItems)
                    {
                        incompleteVolumeGroups.Add($"{stageGroup.Resolution.BaseName}：{stageGroup.Resolution.Reason}");
                        continue;
                    }

                    /*
                     * 整组完整、名字也标准 ⇒ 照老路算"其余物"（源包走完可以进其余物再删）。
                     *
                     * ⚠ **但引擎刚解出来的那一组不算**（用户 2026-10-01 真机）：它是**内容物**
                     * —— 真机里 `222\222.zip` + `222\222.z01` 就是用户要拿去打开的那一份，
                     * 被算进"可删的其余物"就是把它删掉。判据只有一条事实：
                     * 这一组的成员**都是刚刚由本方法改回标准名的那一批**（`renamedVolumeMembers`）
                     * —— "刚改过名"正是"这是我们自己解出来的、名字不标准的那一组"的直接证据。
                     */
                    if (stageGroup.FilePaths.All(
                            member => renamedVolumeMembers.Contains(SafePathHelper.GetFullPathSafe(member))))
                    {
                        isProcessArtifact = false;
                    }
                    else
                    {
                        isProcessArtifact = true;
                    }
                }

                /*
                 * 0 字节的**归档 / 分卷**不是有效的其余物（用户 2026-09-24 要求）。
                 *
                 * 依据：任何真实归档都不可能只有 0 字节（连文件头都放不下）——
                 * 这种文件只有一个来源：7z 用错密码 / 数据不全时写出的**桩文件**
                 * （真机现场就是 `2部轻熟1.7z.001/.002` 两个 0 字节）。
                 * 把它搬进 `其余物` 等于把垃圾当成"用户以后可能还要的东西"收起来，
                 * 而且下一轮还会被当成内层包去解 —— 用户明确要求"不得搬进其余物"。
                 *
                 * ⚠ 只对**归档 / 分卷后缀**生效：内容物里本来就有 0 字节文件是正常情形
                 * （说明文件、占位文件），那些照常定稿（见下面 totalStageBytes 那条的说明）。
                 */
                if (isProcessArtifact && size <= 0)
                {
                    zeroByteArtifacts++;
                    continue;
                }

                /*
                 * ===== ⛔ 刚改过名的那一组是**内容物**，不是过程物（用户 2026-10-01 真机）=====
                 *
                 * 它们在上一段（`NormalizeEngineOutputVolumeNames`）里被改成标准卷名，
                 * 于是下面那道"整组完整 + 名字标准"的闸门会顺理成章地把它们算成"可删的其余物"
                 * —— 而用户要的恰恰是**拿到这一组去打开**（它是内容物）。
                 * 判据只有一条事实：这个文件是不是**刚刚由本方法改名的那一批**。
                 */
                if (isProcessArtifact
                    && renamedVolumeMembers.Contains(SafePathHelper.GetFullPathSafe(file)))
                {
                    isProcessArtifact = false;
                }

                /*
                 * ===== ⛔ 孤儿卷的兜底闸门（带卷标记、却没落进任何一组）=====
                 *
                 * 上面那道闸门只覆盖"归组认得出来的组"。可 <see cref="VolumeGroupDetector"/> 对
                 * **名字被伪装过**的桶还要过一道尺寸规律，过不了就把整桶丢掉（2026-09-28 三层方案，
                 * 为了"认错组比不认糟得多"）。丢掉之后，`x.7z.001` 这种文件在老写法里只剩
                 * "扩展名像分卷 ⇒ 可删的其余物"这一条路 —— 而它旁边那个 `x.7z.002删除`
                 * 恰恰可能是它的兄弟。**一卷被删掉就再也解不开整组**。
                 *
                 * 所以这一档回头**逐文件问判定器**（复用同一份目录事实，不重新列目录）：
                 *   · 判定器证明"整组完整且名字标准" ⇒ 照老路当其余物；
                 *   · 其余一切结论（判不出 / 缺卷 / 疑缺 / 名字不标准）⇒ **整份计划作废**，
                 *     一个字节都不搬、不删（兜底落在"什么都不做"那一档，AGENTS §9.5）。
                 *
                 * ⛔ 只对**卷标记后缀**（`.001` / `.z01` / `.r00` / `.partN`）做这一步：
                 * `.zip` / `.7z` 这种归档本体自己就是完整包，不走分卷这条判据。
                 */
                if (isProcessArtifact && stageGroup == null && IsVolumePartFile(file))
                {
                    /*
                     * ⚠ 例外：这一片正是**我们已经在收的那一组**的片（用户 2026-10-06 真机：
                     * `111(2)_.zip` 解出来的 `111.z01` 就是那一组跨盘 ZIP 的第 1 片，收卷那一档
                     * 已经把它接到入口包那一层、整组由批末那一站解开）。
                     * ⇒ 那不叫"分卷组不完整"，叫"这一组正在收" ⇒ ⛔ 不许拿这道闸门把整层定稿拦死
                     * （真机症状：`定稿布局规划失败：这一层里有分卷组不完整：111` ⇒ 整层不定稿 ⇒
                     * 其余物与源包全留着不动）。
                     * ⛔ 判据只读**既有账**（`_groupPieceProducers` 的组基名），⛔ 不放宽"证明完整"那一步。
                     */
                    string thisBaseName = FileNameHelper.GetArchiveBaseName(Path.GetFileName(file));

                    if (assembledVolumeBaseNames != null
                        && thisBaseName.Length > 0
                        && assembledVolumeBaseNames.Contains(thisBaseName))
                    {
                        continue;
                    }

                    VolumeGroupResolution orphan = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
                    {
                        AnchorPath = file,
                        DirectoryEntries = stageVolumeScan.EntriesFor(file),
                        AllowTrialOpen = false
                    });

                    if (!orphan.CanEnterDeletableRestItems)
                    {
                        incompleteVolumeGroups.Add($"{Path.GetFileName(file)}：{orphan.Reason}");
                        continue;
                    }
                }

                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, file),
                    Size = size,
                    IsProcessArtifact = isProcessArtifact
                });
            }

            /*
             * ⛔ 只要发现**任何一组分卷证明不了完整**，这一层的计划就整份作废（不定稿、不建其余物、
             * 不搬任何文件）—— 调用方拿到 Failed 后按失败收场：源包与其余物的删除全部不发生。
             * 理由见上面那道闸门（用户 2026-09-30：25 GB 就是这么没的）。
             */
            if (incompleteVolumeGroups.Count > 0)
            {
                string detail = string.Join("；", incompleteVolumeGroups.Distinct(StringComparer.Ordinal).Take(3));

                return new FinalLayoutPlan
                {
                    Failed = true,
                    BlockedByIncompleteVolumeGroup = true,
                    FailureReason = $"这一层里有分卷组不完整：{detail}。已按「什么都不动」处理 —— "
                        + "产物与源包一个字节都不搬、不删（补齐全套分卷后再来）",
                    Summary = "分卷组不完整，未定稿"
                };
            }

            if (zeroByteArtifacts > 0)
            {
                warnings.Add(
                    $"{zeroByteArtifacts} 个 0 字节的归档 / 分卷文件不算其余物（它们不是压缩包，留在暂存目录里）");
            }

            /*
             * 暂存区里**全是 0 字节产物** → 一律不定稿（用户 2026-09-24 铁证，不变量 6 的同一口径）。
             *
             * 现场：外层 ZIP 里有加密条目，7z 用错密码时"半成功"地写出两个 **0 字节**的
             * `*.7z.001/.002`。这些桩文件既不是内容物、也不是"内层包"（0 字节的 `*.7z.001` 不是包），
             * 更不该被搬进 `其余物` —— 用户明确要求的三条，全都在这里落地：
             * 计划为空 ⇒ 不定稿、不建最终目录、不建 `其余物`、源包一个字节都不动。
             *
             * 为什么以"整棵树的总字节"为判据（而不是逐个文件丢掉 0 字节的那些）：
             * 归档里**本来就有空文件**是正常情形（说明文件、占位文件），那种包必须照常定稿；
             * 0 字节桩的问题只出现在"整个产物全是 0 字节"这种"什么都没解出来"的形状上。
             * 这个取舍写在 docs/需求变更.md 的边界里，不假装覆盖了第一种情形。
             */
            if (totalStageBytes <= 0 && staged.Count > 0)
            {
                return new FinalLayoutPlan
                {
                    ZeroByteProductsOnly = true,
                    Summary = "暂存区里只有 0 字节产物（没有解出任何内容）"
                };
            }

            // 目录条目也要给：判定表要认"自带一层文件夹""多重空目录嵌套""单链"这些形态，
            // 光有文件路径推不出"这一层是不是空的"。
            foreach (string directory in Directory.EnumerateDirectories(stageRoot, "*", SearchOption.AllDirectories))
            {
                staged.Add(new StagedEntry
                {
                    RelativePath = Path.GetRelativePath(stageRoot, directory),
                    IsDirectory = true
                });
            }

            if (staged.Count == 0)
            {
                return new FinalLayoutPlan();
            }

            FinalizePlan finalize = ResultFinalizer.Plan(
                staged,
                destinationDirectory,
                terminalLayout,
                archiveBaseName,
                contentRoot: null,
                stagingRoot: stageRoot,
                sharedOutputRoot: sharedOutputRoot,
                specialExtraction: specialExtraction,
                suppressPackageFolderLayer: suppressPackageFolderLayer,
                innermostPackageBaseName: innermostPackageBaseName);

            /*
             * 整组改名那一档也进 warnings（调用方按既有口径逐条写日志：
             * 成功那一条是 INFO、没改成那一条是 WARN，见 ApplyStageCommitAsync 里 plan.Warnings 的消费）。
             */
            warnings.AddRange(volumeRenameChanges.Select(change => change.Message));

            var processSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PlannedMove move in finalize.ProcessArtifactMoves)
            {
                processSources.Add(move.From);
            }

            return new FinalLayoutPlan
            {
                Failed = finalize.Layout == FinalizeLayoutKind.Failed,
                FailureReason = finalize.FailureReason,
                Layout = finalize.Layout,
                Moves = finalize.Moves,
                PlannedContentCount = finalize.ContentMoves.Count,
                ProcessArtifactSources = processSources,
                ProcessArtifactDirectory = finalize.ProcessArtifactDirectory,
                ProcessArtifactTotalSize = finalize.ProcessArtifactTotalSize,
                ContentFileCount = finalize.ContentFileCount,
                Summary = finalize.Summary,
                SpecialExtractionApplied = finalize.SpecialExtractionApplied,
                Warnings = finalize.Warnings.Concat(warnings).ToList()
            };
        }

        /// <summary>
        /// 这条路是不是"其余物"（契约 §3.2）：分卷段（<c>.001</c>/<c>.z01</c>/<c>.r00</c>/<c>.part1</c>）
        /// 与归档本体（<c>.7z</c>/<c>.zip</c>/<c>.rar</c>/…）。抠出来的中间 ZIP 也落在这一条里。
        /// </summary>
        internal static bool IsProcessArtifactFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return ExtensionHelper.IsVolumePartExtension(extension) ||
                   ExtensionHelper.IsKnownArchiveExtension(extension);
        }

        /// <summary>
        /// 这个文件是不是**带卷标记的那一片**（<c>.001</c> / <c>.z01</c> / <c>.r00</c> / <c>.partN</c>）。
        ///
        /// <para>与 <see cref="IsProcessArtifactFile"/> 的区别就在这一格：那一条把"归档本体"
        /// （<c>.zip</c> / <c>.7z</c> / <c>.rar</c>）也算其余物，而归档本体**自己就是完整包**，
        /// 不需要分卷那条判据；只有带卷标记的这一档才可能是"某组缺了兄弟的一片"。</para>
        /// </summary>
        internal static bool IsVolumePartFile(string filePath) =>
            ExtensionHelper.IsVolumePartExtension(Path.GetExtension(filePath));

        /// <summary>
        /// 把暂存目录里"**名字不标准的一整套分卷**"整组改回标准卷名（用户 2026-10-01 真机）。
        ///
        /// <para><b>为什么需要它</b>：外层跨盘 zip（`222.zscip.zip` + `222.zscip.z01`）解出来的两个条目
        /// 本身**又是一份完整的两卷 split zip**，而它们的名字是归档内部记的原名（`222.zi删除p` + `222.z0sc1`）。
        /// 跨盘 zip 只按**标准卷名**找兄弟（末片 `X.zip`、首片 `X.z01`），名字不对**谁**都打不开
        /// —— 7-Zip / WinRAR / UnRAR 实测给的是同一句"打不开"。整组改名的两个调用点都在
        /// "任务表"那一侧（批首 / 手动修复），递归产物从没进过任务表 ⇒ 名字原样落地。</para>
        ///
        /// <para><b>放哪一步</b>：定稿规划的最开头（暂存目录已经定下来、还没做任何分类与套层）。
        /// 这样后面的分卷组闸门、套层判定、搬运看到的都是标准名，不必在四处各补一次特例。</para>
        ///
        /// <para><b>判据不放宽一个字节</b>（唯一出口 = <see cref="VolumeGroupResolver"/> 的结论 +
        /// <see cref="VolumeGroupResolution.IsGroupMember"/> 认下来的那一卷）：</para>
        /// <list type="bullet">
        /// <item><description>这一组必须**结论完整**（判不出 / 缺卷 / 疑缺 ⇒ 一个字都不改）；</description></item>
        /// <item><description>每一卷的**规范名都真的在盘上**（`X.zip` / `X.z01` …）—— 这一条正是"名字变体那一档"的解药：</description></item>
        /// </list>
        /// 现场那一组是 `222.zip`（另一个文件）+ `222.zi删除p`（这个名字本身是那一卷的另一个写法），
        /// 规范名 `222.zip` 在盘上、变体 `222.zi删除p` 也在盘上 ⇒ 把后者改回 `222.zip` 不成立（会覆盖），
        /// 这一组**保持原样**（它的内容一个字节都不丢，用户自己看得见）。
        ///
        /// <para>⛔ 全成或全不成：任何一卷没到位就**倒序改回原名**（与 <c>VolumeNameRepair.TryApply</c>
        /// 同一条纪律）；目标名被占的那一卷跳过、其余照改；改名失败只写 WARN，⛔ 绝不拖垮定稿。</para>
        /// </summary>
        private static void NormalizeEngineOutputVolumeNames(
            string stageRoot,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (string.IsNullOrWhiteSpace(stageRoot) || !Directory.Exists(stageRoot))
            {
                return;
            }

            foreach (string directory in EnumerateDirectoriesSafe(stageRoot))
            {
                RenameVolumeGroupInDirectory(directory, changes, renamedMembers);
            }
        }

        /// <summary>这一层（含根）的每个目录各判一次：这个目录里有没有"名字不标准但规范名齐全"的一整套分卷。</summary>
        private static void RenameVolumeGroupInDirectory(
            string directory,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            IReadOnlyList<VolumeGroupEntry> entries;

            try
            {
                var list = new List<VolumeGroupEntry>();
                int order = 0;

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    long size;

                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    list.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(file),
                        Size = size,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                        OrderIndex = order++
                    });
                }

                entries = list;
            }
            catch
            {
                return;
            }

            if (entries.Count < 2)
            {
                return;
            }

            var candidates = entries
                .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                .ToList();

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
                {
                    AnchorPath = group.FirstVolumePath,
                    DirectoryEntries = entries,
                    AllowTrialOpen = false
                });

                TryRenameVolumeGroupToStandardNames(resolution, changes, renamedMembers);
            }
        }

        /// <summary>
        /// 把一组改回标准卷名；判据不成立就原样返回（不写日志、不动盘）。
        ///
        /// <para>闸门只用"这一组**已经是完整的一组**"（<see cref="VolumeGroupVerdict.Complete"/>）+
        /// "**每一个成员**都能唯一推出一个规范名"两条：</para>
        /// <list type="bullet">
        /// <item><description>⛔ **不拿 `HasRenamedVolume` 当拦下条件** —— 它说的正是"这个名字不标准"，
        /// 而本方法的全部目的就是把不标准的名字改成标准的（拿它当拦下条件等于这个方法永远不干活）；
        /// 真机那一组（`222.zi删除p` + `222.z0sc1`）就是被它拦下的。</description></item>
        /// <item><description>⛔ 只要有一个成员推不出规范名、或两个成员推出同一个名字，**整组不动**
        /// （宁可保持原样，也不猜着改用户的名字）。</description></item>
        /// </list>
        /// </summary>
        private static void TryRenameVolumeGroupToStandardNames(
            VolumeGroupResolution resolution,
            List<(string Level, string Message)>? changes,
            HashSet<string>? renamedMembers)
        {
            if (resolution.Verdict != VolumeGroupVerdict.Complete)
            {
                return;
            }

            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in resolution.GroupFilePaths)
            {
                if (File.Exists(member))
                {
                    onDisk.Add(member);
                }
            }

            if (onDisk.Count < 2)
            {
                return;
            }

            var moves = new List<(string From, string To)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string member in onDisk)
            {
                string fileName = Path.GetFileName(member);
                string directory = Path.GetDirectoryName(member) ?? string.Empty;

                if (directory.Length == 0)
                {
                    return;
                }

                if (!TryResolveStandardVolumeName(fileName, out string target))
                {
                    return;
                }

                string targetPath = Path.Combine(directory, target);

                // 已经是标准名 ⇒ 这一卷不动（但它也占住了这个目标名）。
                if (string.Equals(fileName, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (!targets.Add(targetPath))
                    {
                        return;
                    }

                    continue;
                }

                // 目标名被别的文件占着 ⇒ 这一卷跳过（绝不覆盖用户的数据）。
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    continue;
                }

                if (!targets.Add(targetPath))
                {
                    // 两个成员推出同一个名字 ⇒ 这一组认不清，整组不动。
                    return;
                }

                moves.Add((member, targetPath));
            }

            if (moves.Count == 0)
            {
                return;
            }

            var done = new List<(string From, string To)>();

            try
            {
                foreach ((string from, string to) in moves)
                {
                    File.Move(from, to);
                    done.Add((from, to));
                }

                // 全成或全不成：全部改完之后复查一遍（另一个进程可能刚好插手）。
                foreach ((_, string to) in done)
                {
                    if (!File.Exists(to))
                    {
                        throw new IOException($"改名后找不到目标：{Path.GetFileName(to)}");
                    }
                }
            }
            catch (Exception ex)
            {
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(done[i].To, done[i].From);
                    }
                    catch
                    {
                        // 回滚失败如实写进下面那条 WARN 里，⛔ 不假装成功。
                    }
                }

                if (changes != null)
                {
                    changes.Add((
                        "WARN",
                        $"这一层有一组分卷的名字没能改回标准名（{ex.Message}），已尽量改回原名；"
                        + "这一组要打开得手工把名字改成标准卷名（末片 X.zip、首片 X.z01…）。"));
                }

                return;
            }

            if (changes != null)
            {
                changes.Add((
                    "INFO",
                    $"这一层有一组跨盘分卷的名字不是标准名，已改回标准名（{done.Count} 卷，只改名字、内容一个字节没动）："
                    + string.Join("、", done.Select(m => $"{Path.GetFileName(m.From)} → {Path.GetFileName(m.To)}"))));
            }

            /* 记下"刚改过名"的那几个新路径：它们**不许进可删的其余物**（它们是内容物，不是过程物）。 */
            if (renamedMembers != null)
            {
                /*
                 * 整组都记进来（不只是真被改名的那几卷）：调用方据此判"这一组是内容物、不许进可删的其余物"，
                 * 而本来就标准的那几卷（`一只顶美.z01`）同样是这一组的内容物，漏掉它们等于把它们删掉。
                 */
                foreach (string member in onDisk)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(member));
                }

                foreach ((_, string to) in done)
                {
                    renamedMembers.Add(SafePathHelper.GetFullPathSafe(to));
                }
            }
        }

        /// <summary>
        /// 这一卷按跨盘 zip 的族规则该叫什么。
        ///
        /// <para>基名就是**文件名去掉最后一段后缀**（`222.zi删除p` → `222`；`222.z0sc1` → `222`）——
        /// 与归档内部记的名字对不上时也只影响"改完像不像人写的"，能不能打开由"标准卷名"这一条决定。</para>
        ///
        /// <para>认不出来就返回 false ⇒ 调用方**整组不动**（宁可保持原样，也不猜着改用户的名字）。</para>
        /// </summary>
        private static bool TryResolveStandardVolumeName(string fileName, out string target)
        {
            target = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            int dot = fileName.IndexOf('.');

            if (dot <= 0)
            {
                return false;
            }

            string stem = fileName[..dot];
            string last = fileName[(dot + 1)..];

            // 脏本体名（`222.zi删除p`：还原出来是 `zip`）⇒ 它就是末片 `.zip`。
            if (ExtensionHelper.TryRecoverDisguisedArchiveBody(last, out string suffix, out _)
                && suffix.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 干净的末片。
            if (last.Equals("zip", StringComparison.OrdinalIgnoreCase))
            {
                target = stem + ".zip";
                return true;
            }

            // 卷标记（`z01` / `z0sc1` / `z删除01`）⇒ 一律按标准 `.zNN` 写。
            if (ExtensionHelper.TrySplitVolumeSegmentTolerant(last, out string mark, out _)
                && mark.Length == 3
                && (mark[0] == 'z' || mark[0] == 'Z')
                && int.TryParse(mark.AsSpan(1), out int ordinal)
                && ordinal >= 1)
            {
                target = stem + ".z" + ordinal.ToString("D2", CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>暂存树里的全部目录（含根；读不到就是空的 —— 什么都不做）。</summary>
        private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
        {
            var result = new List<string> { root };

            try
            {
                result.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
            }
            catch
            {
                // 读不全就按已经拿到的那些判：宁可少改，也绝不乱改。
            }

            return result;
        }

        ///
        /// <para>事实来源只有一处：<see cref="RecursionResult.FinalOutputPath"/> 指向的那一层产物目录
        /// —— 引擎解出来的东西就是它们，别的一律是让这一单能开工的过程物（源包 / 分卷 / 内层包）。</para>
        ///
        /// <para>⛔ 为什么必须问这一句：外层跨盘 zip 解出来的 <c>222.zip</c> 后缀就是归档后缀，
        /// 老判据当场把它当"待续解的过程物"，随后那道分卷组闸门看见同目录还有一卷名字不标准
        /// ⇒ 整层定稿作废 ⇒ 用户的 678 MB 产物一个字节都没落地（日志还写着"解压成功 ｜ 校验通过"）。
        /// 后缀骗人这件事在别处已经吃过很多次亏了，这里按**产物清单**判。</para>
        ///
        /// <para>拿不到递归结果（预检那一档、或引擎没报产物目录）⇒ 一律 false：退回老判据，
        /// 一个字都不变（兜底落在"什么都不做"那一档）。</para>
        /// </summary>
        private static bool IsEngineOutputFile(string filePath, string stageRoot, RecursionResult? engineOutput)
        {
            if (engineOutput == null)
            {
                return false;
            }

            // ① 这条链最后交出来的那一层（老口径，逐字保留）。
            if (MatchesEngineOutputRoot(filePath, stageRoot, engineOutput.FinalOutputPath))
            {
                return true;
            }

            /*
             * ② ⚠ 2026-10-09：**这条链每一层**的产物目录也算数（真机 EEEE 排障行实测）。
             *
             * 现场：`111.rar` 那条链**中途停在缺卷**（第 2 层什么都没产出）⇒ `FinalOutputPath` 指向
             * 那最后一个空层目录 ⇒ 第 **1** 层解出来的 `111.zip` 按同名相对路径对不上 ⇒
             * 被判成「过程物」收进其余物 —— 可它明明是本任务链产出的**内容物**
             * （用户原话：「他就是最初 111.rar 经过两轮解压产出的内容物而已，为什么会进其余物里面」）。
             * ⇒ 判据从"这一趟最后那一层"改成"**本任务链产出的东西**"。
             * ⛔ 老口径那一条（①）保留：它覆盖"跑完了、FinalOutputPath = 入仓 stage"那一档。
             */
            foreach (RecursionLayerReport layer in engineOutput.Layers)
            {
                if (layer != null && MatchesEngineOutputRoot(filePath, stageRoot, layer.OutputPath))
                {
                    return true;
                }
            }

            /*
             * ③ ⚠ 2026-10-09（EEEE ② 的真根因，上面 ①② 都盖不住）：**问这一层自己那份清单**。
             *
             * 为什么 ①② 会漏（排障行实测：`FinalOutputPath=…\layer-002\output`、
             * `在产物清单里=False`，而那个文件就在暂存目录里）：**发布是"就地替换 + 移动"**
             * （`ExtractionWorkspace.Publish`）—— 产物**搬走之后，原来那一层目录里就没有它了**，
             * 于是 `MatchesEngineOutputRoot` 要的那句"两边同一个相对路径都在盘上"永远不成立。
             * ⇒ 判据不能读"层目录现在还剩什么"（那个事实已被发布本身抹掉），只能读**当场记下来的账**。
             *
             * 记在哪：`RecursionLayerReport.ManifestEntries` —— 每层**解压前列目录那一次**的逐条清单
             * （相对路径 + 解压后字节，`PartialPublishPlanner.ToManifestEntries` 归一过：`/` → `\`、
             * 去掉开头 `./`），与暂存目录那边算相对路径的写法同源。它是"这一层的引擎写出来的东西"
             * 的直接证据，而且**不受发布搬运影响**。
             *
             * ⛔ 判不出来路时（清单没有 / 引擎没报）**一个字都不改**：`engineOutput == null` 那几档
             * 仍然一律 false ⇒ 退回老判据 + 分卷组完整性闸门（`VolumeGroupDeletionSafetyTests`
             * 那一族钉的就是这条兜底，⛔ 不许放宽）。
             */
            foreach (RecursionLayerReport layer in engineOutput.Layers)
            {
                if (layer != null && MatchesEngineManifestEntry(filePath, stageRoot, layer.ManifestEntries))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 暂存里这个文件，是不是<paramref name="manifestEntries"/>（某一层列目录的逐条清单）
        /// 里那一条 —— 判据 = 两边**归一后的相对路径逐字相等**（事实；⛔ 不比中文、⛔ 不看后缀）。
        ///
        /// <para>存在的理由见 <see cref="IsEngineOutputFile"/> ③：发布是移动 ⇒ 层目录里那份没了，
        /// 只有这份**当场记下来的清单**还能回答"这一层到底产出过这个路径没有"。</para>
        /// </summary>
        private static bool MatchesEngineManifestEntry(
            string filePath,
            string stageRoot,
            IReadOnlyList<(string Path, long Size)> manifestEntries)
        {
            if (manifestEntries == null || manifestEntries.Count == 0)
            {
                return false;
            }

            if (!TryGetStageRelativePath(filePath, stageRoot, out string relative))
            {
                return false;
            }

            foreach ((string path, long _) in manifestEntries)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(
                        path.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        relative,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 暂存里这个文件相对暂存根的路径（<c>\</c> 分隔、不带前导分隔符）。
        /// <c>false</c> = 它根本不在暂存根底下（⛔ 那种情形一律不认）。
        /// </summary>
        private static bool TryGetStageRelativePath(string filePath, string stageRoot, out string relative)
        {
            relative = string.Empty;

            string full = SafePathHelper.GetFullPathSafe(filePath);
            string root = SafePathHelper.GetFullPathSafe(stageRoot);

            if (full.Length == 0 || root.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return relative.Length > 0;
        }

        /// <summary>
        /// 暂存里这个文件，是不是<paramref name="outputRoot"/>那一层产物目录里同一个相对路径的那一份
        /// （判据 = 事实：两边都存在、路径逐字相等；⛔ 不比中文、⛔ 不看后缀）。
        /// </summary>
        private static bool MatchesEngineOutputRoot(string filePath, string stageRoot, string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return false;
            }

            string full = SafePathHelper.GetFullPathSafe(filePath);
            string root = SafePathHelper.GetFullPathSafe(stageRoot);

            if (full.Length == 0 || root.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (relative.Length == 0)
            {
                return false;
            }

            string candidate = SafePathHelper.GetFullPathSafe(Path.Combine(outputRoot, relative));

            return candidate.Length > 0
                   && File.Exists(candidate)
                   && string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// **解前预检用的那一问**：这一单所在的目录里，围绕它到底组成了哪一组、齐不齐。
        ///
        /// <para>试开那一档在这里做（有可用引擎时才做）：临时物落
        /// <see cref="PathService.WorkDirectory"/>（= <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，
        /// 用户 2026-09-30 口径）；它和第一卷不在同一个卷上时试开器**如实退回**卷根那一档
        /// （硬链接不能跨卷，⛔ 绝不复制大文件），退化原因写在证据里。</para>
        ///
        /// <para>⛔ 只读：硬链接 + 引擎列目录，源文件一个字节都不动；试开目录收工即删。</para>
        /// </summary>
        private async Task<VolumeGroupResolution> ResolveVolumeGroupAsync(
            ArchiveTask task,
            CancellationToken cancellationToken)
        {
            try
            {
                bool engineReady = _archiveEngine != null && _archiveEngine.IsAvailable;

                var verifier = engineReady ? new VolumeProbeVerifier(_archiveEngine!) : null;

                VolumeGroupResolver resolver = verifier == null
                    ? new VolumeGroupResolver()
                    : new VolumeGroupResolver(verifier.TryOpenAsync);

                return await resolver.ResolveAsync(
                        new VolumeGroupQuery
                        {
                            AnchorPath = task.CurrentPath,
                            WorkRootDirectory = _pathService.WorkDirectory,
                            AllowTrialOpen = verifier != null
                        },
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                /*
                 * 判定器本身出错**不许改变"开不开"**（那不变量由上面那段负责），也不许把整单拖垮：
                 * 退回老口径的那句话，并把出错写清楚（判不出就是判不出）。
                 */
                AppendLog("WARN", $"{task.LogName}：分卷组装判定没能做完（{ex.GetType().Name}），按老口径报缺卷。");

                return new VolumeGroupResolution
                {
                    Verdict = VolumeGroupVerdict.Undetermined,
                    AnchorPath = task.CurrentPath,
                    Reason = "判不出：分卷组装判定没能做完（不删、不移动、不改名）"
                };
            }
        }

        /// <summary>把判定结论拼成给用户看的那一句话（判不出时如实说"判不出" + 依据）。</summary>
        private static string BuildVolumeVerdictMessage(ArchiveTask task, VolumeGroupResolution resolution)
        {
            string baseline = string.IsNullOrWhiteSpace(task.VolumeInfoText)
                ? "分卷不完整，缺少分卷"
                : task.VolumeInfoText;

            if (resolution == null || resolution.Verdict == VolumeGroupVerdict.Undetermined)
            {
                string why = resolution == null || string.IsNullOrWhiteSpace(resolution.Reason)
                    ? string.Empty
                    : $"（{resolution.Reason}）";

                return baseline + why;
            }

            if (resolution.Verdict == VolumeGroupVerdict.Complete)
            {
                /*
                 * ⛔ "完整"有两档来源，**不许混着说**：
                 *   ① 试开真的跑过并成立（决定性证据）→ 才敢说"按试开验证"；
                 *   ② 只有名字 / 体积 / 位置 / 物理同一性（前五条）说完整 → 那是"看不出缺哪一卷"，
                 *      不是"验证过"。老写法在这里一律写"按试开验证这一组是齐的"，
                 *      而试开这一档**在这一路上根本没跑**（没引擎 / 跨卷 / 顺序凑不齐时都是"没试"）——
                 *      那是把"没试"说成"验过"（AGENTS：⛔ 不许假装做到了）。
                 */
                bool trialConfirmed = resolution.Evidence.Any(row =>
                    row.Kind == VolumeEvidenceKind.TrialOpen
                    && row.Outcome == VolumeEvidenceOutcome.Satisfied);

                if (trialConfirmed)
                {
                    /*
                     * 试开证明这一组是**齐的**，只是有卷的名字不是 7-Zip 认的标准名 ——
                     * 老判据在这里只会说「分卷缺失」，把用户指去满盘找一卷根本不缺的卷。
                     */
                    return $"{baseline}；但按试开验证这一组是齐的（{resolution.Reason}）";
                }

                return $"{baseline}；按名字与体积这一组看不出缺哪一卷（{resolution.Reason}）"
                    + " —— 这一条没有经过试开确认，所以只当参考，缺卷以任务自己那份识别结果为准"
                    + "（⛔ 程序不会去改你的文件名，也不会动你的文件）";
            }

            if (resolution.MissingVolumeNames.Count > 0)
            {
                return $"{string.Join("、", resolution.MissingVolumeNames)}：{resolution.Reason}";
            }

            return $"{baseline}；{resolution.Reason}";
        }

        /// <summary>证据表进日志（判不出时也要写：用户与排障都要看得到"依据是什么"）。</summary>
        private void LogVolumeGroupEvidence(ArchiveTask task, VolumeGroupResolution resolution)
        {
            if (resolution == null || resolution.Evidence.Count == 0)
            {
                return;
            }

            AppendLog("INFO", $"{task.LogName}：分卷组装判定 = {resolution.Reason}");

            foreach (VolumeEvidence row in resolution.Evidence)
            {
                AppendLog(
                    "INFO",
                    $"    · 证据「{row.Title}」（{WeightText(row.Weight)}，{OutcomeText(row.Outcome)}）：{row.Detail}");
            }
        }

        private static string WeightText(VolumeEvidenceWeight weight) => weight switch
        {
            VolumeEvidenceWeight.Decisive => "决定性",
            VolumeEvidenceWeight.Strong => "强",
            _ => "弱"
        };

        private static string OutcomeText(VolumeEvidenceOutcome outcome) => outcome switch
        {
            VolumeEvidenceOutcome.Satisfied => "成立",
            VolumeEvidenceOutcome.NotSatisfied => "不成立",
            _ => "没取到"
        };

        /// <summary>
        /// **分卷组完整性确认**（用户 2026-09-30 真机铁证）：⛔ **只有确认整组完整且每一卷名字都标准，
        /// 分卷才允许进"可删的其余物"**。
        ///
        /// <para><b>为什么必须有它</b>：老判据只看"扩展名像不像分卷"，于是"缺首卷 / 缺末卷的残组"里
        /// 每一卷单看都像"待续解的过程物"，被打包收进可删的其余物；再叠加「空间不足」模式的永久删除，
        /// 用户那 25 GB 就是这么没的（日志还写着"解压成功 ｜ 校验通过"）。</para>
        ///
        /// <para><b>判据已经整体搬进唯一出口 <see cref="VolumeGroupResolver"/></b>（2026-09-30 分卷组装算法）：
        /// 这里只做一次"同目录问一问"，不再自己按扩展名拼判据 —— 老实现那五条（<c>.z01</c> 找同名 <c>.zip</c>、
        /// 纯数字找 <c>.001</c>、<c>.partNN</c> 找首卷…）现在全在判定器的证据表里，而且认得出
        /// "末卷被改名成 <c>一只顶美.z删除ip</c>"这种老判据必然漏掉的情形。</para>
        ///
        /// <para>⛔ 兜底不变：**确认不了（目录读不到、名字认不出、判不出）⇒ 一律按不完整处理** ——
        /// 兜底落在"什么都不做"那一档（AGENTS §9.5）。</para>
        /// </summary>
        internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note)
        {
            note = string.Empty;

            VolumeGroupResolution resolution = new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = filePath,
                AllowTrialOpen = false
            });

            if (resolution.Verdict == VolumeGroupVerdict.Undetermined)
            {
                note = $"{Path.GetFileName(filePath)}：{resolution.Reason}";
                return false;
            }

            if (resolution.CanEnterDeletableRestItems)
            {
                return true;
            }

            string missing = resolution.MissingVolumeNames.Count > 0
                ? "缺 " + string.Join("、", resolution.MissingVolumeNames)
                : resolution.Reason;

            note = $"{resolution.BaseName}：{missing}";
            return false;
        }

        /// <summary>一个暂存目录里的分卷组 + 它的判定结论（定稿闸门按文件查它）。</summary>
        internal sealed class StageVolumeGroupView
        {
            /// <summary>这一组的目录（绝对路径）。</summary>
            public string DirectoryPath { get; init; } = string.Empty;

            /// <summary>判定器给出的唯一结论。</summary>
            public VolumeGroupResolution Resolution { get; init; } = new();

            /// <summary>组的全部文件（绝对路径，含推定进来的候选）。</summary>
            public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
        }

        /// <summary>
        /// 暂存树的一次分卷扫描：**已经认出来的组** + **逐目录的事实清单**（后者给"孤儿卷"复查用）。
        ///
        /// <para>为什么要带着事实清单：带卷标记的文件**不一定**落进某个组 ——
        /// <see cref="VolumeGroupDetector"/> 对"名字被伪装过"的桶还要过一道尺寸规律，
        /// 过不了就把整桶丢掉（2026-09-28 三层方案，防认错组）。丢掉之后那些文件在
        /// <see cref="PlanFinalLayout"/> 里就只剩"按扩展名像不像分卷"这一条老判据了 ——
        /// 而它们恰恰可能是**缺了兄弟的那一组**。所以那一档必须回头问判定器（见调用点）。</para>
        /// </summary>
        internal sealed class StageVolumeScan
        {
            /// <summary>认出来的组（每组一条结论）。</summary>
            public IReadOnlyList<StageVolumeGroupView> Groups { get; init; } = Array.Empty<StageVolumeGroupView>();

            /// <summary>逐目录的事实清单（键 = 目录绝对路径）。</summary>
            public IReadOnlyDictionary<string, IReadOnlyList<VolumeGroupEntry>> EntriesByDirectory { get; init; } =
                new Dictionary<string, IReadOnlyList<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>这个文件的同目录事实清单（拿不到就返回空 —— 空清单在判定器里等于"判不出"）。</summary>
            public IReadOnlyList<VolumeGroupEntry> EntriesFor(string filePath)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(filePath);

                    return !string.IsNullOrWhiteSpace(directory) &&
                           EntriesByDirectory.TryGetValue(directory, out IReadOnlyList<VolumeGroupEntry>? entries)
                        ? entries
                        : Array.Empty<VolumeGroupEntry>();
                }
                catch
                {
                    return Array.Empty<VolumeGroupEntry>();
                }
            }
        }

        /// <summary>
        /// 把暂存树里出现的每一组分卷判一次（**逐目录**判：判定器只在同目录里认组）。
        ///
        /// <para>调用时机 = 定稿闸门开工前一次（不是每个文件各判一次 —— 那是 O(n²) 的目录枚举）。
        /// 不做试开：这一步跑在同步的定稿路径上；试开那一档在解前预检里做，两边收的是**同一份结论类型**。</para>
        /// </summary>
        private static StageVolumeScan ResolveStageVolumeGroups(string stageRoot)
        {
            var result = new List<StageVolumeGroupView>();
            var byDirectory = new Dictionary<string, List<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string? directory = Path.GetDirectoryName(file);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    if (!byDirectory.TryGetValue(directory, out List<VolumeGroupEntry>? list))
                    {
                        list = new List<VolumeGroupEntry>();
                        byDirectory[directory] = list;
                    }

                    list.Add(new VolumeGroupEntry
                    {
                        Path = SafePathHelper.GetFullPathSafe(file),
                        Size = new FileInfo(file).Length,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                        OrderIndex = list.Count
                    });
                }
                catch
                {
                    // 单个文件量不出事实（被占用 / 权限）⇒ 它进不了任何组，照老路当内容物处理。
                }
            }

            var resolver = new VolumeGroupResolver();

            foreach (KeyValuePair<string, List<VolumeGroupEntry>> pair in byDirectory)
            {
                IReadOnlyList<VolumeGroupEntry> entries = pair.Value;

                var candidates = entries
                    .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                    .ToList();

                foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
                {
                    VolumeGroupResolution resolution = resolver.Resolve(new VolumeGroupQuery
                    {
                        AnchorPath = group.FirstVolumePath,
                        DirectoryEntries = entries,
                        AllowTrialOpen = false
                    });

                    result.Add(new StageVolumeGroupView
                    {
                        DirectoryPath = pair.Key,
                        Resolution = resolution,
                        FilePaths = resolution.GroupFilePaths
                    });
                }
            }

            return new StageVolumeScan
            {
                Groups = result,
                EntriesByDirectory = byDirectory.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<VolumeGroupEntry>)pair.Value,
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>这个文件属于哪一组（不属于任何组返回 null）。判定器认进来的推定候选也算。</summary>
        private static StageVolumeGroupView? FindStageVolumeGroup(
            IReadOnlyList<StageVolumeGroupView> groups,
            string filePath)
        {
            string full = SafePathHelper.GetFullPathSafe(filePath);

            foreach (StageVolumeGroupView group in groups)
            {
                foreach (string member in group.FilePaths)
                {
                    if (string.Equals(member, full, StringComparison.OrdinalIgnoreCase))
                    {
                        return group;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 执行定稿：按 <see cref="PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
        ///
        /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
        /// 四条硬要求：
        /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
        /// ② 同名绝不**默认**覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
        /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）；
        /// ④ 用户明确选了「覆盖」时才覆盖，而且必须走 <see cref="PathService.TryOverwriteLanding"/>
        ///    的两阶段（先挪到临时名 → 落位 → 再删），并逐个落点写日志留痕。
        /// </summary>
        /// <param name="conflictAction">同名冲突档（<see cref="ConflictActions"/> 的值）。</param>
        /// <param name="conflictDecision">
        /// 用户对同名冲突的答案（Ask 档；非 Ask 档为 null）。为 null 且档位是 Ask 时，
        /// <see cref="PathService.ResolveConflict"/> 会退回保守档（跳过）—— 绝不替用户决定成覆盖。
        /// </param>
        private StageCommitResult ExecuteFinalLayout(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            string conflictAction,
            ConflictDecision? conflictDecision,
            CancellationToken cancellationToken,
            bool verificationPassed = true,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var logEntries = new List<(string Level, string Message)>();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return new StageCommitResult { Attempted = false };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿开始前已取消"
                };
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(
                    task,
                    stageDirectory,
                    destinationDirectory,
                    sharedOutputRoot,
                    terminalLayout,
                    specialExtraction,
                    recursion);
            }
            catch (Exception ex)
            {
                string readFailure = $"读取暂存目录失败：{ex.Message}";

                logEntries.Add(("ERROR", $"{task.LogName}：{readFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { readFailure },
                    Message = readFailure,
                    LogEntries = logEntries
                };
            }

            if (plan.Failed)
            {
                /*
                 * ⚠ 2026-10-09（用户点名第二条红字）：**"分卷组不完整"这一档不是失败、也不写 ERROR**。
                 * 用户口径：「连完整的都没有，这就不是程序的错误」＋「在最后一步之前碰到分卷缺失的就先跳过」。
                 * 判据读**结构化标志**（`plan.BlockedByIncompleteVolumeGroup`），⛔ 不比中文文案；
                 * ⛔ "什么都不动"一个字没放松（照旧不定稿、不搬、不删）。
                 */
                string planFailure = plan.BlockedByIncompleteVolumeGroup
                    ? "这一层里有分卷组还没凑齐（先跳过，等最后那一轮再判）：" + plan.FailureReason
                    : "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add((
                    plan.BlockedByIncompleteVolumeGroup ? "WARN" : "ERROR",
                    $"{task.LogName}：{planFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = 1,
                    Failures = new[] { planFailure },
                    Message = planFailure,
                    LogEntries = logEntries
                };
            }

            foreach (string warning in plan.Warnings)
            {
                logEntries.Add(("WARN", $"{task.LogName}：定稿提醒 —— {warning}"));
            }

            if (plan.Moves.Count == 0)
            {
                /*
                 * 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
                 *
                 * ⚠ "只有 0 字节产物"要单独说清楚（用户 2026-09-24 要求）：它看起来也是"没有产物"，
                 * 但成因完全不同（密码不对 / 数据不全时 7z 会写 0 字节的桩文件），
                 * 写成"这个包本来就是空的"会把人引到错的方向 —— 那句话必须带上"校验未通过，产物视为无效"。
                 */
                string emptyMessage = plan.ZeroByteProductsOnly
                    ? "暂存目录里只有 0 字节产物 —— 校验未通过，产物视为无效：不定稿、不生成其余物、源包留在原地。"
                    : "暂存目录里没有产物，没有需要定稿的内容。";

                logEntries.Add(("WARN", $"{task.LogName}：{emptyMessage}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Message = emptyMessage,
                    LogEntries = logEntries
                };
            }

            bool destinationExisted = Directory.Exists(destinationDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

                logEntries.Add(("ERROR", $"{task.LogName}：{createFailure}"));

                return new StageCommitResult
                {
                    Attempted = true,
                    FailedCount = plan.Moves.Count,
                    Failures = new[] { createFailure },
                    Message = createFailure,
                    LogEntries = logEntries
                };
            }

            var moved = new List<(string From, string To)>();
            var failures = new List<string>();
            var overwritten = new List<string>();

            int movedContent = 0;
            int movedProcess = 0;
            int renamed = 0;
            int skippedContent = 0;
            int skippedProcess = 0;

            // 计划里 **内容物在前、其余物在后**：先让用户要的东西落位，再收拾过程物。
            foreach (PlannedMove move in plan.Moves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                    logEntries.Add(("WARN", $"{task.LogName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                    return new StageCommitResult
                    {
                        Attempted = true,
                        Cancelled = true,
                        Message = "定稿被取消，产物已退回暂存目录",
                        LogEntries = logEntries
                    };
                }

                bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

                try
                {
                    // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                    bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE`（用户最在意的那条「为什么有两个 `111.zip`」）：
                     * **目标位上已经是同一份物理文件 ⇒ 不必再搬**（⛔ 不删、⛔ 不改名，直接跳过这一条）。
                     *
                     * 现场（这一条**两个改动缺一不可**：上面把"本链产出"判成内容物之后，
                     * 它就会来定稿落地，于是撞上这个形状）：接片给"**本单自己暂存目录**"里那一份
                     * **建的是硬链接**（`ShouldMovePieceIntoGroup` 那一档只建链接、绝不搬走，
                     * 真机行 146 的 `moveIntoGroup=False` 就是它）⇒ `111\111\111.zip` 与暂存里的
                     * `111.zip` **是同一个 FileId**。定稿再搬一次，只会在同一个目录里多出一个
                     * `111(1).zip` —— 用户看到的"两个"就从"其余物里一个"变成"`(1)` 一个，症状根本不消失。
                     *
                     * 判据只读事实（唯一出口 <see cref="FileIdentity.IsSamePhysicalFile"/>：卷序列号 + 文件索引），
                     * ⛔ 不比名字、⛔ 不看大小。**一个字节都不动**：数据本来就已经在目标位上（同一条记录）。
                     */
                    if (!isDirectory && FileIdentity.IsSamePhysicalFile(move.From, move.To))
                    {
                        logEntries.Add((
                            "INFO",
                            $"{task.LogName}：{Path.GetFileName(move.To)} 已经在目标位上、而且是「同一份文件」"
                            + "（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。"));

                        continue;
                    }

                    /*
                     * ⚠ 2026-10-09 真机 `EEEE` 的另一半（同一个"两个 `111.zip`"的另一形状）：
                     * **这一片已经由接片搬进"这一组的落点层"了 ⇒ 产出方自己落点里这一份是多余名**。
                     *
                     * 与上面那一条的区别（真机行 161 实测）：那一档 `moveIntoGroup=True` ⇒ 接片对
                     * 层产物用的是**移动**（不是建链接），所以两边**不是同一个 FileId**，
                     * `IsSamePhysicalFile` 判不出来 —— 只能按"组层是不是已经有这个名字"判。
                     *
                     * 判据两条事实，缺一不可：① 这一份是**分卷片**（`IsVolumePartFile`）；
                     * ② 这一组的**落点层**（唯一出口 `ResolveBatchGroupDirectory`）里已经有同名的一份。
                     * ⇒ 那才是它的正位（整组在那一层拼、在那一层解），产出方落点里这一份就是
                     * 用户点名过的"没人认领的名字"（`SiblingFolderVolumeGatherTests` ③ 那条用例钉的就是它）。
                     * ⛔ 数据一个字节都不丢：正位那一份已经在了。
                     */
                    if (!isDirectory && IsVolumePartFile(move.From))
                    {
                        string pieceName = Path.GetFileName(move.From);

                        /*
                         * ⚠ **读"接片那一刻定下来的层"，⛔ 不在这里重算**（2026-10-11 实测）：
                         * 这一档原来转调 `ResolveBatchGroupDirectory` 的 1 参版，而它当年靠"先到先得"那条
                         * 兜底才答得出层；兜底按用户口径删掉之后它答不出 ⇒ 这道闸门**静默失效**
                         * ⇒ 本单的工作区那一份又被发布到产出方落点里，成了**没人认领的多余名**
                         * （实测残留逐字 `…\out-…\111(2)_\111.z01`；守门用例
                         * `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
                         * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它）。
                         * 层只有一个出口：接片那一档算过的那个（`_groupLayerByBaseName`）。
                         */
                        string groupLayer = _groupLayerByBaseName.TryGetValue(
                            FileNameHelper.GetArchiveBaseName(pieceName),
                            out string? rememberedLayer)
                            ? rememberedLayer
                            : string.Empty;

                        if (groupLayer.Length > 0 && File.Exists(Path.Combine(groupLayer, pieceName)))
                        {
                            logEntries.Add((
                                "INFO",
                                $"{task.LogName}：{pieceName} 已经由接片搬进「{Path.GetFileName(groupLayer)}」"
                                + "这一组的落点层了 —— 产出方这里这一份是多余的名字，不落地（一个字节都不丢）。"));

                            continue;
                        }
                    }

                    if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                    {
                        failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                        continue;
                    }

                    string target = move.To;

                    // 覆盖档由 TryOverwriteLanding 自己把新条目搬过去（它要先腾位），这里不再搬第二次。
                    bool landed = false;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        /*
                         * 目标是**最终目录自己**时不许覆盖：那等于把刚落位的产物连同目录一起顶掉，
                         * 而且是用户完全没预期的一次删除。规划器现在不会给出这种条目
                         * （计划里永远是 destDir\子项），这里是最后一道保险。
                         */
                        bool isDestinationRoot = SafePathHelper.PathEquals(target, destinationDirectory);

                        ConflictResolution resolution = isDestinationRoot
                            ? new ConflictResolution(ConflictChoice.AutoRename, target, true, false)
                            : _pathService.ResolveConflict(
                                target,
                                isDirectory ? ConflictTargetKind.Directory : ConflictTargetKind.File,
                                conflictAction,
                                conflictDecision);

                        if (resolution.Choice == ConflictChoice.Skip)
                        {
                            // 「跳过」：这一项不动，已有文件一个字节都不改（新产物留在暂存目录）。
                            if (isProcessArtifact)
                            {
                                skippedProcess++;
                            }
                            else
                            {
                                skippedContent++;
                            }

                            logEntries.Add(("WARN", $"{task.LogName}：同名冲突，按你的选择跳过：{target}"));
                            continue;
                        }

                        if (resolution.Choice == ConflictChoice.Overwrite && !isDestinationRoot)
                        {
                            /*
                             * 「覆盖」= 两阶段落位（AGENTS.md §6 第 3 条）：
                             * 先把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名。
                             * **禁止"先 File.Delete 再 Move"**：中间任何失败都会让用户想保留的那一份
                             * 永久消失，而新条目还没到位。
                             */
                            if (!_pathService.TryOverwriteLanding(
                                    move.From,
                                    target,
                                    isDirectory,
                                    out string overwriteError,
                                    out string overwriteNote))
                            {
                                failures.Add($"{Path.GetFileName(move.From)}（{overwriteError}）");
                                continue;
                            }

                            // 真正覆盖前要留痕：哪个落点被顶掉了必须写进日志（AGENTS.md §6 第 3 条 / 用户要求）。
                            overwritten.Add(target);
                            landed = true;

                            logEntries.Add((
                                "WARN",
                                $"{task.LogName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                        }
                        else
                        {
                            // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                            string renamedTarget = resolution.TargetPath;

                            if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                            {
                                renamed++;
                            }

                            logEntries.Add(("INFO", $"{task.LogName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

                            target = renamedTarget;
                        }
                    }

                    if (!landed)
                    {
                        if (isDirectory)
                        {
                            Directory.Move(move.From, target);
                        }
                        else
                        {
                            File.Move(move.From, target);
                        }
                    }

                    moved.Add((move.From, target));

                    if (isProcessArtifact)
                    {
                        movedProcess++;
                    }
                    else
                    {
                        movedContent++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
                }
            }

            /*
             * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为其余物。
             * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
             * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
             */
            if (MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
            {
                string carvedPath = BuildEmbeddedArchivePath(task);

                if (File.Exists(carvedPath))
                {
                    try
                    {
                        string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                            ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                            : plan.ProcessArtifactDirectory;

                        string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                        SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                        if (File.Exists(carvedTarget))
                        {
                            carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                            renamed++;
                        }

                        File.Move(carvedPath, carvedTarget);

                        movedProcess++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                    }
                }
            }

            string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                ? Path.Combine(destinationDirectory, ProcessArtifactDirectoryName)
                : plan.ProcessArtifactDirectory;

            /*
             * 定稿摘要 —— **"没能搬运"那几条必须带上明细**（用户 2026-10-04 真机）。
             *
             * 现场：`老王.apk` 那一单写着「定稿搬运失败，产物没能写进输出目录：…；10 项没能搬运」，
             * ①页「错误信息」列与日志里都只有这一个数字 —— 那 10 条 `文件名（原因原文）` 在收集处
             * （上面的 `failures`）就已经拿到了，却在 CommitFailed 的提前 return 那条路上被丢掉，
             * 用户手里没有任何可行动的线索（而批末诊断还指着他去看"引擎原话"，定稿类失败没有引擎原话）。
             *
             * 为什么补在摘要里而不是另写一行：摘要这一份字符串**同时**是日志那一行
             * （"定稿完成 —— …"）与①页「错误信息」列（`StageCommitResult.Message` →
             * `CommitFailureMessage`）的来源 ⇒ 一处出口、两个消费点，不会各写一份、也不会对不上。
             * ⛔ 判据一个都没动：这里只把**已经收集到的**事实说出来。
             */
            string summary =
                $"内容物 {plan.ContentFileCount} 个文件 → {destinationDirectory}" +
                (movedProcess > 0
                    ? $"；其余物 {movedProcess} 项（{plan.ProcessArtifactTotalSize} 字节）→ {processDirectory}"
                    : string.Empty) +
                (renamed > 0 ? $"；{renamed} 项同名，已改名未覆盖" : string.Empty) +
                (overwritten.Count > 0 ? $"；{overwritten.Count} 项同名，按你的选择覆盖（已写日志）" : string.Empty) +
                (skippedContent + skippedProcess > 0 ? $"；{skippedContent + skippedProcess} 项同名，按你的选择跳过" : string.Empty) +
                (failures.Count > 0 ? $"；{failures.Count} 项没能搬运" : string.Empty) +
                (failures.Count > 0
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.FinalizeMoveFailureDetailFormat,
                        MaxFinalizeContentNameLines,
                        DescribeFinalizeMoveFailures(failures))
                    : string.Empty);

            /*
             * 「定稿完成」这句话**不许在"没解出东西"时读成正常完成**（用户 2026-09-24 要求）。
             *
             * 现场误读：日志写着"定稿完成 —— 内容物 0 个文件 → …；其余物 2 项（0 字节）→ …"，
             * 而实际上校验早已判否、那 2 项是 0 字节垃圾 —— 用户看到"定稿完成"就以为流程正常走完了。
             * 现在两种情况各挂一句明确的结论：
             * · 校验未通过 → "⚠ 校验未通过，产物视为无效"；
             * · 校验通过但一个内容物都没有 → 如实说"这次没有内容物"（那是合法的，例如只出过程物）。
             */
            string contentNote = plan.ContentFileCount > 0
                ? string.Empty
                : verificationPassed
                    ? "（⚠ 这次没有内容物，只有其余物）"
                    : "（⚠ 校验未通过，产物视为无效；不定稿有效内容、不搬源包）";

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.LogName}：定稿完成{contentNote} —— {summary}"));

            /*
             * 产物**点名**（用户 2026-09-25："出问题我看日志就能知道"）。
             *
             * 为什么必须列名字：续解那一步是按"这一轮新出现的文件"找下一层的，而名字被改坏的产物
             * （真机上是 `222.ra删除r`）在日志里如果只报个数，用户就只能自己去翻目录 —— 那次就是这么绕远的。
             * 上限 20 条，超出只报个数（几千个文件时不能让日志爆掉），但"还有多少没列"必须写出来。
             */
            if (plan.Moves.Count > 0)
            {
                List<PlannedMove> contentMoves = plan.Moves
                    .Where(move => !plan.ProcessArtifactSources.Contains(move.From))
                    .ToList();

                List<string> contentNames = contentMoves
                    .Select(move => Path.GetFileName(move.To))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(MaxFinalizeContentNameLines)
                    .ToList();

                if (contentNames.Count > 0)
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：本次内容物 —— {string.Join("、", contentNames)}"
                        + (contentMoves.Count > contentNames.Count
                            ? $"（…还有 {contentMoves.Count - contentNames.Count} 项没列出来）"
                            : string.Empty)));
                }
            }

            // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
            foreach (string overwrittenPath in overwritten)
            {
                logEntries.Add(("WARN", $"{task.LogName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
            }

            return new StageCommitResult
            {
                Attempted = true,
                PlannedContentCount = plan.PlannedContentCount,
                MovedContentCount = movedContent,
                MovedProcessCount = movedProcess,
                ContentFileCount = plan.ContentFileCount,
                ProcessArtifactDirectory = processDirectory,
                ContentDirectory = ResolveContentLayerDirectory(destinationDirectory, processDirectory),
                ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
                RenamedCount = renamed,
                SkippedCount = skippedContent + skippedProcess,
                SkippedContentCount = skippedContent,
                OverwrittenCount = overwritten.Count,
                OverwrittenPaths = overwritten,
                FailedCount = failures.Count,
                Failures = failures,
                Message = summary,
                LogEntries = logEntries
            };
        }

        /// <summary>
        /// 定稿**没能搬运**的那几条明细（<c>文件名（原因原文）</c>），最多前
        /// <see cref="MaxFinalizeContentNameLines"/> 条，多出来的折成"还有 K 项没列出来"。
        ///
        /// <para>为什么是 5 条：与「本次内容物 —— 名字、名字…」同一个理由（前 5 个足够定性，
        /// 几十个同名文件全列出来只会把日志与①页那一格撑爆）。</para>
        ///
        /// <para>⛔ 这里一个字都不改判据、也不重算原因：明细就是 <c>failures</c> 里已经收好的那一份
        /// （来源：`无法创建目标目录` / 两阶段覆盖的失败原因 / `File.Move` / `Directory.Move` 的异常原文）。</para>
        /// </summary>
        private static string DescribeFinalizeMoveFailures(IReadOnlyList<string> failures)
        {
            List<string> shown = failures.Take(MaxFinalizeContentNameLines).ToList();
            string text = string.Join("、", shown);

            if (failures.Count <= shown.Count)
            {
                return text;
            }

            return text + string.Format(
                CultureInfo.CurrentCulture,
                StatusText.FinalizeMoveFailureHiddenFormat,
                failures.Count - shown.Count);
        }

        /// <summary>
        /// 定稿的回滚：把已经搬过去的条目按**相反顺序**搬回暂存区原路径。
        ///
        /// 为什么必须回滚（契约 §6 第 2 条"定稿前 destDir 不得出现半成品"）：
        /// 用户按下「取消当前」时，搬运可能已经做了一半 —— 只把最终目录里那一半留下，
        /// 用户看到的就是一个"解压到一半"的目录，正是这次改造要根治的观感问题。
        /// 回滚失败（文件被别的程序锁住等）只吞掉：那是更小的问题，而且日志里已经有取消记录。
        ///
        /// 条目可能是**整个目录**（判定表 2/3/4 会整棵搬），所以这里也要按类型分开搬回去。
        /// </summary>
        private static void RollBackFinalLayout(
            List<(string From, string To)> moved,
            string destinationDirectory,
            bool destinationExisted)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(moved[i].From));

                    if (Directory.Exists(moved[i].To) && !File.Exists(moved[i].To))
                    {
                        Directory.Move(moved[i].To, moved[i].From);
                    }
                    else
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                }
                catch
                {
                    // 回滚不掉的单个条目不影响"已经尽量退回"这个结论。
                }
            }

            if (destinationExisted)
            {
                // 目录本来就在（续解内层包时它装着上一轮的内容物），绝不能删。
                return;
            }

            TryDeleteEmptyDirectoryTree(destinationDirectory);
        }

        /// <summary>由深到浅删掉空目录（只删空的；非空一律不碰）。定稿回滚后收拾自己造出来的目录壳。</summary>
        private static void TryDeleteEmptyDirectoryTree(string root)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

                foreach (string directory in directories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(directory).Length == 0)
                        {
                            Directory.Delete(directory, recursive: false);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着，不影响结论。
                    }
                }

                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, recursive: false);
                }
            }
            catch
            {
                // 见方法注释：收拾目录壳是尽力而为。
            }
        }

        /// <summary>
        /// 给递归层提供密码候选（只给值，不给来源说明）。
        /// 顺序与单层解压完全一致 —— 递归的内层包同样是"用户的包"，不该用另一套规则。
        ///
        /// <para>⚠ 2026-10-05（只读审计）这一处改了两件事，两件都关系到"用户刚输的密码到底试没试"：</para>
        /// <list type="number">
        /// <item><b>本批手动输入的密码也要进来</b>：它过去只插进单层路径的候选表
        /// （<c>_manualBatchPasswords</c> 的唯一使用点），递归这条路拿不到 ⇒ 用户刚输对密码、
        /// 内层包照样报「密码错误」。做法是**转调同一个** <see cref="InsertManualPasswordCandidates"/>
        /// （插在空密码之后 + 去重，⛔ 不抄第二份判据）。</item>
        /// <item><b>这里不再截断</b>：老写法 <c>.Take(MaxPasswordAttemptsPerLayer)</c> 在递归内部
        /// "跳过空密码"那一步**之前**执行，而跳空密码只减账不减表 ⇒ 真实候选最多只试到 9 个
        /// （每层上限 10）。截断改由各调用方自己按同一个上限做：递归核心的层循环本来就有
        /// <c>attempts &gt;= MaxPasswordAttemptsPerLayer</c> 这一道，而"分卷试开"那三条调用点
        /// 必须自己截（见 <see cref="BuildVolumeProbePasswordCandidates"/>）——
        /// 否则 <c>VolumeProbeVerifier</c> 会对"每种排列 × 整份候选表"跑一遍试开，工作量放大几十倍。</item>
        /// </list>
        /// </summary>
        /// <param name="sourceSink">
        /// 可空：算完之后把这一层的「候选值 → 来源」写进去（先清空再写）。
        /// 递归层的日志只能靠它说清来源（见 <see cref="ResolveCandidateSourceForLog"/>）——
        /// 「本批已成功 / 密码本命中 / 说明文件旁路 / 手动输入」这些按值回查密码列表是答不出来的。
        /// </param>
        internal IReadOnlyList<string> BuildRecursionPasswordCandidates(
            string archivePath,
            IDictionary<string, string>? sourceSink = null)
        {
            var probe = new ArchiveTask(archivePath);

            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                probe,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            InsertManualPasswordCandidates(candidates, _manualBatchPasswords);

            /*
             * 顺手记一份「值 → 来源」：来源本来就由 PasswordService 给（它的候选表里带着 Source），
             * 这里只是**搬一次**，⛔ 不重新推、也不猜。每次算候选都整份重建 ——
             * 上一层（或上一个包）的来源不许留到这一层来（旁路说明文件就是按归档路径算的）。
             */
            if (sourceSink != null)
            {
                sourceSink.Clear();

                foreach (PasswordItem item in candidates)
                {
                    sourceSink[item.Value ?? string.Empty] = item.Source ?? string.Empty;
                }
            }

            return candidates
                .Select(p => p.Value ?? string.Empty)
                .ToList();
        }

        /// <summary>
        /// 「分卷试开」（<c>VolumeNameRepair.PlanByContentAsync</c> 系列）用的候选表：
        /// **同一个 provider，但在这里截到每层上限**。
        ///
        /// <para>为什么要单独一个出口：<see cref="BuildRecursionPasswordCandidates"/> 现在给的是
        /// **不截断**的完整候选表（递归核心内部自己按上限停手），而试开那一步的对账方式是
        /// "每种卷序 × 每个候选都真跑一次 <c>TestAsync</c>" —— 几百个候选进去就是几十倍的无谓引擎调用。
        /// 三条调用点（同目录 / 无名那两档 + 跨目录放宽那一档）全部走这一个方法，
        /// ⛔ 不许各自 <c>.Take</c> 一遍（那正是"同一件事两个出口"的起点）。</para>
        /// </summary>
        internal IReadOnlyList<string> BuildVolumeProbePasswordCandidates(string archivePath) =>
            BuildRecursionPasswordCandidates(archivePath)
                .Take(MaxPasswordAttemptsPerLayer)
                .ToList();

        /// <summary>
        /// 递归解压一个任务，并把结果落到任务状态上。
        ///
        /// <para>
        /// 多分支（一层里有多个内层包）默认**必须问用户**（不变量 8）。但**一键处理期间一个字都不问**：
        /// 用户 2026-09-27 原话 —— "什么叫做一键解压，就是说用户选中你来，然后自己去忙了，
        /// 根本就没有空管你，以后不要出现弹窗"。一键档下按**保守档**办：只保留当前这一层的结果
        /// （等于用户在那种框里点"取消"），并在日志与汇总里写明"还剩 N 个分支没展开"。
        /// </para>
        /// </summary>
        /// <param name="engineArchivePath">
        /// 真正交给引擎的归档路径。内嵌归档时它是工作区里抠出来的那个文件（不是 <c>task.CurrentPath</c>）。
        /// </param>
        /// <param name="stageDirectory">
        /// 暂存目录（入仓阶段）。递归核心把叶子层产物发布到**这里**，不直接写最终目录 ——
        /// 定稿那一步才把它们搬进 <see cref="ArchiveTask.OutputPath"/>。
        /// </param>
        /// <param name="oneClickRun">
        /// 这一批是不是「一键处理」/「继续解」发起的（**只决定"要不要弹框"**，⛔ 不再决定展开策略 ——
        /// 展开一律按②页所选档位，见方法说明与 <c>expandAll</c> 那一段）。
        /// </param>
        /// <returns>
        /// 递归结论（调用方要用它判断"展开了几层"——结果校验拿什么当预期全靠这个，见
        /// <see cref="PostProcessSuccessAsync"/> 的 <c>recursion</c> 参数）。
        /// </returns>
        private async Task<RecursionResult?> RunRecursiveAsync(
            ArchiveTask task,
            string engineArchivePath,
            string stageDirectory,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            /*
             * ②页「嵌套压缩包」那三档 → 递归核心的模式。**三档三义，一档都不许合并**。
             *
             * ⛔ 2026-10-01 修：这里原来是 `AllBranches ? AllBranches : SingleChain` ——
             * 把 **「只解当前这一层」也当成「单链自动展开」**跑了：界面上写着"只解当前这一层"、
             * `docs/使用说明.md` §10.1 写着"里面的包原样留着、你自己决定下一步"、人工测试清单 C35 也写着
             * "默认档下只解一层"，而程序实际上一条链往下解到底（并且顺带把单层档承诺的"快路"
             * ——第 0 层走 ZIP 直读、每层交给一键处理的一轮——整个绕过去了，见 `DirectReadAppliesTo`）。
             * 用户 2026-10-01 追问"开了续解和不开续解会不会有 bug"，指的就是这个：两档当时**行为完全相同**。
             *
             * ⚠ 2026-10-04（用户当场推翻默认档）：出厂默认改成 **`AllBranches`**（"有压缩包就解压"），
             * 所以走到这一段的**常态**就是全部分支。三档语义（与②页逐字对齐）：
             * · `SingleLayer` = 只解当前这一层，内层包**原样留着**（继续解交给一键处理的轮次 / 手动「继续解」）
             *   —— 上游那道闸门（`递归档位（已删除） != "SingleLayer"`）就把它挡在递归核心之外，到不了这里；
             * · `SingleChain` = 只跟一条链；一层里出现 **≥2 个真归档** ⇒ 第 0 层问用户，
             *   更深的层停在那一层并**如实报停因**（旁边是什么文件都不影响这条判据）；
             * · `AllBranches`（出厂默认）= 内层有多个包也全部解开（仍受每层内层包数量上限约束）。
             *
             * ⚠ 2026-10-01 核查记录（用户问"开了续解和不开续解会不会有 bug"）：下面这条 switch 曾经写成
             * `AllBranches ? AllBranches : SingleChain`，看上去像"把 SingleLayer 也当 SingleChain 跑"，
             * 但**实际不是缺陷** —— 真的闸门在上游（`递归档位（已删除） != "SingleLayer"` 才进递归核心），
             * 撤掉这一处的改动跑守门用例照旧全绿（红检否掉了那个怀疑）。把它写成显式三档只是为了
             * "以后谁动了那道闸门，这里不会静默降级成别的档"。
             */

            // 上限（层数 / 每层密码尝试次数）当场从设置里取，并交给**本次**的递归核心：
            // 构造时固定一份的话，用户改完设置不重启就不生效（见字段上的说明）。
            RecursionLimits limits = BuildRecursionLimits();
            RecursiveExtractor recursiveExtractor = CreateRecursiveExtractor();
            // 「先测试再解压」：用户开了这一档就必须交给递归核心（它才有 TestAsync 那一支）。
            recursiveExtractor.TestBeforeExtract = Settings.TestBeforeExtract;

            /*
             * **这一趟打不开的那几片，抢救到哪儿**（真机 2026-10-06）：
             * 那一片（`111.zip`）是下一轮那一组的入口包，而递归核心收尾会清掉它自己的工作区
             * ⇒ 协调器拿到结论时它已经不在了（排障日志逐字：`那一片已经不在盘上（…\output\111.zip）`）
             * ⇒ "入口包留在它自己那条链里"整条路断掉 ⇒ 结果校验看到空目录 ⇒ 列表显示「解压失败」+ 源包不处理。
             * ⇒ 把这一单的**暂存目录**给它（收尾链读的就是它），它在记下那片的同时复制一份过去。
             */
            recursiveExtractor.UnresolvedPieceRescueDirectory = stageDirectory;

            /*
             * 「可疑条目提示」的回传口（2026-10-05 只读审计）：递归核心在拿到本层清单时算出那句话，
             * 这里把它写进**与单层路径同一个字段**、走**同一个出口**（PublishDangerousEntriesHint）——
             * 于是默认档下内层包里的 `.exe / .bat / .ps1` 也会像源包那样被点名。
             *
             * ⚠ 写的是**协调器手上这个 task**（递归核心拿到的可能是内嵌归档的探针任务），
             * 提示属于用户看到的那一行。
             */
            recursiveExtractor.DangerousEntriesReported = hint => PublishDangerousEntriesHint(task, hint, "INFO");

            /*
             * 「这一层的密码被验证成功」⇒ 记进**本批已成功的密码**（用户 2026-09-26 第 45 条）。
             *
             * ⛔ 为什么必须补这一处：默认档是「有压缩包就解压」，解压交给递归核心做 ⇒
             * 递归路不报这一声的话，`PasswordService.RecordPasswordSuccess` 在**默认档下一次都不会被调到**
             * ⇒ 后面每一个包的候选表里都没有"复用本批已成功的密码" ⇒ 每个包都从空密码开始把整张表重试一遍
             * （用户原话：「不就是在放屁吗，每次都是重新一轮密码检测」）。
             * 2026-10-16 实测（`PasswordReuseTests.第一包第五候选成功…`）：同一批第二个包拿到的候选表
             * 逐字是 ` | <示例密码1> | <示例密码2> | <示例密码3> | <示例密码4> | <示例密码5>` —— 与第一个包**一模一样**。
             *
             * 挂源文件（`task.CurrentPath`）而不是工作区里那一层：与单层路那三处**逐字同一口径**
             * （"密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
             * 而工作区里抠出来的临时文件活不过这次任务"）。
             */
            recursiveExtractor.PasswordVerified = password =>
                _passwordService.RecordPasswordSuccess(task.CurrentPath, password);

            /*
             * 记下"这一单用的是哪个递归核心"：它手上的逐层工作区要在任务收尾时按结论处理
             * （成功由它自己清；失败 / 取消 / 部分完成由 CleanupFailedTaskWorkspace 按设置决定，见那里的说明）。
             * 取消那一条路上 ExtractAsync 直接抛异常，只有在这里留着实例，收尾才找得到那份工作区。
             */
            _taskRecursiveExtractors[task] = recursiveExtractor;

            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 递归核心（RecursiveExtractor）从 task.CurrentPath 取"第 0 层要解哪个归档"。
             * 内嵌归档真正的归档是抠出来的那个文件，所以这里给它一个只带正确路径的**探针任务**，
             * 而不是把 task.CurrentPath 临时改掉 —— 源文件路径是改名 / 清理 / 统计的依据，
             * 任何"改了再改回来"的写法都会在这些地方留下一个窗口期（异常时更是永远改不回来）。
             * 递归结论仍然应用在真正的 task 上（ApplyRecursionResult(task, result)）。
             */
            ArchiveTask recursionTask = string.Equals(
                    engineArchivePath,
                    task.CurrentPath,
                    StringComparison.OrdinalIgnoreCase)
                ? task
                : new ArchiveTask(engineArchivePath);

            /*
             * ⚠ 2026-10-15：这一行原来打印**用户设置里那一档**（真机日志逐字：
             * `002.7z：开始递归解压（模式 SingleChain，最大 5 层）。`）—— 屏幕上不该再出现档位名
             * （用户口径：「我不是将单层路给删掉了吗，现在就是全面解压所有分路」）。
             * 现在如实写"有压缩包就解压"，层数上限照旧是用户设置。
             */
            AppendLog("INFO", $"{task.LogName}：开始递归解压（有压缩包就解压，最大 {limits.MaxDepth} 层）。");

            /*
             * 递归内层的进度（2026-09-22 补）：复用**同一个** TaskProgressSink，
             * 于是界面上的百分比 / 当前条目、详情窗口的耗时、以及"跨 10% 写一行"的日志
             * 与单层路径完全是同一套行为 —— 递归核心只负责把 sink 转发给引擎。
             *
             * 卡住提示同理：一层套一层时最容易出现"几十秒没有任何输出"，
             * 那一段过去在界面上就是死等（整条递归连一条 WARN 都不会有）。
             */
            TaskProgressSink recursionProgressSink = new(this, task);

            RecursionResult result = await recursiveExtractor.ExtractAsync(
                recursionTask,
                stageDirectory,
                previousDecision: null,
                cancellationToken,
                recursionProgressSink,
                notice => OnEngineStalled(task, notice));

            /*
             * ===== 跨链收卷（2026-10-05 真机第九批 CCCC）=====
             *
             * 位置必须在**工作区被清掉之前**：这一趟没走完时，工作区里那一片（真机上是末片 `111.zip`）
             * 可能正是"同一分卷组"里别单缺的那一片，而链一收尾工作区就整份删掉 ⇒ 批末那一站到盘上一看，
             * 缺的东西刚刚被自己扔掉（真机 22:06:02「已清理工作区：2 个文件 / 48.35 MiB」就是这么把末片带走的）。
             */
            /*
             * ⛔ **落地不能放在这里**（2026-10-06 真机当场踩到）：`PublishUnresolvedVolumePieces`
             * 把入口包（`111.zip`）从这一层的产物目录里**搬走**了，而紧接着的结果校验看的正是那个目录
             * ⇒ 「校验未通过：输出目录是空目录，没有产物」⇒ ① 列表里显示「解压失败」② 「完整性：可证不完整」
             * ⇒ 源包一个字节都不处理。落地挪到 `PostProcessSuccessAsync` 里**校验与定稿都过了之后**做
             * （那时 `task.CurrentPath` 已改到入口、也不再有任何校验读那个目录）。
             */
            AdoptUnresolvedVolumePieces(result, task);

            if (result.StopReason == RecursionStopReason.NeedsDecision && result.Decision != null)
            {
                /*
                 * 递归核心在问"这些内层归档要不要也解开"（只有②页「单链自动展开」那一档会问）。
                 *
                 * ⛔ 2026-10-04（用户当场推翻旧口径）：一键档**不再**走"保守不展开、只留当前这一层"。
                 * 老写法是 `bool expandAll = !oneClickRun && await ShowConfirmOnUiThreadAsync(…)` ——
                 * 一键档不看用户在②页选了哪一档，永远只保留当前这一层，日志还写着"按保守档"：
                 * 一键处理因此成了比手动档**更弱**的档位（真机 `第6集.7z` 就是这么停的；
                 * 用户原话「就应该有压缩包就解压啊」）。
                 *
                 * 现在一键档与手动档的差别**只剩"问不问"**（红线：批中间零弹窗，见
                 * `SuppressDecisionPromptsForOneClickRun`），展开策略一律按**用户所选档位**：
                 * · `AllBranches`（出厂默认）：递归核心根本不会给出这个询问（它已经把每个分支都入队了），
                 *   下面那一支只是"哪天门开了"的兜底 —— 全展开，与用户选的那一档一致；
                 * · `SingleChain`：那一档的语义就是"一层里出现 **≥2 个**真归档就停"，
                 *   一键档没法问 ⇒ 与手动档里用户点「取消」同一结果：保留当前这一层 + 如实写日志。
                 */
                bool expandAll = oneClickRun
                    ? true
                    : await ShowConfirmOnUiThreadAsync(
                        result.Decision.Prompt + Environment.NewLine + Environment.NewLine +
                        "选“确定”：把这些内层归档也解开。选“取消”：只保留当前这一层的结果。");

                if (oneClickRun)
                {
                    /*
                     * ⚠ 2026-10-04（真机）：这一行过去写死"（多分支）… 按保守档" ——
                     * 措辞与档位两处都不准：
                     * ① 判据只看**内层归档的数量**（≥2 才停；1 个 + 旁边一个 `.mp4` 会被"这是压缩包吗，
                     *    不是那你停什么"顶回来），所以数量要按真实的说；
                     * ② 停下来的理由不是"一键档保守"，而是**用户在②页选的那一档**如此。
                     */
                    int candidateCount = result.Decision.CandidateArchives.Count;

                    AppendLog(
                        "WARN",
                        candidateCount > 1
                            ? string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchHeldBackMultipleFormat,
                                task.FileName,
                                candidateCount,
                                RecursiveExtractor.DescribeUnexpandedNames(result.Decision.CandidateArchives))
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchHeldBackSingleFormat,
                                task.FileName));
                }

                if (expandAll)
                {
                    // 同一个实例接着跑（上限不变）：续跑用的是用户刚确认的那批候选，不是重新扫一遍。
                    result = await recursiveExtractor.ExtractAsync(
                        recursionTask,
                        stageDirectory,
                        result.Decision,
                        cancellationToken,
                        recursionProgressSink,
                        notice => OnEngineStalled(task, notice));
                }
                else
                {
                    /*
                     * 用户只想保留当前这一层。
                     *
                     * 这里**不能**就这样返回 NeedsDecision：那会让用户以为"东西已经解到输出目录了"，
                     * 而按不变量 12，需要决定时产物还留在工作区里 —— 文案与实现不符等于骗人。
                     *
                     * 做法：把已经解好的第 0 层产物从递归工作区搬进**暂存目录**（它照样是入仓阶段的一部分），
                     * 并把它当成 Completed 继续走"校验 → 定稿 → 归集 → 清理"。
                     * 不重新解压一遍：大包重解代价太大，而产物本来就在工作区里。
                     */
                    string? layerZeroOutput = result.Layers
                        .FirstOrDefault(l => l.Depth == 0 && l.Success)?.OutputPath;

                    if (!string.IsNullOrWhiteSpace(layerZeroOutput) && Directory.Exists(layerZeroOutput))
                    {
                        /*
                         * 搬运是同步磁盘活（递归枚举 + File.Move），同样必须离开 UI 线程 ——
                         * 理由与收尾那一段完全相同（见 PostProcessSuccessAsync 的线程规则）。
                         */
                        int moved = await Task.Run(
                            () => MoveDirectoryContent(layerZeroOutput, stageDirectory),
                            cancellationToken);

                        result = new RecursionResult
                        {
                            StopReason = RecursionStopReason.Completed,
                            Completed = true,
                            PartiallyCompleted = false,
                            Layers = result.Layers,
                            FinalOutputPath = stageDirectory,
                            Summary = $"按你的选择只解开了当前这一层，已取回 {moved} 个文件（定稿后进输出目录）。"
                        };

                        /*
                         * 产物已经取回暂存目录 → 那份递归工作区（含 report.json）从此是纯垃圾，
                         * 留着还会在下次启动时被算进"未完成的工作区"报告。
                         *
                         * 为什么得在这里补一刀：递归核心是以 NeedsDecision 收的尾，按规则**没有**清工作区
                         * （"等你决定"属于部分完成）；结论是在**这里**被改写成 Completed 的 ——
                         * 谁改写结论，谁负责把"这次算成功"的收尾补上（递归成功那一支的清理在
                         * RecursiveExtractor.FinalizeRun 里，走的是同一个 Cleanup()）。
                         *
                         * ⚠ 只有"一个文件都没漏下"才清：MoveDirectoryContent 是尽力而为
                         * （单个文件搬不动就跳过、只记进返回的文件数），漏下的那些还在工作区里，
                         * 删掉它们等于丢用户的产物（不变量 1 的精神）。
                         */
                        if (HasNoFiles(layerZeroOutput))
                        {
                            recursiveExtractor.TryCleanupCurrentWorkspace(
                                task.FileName,
                                "用户选择只保留当前这一层，第 0 层产物已取回暂存目录");
                        }
                    }
                    else
                    {
                        // 第 0 层本身就没成功：保持原结论（部分完成），让它如实报告。
                        AppendLog("WARN", $"{task.LogName}：当前这一层没有可用产物，保留原结论。");
                    }
                }
            }

            ApplyRecursionResult(task, result);

            /*
             * ===== 停因是「分卷缺失」时，把话说对（用户 2026-10-06 口径）=====
             *
             * 用户原话：「111.rar 是「部分完成」…… **这个根本就不是部分完成，对于 111.rar 这个就是成功了**，
             * 只不过这个内容物又是下一轮解压需要的东西」「没有完整的**不是缺卷**，因为你现在还没有将所有的
             * 东西统统解压完，还没有到最后 … 你就**只能显示跳过**」。
             *
             * ⛔ 这里**只改措辞**，⛔ 一个字都不动机器终态与状态常量：`task.Status` 是"装箱单"
             * （`PostProcessSuccessAsync` 那一整条发布链 / 定稿 / 源包处理都读它），在这里改它
             * 会连带改掉"已解出来的东西要不要落地"（实测踩到过一次，整组连入口都不再落地）。
             * 终态那一档的改口径要单独拍板（见交接说明里的"未完成"）。
             */
            if (result.StopReason == RecursionStopReason.MissingVolume)
            {
                bool deferredHere = IsDeferredVolumeDeficit(task);

                /*
                 * ⚠ 2026-10-08（用户口径「没有到最后一步都是先跳过」）：递归**中途**撞上缺卷这一档，
                 * 批中间不许把「分卷缺失」当结论摆出来 —— 缺的那几片完全可能被同批别的包补上
                 * （真机 `111.z01` 就是这么补上的）。这里只记一个"待批末判"的**显示/统计事实位**：
                 * ⛔ `Status` / `Outcome` 一个字不动（它们是"装箱单"，发布链 / 定稿 / 源包处理都读，
                 * 见上面那段说明），⛔ 也不改判据。批末定稿时清掉它，那一格随即回到真结论。
                 */
                if (!_volumeDeficitFinalPass.Contains(task))
                {
                    // 显示/统计那一格照旧点亮（批中间不许顶着红色「分卷缺失」）—— 与改动前逐字相同。
                    task.IsVolumeDeficitDeferred = true;

                    /*
                     * ⚠ 2026-10-10 **修**：老写法**只点亮显示位、不进名单** ⇒ 批末那一站
                     * （<see cref="RecheckDeferredVolumeDeficits"/>）读那份名单时是**空的**，
                     * 于是一个字都不做 ⇒ 「等批末再判」这句话对**中途**撞上缺卷的单是句空话。
                     *
                     * <para>真机实测（EEEE，2026-10-10）：`111.rar` 停在第 3 层（要开的正是分卷组 `111`
                     * 的入口 `111.zip`，入口已由它自己解出来、散着的片还差 `.z03`），界面上也写着
                     * 「跳过（缺卷，等批末再判）」，而批末名单 **0 单** ⇒ 收片那一档
                     * （`TryGatherGroupPiecesInto`）从没跑过 ⇒ `111(4)\111.z03` 那一片永远接不进这一组
                     * （走的是"目标层算不出来 ⇒ 静默什么都不做"那条支）⇒ 整组永远不解、一次引擎调用都不做
                     * —— 这就是"第一大步不闭环"。</para>
                     *
                     * <para>⛔ 进名单**只进"停下来的那一组就是我这一组"的那一单**
                     * （<see cref="IsStalledGroupOwnGroup"/>）：批末那一站是拿**这一单自己的包基名**
                     * 去问"这一组齐了没"的，名字对不上的单进去只会被扣一句不相干的「分卷缺失」。</para>
                     */
                    if (IsStalledGroupOwnGroup(task, result))
                    {
                        RememberDeferredVolumeDeficit(task);
                    }
                    else if (FindStalledVolumePiece(result) is { Length: > 0 } stalledPiece
                             && !AnyTaskRepresentsGroup(stalledPiece))
                    {
                        /*
                         * ⚠ 2026-10-13 困难批 `11111111` 新开的一格：停下来的**是别人那一组**的片。
                         *
                         * <para>现场（运行期实测）：三片 `222-L2.part1/2/3.rar` 各自被 `222-W0-1.zip` /
                         * `222-W1-1.7z` / `222-W2-0.zip` 三条链解出来，每一条链都停在"这一组的入口包不在旁边"
                         * （`Missing volume`）；而这三单的包基名（`222-W0-1` 等）与那一组（`222-L2`）
                         * **族与基名都对不上** ⇒ 老闸门一单都不登记 ⇒ 批末名单 0 条
                         * ⇒ 那一组永远没人收拢、也永远不解开 ⇒ ①红。</para>
                         *
                         * <para>⛔ 安全性没放宽：登记的仍然是**这一单真的产出过的那一片**（`UnresolvedVolumePieces`
                         * 里自报是分卷片的那一个，唯一出口 `VolumeGroupDetector.TryGetVolumeIndex`），
                         * 批末只是拿**它**去问对的那个组；仍然"补齐才跑、仍然缺就如实报缺卷"。</para>
                         */
                        /*
                         * ⚠ 2026-10-14（插桩实测 `10101010`）：登记时**必须把"参照名"当场定下来** ——
                         * 这一片随后会随它那条链的工作区被清掉，到批末再读它的卡已经读不出来
                         * （逐字：批末那一行仍写 `引用名=「pack111-L2.7z.zip」` ⇒ 一个成员都匹配不上
                         * ⇒ 收拢收不到任何一片）。⇒ 现在（文件还在）就用**卡**算出它的规范名当参照：
                         * `pack111-L2.7z.txt`（卡说 `VolumeFirstPiece`）⇒ `pack111-L2.7z.001` ✓。
                         * ⛔ 判不出（卡读不出 / 名字也推不出）⇒ 照旧用原来那个路径（老口径不放宽）。
                         */
                        string reference = VolumePieceFacts.ResolveCanonicalName(
                            stalledPiece,
                            FileNature.Inspect(stalledPiece));

                        RememberDeferredVolumeDeficit(task, reference.Length > 0 ? reference : stalledPiece);
                    }
                    else
                    {
                        /*
                         * ⚠ 2026-10-16（困难批 `21212121`）：**这一档原来一个字都不写** —— 批末名单里没有它、
                         * 日志里也看不出为什么 ⇒ 只能读码猜。缺卷的判据输入必须留痕（同"收卷判据输入"那一档）。
                         * ⛔ 只写事实，⛔ 不加判据、⛔ 不改任何行为。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.LogName}：缺卷登记判据输入：停下的那一组是不是自己这一组={IsStalledGroupOwnGroup(task, result)}｜"
                            + $"这一趟自报的分卷片 {result.UnresolvedVolumePieces.Count} 个｜"
                            + $"停下的那一片里的参照名=「{(FindStalledVolumePiece(result) is { Length: > 0 } stalledForLog ? FileNameHelper.GetFileName(stalledForLog) : "（空）")}」｜"
                            + $"任务表里已有别单代表这一组={(FindStalledVolumePiece(result) is { Length: > 0 } represented ? AnyTaskRepresentsGroup(represented) : false)}｜"
                            + "⇒ 不进批末名单（批末那一站不会为这一组补判）");
                    }
                }

                AppendLog(
                    "INFO",
                    deferredHere
                        ? $"{task.LogName}：这一单该解的都解出来了，停下的那一层要开的是这一组的分卷片，"
                          + "缺的是同一组别的片 —— 这一组先记着（跳过），等这一批的解压都跑完再判一次（⛔ 不是失败）。"
                        : $"{task.LogName}：这一单该解的都解出来了；停下的那一层要开的是这一组的分卷片，"
                          + "而这一组到最后一刻仍然没凑齐（缺哪几片见下面的缺卷结论）。");
            }

            /*
             * 部分完成发布（用户 2026-10-02）：递归这条路上"解了一半的现场"是**最深那一层失败时**
             * 留在它自己产物目录里的东西。登记在这里、真发布在任务收尾（清工作区之前）——
             * 那之前还有链尾的定稿与清理，现场随时可能变。
             */
            RecordRecursivePartialPublishCandidate(task, result);

            /*
             * 「最后一层过程物」按③页「删除操作」那一档处置（用户 2026-10-14 口径 §2.1/§2.2）——
             * **必须在这一刻做**：它的默认档动作是"把那一份收进其余物"，而这一单的工作区随后
             * （失败 / 部分完成收尾）会被整份清掉 ⇒ 晚一步就再也没有那一份了。
             */
            PreserveStoppedLayerProcessArtifact(task, result);

            return result;
        }

    }
}
