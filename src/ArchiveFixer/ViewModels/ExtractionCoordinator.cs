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

        /// <summary>兼容出口（测试/轮次层读它）：实现已搬进 <see cref="ArchiveFixer.Extraction.VolumeNameOrchestration"/>。</summary>
        internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note)
            => ArchiveFixer.Extraction.VolumeNameOrchestration.TryConfirmVolumeGroupComplete(filePath, out note);


        private string GlobalPassword => _vm.GlobalPassword;

        private string SelectedOutputDirectory => _vm.SelectedOutputDirectory;

        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

        private bool IsStopping { get => _vm.IsStopping; set => _vm.IsStopping = value; }

        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }

    }
}