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
    /// <summary>
    /// 批量解压流程协调器。
    /// 负责并发调度、单任务解压管线、停止后续/取消当前。
    /// </summary>
    internal sealed class ExtractionCoordinator
    {
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
                /*
                 * 「续解时省略中间层」这一档同样当场从设置里取（与上限同一个理由：改完不重启也要生效）。
                 * **首层与末层不受它影响** —— 判据只有一处（PackageLayerRules.ShouldKeepLayerFolder），
                 * 递归发布侧与续解侧读的是同一个出口。
                 */
                OmitMiddlePackageLayers = Settings.OmitMiddleContinuationLayers
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
             * `PrepareRestHandlingForBatch`）—— 递归路**不另算一遍**：「移入回收站」档照旧链尾一次
             * （回收站不释放盘上空间），「空间不足」档照旧走它自己那一支（每层各删各的、含源包），
             * 「不动其余物」档更不动内层包。挂钩子这一行读的就是它。
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
                $"{task.FileName}：开工前补拍源文件快照（这份任务过去没有基准），"
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
        private static void FinalizeOutcomeIfPending(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            if (task.Outcome == TaskOutcome.Pending
                && task.EndTime.HasValue
                && task.OutputVerification != OutputVerificationOutcome.Passed)
            {
                task.Outcome = TaskOutcome.Failed;
            }
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

        private readonly Dictionary<ArchiveTask, TaskSpaceEstimate> _refinedEstimates = new();

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
        /// 本批走不走**「逐层回收」**（用户 2026-10-03 拍板；批首与 <see cref="_restHandlingThisBatch"/>
        /// 一起定一次）。
        ///
        /// <para><b>B 档位分叉</b>：只有本批「删除操作 = <see cref="RestHandlingModes.Delete"/>（彻底删除）」
        /// 才逐层回收；「<see cref="RestHandlingModes.RecycleBin"/>（移入回收站）」档**照旧链尾一次**
        /// —— 移入回收站**不释放盘上空间**（见 <c>RestItemPurger.AutoRecycleReason</c> 自己写的那句
        /// "空间要等清空回收站才释放"）⇒ 逐层做零空间收益，只多造回收站条目。
        /// 「<see cref="RestHandlingModes.Keep"/>（不动其余物）」档不回收任何东西（只有第 2、3 类残渣随层清）。</para>
        ///
        /// <para><b>⛔ 只在这里判一次</b>（AGENTS.md §9.5：同一件事的真值只允许有一个出口）——
        /// 每层回收（<c>PostProcessSuccessAsync</c>）与链尾"只处理源包"（
        /// <c>CompleteRootSourcePackagesAfterChainAsync</c>）都只读这一个字段，
        /// 别处⛔ 不许再拿设置 / <see cref="RunOptions"/> 判一遍。</para>
        ///
        /// <para>⚠ 与 <see cref="_restHandlingThisBatch"/> 同样的寿命：批尾**不清**（链尾钩子在批次返回之后
        /// 才跑，清掉会让链尾以为"这一批没有逐层回收"而再去搬已经不存在的内层包）；
        /// 下一批开工时 <c>PrepareRestHandlingForBatch</c> 会重新写一次。</para>
        /// </summary>
        private bool _layerReclaimThisBatch;

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
                            stageDirectory,
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
                    stageDirectory,
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
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = work.Verification.FailureMessage;
                task.VerifyMessage = work.Verification.FailureMessage;

                /*
                 * 机器事实要落成 **Failed**，不能停在"没通过校验"的含糊档：
                 * 调用方（候选循环收尾）与汇总都靠"这一档"区分"校验判否"与
                 * "越界 / 超预算 / 定稿失败"那些**其它**失败（它们的字段是 NotAttempted）。
                 */
                task.OutputVerification = OutputVerificationOutcome.Failed;
                task.Outcome = TaskOutcome.Failed;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"{task.FileName}：{work.Verification.FailureMessage}；不落「解压成功」，源包一个字节都不动。");
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
                AppendLog("WARN", $"{task.FileName}：打开输出目录失败（不影响解压结论）：{ex.Message}");
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

                logEntries.Add(("ERROR", $"{task.FileName}：{echoMessage}"));

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

                logEntries.Add(("ERROR", $"{task.FileName}：{landingMessage}"));

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

                logEntries.Add(("ERROR", $"{task.FileName}：{budgetMessage}"));

                return new PostProcessWorkResult
                {
                    Verification = verification,
                    LogEntries = logEntries,
                    BudgetExceeded = true,
                    BudgetMessage = budgetMessage
                };
            }

            logEntries.Add((verification.Verified ? "INFO" : "WARN", $"{task.FileName}：结果校验 —— {verification.Message}"));

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
                logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，产物已退回暂存目录，未写入最终目录。"));

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

                logEntries.Add(("ERROR", $"{task.FileName}：{failureMessage}"));

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
                logEntries.Add(("INFO", $"{task.FileName}：内层包，产物与父任务同一个目录，跳过结果归集。"));
            }
            else if (collectResults)
            {
                if (!verification.Verified)
                {
                    logEntries.Add(("WARN", $"{task.FileName}：校验未通过，已跳过结果归集。"));
                }
                else
                {
                    collected = new ResultCollector().Collect(task, collectTargetDirectory);

                    logEntries.Add((collected.Success ? "INFO" : "WARN", $"{task.FileName}：结果归集 —— {collected.Message}"));
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
            SourcePackageMoveResult? sourceMove = null;
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
                 * ===== 普通档「逐层回收」（用户 2026-10-03 拍板 + B 档位分叉）=====
                 *
                 * 老口径（2026-09-25 第 32 条起）：续解层**什么都不做**，内层包攒到链尾由
                 * `CollectChainInnerPackagesIntoRestAsync` + 「删除操作」统一清 ⇒ 四层链的峰值是"四倍单层"。
                 *
                 * 现在：③页「删除操作 = 彻底删除」时，续解层跑完就删掉**上一层交给它的那个内层包**
                 * （它对本层就是"解出这一层内容所消耗掉的那份源"，用完了就该当场还回去）；
                 * 最外层源包留到链尾按「源包操作 + 删除操作」处理 —— 用户原话："原包还是比较重要的"。
                 *
                 * ⛔ B 档位分叉（判据只有 `_layerReclaimThisBatch` 一个出口，别处不许再判）：
                 * 「移入回收站」档**不逐层**（回收站不释放盘上空间 ⇒ 逐层做零收益、只多造条目），
                 * 照旧链尾一次；「不动其余物」档更不动内层包（只有第 2、3 类残渣随层清）。
                 *
                 * ⛔ 红线一条没松：这一支只在"定稿成功 + 可证完整 + 未取消"之后才走得到
                 * （commit / verification 关卡与 `:1459` 的令牌检查都在上面），
                 * 而 `PurgeLayerSourcePackage` 自己还会再查一遍 commit 与完整性判据。
                 * 失败 / 部分完成 / 取消 ⇒ 那一层的过程物一个字节都不动。
                 */
                if (_layerReclaimThisBatch)
                {
                    sourceMoveFailure = PurgeLayerSourcePackage(
                        task, verification, commit, logEntries, LayerPurgeTrigger.LayerReclaim);
                }
                else if (sourceHandling != SourceHandlingMode.KeepInPlace)
                {
                    logEntries.Add(("INFO", $"{task.FileName}：内层包，源文件属于其余物里的过程物，已跳过源包处理。"));
                }
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
                logEntries.Add(("INFO", $"{task.FileName}：不删原包 —— 源包一个字节都不动（空间不足的安全档）。"));
            }
            else if (sourceHandling == SourceHandlingMode.MoveToRest)
            {
                sourceMove = MoveSourcePackageIntoRest(
                    task, verification, commit, collected, oneClickRun, logEntries);

                if (sourceMove.FailedCount > 0)
                {
                    // 内容物已经好了，只有源包没搬成 —— 这不是"解压失败"，而是"这件事没做完"。
                    sourceMoveFailure =
                        $"内容物已好，源包未能移入其余物：{sourceMove.Message}（源包仍在原处，内容物不受影响）";
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
                logEntries.Add(("WARN", $"{task.FileName}：{completeness.Blocker} —— 源包留在原地（未移入其余物）。"));
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
                logEntries.Add(("INFO", $"{task.FileName}：源包已经搬进其余物，本次不再搬（幂等）。"));
                return new SourcePackageMoveResult { Attempted = false, Message = "源包已经搬进其余物，不再搬第二次" };
            }

            if (!commit.Attempted || commit.FailedCount > 0)
            {
                // 定稿没发生 / 有搬运失败：内容物没定稿成功，源包不动，其余物也不生成（D-11 第 1 条）。
                logEntries.Add(("WARN", $"{task.FileName}：内容物未全部定稿，源包留在原地（未移入其余物）。"));
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
                    $"{task.FileName}：本轮产出的都是待续解的过程物（没有内容物定稿），" +
                    $"源包先留在原地，等整条续解链跑完后再按设置搬进其余物。"));

                return new SourcePackageMoveResult { Attempted = false, Message = "本轮没有内容物，源包搬运留到链结束后补搬" };
            }

            string artifactRoot = ResolveRestDirectoryAfterCollect(commit, collected);

            if (string.IsNullOrWhiteSpace(artifactRoot))
            {
                logEntries.Add(("WARN", $"{task.FileName}：其余物目录算不出来，源包留在原地。"));
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
            /// 普通档 + ③页「删除操作 = 彻底删除」（用户 2026-10-03 拍板的「逐层回收」）：
            /// 续解层跑完就删掉**上一层交给它的那个内层包**；最外层源包留到链尾按两档处理。
            /// </summary>
            LayerReclaim
        }

        /// <summary>这次是哪个触发点（进日志；⛔ 措辞只在这里拼一次）。</summary>
        private static string DescribeLayerPurgeTrigger(LayerPurgeTrigger trigger) => trigger switch
        {
            LayerPurgeTrigger.LayerReclaim => "逐层回收（删除操作=彻底删除）",
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
        /// <para><b>2026-10-03 起多了一个触发点</b>（用户拍板的「普通档逐层回收」+ B 档位分叉）：
        /// 普通档里③页选「删除操作 = 彻底删除」时，**续解层**也走这一条 —— 每层当场回收这一层的过程物；
        /// ③页选「移入回收站」时**不逐层**（回收站不释放盘上空间，逐层做零收益、只多造条目），
        /// 照旧链尾一次。「不动其余物」档更不动内层包。判据只有 `_layerReclaimThisBatch` 一个出口。
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
            string? reclaimWorkspaceDirectory = null)
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
                    logEntries.Add(("WARN", $"{task.FileName}：内容物未全部定稿，{subject}留在原地（{triggerLabel}也不删）。"));
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
                    logEntries.Add(("WARN", $"{task.FileName}：{completeness.Blocker}，{subject}留在原地（{triggerLabel}也不删）。"));
                    return null;
                }
            }

            if (task.SourcePackageMove == SourcePackageMoveState.Done)
            {
                // 幂等：已经处理过了（删过 / 搬过），绝不第二次。
                logEntries.Add(("INFO", $"{task.FileName}：{subject}已经处理过（删过或搬过），不再动第二次。"));
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
            string? splitBlocker = DescribeLayerSplitBlocker(task, reclaimWorkspaceDirectory);

            if (splitBlocker != null)
            {
                logEntries.Add(("WARN", $"{task.FileName}：{splitBlocker}"));
                return null;
            }

            SourceCleanupResult cleanup = SourcePackageCleanup.CleanupVerified(
                task,
                verified: true,
                verificationNote: $"{triggerLabel}：内容物已定稿，校验通过",
                enabled: true);

            if (cleanup.DeletedFiles.Count == 0 && cleanup.FailedFiles.Count == 0)
            {
                // 没有可删的路径（分卷清单不完整）→ 如实说，不假装删过了。
                logEntries.Add(("WARN", $"{task.FileName}：{triggerLabel}要删{subject}，但任务的源包清单是空的 —— {cleanup.Message}"));
                return null;
            }

            if (cleanup.FailedFiles.Count > 0)
            {
                logEntries.Add((
                    "ERROR",
                    $"{task.FileName}：{triggerLabel}删除{subject}有 {cleanup.FailedFiles.Count} 个失败"
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
                    : $"{task.FileName}：{triggerLabel} —— 定稿 + 校验通过，已立刻永久删除{subject} "
                      + $"{cleanup.DeletedFiles.Count} 个，收回 {WorkspaceCleanupService.FormatSize(cleanup.FreedBytes)}"
                      + tail));

            return null;
        }

        /// <summary>
        /// 这一层准备删的那一份是不是**半套分卷**（同组还有一片留在成品目录树里）？
        /// 返回非 null = **拦下的理由**（调用方写一行 WARN、一个字节都不删）。
        ///
        /// <para>判据**转调唯一出口** <see cref="RestVolumeCompletenessGate.DescribeLayerReclaimBlocker"/>
        /// （⛔ 不在这里另写一份）；候选清单也转调删除侧那一个出口
        /// <see cref="SourceCleanupService.BuildTargetList"/>（判据与真删看同一份清单）。</para>
        /// </summary>
        private string? DescribeLayerSplitBlocker(ArchiveTask task, string? reclaimWorkspaceDirectory = null)
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
                    excluded);

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
                logEntries.Add(("INFO", $"{task.FileName}：{line}"));
            }

            // 日志口径要能区分"本轮直接搬"与"链结束后的补搬"（用户明确要求）。
            string label = trigger == SourceMoveTrigger.AfterChain ? "链结束后的补搬" : "源包处理";

            if (result.Attempted)
            {
                logEntries.Add((result.FailedCount == 0 ? "INFO" : "WARN", $"{task.FileName}：{label} —— {result.Message}"));

                if (result.FailedCount > 0)
                {
                    // 失败必须写 WARN 并说清原因（不许只留一句汇总）。
                    logEntries.Add((
                        "WARN",
                        $"{task.FileName}：{label}没能完成 —— {string.Join("；", result.Failures)}" +
                        $"（源包仍在原处，内容物不受影响）"));
                }
            }
            else
            {
                logEntries.Add(("INFO", $"{task.FileName}：{label} —— {plan.Message}"));

                foreach (ArtifactSkip skip in plan.Skipped)
                {
                    logEntries.Add(("WARN", $"{task.FileName}：{skip.Message}：{skip.SourcePath}"));
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
            FinalizeDeferredVolumeDeficits();

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
                 * ⛔ 「移入回收站」档与「不动其余物」档**照旧调它**（B 档位分叉：那两档不逐层回收，
                 * 内层包就是在这里被收进其余物的）。判据仍然只有 `_layerReclaimThisBatch` 一个出口。
                 */
                if (!_layerReclaimThisBatch)
                {
                    await CollectChainInnerPackagesIntoRestAsync(rootTask, chainTasks, cancellationToken);
                }

                /*
                 * 一键处理里「删除操作」那一档是**留到链尾统一做**的（见 RunRestHandlingAsync 的说明：
                 * 任务收尾那一刻其余物里还躺着内层包，删了就没法接着解）。
                 * 到得了这里就说明整条续解链已经跑完 —— 除了上面那种"待补搬"的任务，
                 * 其余成功任务也要在这里把各自的其余物按档处理掉，否则选了「彻底删除」的人会发现
                 * 过程物那几份一直留着。
                 */
                await ApplyRestHandlingAfterChainAsync(rootTask, chainTasks, cancellationToken);
            }
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
             * ⛔ **留着没跑的内层包不能当过程物清掉**（用户 2026-09-28 真机：
             * `amb909\amb909\amb.7z.001..004` 一直留在成品目录里，他以为是"忘了搬进其余物"）。
             *
             * 为什么不能清：过程物（内层包）能清的前提是"它的内容物已经解出来了" ——
             * 这一单跑成功、内层包被判**已完成**才成立。而"这一轮根本没跑"的内层包，内容还在它里面，
             * 清掉（其余物在删除档下是**彻底删除**）等于把唯一一份内容删了。
             *
             * 所以这里只做一件事：**如实点名 + 告诉他怎么处理**。判据与搬运用同一套
             * （分卷组取 VolumePaths 全卷，单文件取自己；只认在本单输出目录里的）。
             */
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
                        $"{rootTask.FileName}：成品目录里还留着 {leftBehind.Count} 个内层包没被清理（它们的内容还没解出来，"
                        + $"清掉就等于删内容）：{string.Join("、", leftBehind.Take(5))}"
                        + $"{(leftBehind.Count > 5 ? " 等" : string.Empty)}"
                        + " —— 要解就把它们加进任务列表；删除档只清「内容物已经解出来」的过程物。");
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{rootTask.FileName}：清点留在成品目录里的内层包失败（不影响这一单）：{ex.Message}");
            }
            if (moves.Count == 0)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                AppendLog("WARN", $"{rootTask.FileName}：链尾收内层包之前被取消，内层包留在原地（其余物不生成）。");
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
                     */
                    string target = to;

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

                    /*
                     * 同卷直接改名（快、且失败不留半份）；跨卷 / 被占用时回落到"先复制成功再删原件"
                     * （与源包搬运同一口径：复制失败就一个字节都不动）。
                     */
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
                        $"{rootTask.FileName}：内层包已移入其余物 —— {Path.GetFileName(from)}"
                        + $"（{Path.GetFileName(Path.GetDirectoryName(target)) ?? string.Empty} 那一层）");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
                    AppendLog("WARN", $"{rootTask.FileName}：内层包没能移入其余物：{from} —— {ex.Message}（它留在原地）");
                }
            }

            if (moves.Count > failures.Count)
            {
                // 只有真的搬进去了才把其余物记到根任务上（否则删除档会去删一个不存在的目录）。
                rootTask.RestDirectoryPath = restDirectory;
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
                        $"{rootTask.FileName}：{Path.GetFileName(path)} 看着是分卷组的一卷，"
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
                        $"{rootTask.FileName}：{Path.GetFileName(path)} 命中「内容物保留关键词」（{keepKeyword}）"
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
                    $"{task.FileName}：这一卷的名字看着被改坏了（{plan.Describe()}），但改名没成功：{failure}"
                    + " —— 保持原样继续，下面会如实报结论。");

                return engineArchivePath;
            }

            AppendLog(
                "INFO",
                $"{task.FileName}：分卷名字被改坏，已在自己的产物里摆正 —— {plan.Describe()}"
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
                $"{task.FileName}：{StatusText.ChainRestBlockedPrefix} —— {why}；"
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
                    $"{task.FileName}：{verdict.Message}（L3 核对依据：{DescribeManifestBasis(task.ManifestExpectation)}；"
                    + $"机器结论：{verdict.Evidence}）");

                return;
            }

            AppendLog(
                "WARN",
                $"{task.FileName}：{verdict.Blocker}（机器结论：{verdict.Evidence}）");
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
                    $"{rootTask.FileName}：源包处理档是「留在原地」，链结束后的补做按该档不动源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsContinuationTask)
            {
                // 只处理最外层源包：内层包的"源文件"是其余物里的过程物（与本轮直接搬同一口径）。
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
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
                    $"{rootTask.FileName}：{rootCompleteness.Blocker}" +
                    $"（机器结论：{rootCompleteness.Evidence}），链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.Outcome != TaskOutcome.Succeeded)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：根任务的机器终态不是「完成」（当前：{rootTask.Outcome}），" +
                    $"链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (rootTask.IsVolumeGroup && !rootTask.IsVolumeComplete)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：分卷组不完整" +
                    (string.IsNullOrWhiteSpace(rootTask.VolumeInfoText) ? string.Empty : $"（{rootTask.VolumeInfoText}）") +
                    "，链结束后不动源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string destinationDirectory = ResolveContinuationOutputDirectory(rootTask);

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：算不出根任务的最终输出目录，链结束后不补搬源包。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            string? verificationGap = DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

            if (verificationGap != null)
            {
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：链里的输出校验没有全部通过（{verificationGap}），" +
                    $"链结束后不补搬源包（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            int contentFiles = CountContentFiles(destinationDirectory);

            if (contentFiles == 0)
            {
                // 内容物压根没出现（例如第二层解压失败 / 密码不对）：源包留在原地，其余物不为它生成。
                logEntries.Add((
                    "WARN",
                    $"{rootTask.FileName}：{destinationDirectory} 里没有内容物（只有过程物），" +
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
                    $"{rootTask.FileName}：这一轮的其余物目录没有被记下来，为避免把源包搬到别处，" +
                    $"链结束后不补搬（源包留在原地）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logEntries.Add(("WARN", $"{rootTask.FileName}：链结束后的补搬被取消，源包留在原地（其余物不生成）。"));
                return new DeferredSourceMoveWork(logEntries);
            }

            logEntries.Add((
                "INFO",
                $"{rootTask.FileName}：链结束后的补搬 —— 整条续解链已跑完，{destinationDirectory} 里有 " +
                $"{contentFiles} 个内容物且输出校验通过；本轮没有内容物可定稿、留到现在的源包按设置移入其余物。"));

            SourcePackageMoveResult result = ExecuteSourcePackageMove(
                rootTask, artifactRoot, SourceMoveTrigger.AfterChain, logEntries);

            /*
             * 搬成功后记账已经是 Done（见 ExecuteSourcePackageMove）；搬失败时**保持"待补搬"**，
             * 让下一次运行还有机会补上（这一次的失败已经写进任务状态，不会被静默吞掉）。
             */
            string? failure = result.FailedCount > 0
                ? $"内容物已好，源包未能移入其余物：{result.Message}（源包仍在原处，内容物不受影响）"
                : null;

            if (failure == null)
            {
                /*
                 * ⚠ 链结束后的补搬有一个**顺序陷阱**（2026-09-25 第 32 条实现删除操作时被测试逮住）：
                 * 根任务收尾那一刻它的其余物里还只有过程物，源包是**现在**才搬进去的 ——
                 * 所以"删除操作"那一档必须在这里**再做一次**，否则选了「彻底删除 / 移入回收站」的人
                 * 会发现"过程物被清了，源包却躺在其余物里"（正是他抱怨过的那种不一致）。
                 *
                 * 判据与 RunRestHandlingAsync 完全同一套（RestItemPurger 的五道门槛 + 本批档位）：
                 * 到得了这里就说明"整条链跑完 + 全链校验通过 + 目录里确实有内容物 + 未取消"，
                 * 于是把这一份刚搬好的其余物按档处理掉。
                 */
                await ApplyRestHandlingAfterChainMoveAsync(rootTask, chainTasks, logEntries, cancellationToken);
            }

            return new DeferredSourceMoveWork(logEntries, failure);
        }

        /// <summary>
        /// 链结束后的补搬把源包放进其余物之后，按本批「删除操作」档把那一份处理掉
        /// （不动 / 移入回收站 / 彻底删除）。删不掉只写 WARN，绝不改任务结论。
        /// </summary>
        private async Task ApplyRestHandlingAfterChainMoveAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            List<(string Level, string Message)> logEntries,
            CancellationToken cancellationToken)
        {
            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            /*
             * 与 ApplyRestHandlingAfterChainAsync **同一道判据**（2026-10-04 起都一样）：
             * 只问"这一单自己的根源包那一层"——根源包成功 + 输出校验通过 + 未取消 + 完整性可证
             * ⇒ 按本批「删除操作」档处理其余物；⛔ 不再要求链上每个内层包都成功。
             *
             * 到得了这里就说明上面那条路已经把源包搬进其余物了；而删除动作本身的门槛全在
             * <c>RestItemPurger</c> 里（终态 / 可证完整 / 未取消 / 路径形状 / 半套分卷），
             * 这一处只负责"什么时候试"。
             */
            bool cancelled = cancellationToken.IsCancellationRequested ||
                             IsStopping ||
                             _operationCts?.IsCancellationRequested == true;

            DeleteMode deleteMode = string.Equals(mode, RestHandlingModes.RecycleBin, StringComparison.OrdinalIgnoreCase)
                ? DeleteMode.RecycleBin
                : DeleteMode.Permanent;

            RestPurgeOutcome outcome = await Task.Run(
                () => new RestItemPurger(
                        RestDeleteExecutor,
                        null,
                        ContentKeepRules.FromSettings(Settings))
                    .Purge(rootTask, cancelled, deleteMode),
                CancellationToken.None);

            logEntries.Add((
                outcome.Succeeded ? "INFO" : "WARN",
                $"{rootTask.FileName}：链结束后的其余物处理 —— {outcome.Message}"));
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
                    return $"{candidate.FileName}：{completeness.Message}"
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
            ContentKeepRules? contentKeepRules = null)
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
                bool isProcessArtifact = IsProcessArtifactFile(file)
                    && !IsEngineOutputFile(file, stageRoot, engineOutput)
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
            string outputRoot = engineOutput?.FinalOutputPath ?? string.Empty;

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
                AppendLog("WARN", $"{task.FileName}：分卷组装判定没能做完（{ex.GetType().Name}），按老口径报缺卷。");

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

            AppendLog("INFO", $"{task.FileName}：分卷组装判定 = {resolution.Reason}");

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

                logEntries.Add(("ERROR", $"{task.FileName}：{readFailure}"));

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
                string planFailure = "定稿布局规划失败：" + plan.FailureReason;

                logEntries.Add(("ERROR", $"{task.FileName}：{planFailure}"));

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
                logEntries.Add(("WARN", $"{task.FileName}：定稿提醒 —— {warning}"));
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

                logEntries.Add(("WARN", $"{task.FileName}：{emptyMessage}"));

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

                logEntries.Add(("ERROR", $"{task.FileName}：{createFailure}"));

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

                    logEntries.Add(("WARN", $"{task.FileName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

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

                            logEntries.Add(("WARN", $"{task.FileName}：同名冲突，按你的选择跳过：{target}"));
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
                                $"{task.FileName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                        }
                        else
                        {
                            // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                            string renamedTarget = resolution.TargetPath;

                            if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                            {
                                renamed++;
                            }

                            logEntries.Add(("INFO", $"{task.FileName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

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

            logEntries.Add((failures.Count == 0 ? "INFO" : "WARN", $"{task.FileName}：定稿完成{contentNote} —— {summary}"));

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
                        $"{task.FileName}：本次内容物 —— {string.Join("、", contentNames)}"
                        + (contentMoves.Count > contentNames.Count
                            ? $"（…还有 {contentMoves.Count - contentNames.Count} 项没列出来）"
                            : string.Empty)));
                }
            }

            // 覆盖留痕：被顶掉的落点单独再写一条，用户事后追查"我原来那份去哪了"时有据可依。
            foreach (string overwrittenPath in overwritten)
            {
                logEntries.Add(("WARN", $"{task.FileName}：已覆盖（原文件按两阶段落位删除）：{overwrittenPath}"));
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
             *   —— 上游那道闸门（`RecursionMode != "SingleLayer"`）就把它挡在递归核心之外，到不了这里；
             * · `SingleChain` = 只跟一条链；一层里出现 **≥2 个真归档** ⇒ 第 0 层问用户，
             *   更深的层停在那一层并**如实报停因**（旁边是什么文件都不影响这条判据）；
             * · `AllBranches`（出厂默认）= 内层有多个包也全部解开（仍受每层内层包数量上限约束）。
             *
             * ⚠ 2026-10-01 核查记录（用户问"开了续解和不开续解会不会有 bug"）：下面这条 switch 曾经写成
             * `AllBranches ? AllBranches : SingleChain`，看上去像"把 SingleLayer 也当 SingleChain 跑"，
             * 但**实际不是缺陷** —— 真的闸门在上游（`RecursionMode != "SingleLayer"` 才进递归核心），
             * 撤掉这一处的改动跑守门用例照旧全绿（红检否掉了那个怀疑）。把它写成显式三档只是为了
             * "以后谁动了那道闸门，这里不会静默降级成别的档"。
             */
            RecursionMode mode = Settings.RecursionMode switch
            {
                var value when string.Equals(value, "AllBranches", StringComparison.OrdinalIgnoreCase)
                    => RecursionMode.AllBranches,
                var value when string.Equals(value, "SingleChain", StringComparison.OrdinalIgnoreCase)
                    => RecursionMode.SingleChain,
                _ => RecursionMode.SingleLayer
            };

            // 上限（层数 / 每层密码尝试次数）当场从设置里取，并交给**本次**的递归核心：
            // 构造时固定一份的话，用户改完设置不重启就不生效（见字段上的说明）。
            RecursionLimits limits = BuildRecursionLimits();
            RecursiveExtractor recursiveExtractor = CreateRecursiveExtractor();

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

            AppendLog("INFO", $"{task.FileName}：开始递归解压（模式 {mode}，最大 {limits.MaxDepth} 层）。");

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
                mode,
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
             * ⚠ 顺序与取值都是硬要求：**先让这一步解出来的东西落地，再拿"落地之后的那份路径"交给收片那一档**。
             * 旧顺序（只有接片、没有落地）⇒ 入口包 `111.zip` 跟着工作区一起被删，
             * 批末那一站到了盘上一看：这一组连入口都没有。
             * ⚠ 而且**不能把原路径再喂给接片那一档**：落地是 `File.Move` ⇒ 那个路径已经不存在了
             * （实测踩到：接片那一档一开头 `File.Exists` 就 false，整组静默不接）。
             */
            List<string> publishedPieces = PublishUnresolvedVolumePieces(result, task);
            AdoptUnresolvedVolumePieces(result, task, publishedPieces);

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
                    ? mode != RecursionMode.SingleChain
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
                        mode,
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
                        AppendLog("WARN", $"{task.FileName}：当前这一层没有可用产物，保留原结论。");
                    }
                }
            }

            ApplyRecursionResult(task, result);

            /*
             * 部分完成发布（用户 2026-10-02）：递归这条路上"解了一半的现场"是**最深那一层失败时**
             * 留在它自己产物目录里的东西。登记在这里、真发布在任务收尾（清工作区之前）——
             * 那之前还有链尾的定稿与清理，现场随时可能变。
             */
            RecordRecursivePartialPublishCandidate(task, result);

            return result;
        }

        /// <summary>
        /// 把递归结论翻译成任务状态。
        /// 规则：完成 = 成功；部分完成 = **绝不显示成功**；需要用户决定 = 等用户；
        /// 其余（上限 / 密码 / 损坏 / 取消）都要说清停在哪一层、为什么。
        /// </summary>
        private void ApplyRecursionResult(ArchiveTask task, RecursionResult result)
        {
            foreach (RecursionLayerReport layer in result.Layers)
            {
                AppendLog(
                    layer.Success ? "INFO" : "WARN",
                    $"  ├ 第 {layer.Depth} 层：{Path.GetFileName(layer.ArchivePath)} → {layer.Status}" +
                    (string.IsNullOrWhiteSpace(layer.Message) ? string.Empty : $"，{layer.Message}"));
            }

            task.ErrorMessage = result.Summary;

            /*
             * ===== 状态与**机器终态**必须一次落齐（用户 2026-09-27 真机 `giu.7z.001`）=====
             *
             * 现场：同一个任务三处说三种话 —— ①页「部分完成」、一键汇总「未处理 1」、
             * 批末诊断「下一步：其他」。根子有两条：
             * ① 这里只写了 `Status`（中文），`Outcome` 留在 `Pending` ——
             *    批末汇总读的是机器终态，于是把它算成"没轮到"；`IsHandled` 也跟着判否；
             * ② 密码错误 / 损坏这种"一个文件都没解出来"的停因，过去一律落「部分完成」——
             *    用户会去暂存目录里找根本不存在的产物。
             *
             * 现在：判据走**唯一出口** <see cref="TaskOutcomeClassifier.TryResolveRecursionStop"/>
             * （只读停因枚举 + "有没有产出过东西"这条事实），状态与终态**同一次**写下去。
             */
            bool resolved = TaskOutcomeClassifier.TryResolveRecursionStop(
                result.StopReason,
                result.ProducedAnyLayerOutput,
                out string stoppedStatus,
                out TaskOutcome stoppedOutcome);

            switch (result.StopReason)
            {
                case RecursionStopReason.Completed:
                    task.Status = StatusText.ExtractSuccess;
                    task.ProgressText = StatusText.ProgressCompleted;

                    /*
                     * ⛔ 这里**只在终态还没定的时候**才补 Succeeded —— 这是一道**防回归的闸门**，
                     * 不是当前某条路径的修复（不变量 6：部分成功不得显示为成功）。
                     *
                     * 实测定序（2026-09-30，本用例 `<c>递归完成但源包没搬成_终态不许是成功</c>` 的探针）：
                     * `ApplyRecursionResult` 跑在 `PostProcessSuccessAsync` **之前**，走到这一行时
                     * `Outcome` 一定还是初值 `Pending` —— 也就是说"源包没搬成 ⇒ 部分完成"这个结论
                     * 由 `PostProcessSuccessAsync` 在**后面**写成，当前顺序下两边不会打架。
                     *
                     * 那为什么还要这道闸门：`Succeeded` 是**唯一**允许"删源 / 搬源 / 续解 / 危险模式删其余物"
                     * 的档（不变量 1 的例外、不变量 6）。一旦将来有人把收尾顺序调过来、或在递归成功这一支
                     * 里再插一段写终态的代码，无条件赋值就会把"这件事没做完"悄悄改写成"做完了"，
                     * 而那四道门读的正是这一位 —— 后果是不可逆的。
                     * 判据只读终态这一位（`Pending` = 还没有人下过结论），⛔ 不比对中文文案。
                     */
                    if (task.Outcome == TaskOutcome.Pending)
                    {
                        task.Outcome = TaskOutcome.Succeeded;
                    }

                    break;

                case RecursionStopReason.NeedsDecision:
                    // 多分支默认不展开 / 用户选了"只保留当前这一层"：确实是"做了一半"。
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.Outcome = TaskOutcome.PartiallyCompleted;
                    break;

                case RecursionStopReason.UserCancelled:
                    task.Status = StatusText.Cancelled;
                    task.ProgressText = StatusText.Cancelled;
                    task.Outcome = TaskOutcome.Cancelled;
                    break;

                /*
                 * 源文件已变化（不变量 11）：**状态与原因都已经落好了**（拦下那一刻由
                 * StopIfSourceChanged 一次写全），这里只补一句"产物在哪"就收尾。
                 *
                 * 单独一支的理由：它落到下面 default 那一支会被改写成「部分完成」——
                 * 那是**另一个错误结论**：这个任务一个字节都没解（引擎压根没被调用），
                 * 说成"部分完成"会让用户去暂存目录里找根本不存在的产物；
                 * 而 ErrorMessage 若被 result.Summary 覆盖，用户就看不到**哪一个文件、哪一项变了**
                 * 这条唯一能行动的信息。两者都必须原样保住。
                 */
                case RecursionStopReason.SourceChanged:
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressFailed;
                    task.Outcome = TaskOutcome.Failed;
                    task.ErrorMessage = result.Summary;
                    task.EndTime = DateTime.Now;
                    task.LastUpdatedTime = DateTime.Now;
                    task.ClearProgress();
                    task.UpdateElapsedText();

                    AppendLog("ERROR", $"{task.FileName}：{result.Summary}");
                    return;

                default:
                    if (resolved)
                    {
                        task.Status = stoppedStatus;
                        task.Outcome = stoppedOutcome;
                    }
                    else
                    {
                        // 剩下的那些"停在中途"（层数 / 总量 / 展开比 / 内层包太多 / 分支没展开）：
                        // 产物确实解出来了一部分，落「部分完成」是实话。
                        task.Status = StatusText.PartiallyCompleted;
                        task.Outcome = TaskOutcome.PartiallyCompleted;
                    }

                    task.ProgressText = StatusText.ProgressFailed;
                    break;
            }

            /*
             * 「文件名已加密」那一档还要落**密码状态**（2026-10-05，与单层路径逐字对齐）：
             * 单层路在预检认下这一档时同时写 `PasswordStatus = PasswordNeed`（见那一处），
             * 意思不是"密码错了"，而是"**需要正确密码才能列出内容**" ——
             * 少了这一格，①页「密码」那一列对同一个包在两条路上会说两种话。
             */
            if (task.Status == StatusText.EncryptedHeaders)
            {
                task.PasswordStatus = StatusText.PasswordNeed;
            }

            /*
             * 收尾时间必须落（`TaskOutcome` 那一轮收口读的就是它：`EndTime` 有值 + `Outcome == Pending`
             * 才会被补成 `Failed`）。递归这条路以前不写 `EndTime`，于是任务在界面上一直"还在跑"
             * （耗时那一列会跟着 Now 一直涨），批末的"用时"也取不到这一单。
             */
            task.EndTime ??= DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : task.ElapsedText;

            task.LastUpdatedTime = DateTime.Now;

            AppendLog(result.Completed ? "INFO" : "WARN", $"{task.FileName}：{result.Summary}");

            /*
             * task.OutputPath **不跟着递归结论走**。
             *
             * 它必须始终指向**最终目录**（一键处理的续解、界面"输出目录"列、清理源包都读它），
             * 而现在递归的产物先落在**暂存目录**里（调用方传的就是 stage），要等定稿那一步才搬出去。
             * 旧写法 `task.OutputPath = result.FinalOutputPath` 在这里会把任务指向暂存区：
             * 用户点"打开输出目录"会看到工作区里的过程物，续解也会去暂存区里找内层包。
             */
            /*
             * ===== 这一趟**借来用过的用户源片**记账（2026-10-05 真机第八批）=====
             *
             * 跨盘 ZIP 的末片压在包里、其余几片散在用户目录里时，递归层是用**硬链接**把那几片接过来
             * 凑齐再解的 ⇒ 那几片自己那一单在批末不该再报「分卷缺失」（内容已经出来了），
             * 按跟班卷收场（`TryResolveConsumedByAnotherTask` 读的就是这份账）。
             * ⛔ 只在**这一趟真的成功**时记：没解出来就不算"已由谁解出"。
             */
            if (result.Completed && result.ConsumedVolumeSources.Count > 0)
            {
                foreach (string consumed in result.ConsumedVolumeSources)
                {
                    if (!string.IsNullOrWhiteSpace(consumed))
                    {
                        _consumedVolumeSources[consumed] = task.FileName;
                    }
                }
            }

            if (!result.Completed)
            {
                // 没走完（上限 / 密码 / 需要决定）：产物留在暂存区，如实说清它在哪。
                AppendLog(
                    "WARN",
                    $"{task.FileName}：这次没有走完，产物仍在暂存目录，未搬进输出目录：{result.FinalOutputPath}");

                /*
                 * 「下一步该怎么办」必须写出来（用户 2026-09-27 真机：018.7z 的 .rar 里是 **37 个内层包**，
                 * 递归按"多分支默认不展开"停在第 3 层 → 这一单落「部分完成」、产物没搬出、工作区按默认清掉，
                 * 而日志里只有"停在第 3 层"这种**技术结论**，没有一个字告诉他该怎么办）。
                 *
                 * ⚠ 2026-10-04（真机）：这一句过去**只给 `NeedsDecision`** —— 而一键处理档不弹确认框，
                 * 多分支那一档的停因是 **`BranchNotExpanded`**（真机 `第6集.7z`：两层已成功、停在第 3 层、
                 * 产物被清掉），于是用户一条出路都看不到。两档给同一份「两条出路」。
                 *
                 * ⚠ 措辞按**真实数量**说（同一次真机：日志写着"多个"，实际只有 1 个）：
                 * 1 个那一档的真实原因是"它旁边还有别的文件"。
                 *
                 * 两条出路都写清（用户拍板："或在汇总里明确说「这单要去②页开『展开所有分支』」"）：
                 * ① 要一次解到底 → ②页 →「嵌套与压缩包」改成「展开所有分支」再重跑这一单；
                 * ② 只想先把已经解出来的东西留下来 → ③页打开「失败时保留中间产物」，产物就留在上面那个暂存目录里。
                 */
                if (result.StopReason is RecursionStopReason.NeedsDecision or RecursionStopReason.BranchNotExpanded)
                {
                    int unexpanded = result.UnexpandedNames.Count;

                    AppendLog(
                        "WARN",
                        (unexpanded > 1
                            ? string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchAdviceMultipleFormat,
                                task.FileName,
                                unexpanded)
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchAdviceSingleFormat,
                                task.FileName))
                        + StatusText.RecursionBranchAdviceTail);
                }
            }
        }

        /// <summary>
        /// 把目录内容（保持子目录结构）移动到目标目录，同名自动改名**绝不覆盖**，返回移动的文件数。
        /// 只在"用户选择只保留当前一层"这条路径上用。
        /// </summary>
        private static int MoveDirectoryContent(string sourceDirectory, string targetDirectory)
        {
            int moved = 0;

            SafePathHelper.EnsureDirectoryExists(targetDirectory);

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string relative = Path.GetRelativePath(sourceDirectory, file);
                    string destination = Path.Combine(targetDirectory, relative);

                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(destination) ?? targetDirectory);

                    if (File.Exists(destination))
                    {
                        destination = SafePathHelper.AutoRenameFilePath(destination);
                    }

                    File.Move(file, destination);
                    moved++;
                }
                catch
                {
                    // 单个文件搬不动不该让整件事失败：日志里已经能看出解压本身是成功的。
                }
            }

            return moved;
        }

        /// <summary>
        /// 目录里（含各级子目录）**一个文件都没有**时返回 true；目录不存在或读不了返回 false。
        ///
        /// 只给"用户选择只保留当前这一层"那条路用：它是"现在能不能安全清掉这份工作区"的判据 ——
        /// 读不了就按"还有东西"处理，宁可不清理，也不能把没搬走的产物删掉。
        /// </summary>
        private static bool HasNoFiles(string? directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                       Directory.Exists(directory) &&
                       Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在 **UI 线程** 上弹确认框。
        ///
        /// 为什么必须显式调度（这是实测出来的卡死原因）：
        /// 引擎调用与抠出分别用了 ConfigureAwait(false) 和 Task.Run，管线拿到递归结论时
        /// **已经不在 UI 线程上**。此时直接 MessageBox.Show 会创建出一个没人泵消息的窗口 ——
        /// 用户看到的现象是"点了按钮之后什么都动不了、没有弹窗、连 X 都点不掉"，
        /// 而进程既不占 CPU、也没有子进程、磁盘也没在忙，看着像死锁。
        /// 命令行直接调 RecursiveExtractor 会秒回 NeedsDecision，卡的就是这一步弹窗。
        /// </summary>
        private Task<bool> ShowConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上弹警告（**异步**，不阻塞调用线程）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套做法，取代原先任务循环里的
        /// 同步 <c>Application.Current.Dispatcher.Invoke</c>：
        /// ① 同步 Invoke 会让调用线程一直等模态框关掉（将来从后台线程调用时就是死等）；
        /// ② <c>Application.Current</c> 为 null 时（单元测试 / 无界面宿主）它会直接 NRE。
        /// </summary>
        private Task ShowWarningOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 没有 WPF 应用：只留日志（调用方已经写过），绝不在这里弹模态框 —— 那会阻塞调用方且没人点得掉。
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                _dialogService.ShowWarning(message);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowWarning(message)).Task;
        }

        // ================================================================ 进度可见（D5）/ 长时间无响应

        /// <summary>
        /// 把一段动作投递到 UI 线程执行（**异步、不阻塞、无界面宿主也不抛**）。
        ///
        /// <para>
        /// 三条纪律（每一条都对应一次踩过的坑）：
        /// </para>
        /// <list type="number">
        /// <item><description>用 <c>BeginInvoke</c> 而不是同步 <c>Dispatcher.Invoke</c>：
        /// 进度回调是从**读进程输出那条循环**里发出来的，同步 Invoke 会让"读输出"等"UI 处理完"，
        /// 一旦 UI 线程正忙（正是本项目卡死过的那个场景）就会互相拖住。</description></item>
        /// <item><description><c>Application.Current</c> 为 null（单元测试 / 控制台宿主）时就地执行：
        /// 既不抛异常也不死等，而且测试仍然能观察到结果。</description></item>
        /// <item><description>动作本身抛异常一律吞掉：进度显示出问题绝不允许影响解压结论。</description></item>
        /// </list>
        /// </summary>
        private static void PostToUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                TryRunSafely(action);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch
            {
                // 调度器正在关闭（关窗口时）：丢弃这一次进度，绝不影响引擎调用。
            }
        }

        private static void TryRunSafely(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // 界面侧的进度显示失败与解压结论无关。
            }
        }

        /// <summary>
        /// 跨 10% 档位的进度日志行数上限（10 个档位 = 最多 10 行）。
        ///
        /// 为什么必须限：引擎的进度是**每 250ms 一条**，一次 10 分钟的解压有 2400 条 ——
        /// 每条都写日志会把日志彻底淹掉，用户反而看不到真正重要的那几行（密码、失败原因）。
        /// </summary>
        private const int ProgressLogDecileStep = 10;

        /// <summary>进度日志里当前条目名最多留几个字符（再长就截断，免得一行日志比屏幕还宽）。</summary>
        private const int ProgressLogEntryMaxLength = 60;

        /// <summary>
        /// 一个任务的进度接收端。
        ///
        /// <para>
        /// 它接的是引擎层**节流之后**的进度（最多 250ms 一次，见 <c>ArchiveProgressReporter</c>），
        /// 所以这里不需要再去重；它只负责把结论落到任务上、按档位写日志。
        /// </para>
        ///
        /// <para>
        /// 线程：<see cref="Report"/> 可能来自读管道的线程，实际落地一律经
        /// <see cref="PostToUiThread"/> 回到 UI 线程（无界面宿主时就地执行）。
        /// </para>
        /// </summary>
        private sealed class TaskProgressSink : IProgress<ArchiveProgress>
        {
            private readonly ExtractionCoordinator _owner;
            private int _lastLoggedDecile = -1;

            /// <summary>上一次"心跳"写出去的时刻（长任务专用，见 <see cref="HeartbeatInterval"/>）。</summary>
            private DateTime _lastHeartbeatUtc = DateTime.UtcNow;

            public TaskProgressSink(ExtractionCoordinator owner, ArchiveTask task)
            {
                _owner = owner;
                Task = task;
            }

            public ArchiveTask Task { get; }

            public void Report(ArchiveProgress? value)
            {
                if (value == null)
                {
                    return;
                }

                PostToUiThread(() => Apply(value));
            }

            private void Apply(ArchiveProgress progress)
            {
                /*
                 * 任务已经收尾时，迟到的进度一律不落到界面上。
                 *
                 * 引擎层已经把"进程退出后不再回调"做到了（reporter.Complete()），这里是第二道：
                 * 批量解压里上一个任务的最后一帧进度完全可能在它标成"解压成功"之后才排到 UI 队列，
                 * 那一帧如果照写，用户就会看到"解压成功 87%"（不变量 6：收尾后不许再显示进度）。
                 */
                if (Task.EndTime != null)
                {
                    return;
                }

                Task.ApplyProgress(progress.Percent, progress.CurrentEntry);

                // 详情窗口的「耗时」也跟着走：不然任务在跑的时候那一格一直停在 00:00:00。
                Task.UpdateElapsedText();

                if (progress.Percent < 0)
                {
                    // "只知道在动"（扫描阶段）：不写档位日志，免得开头连写好几行没信息量的东西。
                    return;
                }

                int decile = progress.Percent / ProgressLogDecileStep;

                /*
                 * 进度**回退**（或从"未知"回到具体值）＝ 新的一轮：换一个密码候选重试、或者递归解到了下一层。
                 * 档位计数必须跟着重来，否则后面每一轮都因为"decile 没超过上一轮"而一行日志都写不出来
                 * （实测形态：递归第 2 层开始日志里再也没有进度行，看起来像卡住了）。
                 */
                if (decile < _lastLoggedDecile)
                {
                    _lastLoggedDecile = -1;
                }

                if (decile <= _lastLoggedDecile)
                {
                    /*
                     * ── 长任务心跳（第 44 条的补充，用户"如果失败的话你就可以多一点"的另一面）──
                     *
                     * 瘦身之后，一个 40 GB 的包在日志里就是"开始解压"……然后**半小时没动静**，
                     * 直到收尾摘要才再出现一行 —— 用户会以为卡死了（他以前就报过"看着像卡住"）。
                     * 所以：**每 30 秒**至少放一条进度出去（含百分比、当前条目、已用时间），
                     * 明确标成"仍在解压"，它**绕过"成功就丢"的缓冲**（心跳的意义就是"现在还在动"）。
                     */
                    if (DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval)
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;

                        string heartbeatEntry = DescribeEntry(progress.CurrentEntry);

                        _owner.AppendHeartbeat(
                            string.IsNullOrWhiteSpace(heartbeatEntry)
                                ? $"{Task.FileName}：仍在解压 {progress.Percent}%"
                                : $"{Task.FileName}：仍在解压 {progress.Percent}%（当前：{heartbeatEntry}）");
                    }

                    return;
                }

                _lastLoggedDecile = decile;
                _lastHeartbeatUtc = DateTime.UtcNow;

                _owner.AppendLog(
                    "INFO",
                    string.IsNullOrWhiteSpace(DescribeEntry(progress.CurrentEntry))
                        ? $"{Task.FileName}：进度 {progress.Percent}%"
                        : $"{Task.FileName}：进度 {progress.Percent}%（当前：{DescribeEntry(progress.CurrentEntry)}）");
            }

            /// <summary>当前条目名（超长截断）—— 进度行与心跳共用同一份口径。</summary>
            private static string DescribeEntry(string? currentEntry)
            {
                string entry = currentEntry ?? string.Empty;

                return entry.Length > ProgressLogEntryMaxLength
                    ? entry[..ProgressLogEntryMaxLength] + "…"
                    : entry;
            }
        }

        /// <summary>长任务心跳间隔（第 44 条：瘦身不许把"还在动"一起瘦掉）。</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 心跳：**绕开"成功就丢"缓冲**直接写出去 —— 它要回答的正是"此刻还在动吗"。
        /// 任务收尾时的摘要仍然照写，两者不冲突。
        /// </summary>
        private void AppendHeartbeat(string message) => _vm.AppendLog("INFO", message);

        /// <summary>
        /// 批末一条**汇总**（第 44 条的补充）：任务数 / 成功 / 失败 / 跳过 / 用时 + 失败逐条。
        ///
        /// <para>为什么要它：瘦身之后每个任务只剩两行，但 136 个任务仍是 270 多行 ——
        /// 用户真正想知道的是"这批到底成不成、哪几个不成"，那应该是**一眼**能看到的结论，
        /// 而不是让他自己数行。失败逐条写"名字 + 状态"（最多 20 条，多的写个数）。
        /// ⛔ 判据全用机器终态（<see cref="TaskOutcome"/>），不比对中文。</para>
        /// </summary>
        private void AppendBatchSummary(IReadOnlyList<ArchiveTask>? tasks)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            /*
             * 数法只有一份（`Models/BatchOutcomeTally`）—— 这一行、一键汇总那一行、
             * 以及导出的日志头部读的都是它，⛔ 不许在这里再写一遍 `Count(task => …)`。
             *
             * 现场（用户 2026-10-02 真机日志）：这两行各写了一遍数法，于是同一份日志里
             * ① 同一个"跳过"一处算跟班卷、一处不算（`CountsTowardBatchOutcome == false` 的跟班卷
             *    不是"没做成"）；②「终态说成功、校验却判否」那一帧在一处算成功、另一处算失败。
             *    ⇒ 两个"失败的数"，用户拿哪一行去对数都对不上。
             *
             * 跟班卷单独一句、仍占总数一个名额，所以"未处理"那一档要把它们减掉 ——
             * 恒等式「各分项之和 + 未处理 = 本次任务数」由 tally 内部兜底，两处都不许破。
             */
            BatchOutcomeTally tally = BatchOutcomeTally.Count(tasks);

            DateTime? first = tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
            DateTime? last = tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);
            string elapsed = first.HasValue && last.HasValue && last.Value > first.Value
                ? (last.Value - first.Value).ToString(@"hh\:mm\:ss")
                : string.Empty;

            var parts = new List<string>(tally.BuildParts());

            if (!string.IsNullOrWhiteSpace(elapsed))
            {
                parts.Add("用时 " + elapsed);
            }

            AppendLog(
                tally.Failed > 0 ? "WARN" : "INFO",
                $"本批汇总：{tally.Total} 个任务 —— {string.Join(" / ", parts)}。"
                + (tally.FollowerSkipped > 0 ? " " + tally.DescribeFollowerNote() : string.Empty));

            /*
             * 批末那条**红字**（用户 2026-09-29 要求）：把"可能是没有密码 / 密码不对"的那些单独点出来，
             * 好让他一眼知道"该去补密码"而不是去怀疑文件坏了。
             *
             * ⚠ 只按**机器判定的密码类终态**点名，并且必须写"可能" —— 同一个包也可能是缺卷 / 损坏，
             * 引擎给的结论并不专一（用户原话："可能由于，因为有的时候出错不仅仅是在这里"）。
             */
            List<ArchiveTask> passwordSuspects = tasks
                .Where(task => task.Status is
                    StatusText.WrongPassword or
                    StatusText.PasswordAttemptLimitReached or
                    StatusText.EncryptedHeaders)
                .ToList();

            if (passwordSuspects.Count > 0)
            {
                AppendLog(
                    "ERROR",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchPasswordSuspectsLogFormat,
                        passwordSuspects.Count,
                        string.Join("、", passwordSuspects.Take(MaxBatchSummaryFailures).Select(task => task.FileName))));
            }

            if (tally.Failed == 0)
            {
                return;
            }

            /*
             * 逐条列的判据与上面那个数字**同一个出口**（`BatchOutcomeTally.IsCountedAsFailure`）——
             * 否则"终态说成功、校验却判否"那一帧会被算进失败数、却不在失败清单里，
             * 用户数一遍名字发现少一个。
             */
            foreach (ArchiveTask task in tasks.Where(BatchOutcomeTally.IsCountedAsFailure).Take(MaxBatchSummaryFailures))
            {
                AppendLog("WARN", $"  失败：{task.FileName} —— {task.Status}");
            }

            if (tally.Failed > MaxBatchSummaryFailures)
            {
                AppendLog("WARN", $"  …还有 {tally.Failed - MaxBatchSummaryFailures} 个失败没列出来（完整清单见「导出失败清单」）");
            }
        }

        /// <summary>批末汇总里最多逐条列几个失败（再多让他去导出失败清单）。</summary>
        private const int MaxBatchSummaryFailures = 20;

        /// <summary>
        /// 造一个带进度 / 卡住提示的引擎请求（本次改动的接线点）。
        ///
        /// 阈值用引擎层的默认值（90 秒）：它**不是**设置项 —— 这一条只提示、不阻断，
        /// 不需要用户去调；用户真正要做的动作是「取消当前」，界面上已经有了。
        /// </summary>
        private ArchiveRequest BuildTrackedRequest(
            string archivePath,
            string password,
            string? outputPath,
            TaskProgressSink progressSink)
        {
            return new ArchiveRequest
            {
                ArchivePath = archivePath,
                Password = password,
                OutputPath = outputPath,
                Progress = progressSink,
                Stalled = notice => OnEngineStalled(progressSink.Task, notice),
                StallThreshold = EngineOutputActivityMonitor.DefaultStallThreshold
            };
        }

        /// <summary>
        /// 引擎超过阈值没有任何输出时的处理：**写一条 WARN + 在任务上挂提示，绝不杀进程**。
        ///
        /// <para>
        /// 为什么只提示不处理：用户点「取消当前」才是唯一的中止语义（不变量 9）。
        /// 自动杀进程会把"慢"误判成"死"，而真实场景里几十秒不输出（固实块、慢盘、杀毒软件扫描）
        /// 完全正常 —— 那正是这个功能存在的理由，不能反过来用它去打断正常任务。
        /// </para>
        /// </summary>
        private void OnEngineStalled(ArchiveTask task, ArchiveStallNotice notice)
        {
            if (notice == null)
            {
                return;
            }

            PostToUiThread(() =>
            {
                if (task.EndTime != null)
                {
                    // 任务已经收尾，这条提示已经过时（迟到的看门狗巡查）。
                    return;
                }

                int idleSeconds = (int)Math.Round(notice.Idle.TotalSeconds);
                int thresholdSeconds = (int)Math.Round(notice.Threshold.TotalSeconds);

                task.ResponsivenessHint =
                    $"{StatusText.LongTimeNoResponse}：已 {idleSeconds} 秒没有任何引擎输出，" +
                    "进程仍在运行；需要中止请点「取消当前」。";

                // WARN 只写一次（引擎层对同一段沉默只报一次），所以不会刷屏。
                AppendLog(
                    "WARN",
                    $"{task.FileName}：{StatusText.LongTimeNoResponse} —— 已 {idleSeconds} 秒没有任何引擎输出" +
                    $"（阈值 {thresholdSeconds} 秒），进程仍在运行。程序不会自动结束它；需要中止请点「取消当前」。");
            });
        }

        // ================================================================ 同名冲突（ConflictAction = Ask）

        /// <summary>同名冲突处理档（唯一来源；空 / 非法回落 AutoRename，见 <see cref="ConflictActions"/>）。</summary>
        private string ConflictActionValue => ConflictActions.Normalize(Settings.ConflictAction);

        /// <summary>预检里最多列几条冲突给用户看（再多就让他去看日志，别弹一个占满屏幕的框）。</summary>
        private const int MaxConflictSamplesInDialog = 5;

        /// <summary>定稿前的冲突预检结论（Ask 档要在动最终目录之前先问完）。</summary>
        private sealed class ConflictPrecheck
        {
            /// <summary>计划里"落点已经被占着"的条目总数。</summary>
            public int TotalCount { get; set; }

            /// <summary>其中属于**内容物**的条数（其余物撞名不算用户的内容被顶掉）。</summary>
            public int ContentCount { get; set; }

            /// <summary>给用户看的样例（最多 <see cref="MaxConflictSamplesInDialog"/> 条）。</summary>
            public List<(string Path, ConflictTargetKind Kind)> Samples { get; } = new();
        }

        /// <summary>本批开始时把冲突记账清零（上一批的"全部 X"绝不许影响这一批）。</summary>
        private void ResetBatchConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _batchConflictDecision = null;
                _taskConflictDecision = null;
                _conflictPromptUnavailable = false;
                _conflictPromptCount = 0;
            }
        }

        /// <summary>每个任务开始时清掉"这一个"的记账（批量档位不受影响）。</summary>
        private void ResetTaskConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _taskConflictDecision = null;
            }
        }

        /// <summary>本批 / 本任务已经定下的答案（"全部 X"优先于"这一个"）；没有就是 null。</summary>
        private ConflictDecision? GetEffectiveConflictDecision()
        {
            lock (_conflictDecisionLock)
            {
                return _batchConflictDecision ?? _taskConflictDecision;
            }
        }

        private bool IsConflictPromptUnavailable
        {
            get
            {
                lock (_conflictDecisionLock)
                {
                    return _conflictPromptUnavailable;
                }
            }
        }

        /// <summary>
        /// 问一次同名冲突并记账（**本批最多真正弹一次**）。
        ///
        /// <para>
        /// 返回值 <c>null</c> = 没问到答案（无界面宿主 / 等待超时）：调用方按保守档处理，**绝不覆盖**。
        /// 选择「取消本批」时本方法直接抛出取消，由上层把任务落成「已取消」（不变量 6）。
        /// </para>
        /// <para>
        /// 只允许在 UI 线程的上下文里调用（管线的那条 <c>await</c> 续体就在 UI 上下文）：
        /// 弹框本身走 <see cref="DialogService.ShowConflictDecisionAsync"/>，它自己负责
        /// "无界面宿主不弹窗、不阻塞"以及"后台线程用 InvokeAsync 而不是同步 Invoke"。
        /// </para>
        /// </summary>
        private async Task<ConflictDecision?> AskConflictAsync(
            ArchiveTask task,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            lock (_conflictDecisionLock)
            {
                _conflictPromptCount++;
            }

            ConflictDecision? decision = await _dialogService.ShowConflictDecisionAsync(
                new DialogService.ConflictPrompt
                {
                    Message = message,
                    Subtitle = "同名冲突在本批只会问你这一次；勾上「对后面所有同名冲突都照此办理」就不会再打断你。",
                    Detail = detail,
                    Destructive = true
                },
                cancellationToken);

            /*
             * 询问期间被取消（用户点了「取消当前」）：不覆盖、不落位，把取消照原样抛上去，
             * 由 ProcessExtractTaskAsync 落成「已取消」—— 询问期间点了取消却报成功是最不能接受的。
             */
            cancellationToken.ThrowIfCancellationRequested();

            if (decision == null)
            {
                /*
                 * 问不到答案：**绝不代用户拍板成「覆盖」**（不变量 3：默认不得覆盖）。
                 * 记下"问不了"，后面的冲突不再尝试弹窗（否则一次批量会刷满降级日志）。
                 */
                lock (_conflictDecisionLock)
                {
                    _conflictPromptUnavailable = true;
                }

                AppendLog(
                    "WARN",
                    $"{task.FileName}：同名冲突问不到答案（当前宿主没有界面或等待超时），已按保守档继续 —— " +
                    "同名条目自动重命名落位，已有文件一个字节都不动。");

                return null;
            }

            ConflictDecision answered = decision.Value;

            if (answered.IsCancel)
            {
                // 「取消本批」= 既有的两套取消语义一起用：停止后续（不再启动新任务）+ 取消当前（正在跑的这个）。
                AppendLog("WARN", $"{task.FileName}：你在同名冲突询问里选择了「取消本批」，停止后续并取消当前任务。");

                StopAfterCurrent();
                CancelCurrentTask();

                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }

            lock (_conflictDecisionLock)
            {
                if (answered.ApplyToAll)
                {
                    _batchConflictDecision = answered;
                }
                else
                {
                    _taskConflictDecision = answered;
                }
            }

            AppendLog("INFO", $"{task.FileName}：同名冲突 —— 你的选择：{answered.Describe()}");

            if (answered.ApplyToAll)
            {
                AppendLog("INFO", "本批后续的同名冲突一律照此办理，不再询问。");
            }

            return answered;
        }

        /// <summary>
        /// 解析一次同名冲突：按档位（或用户的答案）给出"怎么办 + 落到哪"。
        ///
        /// Ask 档在这里才真的去问 —— 第一次遇到"目标已存在"时聚合问一次；
        /// 用户选了"全部 X"之后本批不再问，选了"这一个"则本任务内不再问（一次询问管一个任务）。
        /// 非 Ask 档一个字都不问，直接由档位推出结论（默认 AutoRename：绝不覆盖）。
        /// 落点的算法只有一处实现（<see cref="PathService.ResolveConflict"/>）。
        /// </summary>
        private async Task<ConflictResolution> ResolveConflictAsync(
            ArchiveTask task,
            ConflictTargetKind kind,
            string targetPath,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            string action = ConflictActionValue;
            ConflictDecision? decision = GetEffectiveConflictDecision();

            if (ConflictActions.IsAsk(action) && decision == null && !IsConflictPromptUnavailable)
            {
                decision = await AskConflictAsync(task, message, detail, cancellationToken);
            }

            if (ConflictActions.IsAsk(action))
            {
                // decision 仍可能为 null —— 那就是"问不到"，走保守档（自动重命名，绝不覆盖）。
                return _pathService.ResolveConflict(
                    targetPath,
                    kind,
                    action,
                    decision ?? ConflictDecision.Conservative);
            }

            return _pathService.ResolveConflict(targetPath, kind, action);
        }

        /// <summary>把预检结论拼成给用户看的冲突清单（等宽小字那一栏）。</summary>
        private static string BuildConflictDetail(ConflictPrecheck precheck)
        {
            if (precheck.Samples.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach ((string path, ConflictTargetKind kind) in precheck.Samples)
            {
                builder.Append(kind == ConflictTargetKind.Directory ? "［目录］" : "［文件］");
                builder.AppendLine(path);
            }

            if (precheck.TotalCount > precheck.Samples.Count)
            {
                builder.Append($"… 另有 {precheck.TotalCount - precheck.Samples.Count} 项同名（完整清单见日志）");
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 定稿冲突预检：**只读**地算出"计划里哪些落点已经被占着"。
        ///
        /// 为什么要先算一遍：Ask 档必须**在动最终目录之前**拿到答案（也就是"暂停该任务"），
        /// 而落位那一刻已经在后台搬运的中途，边搬边问会让最终目录进退两难
        /// （一部分已经落位、剩下的在等一个还没出现的答案）。
        /// 本方法只读目录、只判存在，一个字节都不写；**只允许在后台线程上跑**。
        /// </summary>
        /// <param name="recursion">
        /// 这一次递归的结果（与真正的定稿读**同一个**对象）：落点最少两层那条红线的判据
        /// （"最后一个压缩包那一层"）由它算出来 —— 预检按平铺算、定稿按套层算就会得到
        /// "预检说有冲突、实际没有"这种自相矛盾的结论。
        /// </param>
        private ConflictPrecheck PrecheckFinalLayoutConflicts(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var precheck = new ConflictPrecheck();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return precheck;
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(task, stageDirectory, destinationDirectory, sharedOutputRoot, terminalLayout, specialExtraction, recursion);
            }
            catch
            {
                // 预检失败不改变任何结论：真正的失败会在定稿那一步如实报出来（这里只当"没有冲突"）。
                return precheck;
            }

            if (plan.Failed || plan.Moves.Count == 0)
            {
                return precheck;
            }

            foreach (PlannedMove move in plan.Moves)
            {
                if (!File.Exists(move.To) && !Directory.Exists(move.To))
                {
                    continue;
                }

                ConflictTargetKind kind = Directory.Exists(move.To) && !File.Exists(move.To)
                    ? ConflictTargetKind.Directory
                    : ConflictTargetKind.File;

                precheck.TotalCount++;

                if (!plan.ProcessArtifactSources.Contains(move.From))
                {
                    precheck.ContentCount++;
                }

                if (precheck.Samples.Count < MaxConflictSamplesInDialog)
                {
                    precheck.Samples.Add((move.To, kind));
                }
            }

            return precheck;
        }

        /// <summary>
        /// 续解任务的落点路径里**是不是已经带着"以本层包名命名的那一层"**。
        ///
        /// <para>它是"要不要再套一层"的判据，只读路径本身这一个事实（⛔ 不猜、不看文案）：
        /// 忠实档 / 分支退化档下父任务落点已经追加过本层的包基名 → true（不再套，否则 <c>666\666</c>）；
        /// 简洁档的单链下层落点还停在父任务那一层 → false（末层那一层就该由定稿套出来）。</para>
        ///
        /// <para>单独提出来是因为它是**唯一判据**、而且要被测试直接钉住 —— 埋在定稿流程里
        /// 只能靠整条管线跑真 7z 才验得到。</para>
        /// </summary>
        internal static bool IsContinuationLayerAlreadyInPath(string? parentDirectory, string? archiveBaseName)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(archiveBaseName))
            {
                return false;
            }

            string last = Path.GetFileName(
                parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.Equals(last, FileNameHelper.SanitizeFileName(archiveBaseName), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 定稿布局规划的统一入口：算"终端归档基名"这件事只在这里做一次，
        /// 预检与真正的定稿走**同一份输入**（否则预检说有冲突、定稿却按另一套计划落位）。
        ///
        /// <para>
        /// ⚠ 它刻意**不是 static**：本批是不是「解压到当前文件夹」只读
        /// <c>_extractIntoSourceFolderThisRun</c> 这一个字段 —— 让两处调用各传一个参数，
        /// 迟早会漂移成"预检按平铺算、定稿按套层算"（预检说有冲突、实际没有，或者反过来）。
        /// </para>
        /// </summary>
        private FinalLayoutPlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            string archiveBaseName = OutputPlacement.ResolveArchiveBaseName(task.CurrentPath);

            /*
             * ⛔ 落点最少两层（用户 2026-09-30 真机红线）：**最里层 = 最后一个压缩包那一层**。
             *
             * 它的名字来源只有一处（`PackageLayerRules.ResolveBaseName`，读的就是这一次递归的结果）：
             * 递归展开了内层包时非空 ⇒ 定稿那一侧任何分支都不许把最里层吃掉
             * （真机现场 `26081118.7z` → 内层包解出 `T 小小绘 推特大合集 330P+454V-9.31G\P|V`，
             * 定稿却只留 `P`、`V` 直接躺在 `…\26081118\` 下）。
             * 只解了一层时是空串 ⇒ 一路照旧，与加这条红线之前逐字相同。
             *
             * ⚠ 它与 `suppressPackageFolderLayer` 是**两件事**：后者只免掉"包名那一层"，
             * 最里层由这里另外钉住 —— 预检与真正的定稿走的是**同一个**方法，不会各判一套。
             */
            string innermostPackageBaseName = PackageLayerRules.ResolveBaseName(recursion?.Layers);

            /*
             * "要不要套包名那一层"只有这一个出口（`suppressPackageFolderLayer`），三种成因在这里合并：
             *   ① 手动档「解压到当前文件夹」：落点就是源包所在那一层，按包内原样解开；
             *   ② 续解的这一层已经在落点路径里（忠实档 / 分支退化档）：再套就是 `666\666`；
             *   ③ 这一层本身就是分卷组的一卷（过程物名不成层）。
             * 合成一个布尔往下传 —— 定稿那一侧只认一个判据，不会再出现"落点摊平了、定稿又套一层"。
             *
             * ⚠ ②这条判据**不能简化成"是不是续解任务"**：简洁档下续解的**末层**恰恰需要那一层
             * （`111\222\666\内容物` 里的 `666`），而"自己那一层在不在落点路径里"是**路径本身**
             * 说得清的事实 —— 不需要预知"这是不是最后一层"。
             */
            string parentDirectory = task.ParentOutputDirectory;
            bool continuation = !string.IsNullOrWhiteSpace(parentDirectory);

            bool layerAlreadyInPath = continuation
                && IsContinuationLayerAlreadyInPath(parentDirectory, archiveBaseName);

            /*
             * 过程物名不成层（用户 2026-09-27 红线）：续解的这一层如果**本身就是分卷组的一卷**
             * （`59768866.001`），它的包基名是过程物的名字、不是用户认得的包名 ——
             * 拿它当一层就是真机上那个 `…\包名\59768866\真内容`。
             */
            bool processArtifactName = continuation
                && FileNameHelper.IsVolumePartFileName(FileNameHelper.GetFileName(task.CurrentPath));

            return PlanFinalLayout(
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                archiveBaseName,
                terminalLayout,
                specialExtraction,
                suppressPackageFolderLayer: _extractIntoSourceFolderThisRun || layerAlreadyInPath || processArtifactName,
                innermostPackageBaseName: innermostPackageBaseName,
                /*
                 * ⛔ 引擎自己写出来的东西**不是过程物**（用户 2026-09-27 真机 222 那一单）。
                 *
                 * 现场：外层跨盘 zip 解出来的两个条目叫 `222.zip` 与 `222.zi删除p`（名字里嵌了中文），
                 * 它们**本身就是一份完整的两卷 split zip**。老判据只看后缀像不像归档 ⇒ `222.zip`
                 * 被当成"待续解的过程物"，而旁边那一卷名字不标准 ⇒ 整层定稿作废，
                 * 用户的 678 MB 产物一个字节都没落地。判据见
                 * <see cref="IsEngineOutputFile"/>：这一单引擎这一趟写出来的，就不是过程物。
                 */
                engineOutput: recursion,
                /*
                 * ⛔ 「内容物保留关键词」的那一份判据（用户 2026-10-04 的新功能「内容物压缩文件不解压」）：
                 * 命中关键词的内容文件**不算过程物** ⇒ 它跟着内容物一起定稿落盘、一个字节都不动
                 * （不进其余物、也不参与后面的删除）。判据唯一出口仍是 <see cref="ContentKeepRules"/>。
                 */
                contentKeepRules: ContentKeepRules.FromSettings(Settings));
        }

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗）。</summary>
        private void RecordPasswordFailure(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 三档（2026-10-01）：`PasswordOrCorrupted`（RAR 那句"文件已损坏或密码错误"）也算**密码类失败**。
             *
             * 为什么必须算：那一档恰恰是"最可能缺密码"的一档 —— 用户真机那批 RAR `-p` 包走的全是它，
             * 而原来它既不被登记、批末那条提示（"下一步去密码页导入 / 在确认框里填"）也就**一次都不出现**，
             * 用户只看到一行红字，不知道下一步该干什么。
             *
             * ⛔ 但**不许**因此把它说成确认的密码问题：引擎在那条路上本来就没法区分"密码错"与"数据坏"
             *（§11.4 的 RAR 两义句），批末文案里必须带着"可能"。
             */
            if (task.Status != StatusText.WrongPassword &&
                task.Status != StatusText.PasswordAttemptLimitReached &&
                task.Status != StatusText.PasswordOrCorrupted)
            {
                return;
            }

            // 本批出现过"密码类失败"这个事实：批次结束后据此在合并提示里带上"手动输入密码"的出口。
            _batchHadPasswordFailures = true;

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            lock (_passwordFailuresLock)
            {
                _passwordFailures.Add((fileName, task.Status));
            }
        }

        private void ClearPasswordFailures()
        {
            lock (_passwordFailuresLock)
            {
                _passwordFailures.Clear();
            }
        }

        // ================================================================ 缺卷补救（手动指定缺失卷所在目录）

        /// <summary>本批已经为哪几个任务问过"缺失卷在哪"（同一个包重试时不再重复打扰）。</summary>
        private readonly HashSet<ArchiveTask> _volumeRepairAsked = new();

        // ================================================================
        // 「缺卷不许在批首一次判死」+ 开工前跨目录收卷（用户 2026-10-05 两条口径）
        // ================================================================

        /// <summary>
        /// 这一批里"手上有、但这一组还缺几卷"的任务（**批首只记缺口**那一档）。
        ///
        /// <para>口径（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的情况
        /// 可以留在最后做」）：批首**不落 Failed、不写"本次不开始"、不跳过** —— 只把缺口记在这里；
        /// 等这一批的解压都跑完之后再判一次（那时缺的那几卷可能已经被解出来 / 被收拢到位）。
        /// 补齐了 ⇒ 照常跑这一组；仍然缺 ⇒ 到那时才如实报缺卷（不变量 7 一个字没放松）。</para>
        ///
        /// <para>寿命：一键处理的**同一条续解链跨轮有效**（第 2 轮起不清空 —— 第 1 轮没解出来的
        /// 第 1 卷很可能在第 2 轮才出现），下一次**用户发起**的批次开始时清空。</para>
        /// </summary>
        private readonly List<ArchiveTask> _volumeDeficitDeferred = new();

        /// <summary>
        /// **本批"借来用过的用户源片" → 解出它的那一单**（唯一写入点 = 递归结果带回来那份名单，
        /// 见 <see cref="RecursiveExtractor.RecursionResult.ConsumedVolumeSources"/>）。
        ///
        /// <para>用途只有一个：那一单自己不该再报「分卷缺失」（内容已经出来了），按跟班卷收场
        /// （<see cref="TryResolveConsumedByAnotherTask"/>）。⛔ 它不改任何删除 / 搬运判据。</para>
        /// </summary>
        private readonly Dictionary<string, string> _consumedVolumeSources =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"某一组由谁在解"（组基名 → 消费方任务名）——跨链收卷那一条用（真机第九批 CCCC）。
        ///
        /// <para>用途只有一件：同一组的**别的单**在这个消费方收尾之前**不许落任何结论**
        /// （否则会得到一句"分卷缺失、未开始解压"，而那一刻内容正在被解开）。</para>
        /// </summary>
        private readonly Dictionary<string, string> _groupConsumerByName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"哪一单吐出了某一组的片"（组基名 → 那一单）——跨链收卷用（真机第九批 CCCC）。
        ///
        /// <para>用途：整组由别单解开、而且**可证完整**之后，这些"只是包装"的单也按**跟班卷**收场
        /// （它的内容已经全在解出来的那一组里了）—— 否则真机上 `111.rar` / `111(2)_.zip` 会一直留着，
        /// 用户当场就问"为什么这两个原包还留着"。</para>
        /// </summary>
        private readonly Dictionary<string, List<ArchiveTask>> _groupPieceProducers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>这些生产者的源包**已经交给既有出口搬过了**（幂等：搬一次就够，⛔ 不重复搬）。</summary>
        private readonly HashSet<ArchiveTask> _producerSourcesCollected = new();

        /// <summary>
        /// 已经在"批末补判"里跑过一遍的任务：⛔ 不许再记一次缺口（否则它会永远留在
        /// <see cref="_volumeDeficitDeferred"/> 里、永远不落结论）。</summary>
        private readonly HashSet<ArchiveTask> _volumeDeficitFinalPass = new();

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
                AppendLog("INFO", $"{task.FileName}：没有手动指定缺失卷所在目录，按缺卷处理（不启动）。");
                return false;
            }

            string directory = ShowFolderBrowserOnUiThread();

            if (string.IsNullOrWhiteSpace(directory))
            {
                AppendLog("INFO", $"{task.FileName}：没有选择目录，按缺卷处理（不启动）。");
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
                $"{task.FileName}：在 {directory} 里找到 {volumeFiles.Count} 个分卷文件，正在重新归组判定。");

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

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage} 仍然不启动（不变量 7）。");
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
                AppendLog("ERROR", $"{task.FileName}：归组给出的第一卷不存在（{firstVolume}），按缺卷处理（不启动）。");
                return false;
            }

            new VolumeGroupingService().ApplyGroupInfo(task, group);

            task.CurrentPath = firstVolume;
            task.FileName = Path.GetFileName(firstVolume);
            task.CurrentExtension = Path.GetExtension(firstVolume);

            AppendLog(
                "INFO",
                $"{task.FileName}：分卷已找齐（{task.VolumeCount} 卷，起点 {firstVolume}）" +
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

        // ================================================================
        // 开工前跨目录收卷（用户 2026-10-05 口径 1）
        // ================================================================

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
                 * 入口 = 引擎这一趟要打开的那一份（分卷组只有"第 1 卷"这一个入口）。
                 * 手上是后续卷（用户只把 `.002` 拖进来）时，先在**找卷窗口**里找同一组的第 1 卷 ——
                 * 找得到就以它为入口（收卷的目标目录 = 它的那一层），找不到就退回自己
                 * （`ResolveCrossLayerVolumeGather` 会判"不适用"，一个字节都不动）。
                 */
                string entryPath = ResolveGatherEntryPath(current);

                if (string.Equals(entryPath, current, StringComparison.OrdinalIgnoreCase)
                    && VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(current)) != 1
                    && task.IsVolumeGroup)
                {
                    /*
                     * 说话要对（2026-10-05 真机第八批）：**跨盘 ZIP 那一族的头尾是反的** ——
                     * 它的第 1 片叫 `111.z01`，而 `.zip` 是**末片**（7-Zip 就是拿 `.zip` 当入口的）。
                     * 按名字编号说话时"缺 index 1"其实等于"缺 `.zip`" ⇒ 对它必须说「缺的是末片」，
                     * ⛔ 不许说成「缺的是第 1 卷」：真机上那三片的第一片就躺在隔壁文件夹里，
                     * 用户会拿着"缺第 1 卷"这句去找一个根本不缺的东西。
                     *
                     * 判据只看一件事：手上这一份与 `<基名>.zip` **是不是同一族同基名**
                     * （既有出口 `BelongsToSameGroup`；⛔ 不新造family判据）。
                     */
                    string baseName = FileNameHelper.GetArchiveBaseName(current);
                    bool spannedZipFamily = baseName.Length > 0
                        && VolumeGroupDetector.BelongsToSameGroup(
                            Path.GetFileName(current),
                            baseName + ".zip");

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
                 * 窗口的基准 = **手上那一卷**（口径 1）。⛔ 它只"看"：候选还要过下面那三道闸门
                 * （同族同基名 / 卷号一份对一份 / 整组自洽 + 7z 起始头字节数证据）。
                 */
                IReadOnlyList<VolumeCandidate> window = VolumeNameRepair.EnumerateVolumeCandidatesNearby(current);

                VolumeNameRepair.CrossLayerVolumeGather decision =
                    VolumeNameRepair.ResolveCrossLayerVolumeGather(entryPath, null, window);

                if (!decision.Applicable)
                {
                    return false;
                }

                if (decision.Plan is { CanRepair: true })
                {
                    int count = decision.MovedCount;
                    VolumeNameRepairResult applied = VolumeNameRepair.TryApply(decision.Plan);

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
                AppendLog("WARN", $"{task.FileName}：开工前跨目录收卷跳过（{ex.Message}）。");
                return false;
            }
        }

        /// <summary>
        /// 这一组的第 1 卷是不是**同批另一个任务**在负责（判据与
        /// <see cref="SkipWhenAnotherTaskOwnsThisVolumeGroup"/> 同一个口径：任务表里有没有别人指着它）。
        /// 是 ⇒ 这一单不许把起点改过去（那一组只解一次，别的卷按「跟班卷」跳过）。
        /// </summary>
        private bool IsFirstVolumeOwnedByAnotherTask(ArchiveTask task, string firstVolumePath)
        {
            string first = SafePathHelper.GetFullPathSafe(firstVolumePath);

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || ReferenceEquals(candidate, task))
                {
                    continue;
                }

                if (string.Equals(
                        SafePathHelper.GetFullPathSafe(candidate.CurrentPath),
                        first,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 引擎这一趟该打开的那一份：手上就是第 1 卷 ⇒ 它自己；手上是后续卷 ⇒ 在
        /// <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 的窗口里找同一组的第 1 卷
        /// （窗口里自己那一层排在最前，所以"同一层优先"是白拿的）；判不出 ⇒ 退回手上那一份。
        /// </summary>
        private static string ResolveGatherEntryPath(string current)
        {
            try
            {
                string name = Path.GetFileName(current);

                if (VolumeGroupDetector.TryGetVolumeIndex(name) == 1)
                {
                    return current;
                }

                foreach (VolumeCandidate candidate in VolumeNameRepair.EnumerateVolumeCandidatesNearby(current))
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                    {
                        continue;
                    }

                    string candidateName = Path.GetFileName(candidate.Path);

                    if (VolumeGroupDetector.TryGetVolumeIndex(candidateName) == 1
                        && VolumeGroupDetector.BelongsToSameGroup(name, candidateName)
                        && File.Exists(candidate.Path))
                    {
                        return candidate.Path;
                    }
                }
            }
            catch
            {
                // 判不出 ⇒ 什么都不做（收卷那一档会因为"入口不是第 1 卷"整档不适用）。
            }

            return current;
        }

        /// <summary>
        /// 找卷的基准那一句（只在**基准不是入口那一卷**时补出来）：用户口径是"以一开始的分卷文件为准，
        /// 不要以 001 为准" —— 这一行就是它落地的证据（窗口的父文件夹是**基准**那一层的父文件夹）。
        /// ⛔ 只写目录名，不写完整路径（§8）。
        /// </summary>
        private static string DescribeGatherAnchor(
            VolumeNameRepair.CrossLayerVolumeGather decision,
            string entryPath)
        {
            string? anchor = decision.AnchorPath;

            if (string.IsNullOrWhiteSpace(anchor)
                || string.Equals(
                    SafePathHelper.GetFullPathSafe(anchor),
                    SafePathHelper.GetFullPathSafe(entryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "。" + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeGatherAnchorFormat,
                Path.GetFileName(anchor),
                Path.GetFileName(Path.GetDirectoryName(anchor) ?? string.Empty));
        }

        // ================================================================
        // 「缺卷不许在批首一次判死」（用户 2026-10-05 口径 2）
        // ================================================================

        /// <summary>
        /// 这一单**刚发布出来的成品**里，有没有"某一组还缺的那一片" —— 有就按规范卷名接到那一组的目录里。
        ///
        /// <para>挂点 = <see cref="PostProcessSuccessAsync"/> 的收尾（校验通过之后、清理工作区之前）⇒
        /// **单层路与轮次续解那条路自动都在内**（它们每一轮都走这一个收尾）。⛔ 判据与执行体与递归路
        /// 是同一份 <see cref="TryAdoptUnresolvedVolumePiece"/> —— 这里只负责"挑出像分卷片的文件"。</para>
        ///
        /// <para>⛔ 只扫这一层（不递归）；⛔ 一个字节都不复制、不改名、不覆盖：`TryAdoptUnresolvedVolumePiece`
        /// 只在"那一组的目录里还没有这个规范卷名 + 同一个盘"时才建一个零字节硬链接，
        /// 所以那一组已经齐的时候这一档是**空操作**。</para>
        /// </summary>
        private void AdoptPublishedVolumePieces(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 两处都要看（真机 CCCC 单层路实测）：
             * · **成品那一层**（`ContentDirectoryPath`）—— 内容物里的片；
             * · **过程物那一层**（`RestDirectoryPath`）—— 单层路把"内层包"当过程物放进其余物，
             *   而这一组的片**恰恰就是内层包**（`111.zip` 就是在那儿被下一轮当新任务又解了一次）。
             * 这些片由 `TryAdoptUnresolvedVolumePiece` 按"过程物目录里的用移动"那一条接走。
             */
            foreach (string directory in new[] { task.ContentDirectoryPath, task.RestDirectoryPath })
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string name = Path.GetFileName(file);

                        /*
                         * 只挑"像分卷片"的：名字带卷标记（`.z01` / `.001` / `part1` …），
                         * 或者它自己自述是跨盘 ZIP 的末片（末片的 EOCD 是明文 ⇒ 这一条是**硬证据**）。
                         */
                        bool plausible = VolumeGroupDetector.TryGetVolumeIndex(name) != null
                            || Detection.SpannedZipIndex.TryRead(file) != null;

                        if (!plausible)
                        {
                            continue;
                        }

                        TryAdoptUnresolvedVolumePiece(file, task);
                    }
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片就留在原地当内容物 / 过程物）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// 把一片从**我们自己的过程物目录**挪进这一组的目录（同盘改名、零字节；失败 ⇒ false 由调用方退回硬链接）。
        /// ⛔ 只用于过程物：用户目录里的片从这条路走不进来。
        /// </summary>
        private static bool TryMovePieceIntoGroup(string piecePath, string target)
        {
            try
            {
                File.Move(piecePath, target);
                return File.Exists(target);
            }
            catch
            {
                // 挪不动（被占用 / 目标刚出现）⇒ 交给调用方退回"建硬链接"那一档。
                return false;
            }
        }

        /// <summary>
        /// **跨链收卷**（2026-10-05 真机第九批 CCCC；用户口径：「现在找不到的先跳过，等全部结束之后再看看」
        /// 「让批末那一站去查本批各单已经解出来的片」）。
        ///
        /// <para><b>现场</b>：一组跨盘 ZIP 四片分在四户人家 —— <c>.z02</c>/<c>.z03</c> 在用户源目录、
        /// 第 1 片 <c>111.z01</c> 压在 <c>111(2)_.zip</c> 里、末片 <c>111.zip</c> 压在 <c>111.rar</c> 里。
        /// 两条链各自把**对方缺的那一片解了出来**，可两条链自己都没走完（互相等对方）⇒ 收尾时工作区整份删掉
        /// ⇒ 批末那一站到盘上一看：缺的东西刚刚被自己扔掉（真机 22:06:02 与 22:06:12 那两行「已清理工作区」）。</para>
        ///
        /// <para>做法只有一件事：把这一趟**打不开的那几片**按**规范卷名**硬链接到"这一组还缺卷"的那一单
        /// 自己所在的目录里（真机 = <c>111(4)\</c>，与那两片源卷同一个目录），随后**转调既有出口**
        /// <see cref="VolumeNameRepair.ResolveSpannedZipDiskGather"/> 把这一组其余的片（散在兄弟目录里的）
        /// 也按规范名接过来 —— 判据（末片自述 / 一片对一片 / 除末片外等大 / 同盘）一条都没另写。</para>
        ///
        /// <para>⛔ 只是**硬链接**（零字节）：不改名、不搬、不覆盖、不删用户的任何文件；跨盘 / 判不出 /
        /// 建不了 ⇒ 什么都不做（⛔ 绝不复制大文件）。这几片按用户 2026-10-05 的拍板算**源包**：
        /// 这一组真解开之后，它们跟着一起进其余物、按删除档处理。</para>
        /// </summary>
        /// <param name="publishedPieces">
        /// <see cref="PublishUnresolvedVolumePieces"/> 刚落地的那几份（**落地之后的路径**）。
        /// ⛔ 不许拿 <see cref="RecursionResult.UnresolvedVolumePieces"/> 里那份原路径代替 ——
        /// 落地是移动，原路径已经不存在了。
        /// </param>
        private void AdoptUnresolvedVolumePieces(
            RecursionResult? recursion,
            ArchiveTask? owner,
            IReadOnlyList<string>? publishedPieces = null)
        {
            var pieces = new List<string>();

            if (publishedPieces != null)
            {
                pieces.AddRange(publishedPieces);
            }

            if (recursion != null)
            {
                pieces.AddRange(recursion.UnresolvedVolumePieces);
            }

            if (pieces.Count == 0)
            {
                return;
            }

            foreach (string piece in pieces)
            {
                try
                {
                    TryAdoptUnresolvedVolumePiece(piece, owner);
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片跟着工作区一起消失，与改动前一个字都不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// 记下"**这一单**吐出了「<paramref name="baseName"/>」这一组的一片"（去重；⛔ 只记账，不动盘上任何东西）。
        /// 整组由别单解开之后由 <see cref="CollectConsumedSourcePiecesIntoRest"/> 让它按跟班卷收场。
        /// </summary>
        private void RememberGroupPieceProducer(string baseName, ArchiveTask? owner)
        {
            if (owner == null || baseName.Length == 0)
            {
                return;
            }

            if (!_groupPieceProducers.TryGetValue(baseName, out List<ArchiveTask>? producers))
            {
                producers = new List<ArchiveTask>();
                _groupPieceProducers[baseName] = producers;
            }

            if (!producers.Contains(owner))
            {
                producers.Add(owner);
            }
        }

        /// <summary>收**一片**：按规范卷名接到"这一组还缺卷"那一单的目录里；末片在手时顺手把整组接齐。</summary>
        private bool TryAdoptUnresolvedVolumePiece(string piecePath, ArchiveTask? owner)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || !File.Exists(piecePath))
            {
                return false;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);
            int? familyIndex = VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(piecePath));

            if (baseName.Length == 0 || familyIndex == null)
            {
                return false;
            }

            /*
             * 跨盘 ZIP 这一族的编号：`.zip` = 1（**末片**，也正是引擎要打开的入口）、`.zNN` = NN + 1
             * ⇒ 真实盘序 = 编号 - 1（同一句规则也写在 VolumeNameRepair.ResolveSpannedZipDiskGather 里）。
             */
            int disk = familyIndex.Value - 1;

            if (disk < 0)
            {
                return false;
            }

            /*
             * 目标层 = **入口包所在的那一层**（用户 2026-10-06：「你把分卷移过来不就是了吗」）——
             * 入口包（跨盘 ZIP 族 = 末片 `X.zip`）由 `PublishUnresolvedVolumePieces` 先落在
             * **产出它的那个包自己那条链的落点目录**里，`ResolveBatchGroupDirectory` 读的就是它；
             * 于是"收片层 = 入口层 = 落点层"三处是同一个事实位，⛔ 不再看任务表顺序。
             *
             * 入口包还不在盘上（批首那一刻 / 它没被解出来）⇒ 退回旧口径（任务表里第一单那一层），
             * 那一档行为与改动前逐字相同。
             */
            string targetDir = ResolveBatchGroupDirectory(baseName);

            if (targetDir.Length == 0)
            {
                return false;
            }

            string canonical = disk == 0 ? baseName + ".zip" : VolumeNameRepair.CanonicalDiskName(baseName, disk);
            string target = Path.Combine(targetDir, canonical);
            bool adopted = false;

            if (Directory.Exists(target))
            {
                // 目标名被一个**目录**占着：硬链接建不出来（⛔ 绝不删、绝不覆盖）。
                return false;
            }

            if (!File.Exists(target) && HardLinkHelper.CanHardLink(piecePath, targetDir))
            {
                /*
                 * 这一片还在**我们自己的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 用**移动**
                 * （同盘改名、零字节）。为什么不能只建链接：那些目录收尾会整份删掉 / 按删除档处理，
                 * 留一份在里面等于"接了一个马上就要消失的名字"，而且轮次续解下一轮还会把剩下的那一份
                 * 当成一个"内层包"再去解一次（真机上就是那句「111.zip：分卷缺失」，用户最烦看到它）。
                 *
                 * 用户目录里的片一律**只硬链接**：⛔ 名字一个字符都不许改、文件不搬。
                 */
                bool fromProcessFolder = ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath);

                adopted = fromProcessFolder
                    ? TryMovePieceIntoGroup(piecePath, target) || HardLinkHelper.TryCreateHardLink(target, piecePath)
                    : HardLinkHelper.TryCreateHardLink(target, piecePath);

                if (adopted)
                {
                    AppendLog(
                        "INFO",
                        $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 接到「{Path.GetFileName(targetDir)}」这一层；"
                        + "零字节的硬链接：你的源文件一个字节没动、名字也一个字符没改）—— 这一组留到这一批都跑完再一起判。");
                }
            }

            if (adopted)
            {
                /*
                 * 记下"是这一单吐出了这一片"：整组由别单解开、而且**可证完整**之后，这一单也按跟班卷收场
                 * （真机第九批 CCCC：`111.rar` / `111(2)_.zip` 各吐一片，整组解开并校验通过了，
                 * 这两个原包却因为自己"部分完成"一直留在盘上 —— 用户当场就问为什么）。
                 */
                RememberGroupPieceProducer(baseName, owner);
            }

            // 末片在哪一层，整组就得凑到哪一层（引擎只看入口旁边那一层）⇒ 末片到手就顺手把整组接齐。
            string tail = Path.Combine(targetDir, baseName + ".zip");

            if (!File.Exists(tail))
            {
                return adopted;
            }

            VolumeNameRepair.SpannedZipDiskGather gather = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(tail));

            if (gather.Applicable && gather.LinkedCount > 0)
            {
                AppendLog("INFO", $"「{baseName}」这一组又接过来 {gather.LinkedCount} 片：{gather.Detail}");
            }

            if (gather.Applicable && gather.Complete)
            {
                AppendLog(
                    "INFO",
                    $"「{baseName}」这一组现在凑齐了 —— 等这一批的解压都跑完，批末那一站会直接把这一组解开。");
            }

            return adopted || (gather.Applicable && gather.Complete);
        }

        /// <summary>
        /// **这一片自己那条链的家** = 产出它的那个包（<paramref name="owner"/>）自己那一层的**子文件夹**
        /// （<c>111\111.rar</c> ⇒ <c>111\111\</c>），由**唯一落点实现** <see cref="PathService.ResolveOutputPlacement"/>
        /// 算出来 —— ⛔ 这里不自己拼路径（AGENTS.md §9.5）。
        ///
        /// <para>用户 2026-10-06 原话：「`111.rar` 原本就在文件夹 `111` 里面，按照程序你就应该在文件夹 `111`
        /// 的子文件夹里面得到 `111.zip`，而不是这种情况（`111.zip` 所在那一层 `111(4)`），**这种情况是你私自
        /// 移动 `111.zip` 到了 `111(4)`，这是不应该存在的 bug**」。</para>
        ///
        /// <para>拿不到 `owner` / 算不出落点 ⇒ 空串，由调用方退回既有口径。</para>
        /// </summary>
        private string ResolveOwnerChainDirectory(ArchiveTask? owner)
        {
            if (owner == null || string.IsNullOrWhiteSpace(owner.CurrentPath))
            {
                return string.Empty;
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
            }
            catch (Exception ex)
            {
                // 加法：算不出落点 ⇒ 退回既有口径（这一片落在哪儿与改动前一个字都不差）。
                AppendLog("WARN", $"跨链收卷算不出这一片的落点（{ex.Message}）。");
            }

            return string.Empty;
        }

        /// <summary>
        /// **链因「分卷缺失」停下时，把它已经解出来、下一步要用的产物搬回它自己那条链的家**
        /// （用户 2026-10-06 口径：「第一步就让你将能解压的统统解压了」「这个根本就不是部分完成，
        /// 对于 `111.rar` 这个就是成功了，只不过这个内容物又是下一轮解压需要的东西」）。
        ///
        /// <para><b>现场</b>：`111.rar` 第 0 层解出 `111\111.rar`、第 1 层解出 **`111\111.zip`**，
        /// 第 2 层要开 `111.zip` 时缺 `.z01` ⇒ 这一层不开（不试）。而 `111.zip` 是下一轮**那一组跨盘 ZIP
        /// 的入口包** —— 旧口径把它当"没走完的现场"，收尾连工作区一起删掉（真机磁盘上四个文件夹里
        /// **一个 `111.zip` 都没有**，就是这个原因）。⇒ 现在：**认得出是分卷片/入口包的，一律搬到
        /// `owner` 自己那条链的落点目录**（`CCCC\111\111\`），随后的收片与整组解压都在那一层。</para>
        ///
        /// <para>⛔ 动作只有"同卷改名"（我们自己工作区里的文件，零字节代价）；⛔ 用户源目录里的片
        /// 这一档碰都不碰（名字与字节都不动）；⛔ 搬不动（占用 / 目标被目录占着）⇒ 留在原地、
        /// 什么都不做（随后由既有的接片那一档按规范卷名处理）。</para>
        /// </summary>
        private List<string> PublishUnresolvedVolumePieces(RecursionResult? recursion, ArchiveTask? owner)
        {
            var published = new List<string>();

            if (recursion == null || recursion.UnresolvedVolumePieces.Count == 0 || owner == null)
            {
                return published;
            }

            string home = ResolveOwnerChainDirectory(owner);

            if (home.Length == 0)
            {
                return published;
            }

            try
            {
                Directory.CreateDirectory(home);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{owner.FileName}：这一片的落点目录建不出来（{ex.Message}），按原样留着。");
                return published;
            }

            foreach (string piece in recursion.UnresolvedVolumePieces)
            {
                try
                {
                    if (!File.Exists(piece))
                    {
                        continue;
                    }

                    string name = FileNameHelper.GetFileName(piece);
                    string target = Path.Combine(home, name);

                    /*
                     * ⛔ 已经在那一层了就什么都不做（幂等）；目标被占（文件或目录）⇒ 一个字节都不动。
                     */
                    if (File.Exists(target)
                        || Directory.Exists(target)
                        || SafePathHelper.PathEquals(piece, target))
                    {
                        // 已经在那一层了（幂等）⇒ 仍然要把它交给收片那一档：它就是入口。
                        if (File.Exists(target) && SafePathHelper.PathEquals(piece, target))
                        {
                            published.Add(target);
                        }

                        continue;
                    }

                    File.Move(piece, target);

                    if (File.Exists(target))
                    {
                        AppendLog(
                            "INFO",
                            $"{owner.FileName}：这一步解出来的「{name}」留在它自己那条链的落点目录里 —— "
                            + $"{Path.GetFileName(home)}（下一轮解压要用的就是它；⛔ 不是失败、也不算残缺）。");

                        RememberGroupPieceProducer(FileNameHelper.GetArchiveBaseName(target), owner);
                        published.Add(target);
                    }
                }
                catch (Exception ex)
                {
                    // 加法：搬不动 ⇒ 退回"什么都不做"（这一片仍留在工作区，行为与改动前一个字不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }

            return published;
        }

        /// <summary>
        /// 这一组该**拼到哪一层** = 入口包（引擎要打开、要对它输密码的那一份）所在目录，
        /// 也就是这一组该**落**的那一层（用户 2026-10-05 口径：「谁可以输入密码，就解压到谁那边」；
        /// 见 <see cref="Detection.GroupVolumeDirectory"/>：RAR 分卷 = `.part1.rar`、7z 分卷 = `.7z.001`、
        /// zip 分卷 = `.zip`（跨盘那一族）或 `.zip.001`（7-Zip 切的通用分片））。
        ///
        /// <para>入口包已经在盘上（第一次接片之后就一直在了）⇒ 按它的位置收；还不在盘上
        /// （真机 CCCC 批首那一刻：末片 `111.zip` 正压在 `111.rar` 里）⇒ 退回旧口径
        /// （任务表里这一组第一单那一层）当兜底 —— 兜底那一档是**同一个事实位**：
        /// 那一片接进去之后，入口包就在那一层，落点推导与这里读到的答案永远一致。</para>
        ///
        /// <para>判据全部转调既有那几把尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/> 认"同一组"，
        /// 所以脏名 `111.z0删除2` 照样认得出；<see cref="Detection.GroupVolumeDirectory.Resolve"/> 认"入口包在哪儿"），
        /// ⛔ 这里不自己拼族规则、⛔ 不看盘上还有没有别的兄弟（那件事属于收卷，见 <c>ResolveSpannedZipDiskGather</c>）。
        /// 找不到 ⇒ 空串 = 什么都不做。</para>
        /// </summary>
        private string ResolveBatchGroupDirectory(string baseName)
        {
            string tailName = baseName + ".zip";
            var members = new List<string>();

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                string path = candidate?.CurrentPath ?? string.Empty;

                if (path.Length > 0
                    && VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    members.Add(path);
                }
            }

            Detection.GroupVolumeDirectory.Entry entry = Detection.GroupVolumeDirectory.Resolve(members);

            if (entry.Directory.Length > 0 && Directory.Exists(entry.Directory))
            {
                return entry.Directory;
            }

            /*
             * 入口包还不在**任务表**那几层里 ⇒ 问**产出它的那一单**：它自己那条链的落点目录
             * （`111.rar` ⇒ `111\111\`）。真机 CCCC 的第一步修好之后就是这一档：
             * `111.zip` 由 `111.rar` 那一单解出来、并由 `PublishUnresolvedVolumePieces` 落在
             * `CCCC\111\111\`，而"这一组还缺卷"那两单是散在 `111(3)`/`111(4)` 的源片 ——
             * ⛔ 收片层必须是入口所在那一层（用户 2026-10-06：「你把分卷移过来不就是了吗」），
             * 不能退回"任务表里第一单那一层"（那正是把 `111.zip` 搬去 `111(4)` 的旧机制）。
             */
            string fromProducer = ResolveGroupProducerDirectory(baseName);

            if (fromProducer.Length > 0)
            {
                return fromProducer;
            }

            // 连产出方都问不出来（批首那一刻 / 这一组谁都没吐过片）⇒ 退回旧口径（任务表里第一单那一层）——
            // 兜底，行为与改动前逐字相同；等入口包落地之后的每一次重判都会走上面那两支。
            foreach (string member in members)
            {
                string? directory = Path.GetDirectoryName(member);

                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory!;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// **产出这一组某一片的那一单**自己那条链的落点目录（唯一出口 <see cref="ResolveOwnerChainDirectory"/>）。
        /// 找不到产出方 ⇒ 空串（调用方退回旧口径）。
        /// </summary>
        private string ResolveGroupProducerDirectory(string baseName)
        {
            if (!_groupPieceProducers.TryGetValue(baseName, out List<ArchiveTask>? producers) || producers == null)
            {
                return string.Empty;
            }

            foreach (ArchiveTask producer in producers.ToList())
            {
                string directory = ResolveOwnerChainDirectory(producer);

                if (directory.Length > 0)
                {
                    return directory;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 把"这一组**其余的片**"记进既有那份"借来用过的源片"账（消费方 = <paramref name="consumer"/>）。
        ///
        /// <para>⛔ 只记**同一组**、且不是消费方自己那一条；⛔ 一个字节都不动 —— 搬运与删除仍旧由既有出口
        /// 在消费方**真的成功**之后按那六道闸门办（见 <see cref="CollectConsumedSourcePiecesIntoRest"/>）。</para>
        /// </summary>
        private void RememberGroupPiecesConsumedBy(ArchiveTask consumer)
        {
            string tailName = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath) + ".zip";

            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                if (other == null || ReferenceEquals(other, consumer))
                {
                    continue;
                }

                string path = other.CurrentPath ?? string.Empty;

                if (path.Length == 0
                    || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    continue;
                }

                _consumedVolumeSources[path] = consumer.FileName;
            }
        }

        /// <summary>
        /// **批首只记缺口**（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的
        /// 情况可以留在最后做」）：⛔ 不落 Failed、⛔ 不写"本次不开始"、⛔ 不跳过 —— 只记进名单。
        ///
        /// <para>不变量 7 **一个字都没放松**：这一单现在**确实没有开始解压**（一个字节都没动），
        /// 只是"还缺哪几卷"这句话留到这一批的解压都跑完之后再说 —— 那时缺的第 1 卷可能已经被同批另一个包
        /// 解出来、或者已经被收拢到位（真机 `B250135.7z.002` 16:29:33 就被判死，而它的 `.001`
        /// 正躺在同一个目录树里没被解出来）。</para>
        /// </summary>
        private void RecordDeferredVolumeDeficit(ArchiveTask task, string missingNames)
        {
            if (task == null)
            {
                return;
            }

            if (!_volumeDeficitDeferred.Contains(task))
            {
                _volumeDeficitDeferred.Add(task);
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeDeficitDeferredLogFormat,
                    task.FileName,
                    missingNames));
        }

        /// <summary>缺卷清单那一句话（任务账上那份；空 ⇒ 如实说"名字上看不出"）。</summary>
        private static string DescribeMissingVolumeNames(ArchiveTask task)
        {
            IReadOnlyList<string> names = task == null ? Array.Empty<string>() : task.MissingVolumeNames;

            return names.Count > 0
                ? string.Join("、", names)
                : StatusText.CrossLayerGatherNoMissingNames;
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
        private List<ArchiveTask> RecheckDeferredVolumeDeficits(bool finalPass)
        {
            var toRun = new List<ArchiveTask>();

            /*
             * 先给"吐出这一片的那几单"收场（真机第九批 CCCC 的第二个现场问题：`111.rar` / `111(2)_.zip`
             * 两个原包为什么还留着）。判据只有两条事实位：那份"谁在解"的账 + 消费方的**机器终态**（成功）。
             * 位置在名单判据之前 —— 与"缺卷留到最后再判"那份名单无关，幂等。
             */
            SettleSucceededGroupPieceProducers();

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
                        $"{task.FileName}：这一组的内容正在由「{pendingConsumer}」解出来 —— 这一单先不动，等它收尾再判（不提前把话说死）。");

                    continue;
                }

                // ② 现归一次组（盘上事实）。
                VolumeGroup? group = ResolveCompleteGroupFromDisk(task);

                if (group != null)
                {
                    string firstVolume = group.FirstVolumePath;

                    /*
                     * 补齐了 ⇒ 收拢 + 正常跑这一组（起点改到第 1 卷、快照重拍）。
                     *
                     * ⚠ 第 1 卷已经由**同批另一个任务**负责时**不改起点、也不改账**：那一组的活该由
                     * 首卷那一单干（很可能它刚刚已经解成功了）。这一单照旧按"跟班卷"走既有的跳过口径 ——
                     * 改起点会让两单指着同一个文件、把同一份内容解第二遍。
                     */
                    if (IsFirstVolumeOwnedByAnotherTask(task, firstVolume))
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
                            new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);
                            task.CurrentPath = firstVolume;
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
                    toRun.Add(task);

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
                    _groupConsumerByName[FileNameHelper.GetArchiveBaseName(task.CurrentPath)] = task.FileName;
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

                MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);
                AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");

                // 机器终态收口：这一单是在**任务收尾之后**才落的状态，走不到单任务那个 finally ⇒ 这里补。
                FinalizeOutcomeIfPending(task);

                _volumeDeficitDeferred.Remove(task);
                _volumeDeficitFinalPass.Add(task);
            }

            return toRun;
        }

        /// <summary>
        /// **已由本批另一单解出的"借来用过的源片"**（如那三片散在源目录里的跨盘 ZIP 分卷）——
        /// 它们也算**源包**，跟着这一组一起进其余物、一起按删除档处理
        /// （用户 2026-10-05 拍板原话：「那肯定啊，这个算原包，最后放到其余物里面一同删除」）。
        ///
        /// <para>判据只有两件事：① 这一片在 <see cref="_consumedVolumeSources"/> 里、且消费方正是
        /// <paramref name="consumer"/> 这一单；② 这一片自己的任务**确实是**按"内容已由别单解出"收场的
        /// 跟班卷（<see cref="ArchiveTask.IsVolumeGroupFollower"/>、终态 <see cref="TaskOutcome.Skipped"/>）。
        /// 搬运与删除全部交给既有出口（<see cref="ExecuteSourcePackageMove"/> → `SourcePackageMover`，
        /// 六道闸门一条都不绕过；后面那一段"按删除档处理其余物"照旧跑）。
        /// ⛔ 判不出 / 找不到那一单 ⇒ 什么都不做（源片留在原地）。</para>
        /// </summary>
        private void CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer)
        {
            if (consumer == null)
            {
                return;
            }

            var logEntries = new List<(string Level, string Message)>();

            SettleGroupPieceProducers(consumer, moveSources: true, logEntries);

            if (_consumedVolumeSources.Count == 0)
            {
                foreach ((string level, string message) in logEntries)
                {
                    AppendLog(level, message);
                }

                return;
            }

            foreach ((string piecePath, string ownerName) in _consumedVolumeSources.ToList())
            {
                if (!string.Equals(ownerName, consumer.FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                ArchiveTask? pieceTask = _vm.Tasks.FirstOrDefault(
                    candidate => candidate != null &&
                                 candidate.IsVolumeGroupFollower &&
                                 candidate.Outcome == TaskOutcome.Skipped &&
                                 EnumerateTaskPaths(candidate).Any(
                                     path => string.Equals(
                                         SafePathHelper.GetFullPathSafe(path),
                                         SafePathHelper.GetFullPathSafe(piecePath),
                                         StringComparison.OrdinalIgnoreCase)));

                if (pieceTask == null)
                {
                    continue;
                }

                ExecuteSourcePackageMove(
                    pieceTask,
                    consumer.RestDirectoryPath,
                    SourceMoveTrigger.AfterChain,
                    logEntries);

                _consumedVolumeSources.Remove(piecePath);
            }

            foreach ((string level, string message) in logEntries)
            {
                AppendLog(level, message);
            }
        }

        /// <summary>
        /// **吐出这一组某一片的那几单**按跟班卷收场（真机第九批 CCCC：`111.rar` / `111(2)_.zip`）。
        ///
        /// <para>它们各自只吐一片、自己因为缺对方那一片停在中途 ⇒ 落成「部分完成」⇒ 源包照"没成功就不动"
        /// 留在盘上（用户当场问：为什么这两个原包还留着）。可整组已经由 <paramref name="consumer"/> 解开、
        /// **可证完整** ⇒ 它们的内容**全在解出来的那一组里了**。</para>
        ///
        /// <para>⛔ 只认记账里那几单（<see cref="RememberGroupPieceProducer"/> 写、这里读）；⛔ 不碰消费方自己；
        /// ⛔ 搬运仍只有既有出口 <c>ExecuteSourcePackageMove</c>（六道闸门一条都不绕过）；
        /// ⛔ 已经收过场的（跟班 + 已搬）不重复做。</para>
        /// </summary>
        private void SettleGroupPieceProducers(
            ArchiveTask consumer,
            bool moveSources,
            List<(string Level, string Message)> logEntries)
        {
            string groupBaseName = FileNameHelper.GetArchiveBaseName(consumer.CurrentPath ?? string.Empty);

            if (groupBaseName.Length == 0
                || !_groupPieceProducers.TryGetValue(groupBaseName, out List<ArchiveTask>? producers))
            {
                return;
            }

            foreach (ArchiveTask producer in producers.ToList())
            {
                if (producer == null || ReferenceEquals(producer, consumer))
                {
                    continue;
                }

                if (!producer.IsVolumeGroupFollower || producer.Outcome != TaskOutcome.Skipped)
                {
                    string consumedNote = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumePieceProducerConsumedByOtherTaskFormat,
                        producer.FileName,
                        consumer.FileName,
                        groupBaseName);

                    /*
                     * 与既有那条跳过路**逐行同一套**（清校验 / 清落点 + 落终态 + 标跟班）：
                     * `MarkSkipped` 只写 Status，机器终态必须显式落 `Skipped`，四处消费点才算得对。
                     */
                    producer.IsOutputVerified = false;
                    producer.OutputVerification = OutputVerificationOutcome.NotAttempted;
                    producer.OutputPath = string.Empty;
                    producer.Outcome = TaskOutcome.Skipped;
                    producer.IsVolumeGroupFollower = true;
                    producer.MarkSkipped(consumedNote);

                    AppendLog("INFO", consumedNote);
                }

                if (!moveSources || !_producerSourcesCollected.Add(producer))
                {
                    continue;
                }

                ExecuteSourcePackageMove(
                    producer,
                    consumer.RestDirectoryPath,
                    SourceMoveTrigger.AfterChain,
                    logEntries);
            }
        }

        /// <summary>遍历"谁在解"的账：消费方**已经成功**的那几组，给它们的生产者收场（幂等；见 <see cref="SettleGroupPieceProducers"/>）。</summary>
        private void SettleSucceededGroupPieceProducers()
        {
            if (_groupConsumerByName.Count == 0 || _groupPieceProducers.Count == 0)
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

            foreach (string path in EnumerateTaskPaths(task))
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !_consumedVolumeSources.TryGetValue(path, out string? consumer)
                    || string.IsNullOrWhiteSpace(consumer))
                {
                    continue;
                }

                ArchiveTask? owner = _vm.Tasks.FirstOrDefault(
                    candidate => candidate != null &&
                                 string.Equals(candidate.FileName, consumer, StringComparison.Ordinal));

                if (owner == null || owner.Outcome != TaskOutcome.Succeeded)
                {
                    continue;
                }

                consumerName = consumer;
                return true;
            }

            return false;
        }

        /// <summary>这一单自己的全部路径（<c>CurrentPath</c> + 账上那几卷；去重）。</summary>
        private static IEnumerable<string> EnumerateTaskPaths(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        private static VolumeGroup? ResolveCompleteGroupFromDisk(ArchiveTask task)
        {
            try
            {
                var extra = new List<string>();
                string? directory = Path.GetDirectoryName(task.CurrentPath ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    extra.AddRange(CollectVolumeFilesInDirectory(directory));
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
        internal void FinalizeDeferredVolumeDeficits()
        {
            foreach (ArchiveTask task in RecheckDeferredVolumeDeficits(finalPass: true))
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.VolumeDeficitChainEndCompleteLogFormat,
                        task.FileName));
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
                if (FileNameHelper.IsVolumePartFileName(currentName)
                    || task.IsVolumeGroup
                    || VolumeNameRepair.HasSpannedZipTailNearby(current))
                {
                    plan = VolumeNameRepair.Plan(
                        current,
                        VolumeNameRepair.EnumerateFileNamesInDirectory(current));
                }

                if (plan != null && !plan.CanRepair)
                {
                    /*
                     * 名字那条路走不通（推不出标准名）→ 再回退到内容级推断一次。
                     * ⛔ 判据与执行体仍然只有 VolumeNameRepair 那一份，这里只是换个入口。
                     *
                     * ⚠ 这一档**先用同目录候选**（老口径，逐字不变）：候选池一放宽，邻近目录里**别的包**的卷
                     * 就会一起进内容判据 —— 真机夹具实测过：几组 RAR 卷大小完全一样，放宽之后
                     * "同目录里认出 4 卷、卷号连不成 1..N" ⇒ 本来能改的组一个都不改。
                     * 跨目录那一步只放在**下面这一次重试**里（同目录判不出来才放宽）。
                     */
                    plan = await VolumeNameRepair.PlanByContentAsync(
                        current,
                        VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(current),
                        _archiveEngine,
                        volumeProbeWorkRoot,
                        volumeTrialPasswords,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (plan == null)
                {
                    /*
                     * 名字里**完全没有卷号**这一档（用户 2026-09-28 现场：`amb909.7.01` / `amb909.z.2` / `amb909..3`）。
                     * 名字判据必然返回 null，所以不进 Plan，直接交给内容级推断：
                     * 内容认 7z 第一卷 + 同目录等长文件 + 硬链接试开。
                     */
                    plan = await VolumeNameRepair.PlanByContentAsync(
                        current,
                        VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(current),
                        _archiveEngine,
                        volumeProbeWorkRoot,
                        volumeTrialPasswords,
                        cancellationToken).ConfigureAwait(false);
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
                                ? $"{task.FileName}：按内容试开过这一组分卷（同卷硬链接 + 引擎列目录），不成立 —— "
                                  + $"{plan.Reason}；这一单按原名继续（源文件一个字节都没动）。"
                                : $"{task.FileName}：没有动它 —— {plan.Reason}；这一单按原名继续（源文件一个字节都没动）。");
                    }

                    return;
                }

                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                if (!result.Success)
                {
                    AppendLog("WARN", $"{task.FileName}：分卷名没改（{result.Message}），这一单可能因此解不开。");
                    return;
                }

                AppendLog(
                    "INFO",
                    $"{task.FileName}：分卷名不标准，已按标准名改好（{plan.Items.Count} 卷，只改名字、内容一个字节没动）：{plan.Describe()}"
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
                SyncTasksAfterVolumeRename(plan.Items, task);
            }
            catch (Exception ex)
            {
                // 改名失败不该拖垮整单：写清楚，后面的流程按原名去跑（它会如实报缺卷 / 打不开）。
                AppendLog("WARN", $"{task.FileName}：分卷名自动修正跳过（{ex.Message}）。");
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

                await NormalizeDisguisedVolumeNamesAsync(task, cancellationToken).ConfigureAwait(true);
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
        /// 这一趟**真解出来的东西**够不够证明"密码是对的"。
        ///
        /// <para>判据（为什么是"至少两个非空文件"、为什么⛔ 不许放宽）**唯一出口**
        /// <see cref="ProducedContentGate"/> —— 单层这条路与递归那条路（<c>RecursiveExtractor</c>）
        /// 问的是同一个方法，⛔ 不许各写一份（2026-10-01 只接了单层那条路，
        /// 而真机 `giu910` 走的是递归那条 ⇒ 修复等于没生效，用户白等 13 分钟）。</para>
        /// </summary>
        private static bool TryMeasureProducedContent(string? directory, out int files, out long bytes) =>
            ProducedContentGate.TryMeasure(directory, out files, out bytes);

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
                AppendLog("WARN", $"{task.FileName}：分卷归组跳过判定失败（{ex.Message}），这一单按原名照常处理。");

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
                        $"{candidate.FileName}：这一卷在同一个改名批次里被改成了标准名，"
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
             * 而不是按上面那样被覆盖成「放入其余物」—— 那会与安全档的真实行为相反。
             */
            bool spaceTightKeepSource = spaceTight && _vm.SpaceTightKeepSource;

            if (spaceTight)
            {
                sourceHandling = spaceTightKeepSource
                    ? SourceHandlingMode.KeepInPlace
                    : SourceHandlingMode.MoveToRest;

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
                    RestHandlingModes.RecycleBin => StatusText.OneClickConfirmRestRecycle,
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
            AppendLog(level, $"{task.FileName}：{hint}");
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

            return $"{task.FileName}：{string.Join(" ｜ ", parts)}";
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
             * "不同次文件一律严格按我列表顺序"）⇒ 批首清一次：
             * 同一批里后面的包照旧复用前面刚解开的密码，而**下一批**回到④页列表顺序。
             */
            _passwordService.BeginBatch();

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
                _consumedVolumeSources.Clear();
                _groupConsumerByName.Clear();
                _groupPieceProducers.Clear();
                _producerSourcesCollected.Clear();
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
                WorkspaceRootResolution workspaceResolution = ApplyBatchWorkspaceRoot(selectedTasks);

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
                await NormalizeDisguisedVolumeNamesForBatchAsync(selectedTasks, _operationCts.Token)
                    .ConfigureAwait(true);

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
                            $"{task.FileName}：现在空间不够（{gate.Reason}）—— 先排队等空间，"
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
                                $"{waitingTask.FileName}：腾出空间了，现在补跑它（它刚才被拒的原因：{waitingGate.Reason}）");

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
                    List<ArchiveTask> deferredVolumeTasks = RecheckDeferredVolumeDeficits(finalPass: !oneClickRun);

                    if (deferredVolumeTasks.Count == 0)
                    {
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

                AppendBatchSummary(selectedTasks);

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

        /// <summary>
        /// 给界面算一次"按当前可用空间，最多能并行几个"（用户 2026-09-22 需求第 2 条）。
        ///
        /// <para>它与真正开跑时用的是**同一段实现**（<see cref="BuildSchedulePlan"/>），
        /// 所以界面上显示的建议与日志里那一刻的调度口径不会分叉 ——
        /// "界面写一个数、跑起来按另一个数"正是本项目最不能接受的那类形态。</para>
        ///
        /// <para>⚠ 它会 stat 每个源包（以及分卷的每一卷），所以调用方要放后台线程。</para>
        /// </summary>
        internal ExtractionSchedulePlan BuildSpaceAdvice(IReadOnlyList<ArchiveTask> tasks)
        {
            int requested = Math.Clamp(Settings.MaxParallelExtractCount, 1, ExtractionScheduler.ParallelCeiling);

            return BuildSchedulePlan(tasks ?? Array.Empty<ArchiveTask>(), requested);
        }

        /// <summary>
        /// 「其余物」这一档会动到什么 —— **其余物 = 过程物 + 原包**（用户 2026-09-27 定死的口径）。
        ///
        /// <para>为什么必须由档位决定这句话：源包那一档选「留在原地」时，其余物里**根本没有源包**。
        /// 2026-09-27 真机上就是这里出的洋相 —— 同一份日志一边写"源包处理：留在原地（一个字节都不搬）"，
        /// 一边写"其余物：任务成功后会彻底删除（源包 + 过程物）"，用户直接问"我都设置了原包不动，
        /// 你怎么还说原包在其余物里面"。</para>
        /// </summary>
        internal static string DescribeRestScope(SourceHandlingMode sourceHandling)
            => sourceHandling == SourceHandlingMode.KeepInPlace
                ? "解压过程中产生的过程物（源包留在原地：一个字节都不搬、不删）"
                : "源包 + 过程物";

        /// <summary>
        /// 本批（或本单）生效的「源包怎么处理」——**唯一出口**：
        /// `RunOptions`（一键处理的「本次选项」）优先，否则读设置。
        /// </summary>
        private SourceHandlingMode EffectiveSourceHandling
            => RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

        /// <summary>
        /// 本批源包**实际**会怎么被处理（空间不足模式覆盖之后的口径；供话术与判据共用）。
        ///
        /// <para>三种可能：普通档照设置 / 空间不足档（校验通过即删）/ 空间不足的安全档（一个字节都不动）。
        /// ⛔ 说"会不会动源包"的地方只准读它 —— 各写一套必然出现"同一份日志里两句自相矛盾的话"。</para>
        /// </summary>
        private string DescribeEffectiveSourceHandlingForBatch()
        {
            if (!_spaceTightThisBatch)
            {
                return OneClickRunOptions.DescribeSourceHandling(EffectiveSourceHandling);
            }

            return _spaceTightKeepSourceThisBatch
                ? "一个字节都不动（空间不足 + 不删原包）"
                : "定稿 + 校验通过后立刻永久删除（空间不足模式）";
        }

        /// <summary>
        /// 供界面显示的"当前空间档位"一句话（其余物会不会被自动处理也在这句里说清）。
        /// </summary>
        internal string DescribeSpaceMode()
        {
            string mode = RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);
            SourceHandlingMode sourceMode = AppSettings.ParseSourceHandling(Settings.SourceHandling);
            string scope = DescribeRestScope(sourceMode);

            string rest = mode switch
            {
                RestHandlingModes.RecycleBin =>
                    $"其余物：任务成功后移入回收站（{scope}；可还原，空间要等清空回收站才释放）",
                RestHandlingModes.Delete =>
                    $"其余物：任务成功后彻底删除（{scope}，不可恢复）",
                _ => "其余物：保留（不动其余物）"
            };

            string source = sourceMode == SourceHandlingMode.KeepInPlace
                ? "源包：留在原地"
                : "源包：移入其余物";

            return source + "；" + rest;
        }

        /// <summary>界面上那句"当前档位"用的并发数（与真正开跑时同一个口径：设置项 1–8）。</summary>
        private int ResolveParallelCountForDisplay()
        {
            return Math.Clamp(Settings.MaxParallelExtractCount, 1, MaxParallelExtractCountCeiling);
        }
        /// <summary>本批探测可用空间用的路径（一次定住：同一批里所有判断都对着同一块盘，数字才可比）。</summary>
        private string _spaceProbePath = string.Empty;

        /// <summary>
        /// 可用空间探测（可注入）。
        ///
        /// <para>为什么必须留这个口子：真机上没法把盘写成只剩 3 MiB，而"空间不足就不启动"这条
        /// 是本批最核心的行为之一 —— 只靠读代码是证明不了的。单测用它造一块假盘。</para>
        /// </summary>
        internal Func<string, long?>? SpaceProbeOverride { get; set; }

        /// <summary>
        /// 要保留的余量（可注入）。默认取 <see cref="SpaceGate.DefaultReserveBytes"/>（512 MiB）。
        /// 单测里不调小它，任何几十字节的样本都会被那 512 MiB 拦下 —— 那样测的就不是被测逻辑了。
        /// </summary>
        internal long? SpaceReserveOverride { get; set; }

        private long? ProbeAvailableSpace(string path)
        {
            return SpaceProbeOverride != null
                ? SpaceProbeOverride(path)
                : SpaceChecker.GetAvailableFreeSpace(path);
        }

        /*
         * ===== 空间变化侦察（用户 2026-09-27："你要时刻弄空间检测"）=====
         *
         * 一批里采几针、什么时候采、报不报，全在 Storage/SpaceTrendMonitor 里（纯逻辑、可单测）；
         * 这里只负责"什么时候起、什么时候停、把哪句话写进日志"。
         *
         * ⚠ 它**只观察、不判断**：一次都不参与放行与调度 —— 拦人的仍然是每个任务启动前那道空间门。
         * 侦察一旦变成判据，就会出现"日志说的"与"程序做的"两套口径（本项目最不能接受的那类形态）。
         */
        private SpaceTrendMonitor? _spaceTrend;
        private CancellationTokenSource? _spaceTrendCts;

        /// <summary>
        /// 批末收工之后**还留着**的那个侦察器：等一键处理把其余物处理完，再补一针真实可用空间
        /// （见 <see cref="RecordSpaceTrendPostscript"/>）。它是**同一个**侦察器（同一个探测函数、
        /// 同一份采样表），不是第二套取数；一批只补一针，下一批开工时清掉。
        /// </summary>
        private SpaceTrendMonitor? _spaceTrendPostscript;

        /// <summary>周期采样的间隔（用户要的是"时刻"，但一秒一针又纯属噪声 —— 5 秒够看清曲线）。</summary>
        internal static readonly TimeSpan SpaceTrendInterval = TimeSpan.FromSeconds(5);

        /// <summary>起侦察：批首采一针，然后每 <see cref="SpaceTrendInterval"/> 采一针。</summary>
        private void StartSpaceTrendMonitor(string probePath)
        {
            StopSpaceTrendMonitor();

            /*
             * 新一批开工 ⇒ 上一批那个"等着补记一针"的侦察器作废（它探的是上一批的盘、
             * 采样表也是上一批的）。不清掉的话，这一批跑完时补出来的可能是上一批的数字。
             */
            _spaceTrendPostscript = null;

            if (string.IsNullOrWhiteSpace(probePath))
            {
                AppendLog("WARN", "空间侦察没起来：这一批连「目标盘在哪」都没定下来（探测路径为空）。");
                return;
            }

            _spaceTrend = new SpaceTrendMonitor(() => ProbeAvailableSpace(probePath));
            _spaceTrendCts = new CancellationTokenSource();

            SpaceTrendChange? first = _spaceTrend.Record("批首");

            if (first != null)
            {
                AppendLog("INFO", SpaceTrendMonitor.DescribeChange(first, "目标盘") + $"（每 {SpaceTrendInterval.TotalSeconds:0} 秒侦察一次）");
            }

            SpaceTrendMonitor monitor = _spaceTrend;
            CancellationToken token = _spaceTrendCts.Token;

            _ = Task.Run(
                () => monitor.RunAsync(SpaceTrendInterval, line => AppendLog("INFO", line), token),
                CancellationToken.None);
        }

        /// <summary>停侦察 + 批末把空间曲线写进日志（这是"这一批空间怎么变的"唯一交代）。</summary>
        private void StopSpaceTrendMonitor()
        {
            try
            {
                _spaceTrendCts?.Cancel();
            }
            catch
            {
                // 取消失败不影响收尾。
            }

            try
            {
                _spaceTrendCts?.Dispose();
            }
            catch
            {
            }

            _spaceTrendCts = null;

            SpaceTrendMonitor? monitor = _spaceTrend;
            _spaceTrend = null;

            if (monitor == null)
            {
                return;
            }

            // 收尾再采一针，然后写曲线（收工那一刻的数字是用户最关心的那一个）。
            monitor.Record("批末");

            foreach (string line in monitor.DescribeReport())
            {
                AppendLog("INFO", line);
            }

            /*
             * 侦察器**不丢**：批末那条曲线的"收"是在**其余物删除之前**打的（删其余物发生在
             * `OneClickCoordinator` 的链尾那一步，比这里更晚）—— 真机上那一针因此比真实可用少 6 GB，
             * 用户按日志读成"这一批净吃掉 5.5 GB"（实际净省 0.5 GB）。
             *
             * 留着它，等一键处理把其余物处理完之后由 <see cref="RecordSpaceTrendPostscript"/> 补一针：
             * **同一个探测函数、同一份采样表、同一套曲线文案** —— 不新开第二套取数。
             * 下一批开工时 `StartSpaceTrendMonitor` 会先把它清掉，所以它只服务这一批的收尾。
             */
            _spaceTrendPostscript = monitor;
        }

        /// <summary>
        /// **其余物处理完之后**补一针真实可用空间并写一行 INFO（用户 2026-10-02 真机）。
        ///
        /// <para>为什么要补：批末那条空间曲线的"收"打在其余物删除**之前**，随后那些源包 / 过程物
        /// 被彻底删掉，盘上真实可用比曲线里的"收"多一大截 —— 不补这一针，用户会把这一批读成
        /// "净吃掉好几 GB"（真机那一批实际净省 0.5 GB）。</para>
        ///
        /// <para>⛔ 三条纪律：① 这一针走的是**本批那个侦察器自己的探测函数**（与每 5 秒那一针、
        /// 与每个任务开工 / 收尾那一针完全同源），不是第二套取数；② 文案由 <c>SpaceTrendMonitor</c>
        /// 产出，不是第二套曲线；③ **一批只补一针**（取走即清），没有侦察器（手动档 / 单包重试）
        /// 时什么都不写 —— 绝不编一个数字。</para>
        /// </summary>
        /// <param name="note">这一针的来由（写进那一行）。</param>
        /// <returns>真的写了一行才返回 true。</returns>
        internal bool RecordSpaceTrendPostscript(string note)
        {
            SpaceTrendMonitor? monitor = _spaceTrendPostscript;

            // 取走即清：同一批里被调用两次也不会补出两针（第二针的数字没有意义）。
            _spaceTrendPostscript = null;

            if (monitor == null)
            {
                return false;
            }

            monitor.Record(note, at: null, notify: false);

            string? line = monitor.DescribePostscript(note);

            if (string.IsNullOrWhiteSpace(line))
            {
                // 这一针与之前的针都取不到可用空间：如实不写（DescribePostscript 已经说明了原因）。
                return false;
            }

            AppendLog("INFO", line!);
            return true;
        }

        /// <summary>
        /// 逐任务采一针（开工 / 收尾各一次）。
        ///
        /// <para>两条纪律，缺一条就会踩到别人的规矩上：</para>
        /// <list type="number">
        /// <item><description><b>只记不报</b>（`notify: false`）：这一针不进过程日志 ——
        /// 第 45 条定的是"成功的任务只留一行"，在这里多写一行就是废掉它。</description></item>
        /// <item><description><b>注脚里不写任务文件名</b>（只写"任务开工 / 任务收尾"）：批末那条空间曲线
        /// 会把最低点的注脚抄进去，而它同样是"含该文件名的行" ——
        /// 全量回归实测：两条真 7z 用例（`Item45LogAndPasswordTests` 里用"行数 ≤ 3"钉着的）正是这么变红的。</description></item>
        /// </list>
        /// <para>曲线本身不受影响：这一针照样进采样表，最低点仍然写清"哪一刻、在什么节点上"。</para>
        /// </summary>
        private void RecordSpaceTrend(string note)
        {
            _spaceTrend?.Record(note, at: null, notify: false);
        }

        private long ReserveSpaceBytes => SpaceReserveOverride ?? SpaceGate.DefaultReserveBytes;

        /*
         * ===== 并发下的最终目录占位（见 ExtractSingleTaskAsync 里的调用点）=====
         *
         * 只记"还没落盘、但已经被某个正在跑的任务要用"的目录。任务收尾即释放 ——
         * 那一刻目录要么已经带着产物存在（后来的任务走既有的"已存在"冲突档），
         * 要么根本没建起来（后来的任务就该用它）。
         */
        private readonly HashSet<string> _claimedOutputDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _outputClaimLock = new();

        /// <summary>占位失败时的兜底尝试上限（与 <c>SafePathHelper.AutoRenameDirectoryPath</c> 同一个量级）。</summary>
        private const int MaxOutputClaimAttempts = 10000;

        /// <summary>
        /// 把一个"还不存在的最终目录"在进程内占下来；被别的正在跑的任务占了就换 <c>名字(1)</c>。
        ///
        /// <para><c>(i)</c> 的命名规则与 <see cref="SafePathHelper.AutoRenameDirectoryPath"/> 一致
        /// （同一个用户可见约定），区别只有一个：那里判"磁盘上有没有"，这里还要判"有没有被并发任务占下"——
        /// 被占下的目录此刻在磁盘上还不存在，所以那个方法看不见它。</para>
        /// </summary>
        private string ClaimOutputDirectory(string requestedPath, ArchiveTask task, ref string outputRedirectNote)
        {
            lock (_outputClaimLock)
            {
                string candidate = requestedPath;

                for (int attempt = 1; attempt <= MaxOutputClaimAttempts; attempt++)
                {
                    string full = SafePathHelper.GetFullPathSafe(candidate);

                    if (string.IsNullOrWhiteSpace(full))
                    {
                        return candidate;
                    }

                    bool claimedByAnotherTask = _claimedOutputDirectories.Contains(full);

                    // 磁盘上真出现了也算"占不到"（TOCTOU 的温和版本：两次系统调用之间别人建了）。
                    bool existsOnDisk = Directory.Exists(candidate);

                    if (!claimedByAnotherTask && !existsOnDisk)
                    {
                        _claimedOutputDirectories.Add(full);

                        if (!string.Equals(candidate, requestedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            /*
                             * 与"目录已存在"那条路同一口径：实际落点与打算的落点不一致**不许静默**。
                             * 日志 + outputRedirectNote（会进任务详情的校验结论）+ task.OutputPath 回写，三处都要有。
                             */
                            string note =
                                $"原定输出目录 {requestedPath} 正被另一个正在解压的任务使用，本次实际输出到 {candidate}";

                            outputRedirectNote = string.IsNullOrWhiteSpace(outputRedirectNote)
                                ? note
                                : outputRedirectNote + "；" + note;

                            AppendLog(
                                "WARN",
                                $"{task.FileName}：同一个名字的包正在并行解压，为避免两个任务写进同一个目录，" +
                                $"本次落成 {candidate}（绝不合并、绝不覆盖）。");
                        }

                        return candidate;
                    }

                    try
                    {
                        string? parent = Path.GetDirectoryName(requestedPath);
                        string name = Path.GetFileName(requestedPath);

                        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                        {
                            return candidate;
                        }

                        candidate = Path.Combine(parent, $"{name}({attempt})");
                    }
                    catch
                    {
                        return candidate;
                    }
                }

                return candidate;
            }
        }

        /// <summary>释放最终目录占位（任务收尾时调；没占过就是一次空操作）。</summary>
        private void ReleaseOutputDirectoryClaim(string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            string full = SafePathHelper.GetFullPathSafe(outputPath);

            if (string.IsNullOrWhiteSpace(full))
            {
                return;
            }

            lock (_outputClaimLock)
            {
                _claimedOutputDirectories.Remove(full);
            }
        }

        /// <summary>
        /// 批首清零空间规划与本批记账（账本、逐任务运行期记录、精确估算、被跳过的清单）。
        /// </summary>
        private void ResetSpacePlanningState()
        {
            _spaceLedger = null;
            _spaceProbePath = string.Empty;

            lock (_spaceRuntimeLock)
            {
                _spaceRuntime.Clear();
                _refinedEstimates.Clear();
            }

            _spaceBlockedTasks.Clear();

            // 那一次"中途空间不足"的纯提示也跟着本批记账一起清零：下一批该弹还得弹。
            _spaceBlockedNoticeShown = false;
        }

        // ================================================================ 工作区根（默认跟输出盘）

        /// <summary>单测注入：把"一个目标目录"映射到**假盘根**（默认 = <see cref="Path.GetPathRoot(string)"/>）。
        ///
        /// <para>⚠ 现在它**只影响日志里"跨了几个盘"那句描述**：工作区位置不再取盘符，
        /// 而是落在这一单的目标目录里面（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30）。
        /// 于是单测可以直接用真实临时目录验证落点，不必再注入假盘。</para>
        /// </summary>
        internal Func<string, string>? WorkspaceDriveOverride { get; set; }

        /// <summary>
        /// 本批开工前定一次**工作区根**（用户 2026-09-30：默认落在这一单的目标目录里面）。
        ///
        /// <para>三档与理由都写在 <see cref="WorkspaceRootResolver"/> 里，这里只负责"什么时候定"与"怎么告诉用户"：</para>
        /// <list type="number">
        /// <item><description>目标目录全部由**唯一实现** <see cref="PathService.ResolveOutputPlacement"/> 算出来
        /// （这里不自己拼路径）；算不出落点的任务跳过 ——
        /// 它本来就会在任务级被报"输出目录无效"，不该让整批的工作区跟着定不下来。</description></item>
        /// <item><description>定完立刻写进 <see cref="PathService.WorkDirectory"/> 与
        /// <c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>：此后本批所有中间产物
        /// （暂存、抠取副本、递归逐层）都在这一个根下面，**不会再有第二条拼路径的实现**。</description></item>
        /// <item><description>日志里写清"在哪个目标目录里、跨不跨盘"。</description></item>
        /// <item><description>⛔ <b>定不下来就是 ERROR + 整批停手</b>（见返回值）：绝不悄悄换个盘
        /// （用户点名"危险操作固定到了 C 盘"）。</description></item>
        /// <item><description>定出来的根要**记进小账本**（<see cref="WorkspaceRootIndex"/>）：
        /// ③ 页与下次启动的残留扫描靠它才找得到（根会随目标目录变，不记住就等于漏报）。</description></item>
        /// </list>
        /// </summary>
        /// <returns>本批的工作区结论；<see cref="WorkspaceRootResolution.Resolved"/> 为 false 时调用方必须停手。</returns>
        private WorkspaceRootResolution ApplyBatchWorkspaceRoot(IReadOnlyList<ArchiveTask> tasks)
        {
            ExtractOptions options = BuildExtractOptions(tryExtractUnknownFormat: false);

            // 本次选项面板选出来的落点也要算进去 —— 不然"这批落在 D 盘"会被算成设置里那个盘。
            RunOptions?.ApplyTo(options);

            var destinations = new List<string>();

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                OutputPlacementResult placement = _pathService.ResolveOutputPlacement(task, options);

                if (placement.Success && !string.IsNullOrWhiteSpace(placement.DestinationDirectory))
                {
                    destinations.Add(placement.DestinationDirectory);
                }
            }

            /*
             * ===== 上一次跑到一半被中断留下的空壳：**这一次跑的时候顺手收掉**（用户 2026-09-30 亲口点了"推荐"）=====
             *
             * 现场：正常跑完的收尾会连壳一起删（批尾那两步），只有**中断**这一档会残留 ——
             * 真机上 `H:\成果\1111\` 里就只剩一个空的 `.ArchiveFixer.work`，一个成品都没有，
             * 而用户翻自己目录时会看见这个凭空多出来的文件夹。
             *
             * 位置是**硬要求**：必须在 `WorkspaceRootResolver.Resolve` **之前**。
             * 那一句会按"解析之前它在不在"如实记下 TargetDirectoryCreated，并把目标目录与工作区都建出来 ——
             * 先建后收会把**刚建出来的工作区**当成残留删掉（顺序反了就是自伤）。
             *
             * ⛔ 只查**本批这些目标目录这一层**（destinations 就是唯一实现 ResolveOutputPlacement 算出来的落点），
             * 不递归、不看兄弟目录、更不做全盘扫描；判据一条都不在这里写（唯一出口
             * `Storage/WorkspaceLeftoverShellCleaner.ReclaimAll`：除了那个空工作区什么都没有才删）。
             */
            foreach (WorkspaceLeftoverShellOutcome leftover in WorkspaceLeftoverShellCleaner.ReclaimAll(destinations))
            {
                AppendLog(leftover.LogLevel, leftover.Message);
            }

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                destinations,
                WorkspaceDriveOverride);

            AppendLog(resolution.LogLevel, resolution.Reason);

            if (!resolution.Resolved)
            {
                /*
                 * ⛔ 定不下来就**什么都不做**：不写 WorkDirectory（留空只会让后面拼出相对路径，
                 * 那是"写到一个没人知道的地方"的更坏版本）、不碰递归静态、不记账本。
                 * 停手由调用方做（它才知道怎么把这一批收干净）。
                 */
                return resolution;
            }

            _pathService.WorkDirectory = resolution.RootDirectory;

            /*
             * 记下"目标目录是不是我们建的"：工作区必须先有目标目录才能落在它里面，
             * 所以这一批必然会建出那一个目录壳；批尾它要是空的，就该由我们自己收掉
             * （见 RemoveEmptyTargetShellIfWeCreatedIt，用户 2026-09-30）。
             */
            _batchCreatedTargetDirectory = resolution.TargetDirectoryCreated
                ? resolution.TargetDirectory
                : string.Empty;

            // 递归核心的工作区根是进程级静态，且它自己会再挂一层 "recursive"（见 RecursiveExtractor）。
            RecursiveExtractor.ConfiguredWorkspaceRoot = resolution.RootDirectory;

            /*
             * 工作区与目标盘的关系**永远**是"同一棵树"（工作区就建在目标目录里面），
             * 所以这里不再有"工作区不在批目标盘上"那一档 —— 那个警告的前提（用户设过缓存根目录）
             * 已随设置项一起删除。跨盘剩下的是**批内部**的：各任务落点在不同盘上，那个由
             * WorkspaceRootResolver 的跨盘说明如实写出来。
             */

            WorkspaceRootIndex.Remember(resolution.RootDirectory);

            return resolution;
        }

        private ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task)
        {
            lock (_spaceRuntimeLock)
            {
                if (!_spaceRuntime.TryGetValue(task, out ScheduledTaskRuntime? runtime))
                {
                    runtime = new ScheduledTaskRuntime();
                    _spaceRuntime[task] = runtime;
                }

                return runtime;
            }
        }

        /// <summary>
        /// 排一次执行计划：按空间需求从小到大排序 + 算建议并行数。
        ///
        /// <para>估算用**粗估**（只 stat 文件）而不是逐个列目录：50–200 个包的场景下，
        /// 为了排序去把每个包的目录都列一遍，等于在"还没开始解"之前先跑一遍整批。
        /// 真正的精确值在解压前的预检里算（那时本来就要列目录），并会在开工后调整预留。</para>
        /// </summary>
        private ExtractionSchedulePlan BuildSchedulePlan(
            IReadOnlyList<ArchiveTask> tasks,
            int requestedParallel,
            bool spaceTightOrdering = false)
        {
            _spaceProbePath = ResolveSpaceProbePath(tasks);

            /*
             * 排序键（空间不足模式）：**净占用** = 解完之后真正留在盘上的字节数（= 内容物）。
             * 那个模式下源包与过程物都会被删掉，所以"需求"里那一截过程物是**会回来的** ——
             * 先解净占用小的，盘上越跑越宽；按需求排则会先啃过程物多的包。
             * ⚠ 只改顺序：放行判断照旧按需求（内容物 + 过程物，见 ExtractionScheduler.Build 的说明）。
             */
            Func<ScheduledExtractionItem, long>? sortKey = spaceTightOrdering
                ? item => item.Estimate.NetOccupancyBytes
                : null;

            return ExtractionScheduler.Build(
                tasks,
                task => SpaceEstimator.FromSourceFiles(task, DirectReadAppliesTo(task), PartialPublishActive)
                    .WithVolumeLayout(ResolveWorkspaceVolumeShare(ResolveSpaceProbePathOrBatch(task))),
                ProbeAvailableSpace(_spaceProbePath),
                ReserveSpaceBytes,
                requestedParallel,
                sortKey,
                spaceTightOrdering ? ExtractionSchedulePlan.NetOccupancyOrderBasis : null);
        }

        /// <summary>
        /// 工作区根与这个落点是不是**同一块盘**（空间口径用，见 <see cref="TaskSpaceEstimate"/> 的同卷 / 跨卷说明）。
        ///
        /// <para>取不到（工作区根还没定 / 落点为空 / 路径认不出盘符）一律返回 null ——
        /// **绝不默认成同卷**：默认同卷等于把"工作区那块盘也要留一份"这件事从账面上抹掉，
        /// 而那正是用户 2026-09-24 要求如实算进去的那一条。</para>
        /// </summary>
        private bool? ResolveWorkspaceVolumeShare(string? targetPath)
        {
            string workRoot = _pathService.WorkDirectory;

            if (string.IsNullOrWhiteSpace(workRoot) || string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            string workDrive = ResolveDriveOf(workRoot);
            string targetDrive = ResolveDriveOf(targetPath);

            if (string.IsNullOrWhiteSpace(workDrive) || string.IsNullOrWhiteSpace(targetDrive))
            {
                return null;
            }

            return string.Equals(workDrive, targetDrive, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"这个路径在哪块盘上"：默认 <see cref="Path.GetPathRoot(string)"/>，单测可用假盘映射覆盖。</summary>
        private string ResolveDriveOf(string path)
        {
            string full = SafePathHelper.GetFullPathSafe(path);

            if (WorkspaceDriveOverride != null)
            {
                return WorkspaceDriveOverride(full);
            }

            try
            {
                return Path.GetPathRoot(full) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 这个任务的**内嵌归档**这次会不会走直读（<c>Extraction/EmbeddedZipStreamExtractor</c>）。
        ///
        /// <para>两个条件缺一不可：</para>
        /// <list type="number">
        /// <item><description>识别阶段探过、结论是"能直读"（<see cref="ArchiveTask.EmbeddedDirectReadSupported"/>）；</description></item>
        /// <item><description>这一批是**单层模式**：递归模式下第 0 层必须交给引擎逐层展开
        /// （<c>RecursiveExtractor</c> 拿的是"一个归档路径"，直读解出来的是散文件，没有对应的入口），
        /// 所以那条路上照旧抠取 —— 空间核算也必须跟着记那笔副本，否则账面就是假的。</description></item>
        /// </list>
        ///
        /// <para>它只回答"空间账面上要不要预留那份副本"，不决定解压怎么做；解压那一刻会再探一次，
        /// 探不通就原地回落到抠取（见 <c>ExtractSingleTaskAsync</c> 里内嵌归档那一段）。</para>
        /// </summary>
        private bool DirectReadAppliesTo(ArchiveTask? task)
        {
            if (task == null ||
                task.EmbeddedArchiveOffset <= 0 ||
                !task.EmbeddedDirectReadSupported)
            {
                return false;
            }

            return IsSingleLayerRecursion();
        }

        /// <summary>这一批是不是单层模式（递归模式下第 0 层要交给引擎，见 <see cref="DirectReadAppliesTo"/>）。</summary>
        private bool IsSingleLayerRecursion() =>
            string.Equals(Settings.RecursionMode, "SingleLayer", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 本批探测哪块盘：优先归集目标目录（开了归集时产物最终都落那儿），
        /// 其次是第一个任务的输出目录，最后退回源包所在目录。
        /// </summary>
        private string ResolveSpaceProbePath(IReadOnlyList<ArchiveTask> tasks)
        {
            if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
            {
                return Settings.CollectTargetDirectory;
            }

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                string path = ResolveSpaceProbePathForTask(task);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }

            return string.Empty;
        }

        private static string ResolveSpaceProbePathForTask(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return task.OutputPath;
            }

            return task.DirectoryPath;
        }

        private string ResolveSpaceProbePathOrBatch(ArchiveTask task)
        {
            string path = ResolveSpaceProbePathForTask(task);

            return string.IsNullOrWhiteSpace(path) ? _spaceProbePath : path;
        }

        /// <summary>
        /// 空间门拦下：任务**不启动**，状态落「磁盘空间不足」，原因里带具体数字与建议动作。
        ///
        /// <para>为什么用这个状态而不是「解压失败」：归档本身没有任何问题，连开始都没开始 ——
        /// 用户要做的是清空间 / 换盘 / 用危险模式，而不是去怀疑包坏了。</para>
        /// </summary>
        private void MarkSpaceBlocked(ArchiveTask task, ScheduledExtractionItem item, SpaceGateDecision gate)
        {
            MarkSpaceBlockedCore(
                task,
                gate.ToLogLine() + $"（{item.Estimate.Describe()}；依据：{item.Estimate.Basis}）",
                item.RequiredBytes,
                gate.AvailableBytes,
                gate.ShortfallBytes);
        }

        /// <summary>
        /// 空间门拦下的共用落点（启动前与解压前预检两条路都走它）：
        /// 状态、错误信息、日志、批末报告清单**一次写全**。
        /// </summary>
        private void MarkSpaceBlockedCore(
            ArchiveTask task,
            string message,
            long requiredBytes,
            long availableBytes,
            long shortfallBytes)
        {
            task.Status = StatusText.DiskSpaceInsufficient;
            task.ProgressText = StatusText.ProgressFailed;
            task.Operation = StatusText.OpExtract;
            task.ErrorMessage = message;
            task.EndTime = DateTime.Now;
            task.LastUpdatedTime = DateTime.Now;

            /*
             * 终态**显式落 Failed**（用户 2026-10-02 真机暴露出来的不一致）：这个方法过去只写 Status，
             * 不写 Outcome —— 于是同一个"空间不足"在不同路径下落出两种机器终态：
             * 真机那批（解压前预检那条路）报**失败 6**，而用例里（启动前那道门）批末汇总是**未处理 1**，
             * 同一批日志里还有一行写着**跳过 N 个** —— 三种说法指同一件事。
             *
             * 为什么是 Failed 而不是 Skipped：它**没有做成**、而且是要用户动手（清空间 / 换盘）的那种，
             * 落 Skipped 会被汇总与色带读成"按设计跳过、不用管"。⛔ 落 Failed 也绝不等于"源包动了"——
             * 这一档从头到尾一个字节都没动（源包原地、其余物不生成）。
             */
            task.Outcome = TaskOutcome.Failed;

            /*
             * 用户 2026-09-30 第 1 条：批末那个汇总框要"点名差多少"。
             * 这三个数**就是这里手上的**（空间门刚算出来的），原样记到任务上 ——
             * 批末诊断清单只读它（Models\BatchSummaryDiagnostics.cs），⛔ 不重算一遍。
             * 只在这里写：启动前那道门与解压前预检两条路都收口到本方法。
             */
            task.SpaceBlocked = new SpaceBlockedFacts
            {
                RequiredBytes = requiredBytes,
                AvailableBytes = availableBytes,
                ShortfallBytes = shortfallBytes
            };

            // 措辞用"未解压"而不是"未启动"：这一条同时覆盖两条路 —— 批调度里根本没开跑的那些，
            // 以及跑起来之后在解压前预检里被拦下的那些（那时引擎一个字节都还没写）。
            AppendLog("ERROR", $"空间不足，未解压：{task.FileName} —— {message}");

            _spaceBlockedTasks.Add((
                string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName,
                requiredBytes,
                availableBytes,
                shortfallBytes));

            NotifySpaceBlockedOnce(task, requiredBytes, availableBytes, shortfallBytes);
        }

        /// <summary>
        /// 中途撞上空间不足时那**一次纯提示**（用户 2026-09-29 第 2 条）。
        ///
        /// <para>他的新口径：「错误也可以弹窗，因为这就相当于结束了，而且是解压失败了」——
        /// 但一键档的红线是"批中间不许要用户点一下才能继续"，所以这里三条都钉死：</para>
        /// <list type="number">
        /// <item><description><b>非模态</b>（<c>DialogService.ShowSpaceBlockedNotice</c> → <c>Show()</c>）：
        /// 窗口浮着，批一秒都不停；</description></item>
        /// <item><description><b>一个按钮</b>（只有"确定"）；</description></item>
        /// <item><description><b>同一批只弹一次</b>：后面的同类情况只写日志 —— 一批几十个包逐个弹
        /// 等于把他按在屏幕前点几十次，而那正是这条红线要防的。合并后的完整清单在批末
        /// （<see cref="ReportSpaceBlockedTasks"/> 逐条点名）与批末汇总框里。</description></item>
        /// </list>
        ///
        /// <para>⚠ 手动档**照旧只写日志**（用户原话："手动档照旧"）：他坐在屏幕前，任务状态当场变红，
        /// 不需要再飘一个窗口。</para>
        /// </summary>
        private void NotifySpaceBlockedOnce(ArchiveTask task, long requiredBytes, long availableBytes, long shortfallBytes)
        {
            if (!_oneClickThisBatch || _spaceBlockedNoticeShown)
            {
                return;
            }

            _spaceBlockedNoticeShown = true;

            string name = string.IsNullOrWhiteSpace(task.FileName) ? task.CurrentPath : task.FileName;

            _dialogService.ShowSpaceBlockedNotice(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.SpaceBlockedNoticeFormat,
                1,
                name,
                TaskSpaceEstimate.FormatSize(requiredBytes),
                availableBytes < 0 ? "未知" : TaskSpaceEstimate.FormatSize(availableBytes),
                TaskSpaceEstimate.FormatSize(shortfallBytes)));
        }

        /// <summary>
        /// 把工作区根下面**空掉的壳目录**收掉（见调用点的三条安全边界）。
        /// </summary>
        /// <summary>
        /// 收掉**我们自己建出来的那个空目标目录**（用户 2026-09-30：工作区改到目标目录里面之后的新收尾）。
        ///
        /// <para><b>为什么必须有这一步</b>：工作区要落在 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，
        /// 所以批首必须先建出目标目录（用户原话"原地就在原地创"）。老口径那句
        /// "最终目录连建都不该建"在实现上因此不再成立；但**用户看得见的残留一条都不许多** ——
        /// 一批全失败 / 被取消 / 越界时，那个目录里只剩一个空壳，留着就是"凭空多出来一个文件夹"，
        /// 正是用户反复抱怨过的那种东西。</para>
        ///
        /// <para><b>三条安全边界（⛔ 一条都不许松）</b>：</para>
        /// <list type="number">
        /// <item><description>只处理**这一次建出来的**那一个（批首记的事实，用户本来就有的目录永远不碰）；</description></item>
        /// <item><description>只删**空的**（现枚举；里面还有任何条目 —— 包括我们删不掉的工作区残留、
        /// 目录联接点 —— 就一个字节都不动）；</description></item>
        /// <item><description>**非递归**删（<c>recursive: false</c>），且整段包在 try/catch 里：
        /// 收尾的顺手活，失败只写日志，绝不影响批结论。</description></item>
        /// </list>
        /// </summary>
        private void RemoveEmptyTargetShellIfWeCreatedIt()
        {
            string directory = _batchCreatedTargetDirectory;

            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    // 里面有东西（内容物 / 清不掉的工作区残留 / 用户后来放进去的）→ 一律不碰。
                    return;
                }

                Directory.Delete(directory, recursive: false);
                AppendLog("INFO", $"顺手收掉了这一批建出来的空目标目录：{directory}");
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"空目标目录没清掉（不影响结果）：{directory} —— {ex.Message}");
            }
        }

        /// <summary>
        /// 收掉**空的工作区壳**（用户 2026-09-27：真机跑完 <c>…\.ArchiveFixer.work\recursive</c> 会留一个空壳）。
        /// </summary>
        private void RemoveEmptyWorkspaceShells()
        {
            try
            {
                string root = _pathService.WorkDirectory;

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return;
                }

                // ① 只要根下面还有一个文件，就什么都不动（宁可留壳，也不误删）。
                if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
                {
                    return;
                }

                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        // ② 只删直接子目录（`recursive` 这种父壳就在这一层）。
                        Directory.Delete(directory, recursive: true);
                        AppendLog("INFO", $"顺手清掉了空的工作区壳：{directory}");
                    }
                    catch (Exception ex)
                    {
                        // ③ 收尾的顺手活：失败只写一句，绝不影响批结论。
                        AppendLog("INFO", $"空的工作区壳没清掉（不影响结果）：{directory} —— {ex.Message}");
                    }
                }

                /*
                 * ④ 连 `<目标目录>\.ArchiveFixer.work` 这一层空壳一起收掉（用户 2026-09-30 明确要求：
                 * "整批结束后这一层空壳也要删掉，别留在用户目录里"）。
                 *
                 * 走到这里**已经确认整棵树里一个文件都没有**（① 那道闸门），所以这一删不可能碰到内容物、
                 * 也不可能碰到源包 —— 它删掉的只是一个空的隐藏目录。
                 * 删完若用户目录那一层也是空的，那说明这一批什么都没解出来，那个目录本来就该自己消失，
                 * 但**不越界去删目标目录本身**（那是用户的目录，可能有别的用途，也不是我们造的）。
                 */
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        Directory.Delete(root, recursive: false);
                        AppendLog("INFO", $"顺手收掉了空的工作区目录：{root}");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("INFO", $"空的工作区目录没清掉（不影响结果）：{root} —— {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                AppendLog("INFO", $"清理空工作区壳时出错（不影响结果）：{ex.Message}");
            }
        }

        /// <summary>
        /// 批末如实报告被空间门跳过的任务（不变量：不许静默跳过）。
        /// 一条汇总 + 逐个点名"需要多少、差多少、当时可用多少"。
        /// </summary>
        private void ReportSpaceBlockedTasks()
        {
            if (_spaceBlockedTasks.Count == 0)
            {
                return;
            }

            AppendLog(
                "WARN",
                $"空间不足，本批没做成 {_spaceBlockedTasks.Count} 个任务（一个字节都没动过）：");

            foreach ((string name, long required, long available, long shortfall) in _spaceBlockedTasks)
            {
                AppendLog(
                    "WARN",
                    $"  没做成：{name} —— 需要 {TaskSpaceEstimate.FormatSize(required)}，"
                    + $"当时可用 {(available < 0 ? "未知" : TaskSpaceEstimate.FormatSize(available))}，"
                    + $"差 {TaskSpaceEstimate.FormatSize(shortfall)}");
            }

            AppendLog(
                "WARN",
                "处理办法：清理「其余物」腾空间 / 换一个空间更大的输出盘 / "
                + "开①页那个「空间不足」模式（边解边删源包，每解完一个就收回一份空间；"
                + "⚠ 它会永久删除源包，只对这一次运行有效、不写设置）。"
                + (_spaceTightThisBatch ? "本批已经开着那个模式了 —— 那说明连它也不够：请换盘或先清理。" : string.Empty));
        }

        /// <summary>
        /// 跑一个任务：解压 → （空间不足模式下当场删源包）→ 其余物按档处理 → 释放预留 + 刷新可用空间。
        ///
        /// <para>⚠ 顺序是刻意的：删除发生在**任务收尾之后、释放预留之前**。
        /// 这样"删源包回收回来的空间"会出现在下一个任务的空间门判断里（这正是那个模式能解决空间不够的原因），
        /// 而账本上的预留仍然按"这些字节还在盘上"来算，绝不会提前把它许给别的任务。</para>
        /// </summary>
        private async Task RunScheduledTaskAsync(ArchiveTask task, bool oneClickRun, ScheduledTaskRuntime runtime)
        {
            try
            {
                RecordSpaceTrend("任务开工");

                await ProcessExtractTaskAsync(task, oneClickRun);

                await RunRestHandlingAsync(task, runtime, oneClickRun);
            }
            finally
            {
                runtime.AvailableAfterFinish = ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task))
                                               ?? SpaceReservationLedger.UnknownAvailable;

                SpaceReservationLedger? ledger = _spaceLedger;

                if (ledger != null)
                {
                    ledger.Release(runtime.ReservedBytes);
                    ledger.RefreshAvailable(runtime.AvailableAfterFinish >= 0 ? runtime.AvailableAfterFinish : null);
                    runtime.ReservedBytes = 0;
                }

                RecordSpaceTrend("任务收尾");

                UpdateSummary();
            }
        }

        /// <summary>「定稿完成」之后点名产物时的条数上限（超了只报个数，但会写明还有多少没列）。</summary>
        private const int MaxFinalizeDetailLines = 20;

        /// <summary>
        /// 「本次内容物 —— 名字、名字…」最多列几个（用户 2026-09-25 第 44 条）。
        ///
        /// <para>那一行原来是**整份日志里最占字节的一条**（56 行 / 3 万字符：每行二十个文件名的完整清单，
        /// 而他这批是 68 套同名照片）。名字仍然有用（续解找下一层靠它、名字被改坏时肉眼能看出来），
        /// 但**前 5 个足够定性**：多出来的只写个数。</para>
        /// </summary>
        private const int MaxFinalizeContentNameLines = 5;

        /// <summary>
        /// 「删除操作」那一档的**唯一触发点**：任务收尾之后，按用户选的档处理它自己的其余物。
        ///
        /// <para>三档（用户 2026-09-25 第 32 条亲自定的）：不动（Keep）/ 移入回收站 / 彻底删除。
        /// 后两档的门槛全在 <see cref="RestItemPurger"/> 里（终态必须是「完成」+ 校验通过 + 未取消 +
        /// 路径是定稿那一刻记下来的那一条 + 落在自己的输出根之内）——这里只负责"什么时候试"和"怎么记账"。
        /// **失败 / 部分完成 / 取消的任务一个字节都不会被删**（不变量 1 的红线）。</para>
        /// </summary>
        private async Task RunRestHandlingAsync(
            ArchiveTask task,
            ScheduledTaskRuntime runtime,
            bool oneClickRun,
            bool force = false)
        {
            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            /*
             * ⚠ 一键处理里**不能在这里动手**（2026-09-25 第 32 条实现删除操作时被测试逮住的顺序陷阱）：
             * 任务收尾这一刻，其余物里躺着的正是**下一层要解的内层包** —— 当场删掉，续解就没粮草了
             * （实测：选了「彻底删除」之后 ContinuationLayers 直接变成 0，第二层再也解不出来）。
             * 所以一键档统一记成"留到链尾"，由
             * CompleteRootSourcePackagesAfterChainAsync → ApplyRestHandlingAfterChainAsync 处理；
             * 手动「只解压」是单层路径，没有链可等，当场按档处理。
             */
            if (oneClickRun && !force)
            {
                if (!string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * ⚠ 2026-10-03（普通档「逐层回收」）：续解层的过程物**已经当场回收掉了**
                     * （`PostProcessSuccessAsync` 里那一支），链尾那一档从今天起只处理最外层源包 ——
                     * 老文案"整条续解链跑完后再按「删除操作」处理"在这一档下就是**假话**
                     * （用户照它去等，什么都不会再发生）。
                     * ⛔ 只改这一支的措辞；最外层那一单照旧（它的其余物确实要等链尾）。
                     *
                     * ⚠ 2026-10-04（同一天的真机）：这一句以前是**无条件**的 —— 续解层「定稿搬运失败」
                     * 时它照样写着"已当场回收"，而那一刻回收判据一条都没过（`PurgeLayerSourcePackage`
                     * 要么被 `commit.FailedCount > 0` 挡下、要么根本走不到），过程物**原封不动躺在盘上**。
                     * 现在**按事实说**（用户要求：真删了才说已回收，被拒就如实说没回收 + 原因）。
                     *
                     * 事实位 = `task.SourcePackageMove == Done`：对续解任务它**只可能**由
                     * `PurgeLayerSourcePackage` 写（`MoveSourcePackageIntoRest` 那一支对续解任务走不到，
                     * 上面那个 `else if (task.IsContinuationTask)` 先接住了），而它只在
                     * "真的删掉了至少一个文件、且一个都没失败"时才落 —— 正是"真删了"。
                     */
                    if (_layerReclaimThisBatch && task.IsContinuationTask)
                    {
                        AppendLog(
                            "INFO",
                            task.SourcePackageMove == SourcePackageMoveState.Done
                                ? string.Format(
                                    CultureInfo.CurrentCulture,
                                    StatusText.LayerProcessReclaimedFormat,
                                    task.FileName)
                                : string.Format(
                                    CultureInfo.CurrentCulture,
                                    StatusText.LayerProcessNotReclaimedFormat,
                                    task.FileName,
                                    DescribeLayerReclaimBlocker(task)));

                        return;
                    }

                    /*
                     * ⚠ 措辞要**如实**（第 35 条）：这一轮**没有**产生其余物时（内容物里就是那个内层包），
                     * 老文案"其余物先留着（里面还有要接着解的内层包）"会让人以为目录已经建好了 ——
                     * 用户按这句话去找其余物，只会更糊涂。有其余物才说"留着"，没有就说清"这一轮没有其余物"。
                     */
                    AppendLog(
                        "INFO",
                        string.IsNullOrWhiteSpace(task.RestDirectoryPath)
                            ? $"{task.FileName}：这一轮没有产生其余物（过程物就是内容物里的那个内层包）；"
                              + "整条续解链跑完后按「删除操作」处理内层包。"
                            : $"{task.FileName}：其余物先留着（里面还有要接着解的内层包），"
                              + "整条续解链跑完后再按「删除操作」处理。");
                }

                return;
            }

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                // 不动其余物：也要**说一句**（用户 2026-09-25 真机反馈："源包确实没有了，但是其余物还在"）——
                // 静默正是问题本身。只对真的产出了其余物的任务写，免得整批刷屏。
                if (!string.IsNullOrWhiteSpace(task.RestDirectoryPath))
                {
                    /*
                     * 一行说清（第 44 条：一次导出 713 KB，"每次不需要汇报得那么详细"）。
                     * "去哪儿改档位"这句话**批首已经说过一遍**（`其余物：本批保留（③页「删除操作」= 不动其余物）…`），
                     * 逐任务再念一遍 68 次只是把日志撑大。
                     */
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RestKeptCompactFormat,
                            task.FileName,
                            DescribeShortDestination(task.RestDirectoryPath, task.RestDirectoryPath),
                            CountRestEntries(task.RestDirectoryPath)));
                }

                return;
            }

            bool cancelled = IsStopping || _operationCts?.IsCancellationRequested == true;
            DeleteMode deleteMode = string.Equals(mode, RestHandlingModes.RecycleBin, StringComparison.OrdinalIgnoreCase)
                ? DeleteMode.RecycleBin
                : DeleteMode.Permanent;

            RestPurgeOutcome outcome;

            try
            {
                // 磁盘活（量大小 + 删目录）→ 放后台，别占住 UI 线程。
                outcome = await Task.Run(
                    () => new RestItemPurger(
                            RestDeleteExecutor,
                            null,
                            ContentKeepRules.FromSettings(Settings))
                        .Purge(task, cancelled, deleteMode));
            }
            catch (Exception ex)
            {
                // 这一层不该抛（RestItemPurger 内部全部收敛成结论），但"删东西"这件事
                // 绝不允许把已经成功的解压拖成异常：结论落成"没删成"，内容物不受影响。
                outcome = new RestPurgeOutcome
                {
                    Message = $"{task.FileName}：处理其余物时出现意外错误：{ex.Message}"
                };
            }

            /*
             * 逐条 LogLines（`[彻底删除] 成功 路径=…；理由=…；条目数=…；总大小=…；说明=…`）
             * **只在没删成时写**（用户 2026-09-25 第 44 条：成功那一支一行就够，见下面）。
             */
            if (!outcome.Succeeded)
            {
                foreach (string line in outcome.LogLines)
                {
                    AppendLog("INFO", line);
                }
            }

            if (outcome.Succeeded)
            {
                runtime.RestPurged = deleteMode == DeleteMode.Permanent;
                runtime.PurgedBytes = outcome.FreedBytes;

                /*
                 * 一行说清（用户 2026-09-25 第 44 条："最后删除文件你还弄得这么多的文字"）。
                 * 以前这里是两句长文案（含理由、条目数、总大小、说明、完整路径、不可逆提示），
                 * 68 个包就是 400 多行、7 万多字符。理由与"不可逆"已经在批首那条 + ③页常驻红提示里
                 * 说过一次了，逐任务再念一遍只是把日志撑大；**失败时仍然全量**（下面那一支）。
                 */
                NoteBatchPurge(outcome.FreedBytes, deleteMode == DeleteMode.Permanent);

                AppendLog("INFO", DescribeRestPurgedLine(outcome, deleteMode));

                return;
            }

            runtime.PurgeNote = outcome.Message;

            // 其余物没删成也是"其余物还在"的一个答案 ⇒ 同一句话落到①页 / 批末诊断 / 失败清单。
            RecordRestKept(task, outcome.Message);

            AppendLog(outcome.Attempted ? "ERROR" : "INFO", outcome.Message);
        }

        /// <summary>
        /// 逐层回收那一句里"**为什么没回收**"（只有 <c>SourcePackageMove != Done</c> 时才会问到这里）。
        ///
        /// <para>三档都只读**任务上已有的**事实（状态常量 / 机器终态），⛔ 不重推判据、不重算原因：</para>
        /// <list type="number">
        /// <item><description><b>部分完成</b>（删了一部分没删完，占用 / 只读 / 权限）——
        /// 这时候那一层已经写过一行 ERROR，指过去。</description></item>
        /// <item><description><b>终态成功</b> ⇒ 这一层跑成了，是回收判据没放行（完整性判不出 / 半套分卷闸门 /
        /// 源包清单是空的）—— 那一层也各写了一行 WARN，指过去。</description></item>
        /// <item><description><b>其余</b> ⇒ 这一层自己没跑成（定稿搬运失败 / 校验没过 / 已取消），
        /// 回收那一步**根本没走到** ⇒ 原因在①页「错误信息」列（用户 2026-10-04 真机正是这一档）。</description></item>
        /// </list>
        /// </summary>
        private static string DescribeLayerReclaimBlocker(ArchiveTask task)
        {
            if (string.Equals(task.Status, StatusText.PartiallyCompleted, StringComparison.Ordinal))
            {
                return StatusText.LayerReclaimPartialBlockedReason;
            }

            if (task.Outcome == TaskOutcome.Succeeded)
            {
                return StatusText.LayerReclaimGateBlockedReason;
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                StatusText.LayerReclaimLayerFailedReasonFormat,
                task.Status);
        }

        /// <summary>
        /// 其余物处理成功后那**一行**（措辞唯一出口，单独可测）。
        ///
        /// <para><b>两档措辞</b>：</para>
        /// <list type="bullet">
        /// <item><description><b>空壳档</b>（<see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>）：
        /// 其余物里递归地一个文件都没有 ⇒ 就地删掉空目录、**没有进回收站**
        /// （<see cref="StatusText.RestPurgedEmptyShellFormat"/>）。</description></item>
        /// <item><description><b>普通档</b>：<see cref="StatusText.RestPurgedCompactFormat"/>
        /// —— 档名 + 短路径 + 条目数 + 释放大小（用户 2026-09-25 第 44 条："一次导出 713KB，这个多吓人"）。</description></item>
        /// </list>
        ///
        /// <para>⚠ <b>为什么必须按事实位选、不能按数字猜</b>（用户 2026-10-03 真机）：空壳档在
        /// 「移入回收站」这一档下会写成「移入回收站：…\其余物（0 项 / 0 B）」，而空壳**一个字节都没进回收站**
        /// —— 那就是一句假话。判据只读 <see cref="RestPurgeOutcome.RemovedAsEmptyShell"/>（AGENTS.md §9.5：
        /// 要删 / 要写盘的动作判据只准读事实；"0 项 0 B"证明不了"没进回收站"）。</para>
        /// </summary>
        internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome, DeleteMode deleteMode)
        {
            if (outcome.RemovedAsEmptyShell)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RestPurgedEmptyShellFormat,
                    DescribeShortTail(outcome.Directory, 2));
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RestPurgedCompactFormat,
                deleteMode == DeleteMode.Permanent ? StatusText.RestActionDelete : StatusText.RestActionRecycleBin,
                DescribeShortTail(outcome.Directory, 2),
                outcome.EntryCount,
                WorkspaceCleanupService.FormatSize(outcome.FreedBytes));
        }

        /// <summary>
        /// 批首定一次"本批的删除操作按哪一档走"（用户中途改了设置也只影响下一批，同一批口径一致）。
        ///
        /// <para>⚠ 2026-09-25 第 33 条：**「本次选项」优先**（<see cref="RunOptions"/> 里那一档）——
        /// 用户在确认框的折叠区里改了「删除操作」，这一次就必须按他选的跑；
        /// 没有本次选项（无界面宿主 / 勾了"以后不再询问"）时才读设置。</para>
        /// </summary>
        private void PrepareRestHandlingForBatch()
        {
            /*
             * ⚠ 「空间不足」模式**覆盖**这一档（用户 2026-09-27 拍板的口径：
             * 源包 → 放入其余物 + 其余物 → 彻底删除，运行期自动覆盖、⛔ 不写设置）。
             *
             * 注意覆盖的**不是**"是否删源包"那件事 —— 源包在这个模式下的命运在
             * PostProcessSuccessAsync 里已经定了（定稿 + 校验通过 → 当场删）。
             * 这一档管的是**过程物**（内层包、暂存残渣）：它们仍然按链尾统一处理，
             * 所以这里给它落到"彻底删除"。
             */
            if (_spaceTightThisBatch)
            {
                _restHandlingThisBatch = RestHandlingModes.Delete;

                /*
                 * 「空间不足」模式**不走逐层回收这一档**：它有自己的那一支（`PostProcessSuccessAsync`
                 * 的第一支，连**源包**都在每层当场删），措辞与门槛都是它自己那套。
                 * ⛔ 置 false 只是"别让普通档那一支抢它的活"，那个模式的判据一个字都没改。
                 */
                _layerReclaimThisBatch = false;

                return;
            }

            _restHandlingThisBatch = RunOptions != null
                ? RestHandlingModes.Normalize(RunOptions.RestHandling)
                : RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);

            /*
             * B 档位分叉（用户 2026-10-03）：**只有「彻底删除」档逐层回收**。
             * 「移入回收站」档照旧链尾一次 —— 移入回收站不释放盘上空间，逐层做零收益、只多造回收站条目。
             * ⛔ 判据只读上面那**一个**出口（`_restHandlingThisBatch`），别处不许再判一遍。
             */
            _layerReclaimThisBatch = string.Equals(
                _restHandlingThisBatch,
                RestHandlingModes.Delete,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 本批「其余物」的范围那一句话（口径的唯一来源）。
        ///
        /// <para>「空间不足」模式必须**另说一句**：那种模式下源包根本不进其余物（校验通过即删），
        /// 照普通档说"源包 + 过程物"会让用户以为源包还在其余物里躺着 ——
        /// 而他会去找一个根本不存在的东西（2026-09-27 真机上正是这类自相矛盾的日志挨了骂）。</para>
        /// </summary>
        private string DescribeRestScopeForBatch()
        {
            if (!_spaceTightThisBatch)
            {
                return DescribeRestScope(EffectiveSourceHandling);
            }

            return _spaceTightKeepSourceThisBatch
                ? "过程物（源包在本模式下「一个字节都不动」：不搬进其余物、也不删除）"
                : "过程物（源包在本模式下不进其余物：定稿 + 校验通过后立刻永久删除）";
        }

        /// <summary>
        /// 批首写一条"其余物这一批会怎么处理"的日志（用户 2026-09-25 第 32 条：
        /// 他要的是**当场看得懂**，而不是跑完发现"其余物还在"却不知道为什么）。
        /// </summary>
        private void LogRestHandlingForBatch()
        {
            string mode = RestHandlingModes.Normalize(_restHandlingThisBatch);

            /*
             * 范围这句话**跟着源包那一档说**（2026-09-27 用户真机：他选了"源包留在原地"，
             * 这条却写"彻底删除（源包 + 过程物）"，两句自相矛盾）。判据来自 `EffectiveSourceHandling`，
             * ⛔ 不许在这里另读一遍设置。
             */
            string scope = DescribeRestScopeForBatch();

            if (_spaceTightThisBatch)
            {
                /*
                 * 这一批的"为什么这么办"写在最前面（用户 2026-09-26 教训：同一份日志里两句话自相矛盾
                 * 比少写一句更糟）。它必须同时说清三件事：覆盖了什么、**没写设置**、以及源包的下场。
                 *
                 * ⚠ 两档分开说（用户 2026-09-27 加了「不删原包」安全档）：会删的那一档说"立刻删除"，
                 * 安全档说"一个字节都不动" —— 一条文案盖两种行为就是撒谎。
                 */
                AppendLog(
                    "WARN",
                    "「空间不足」模式（本次运行，不写设置）：本批忽略「全速」，并发由空间自己定"
                    + "（你要是把②页的「最大并发解压数」调得更低，就按更低的那个跑 —— 慢盘上少开几个反而更快）；"
                    + (_spaceTightKeepSourceThisBatch
                        ? "「不删原包」也开着 —— 源包一个字节都不动（不搬、不删），"
                          + "只把过程物在任务成功后彻底删掉。⚠ 这一档不回收源包那份空间，需要的余量更大。"
                        : "每个包定稿 + 校验通过后立刻永久删除它的源包"
                          + "（不进回收站、不可恢复），收回的空间马上给后面的包用。")
                    + "失败 / 部分完成 / 取消的任务一个字节都不删。");
            }

            if (string.Equals(mode, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    "INFO",
                    $"其余物：本批保留（③页「删除操作」= 不动其余物）。任务成功后其余物（{scope}）留在成品目录里，随时可手工删。");
                return;
            }

            if (string.Equals(mode, RestHandlingModes.RecycleBin, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    "WARN",
                    $"其余物：本批任务成功后会移入回收站（{scope}；可还原，空间要等清空回收站才真正释放）。"
                    + "失败 / 部分完成 / 取消的任务一个字节都不动。");
                return;
            }

            AppendLog(
                "WARN",
                $"其余物：本批任务成功后会彻底删除（{scope}，不进回收站、不可恢复）。"
                + "失败 / 部分完成 / 取消的任务一个字节都不动。");
        }

        /// <summary>
        /// 批首写一条"这一批用的是哪四条安全上限"的日志（用户 2026-09-25 第 36 条）。
        ///
        /// <para>为什么要在**每一批**都写：那四条上限以前是硬编码的，用户被拦下时
        /// 手里只有一句"超过单文件上限 4 GiB"和满肚子疑问（"我这盘还有 47 GB，为什么不行"）。
        /// 现在它们既是设置项、又在这里当场报出来 —— 事后翻日志就能对上"是哪个上限拦的、当时设的是多少"。</para>
        /// </summary>
        private void LogExtractionLimitsForBatch()
        {
            AppendLog(
                "INFO",
                "安全上限（解压前预算）：" + BudgetLimits.Describe()
                + "。超过就不解这个包（预检拒绝，一个字节都不写）；"
                + "可在⑥设置 →「安全上限」里按自己的盘与资源改。");
        }

        /// <summary>
        /// 本批最多允许解几层（②页「最大嵌套层数」）。
        ///
        /// <para>⚠ 这个数**不是第二套轮数**：它就是 <c>OneClickCoordinator.RoundLimit</c> 读的那一个
        /// 设置项（`MaxRecursionDepth`，默认 5、夹在 1~10）。这里只把它取出来说明白"最多几层"，
        /// 绝不参与"实际跑几轮"的裁决（那个裁决只有 <c>OneClickCoordinator</c> 一处）。</para>
        /// </summary>
        private int ResolveMaxLayersForBatch() =>
            Math.Clamp(Settings.MaxRecursionDepth, 1, OneClickCoordinator.MaxRoundsCeiling);

        /// <summary>
        /// 本批**有没有可能**解出第二层：一键档按轮数上限（&gt;1 就可能续解），
        /// 手动档还多一条"递归模式没关"（手动路径的递归走 <c>RecursiveExtractor</c>）。
        /// </summary>
        private bool IsMultiLayerPossibleForBatch() =>
            ResolveMaxLayersForBatch() > 1 || !IsSingleLayerRecursion();

        /// <summary>
        /// 「多层解压可能中途空间不足」的判据（**唯一出口**，批首日志与确认框都走它）。
        ///
        /// <para>输入全部来自**现成的估算口径**：各任务的
        /// <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/> 之和（= 这次要从可用空间里新写多少，
        /// 已经含一层内层展开的增量）与排计划那一刻的可用空间。⛔ 不用 PeakBytes（含源包，
        /// 拿它比可用空间就是把源包算两遍 —— 2026-09-29 那场真机事故的成因）。</para>
        ///
        /// <para>它只是一句提醒：不拦任务、不改设置、不改勾选（放行仍然由每个任务开工前那道空间门决定）。</para>
        /// </summary>
        internal MultiLayerSpaceRisk EvaluateMultiLayerSpaceRisk(ExtractionSchedulePlan plan)
        {
            if (plan == null)
            {
                return new MultiLayerSpaceRisk { AvailableBytes = -1 };
            }

            return MultiLayerSpaceRiskRules.Evaluate(
                multiLayerPossible: IsMultiLayerPossibleForBatch(),
                maxLayers: ResolveMaxLayersForBatch(),
                totalDemandBytes: MultiLayerSpaceRiskRules.SumDemand(
                    plan.Ordered.Select(item => item.RequiredBytes)),
                availableBytes: plan.AvailableBytes);
        }

        /// <summary>那句话（日志与确认框**同一段文案**，免得两处说法分叉）。</summary>
        internal static string DescribeMultiLayerSpaceRisk(MultiLayerSpaceRisk risk)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.MultiLayerSpaceRiskFormat,
                risk.MaxLayers,
                TaskSpaceEstimate.FormatSize(risk.TotalDemandBytes),
                TaskSpaceEstimate.FormatSize(risk.AvailableBytes),
                TaskSpaceEstimate.FormatSize(risk.ShortfallBytes));
        }

        /// <summary>批首那一条 WARN（用户要求"开工前就要看得见"；不成立时一个字都不写）。</summary>
        private void LogMultiLayerSpaceRisk(ExtractionSchedulePlan plan)
        {
            MultiLayerSpaceRisk risk = EvaluateMultiLayerSpaceRisk(plan);

            if (!risk.Applies)
            {
                return;
            }

            AppendLog("WARN", DescribeMultiLayerSpaceRisk(risk));
        }
        /// <summary>把解压前那一遍 list 算出来的**精确**空间需求记下来（自测的空间曲线要用）。</summary>
        private void RecordRefinedEstimate(ArchiveTask task, TaskSpaceEstimate estimate)
        {
            if (task == null || estimate == null)
            {
                return;
            }

            lock (_spaceRuntimeLock)
            {
                _refinedEstimates[task] = estimate;
            }
        }

        private TaskSpaceEstimate? TryGetRefinedEstimate(ArchiveTask task)
        {
            lock (_spaceRuntimeLock)
            {
                return _refinedEstimates.TryGetValue(task, out TaskSpaceEstimate? estimate)
                    ? estimate
                    : null;
            }
        }

        /// <summary>
        /// 拿开工时申请的预留与精确需求对一次账：不够就加、多了就还。
        /// 加不上（盘已经不够了）时按「磁盘空间不足」处理 —— **这一步必须在真正写盘之前**。
        ///
        /// <para>⚠ 用的数字是 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>（内容物 + 过程物），
        /// **不是** <see cref="TaskSpaceEstimate.PeakBytes"/>：源包已经在盘上、不在可用空间里，
        /// 拿峰值比可用空间就是把源包算两遍（2026-09-29 真机事故，见那个属性的说明）。</para>
        /// </summary>
        private SpaceGateDecision ReconcileReservation(ArchiveTask task, TaskSpaceEstimate refined)
        {
            SpaceReservationLedger? ledger = _spaceLedger;

            if (ledger == null)
            {
                // 没有账本（例如从别处直接调解压管线）：退化成一次直接判断，不假装知道并发情况。
                return SpaceGate.Check(
                    refined.FreeSpaceDemandBytes,
                    ProbeAvailableSpace(ResolveSpaceProbePathOrBatch(task)),
                    ReserveSpaceBytes,
                    0,
                    refined.ReclaimableBytes);
            }

            ScheduledTaskRuntime runtime = GetOrCreateRuntime(task);
            SpaceGateDecision decision = ledger.Adjust(
                runtime.ReservedBytes,
                refined.FreeSpaceDemandBytes,
                refined.ReclaimableBytes);

            if (decision.Allowed && !decision.ProbeFailed)
            {
                runtime.ReservedBytes = refined.FreeSpaceDemandBytes;
            }

            return decision;
        }

        private async Task ProcessExtractTaskAsync(ArchiveTask task, bool oneClickRun)
        {
            var taskCts = new CancellationTokenSource();
            TrackRunningTask(taskCts);

            BeginTaskLogCapture(task);

            try
            {
                await ExtractSingleTaskAsync(task, taskCts.Token, oneClickRun);
            }
            catch (OperationCanceledException)
            {
                task.Status = StatusText.Cancelled;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.Cancelled;
                task.ErrorMessage = "任务已取消";
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("WARN", $"任务已取消：{task.FileName}");
            }
            catch (Exception ex)
            {
                task.Status = StatusText.UnknownError;
                task.ErrorMessage = ex.Message;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressFailed;
                task.EndTime = DateTime.Now;
                task.ElapsedText = task.StartTime.HasValue
                    ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                    : "-";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}");
            }
            finally
            {
                UntrackRunningTask(taskCts);

                try
                {
                    taskCts.Dispose();
                }
                catch
                {
                }

                // 释放"最终目录占位"：这一刻目录要么已经带着产物存在（后来的任务走既有的冲突档），
                // 要么根本没建起来（后来的任务就该用它）。
                ReleaseOutputDirectoryClaim(task.OutputPath);

                /*
                 * 没成功的工作区：失败 / 取消 / 部分完成时，默认把**这一单自己的**工作区整份删掉
                 * （用户 2026-09-25 第 25 条："我不希望有这么多的失败残留"），
                 * 打开「失败时保留中间产物」才留现场；零文件空壳无论如何都删。
                 * 放在 finally 里是因为三条收尾路径（正常返回、OperationCanceledException、异常）
                 * 都要走到它 —— 尤其是取消那一条：用户点停之后留下的残留正是他最不想看到的。
                 * 成功路径不从这里走（那一支的清理在 PostProcessSuccessAsync 里按"校验通过"删）。
                 */

                /*
                 * 机器终态收口（第 44 条追加）：任务已经结束（`EndTime` 有值）而 `Outcome` 还停在 `Pending`，
                 * 说明它走的是那些"只写了中文状态、没写终态"的失败分支（实测：识别不出格式的包
                 * 状态是「解压失败」，`Outcome` 却是 `Pending`，于是批末汇总把它算进"未处理"）。
                 *
                 * 汇总 / 失败清单 / 四道删除门读的都是这一位，⛔ 一律不许靠比对中文。
                 * 判据用"没通过输出校验"这条事实，只有真正的失败才会落进来。
                 *
                 * ⚠ 这一步**刻意排在下面两件事之前**（2026-10-02 部分完成发布）：那一档只认机器终态
                 * （失败 / 部分完成），而这一位此刻还是 `Pending` 的话，一个字节都不会发布 ——
                 * 顺序错了就等于"这一档在对绝大多数失败分支上不生效"。
                 */
                FinalizeOutcomeIfPending(task);
                /*
                 * 部分完成发布（用户 2026-10-02 拍板的口径 A）：把已经解出来、逐条核对过的那部分
                 * 放进 <c>&lt;目标&gt;\&lt;包名&gt;\部分完成\</c>。**必须排在清工作区之前** ——
                 * 那些内容此刻还在这一单的暂存目录里，清掉就再也没有了（老口径正是这么丢的）。
                 * 返回值 = "内容没全发布，工作区不能清"（见 PartialPublisher.Result.AllMoved）。
                 */
                bool keepWorkspaceForUnpublished = await TryPublishPartialProductsAsync(
                    task,
                    cancelled: taskCts.IsCancellationRequested);

                CleanupFailedTaskWorkspace(task, keepWorkspaceForUnpublished);

                // 日志收尾：成功 → 只留一行摘要（细节全丢）；失败 / 取消 → 细节全吐出来再收摘要。
                EndTaskLogCapture(task);

                UpdateSummary();
            }
        }

        /// <summary>
        /// 本批「部分完成发布」生不生效（**唯一判据**）。
        ///
        /// <para>= ③页那个开关打开 **且** 本批不是「空间不足」模式。用户 2026-10-02 顾虑 2 点名的危险组合
        /// （空间不足 + 大容量 + 多份相同文件一起开）就靠这一条**硬互斥**挡住：那一档的全部价值是
        /// "定稿 + 校验通过后当场删源包回收空间"，而部分完成**天然回收不了源包**（缺的那些条目只在源包里）
        /// ⇒ 两者目的正好相反。⛔ 只让行为失效，**绝不去改用户的设置**（勾还勾着，只是这一轮不生效）。</para>
        /// </summary>
        private bool PartialPublishActive => Settings?.PartialPublishEnabled == true && !_spaceTightThisBatch;

        /// <summary>
        /// 部分完成发布的候选：**失败路径**填进来，收尾处（清工作区之前）真发布。
        ///
        /// <para>为什么用"候选"而不是就地发布：发布要读到三样只有收尾那一刻才齐的东西 ——
        /// 机器终态（失败 / 部分完成才算数）、最终输出目录（同名冲突可能刚改过名）、
        /// 以及"这一单到底还剩哪些产物"（候选循环里每个候选都会清一次产物目录）。</para>
        /// </summary>
        private sealed class PartialPublishCandidate
        {
            /// <summary>已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</summary>
            public string StagingRoot { get; init; } = string.Empty;

            /// <summary>交给引擎的那个归档（懒取清单时要用；内嵌归档这一档是抠出来的临时文件）。</summary>
            public string ArchivePath { get; init; } = string.Empty;

            /// <summary>这一层最后用的密码（懒取清单时用；只在内存里，⛔ 永不落日志）。</summary>
            public string Password { get; init; } = string.Empty;

            /// <summary>引擎点名的坏条目 / 自报的出错数 / 这一趟是不是报了失败（三道闸门）。</summary>
            public IReadOnlyList<string> EngineFailedEntries { get; init; } = Array.Empty<string>();

            public int EngineReportedErrorCount { get; init; }

            public bool EngineReportedFailure { get; init; }

            /// <summary>
            /// 逐条清单（相对路径 + 解压后字节）。**空表 = 手上没有清单** ⇒ 收尾时用这一层的密码
            /// 现问引擎一次（见 <see cref="TryListForPartialPublishAsync"/>）；再拿不到就什么都不发布。
            /// </summary>
            public IReadOnlyList<(string Path, long Size)> ManifestEntries { get; init; } =
                Array.Empty<(string, long)>();
        }

        /// <summary>
        /// 记下"这一单失败时盘上还剩什么"（部分完成发布的输入）。**失败路径调用**，成功路径一次都不调。
        ///
        /// <para>⛔ 这里刻意**不看③页那个开关**：开关在收尾那一刻才读（见 <see cref="PartialPublishActive"/>），
        /// 否则"开着空间不足模式时本开关不生效"这句说明就没机会写出来了
        /// （一个候选都没记下来 ⇒ 收尾时连"为什么没发布"都答不上）。记的都是引用，没有额外的磁盘开销。</para>
        /// </summary>
        /// <param name="stagingRoot">已解出来的东西在哪（暂存目录 / 这一层的产物目录）。</param>
        /// <param name="manifestEntries">逐条清单（递归那条路直接带过来；没有就传 null，收尾时懒取一次）。</param>
        private void RecordPartialPublishCandidate(
            ArchiveTask task,
            string stagingRoot,
            string archivePath,
            string password,
            IReadOnlyList<(string Path, long Size)>? manifestEntries,
            IReadOnlyList<string>? engineFailedEntries,
            int engineReportedErrorCount,
            bool engineReportedFailure)
        {
            if (task == null || string.IsNullOrWhiteSpace(stagingRoot))
            {
                return;
            }

            _partialPublishCandidates[task] = new PartialPublishCandidate
            {
                StagingRoot = stagingRoot,
                ArchivePath = archivePath ?? string.Empty,
                Password = password ?? string.Empty,
                ManifestEntries = manifestEntries ?? Array.Empty<(string, long)>(),
                EngineFailedEntries = engineFailedEntries ?? Array.Empty<string>(),
                EngineReportedErrorCount = engineReportedErrorCount,
                EngineReportedFailure = engineReportedFailure
            };
        }

        /// <summary>
        /// 递归那条路的登记（<see cref="RunRecursiveAsync"/> 收尾时调用）。
        ///
        /// <para>只认"**最深的那一层失败了**"这一档：那一层的产物才是"解了一半的现场"。
        /// 层成功、只是整条链按上限 / 多分支停住的那些情形**不登记** —— 它们的产物该走既有的
        /// 定稿与链尾口径（那是另一条路，⛔ 不许被这一档抢过来当成"部分完成"发布）。</para>
        /// </summary>
        private void RecordRecursivePartialPublishCandidate(ArchiveTask task, RecursionResult? result)
        {
            if (task == null || result == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return;
            }

            RecursionLayerReport? failedLayer = null;

            foreach (RecursionLayerReport layer in result.Layers)
            {
                if (!layer.Success)
                {
                    failedLayer = layer;
                }
            }

            if (failedLayer == null || string.IsNullOrWhiteSpace(failedLayer.OutputPath))
            {
                return;
            }

            /*
             * 密码传空串：这一层的候选是递归核心自己挑的，它只把**脱敏**的占位符交出来
             * （报告里绝不出现明文密码）。所以这一档要么拿现成的逐条清单（正常情形：
             * 解压前列目录那一次就是拿对的密码列的），要么什么都不发布 —— ⛔ 不去猜密码。
             */
            RecordPartialPublishCandidate(
                task,
                failedLayer.OutputPath,
                failedLayer.ArchivePath,
                string.Empty,
                failedLayer.ManifestEntries,
                failedLayer.FailedEntryNames,
                failedLayer.ReportedSubItemErrors,
                engineReportedFailure: true);
        }

        /// <summary>
        /// **部分完成发布的唯一入口**（收尾处调用）：逐条对账 → 二次空间体检 → 真搬 → 其余物/工作区收尾。
        /// </summary>
        /// <returns>
        /// true = 这一单的工作区**不能清**（有内容没能发布，它们只在那里）；false = 按既有口径清。
        /// </returns>
        /// <param name="cancelled">
        /// 这一单是不是被用户取消的（判据 = **任务自己的取消令牌**，⛔ 不是比对中文文案）。
        ///
        /// <para>为什么必须单独传这一位：单层那条取消路径（<c>ProcessExtractTaskAsync</c> 的
        /// <c>catch (OperationCanceledException)</c>）只落了中文状态、没落机器终态，而收尾那一步会把
        /// `Pending` 兜底改成 `Failed` ⇒ "取消"在终态上与"失败"分不开。拿终态当唯一判据的话，
        /// 用户按下取消之后，半成品照样会被摆进目标目录 —— 那正是他要停下来的东西。</para>
        /// </param>
        private async Task<bool> TryPublishPartialProductsAsync(ArchiveTask task, bool cancelled)
        {
            if (task == null || !_partialPublishCandidates.TryRemove(task, out PartialPublishCandidate? candidate))
            {
                return false;
            }

            if (cancelled)
            {
                return false;
            }

            if (cancelled)
            {
                return false;
            }

            /*
             * ⛔ 只认机器终态（判据是枚举，⛔ 不比中文）：失败 / 部分完成才谈得上"发布已解出的那部分"。
             * 成功走定稿那条路（早就有内容物落地了，不需要这一档）；取消 / 跳过一个字节都不许发布
             * —— 用户按了取消，他要的是"停下来"，不是"把半成品摆进目标目录"。
             */
            if (task.Outcome is not (TaskOutcome.Failed or TaskOutcome.PartiallyCompleted))
            {
                return false;
            }

            if (Settings?.PartialPublishEnabled != true)
            {
                return false;
            }

            if (_spaceTightThisBatch)
            {
                // 硬互斥（见 PartialPublishActive）。写一行 INFO 说清为什么没生效，⛔ 设置一个字都不改。
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSpaceTightBlockedFormat,
                        task.FileName));

                return false;
            }

            string packageOutputRoot = task.OutputPath;

            if (string.IsNullOrWhiteSpace(packageOutputRoot))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        "这一单没有可用的成品目录（拿不到落点），按「判不出 ⇒ 什么都不做」处理"));

                return false;
            }

            IReadOnlyList<(string Path, long Size)> manifestEntries = candidate.ManifestEntries;

            if (manifestEntries.Count == 0 && !string.IsNullOrWhiteSpace(candidate.ArchivePath))
            {
                /*
                 * 手上没有清单，现问一次引擎（**只在这一档**、只问一次）。
                 *
                 * 为什么必须补这一次：真机 `giu910` 那一单是 7z `-mhe`（文件名也加密）——
                 * 前面几个候选连目录都列不出来，而**把包解到 98% 的那个候选**从没列过目录
                 * （解压成功才会走校验、校验里才列目录，而它恰恰没成功）⇒ 不补这一问，
                 * 这一档在最需要它的那个场景上永远拿不到清单、永远不发布。
                 *
                 * 密码用的是**这一趟最后那个候选**的：密码不对时它照样列不出来（返回失败），
                 * 于是下游按"没有清单"办 —— ⛔ 绝不因为"列不出来"就放松任何一道闸门。
                 */
                manifestEntries = await TryListForPartialPublishAsync(task, candidate);
            }

            PartialPublishOutcome outcome = PartialPublishRunner.Run(
                candidate.StagingRoot,
                packageOutputRoot,
                manifestEntries,
                candidate.EngineFailedEntries,
                candidate.EngineReportedErrorCount,
                candidate.EngineReportedFailure);

            if (!outcome.Published)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        outcome.BlockedBySpace
                            ? StatusText.PartialPublishSpaceBlockedFormat
                            : StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        outcome.Reason));

                return false;
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishedFormat,
                    task.FileName,
                    outcome.PublishedCount,
                    TaskSpaceEstimate.FormatSize(outcome.PublishedBytes),
                    outcome.DestinationDirectory,
                    outcome.Verdict.Entries.Count,
                    TaskSpaceEstimate.FormatSize(outcome.Verdict.ExpectedBytes),
                    outcome.Verdict.Rejected.Count));

            if (outcome.Publish.RenamedCount > 0)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishedRenamedFormat,
                        task.FileName,
                        outcome.Publish.RenamedCount));
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishSourceKeptFormat,
                    task.FileName));

            /*
             * 把"这一单救回来多少"记在任务上（批末诊断要用它说清"不是全丢"）。
             * 判据只有写在产生它的那一刻才是事实 —— 批末那一刻发布早就做完了，绕一圈去推必然推歪。
             */
            task.PartialPublishedCount = outcome.PublishedCount;
            task.PartialPublishDirectoryPath = outcome.DestinationDirectory;

            /*
             * 全部搬成之后才动其余物与工作区（「宁可少删」）：
             * 有文件没能发布时它们还在工作区里，那是这一份唯一的线索 —— 删掉等于丢用户的产物。
             */
            if (!outcome.Publish.AllMoved)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        $"计划里有 {outcome.Publish.Failures.Count} 个文件没能发布（{DescribeFirstFailures(outcome.Publish.Failures)}），"
                        + "所以其余物与工作区都原样留着"));

                return true;
            }

            PurgeRestItemsExceptChainRootSource(task);

            return false;
        }

        /// <summary>
        /// 懒取清单（**只服务部分完成发布**）：拿最后那个候选的密码问一次引擎。
        /// 列不出来（密码不对 / 加密头 / 引擎不可用）时返回空表 ⇒ 下游按"没有清单"办、什么都不发布。
        /// </summary>
        private async Task<IReadOnlyList<(string Path, long Size)>> TryListForPartialPublishAsync(
            ArchiveTask task,
            PartialPublishCandidate candidate)
        {
            try
            {
                ArchiveListResult list = await _archiveEngine.ListAsync(
                    ArchiveRequest.For(candidate.ArchivePath, candidate.Password),
                    CancellationToken.None);

                return PartialPublishPlanner.ToManifestEntries(list);
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishSkippedFormat,
                        task.FileName,
                        $"为了逐条核对清单又列了一次目录，这次列目录出错了（{ex.Message}）—— 判不出 ⇒ 什么都不发布"));

                return Array.Empty<(string, long)>();
            }
        }

        /// <summary>把发布失败的头几条拼成一句话（最多 3 条，详情在日志里，⛔ 不刷屏）。</summary>
        private static string DescribeFirstFailures(IReadOnlyList<(string Path, string Reason)> failures)
        {
            if (failures == null || failures.Count == 0)
            {
                return "原因没有记下来";
            }

            var parts = new List<string>();

            foreach ((string path, string reason) in failures.Take(3))
            {
                parts.Add($"{path}：{reason}");
            }

            if (failures.Count > 3)
            {
                parts.Add($"还有 {failures.Count - 3} 个");
            }

            return string.Join("；", parts);
        }

        /// <summary>
        /// 部分完成收尾：把其余物里**除这条链最外层源包以外**的项删掉（用户 2026-10-02 改的口径）。
        ///
        /// <para><b>为什么可以删</b>：其余物里装的都是过程物（内层包、抠出来的内嵌归档副本），
        /// 全都能从最外层源包重新解出来；用户原话是「我建议源包 + 已解出的内容物留着，其他的都删掉」——
        /// 这样盘上正好是"两份"，与③页小字那句承诺对得上。</para>
        ///
        /// <para><b>⛔ 安全闸门（这一条我来守）</b>：只有"这条链的最外层源包**此刻还在盘上**"时才动手；
        /// 它不在了（已被删 / 路径失效）⇒ **一个字节都不删**（判不出 ⇒ 什么都不做）——
        /// 源包是唯一能重建整条链的东西，它都没了还清过程物，等于把最后一份可恢复路径也删掉。</para>
        /// </summary>
        private void PurgeRestItemsExceptChainRootSource(ArchiveTask task)
        {
            string rootSource = task.ChainRootIdentity;

            if (string.IsNullOrWhiteSpace(rootSource))
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        "算不出这条链的最外层源包是谁"));

                return;
            }

            string rootSourceFull = SafePathHelper.GetFullPathSafe(rootSource);

            if (rootSourceFull.Length == 0 || !SafePathHelper.FileExists(rootSourceFull))
            {
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        $"这条链的最外层源包不在盘上了（{rootSource}）—— 它是唯一能把整条链重新解出来的东西，"
                        + "所以其余物里的过程物一个都不删"));

                return;
            }

            if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                // 这一单压根没建过其余物（部分完成这一档不生成其余物）⇒ 没事可做，不写日志。
                return;
            }

            RestPurgeOutcome purge = new RestItemPurger(
                    null,
                    null,
                    ContentKeepRules.FromSettings(Settings))
                .PurgeExcept(
                task,
                new[] { rootSourceFull },
                DeleteMode.Permanent);

            AppendLog(
                purge.Succeeded ? "INFO" : "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    purge.Succeeded
                        ? StatusText.PartialPublishRestPurgedFormat
                        : StatusText.PartialPublishRestKeptFormat,
                    task.FileName,
                    purge.EntryCount,
                    purge.Message));
        }

        /// <summary>
        /// 失败 / 取消 / 部分完成收尾时清理**这个任务自己的工作区**（用户 2026-09-25 第 25 条）。
        ///
        /// <para><b>默认档（<see cref="AppSettings.KeepFailedWorkspace"/> = false）：整份删掉。</b>
        /// 用户原话："我不希望有这么多的失败残留……如果解压 40G，两层，解压失败有 80G 的卸载残留，
        /// 用户不得气死……失败了就失败了，成功了就成功了"。删掉的是 <c>&lt;work&gt;\&lt;taskId&gt;</c>
        /// 整棵（含 <c>stage</c>、抠出来的内嵌归档副本、已解出的过程物）。
        /// <b>零文件空壳无论如何都删</b>（没有现场可留，这条与设置无关；
        /// 它就是原来的"空壳当场清"，现在并进这一条）。</para>
        ///
        /// <para><b>打开 <see cref="AppSettings.KeepFailedWorkspace"/> 时保持老行为</b>：
        /// 有文件就保留 + 一条 INFO 说清在哪、几个、多大（③ 页扫得到、能清）。</para>
        ///
        /// <para><b>四条边界</b>（这是删目录，每条都要有）：</para>
        /// <list type="number">
        /// <item><description>成功的任务不走这里（成功路径的清理是 <see cref="CleanupTaskWorkspaceDirectory"/>，
        /// 它按"校验通过"删，口径不同，不许两处都插一脚）；</description></item>
        /// <item><description>目录必须在**当前生效的工作区根**之下（容器内校验，越界只写 WARN、什么都不删）；
        /// 而且**只认记下来的那一个目录**，绝不按名字 / 按"最新"去扫工作区根 —— 并发跑两个任务时，
        /// 扫描式删除会把对方正在写的工作区端掉；</description></item>
        /// <item><description>目录里除 <c>stage</c> 之外不许有别的子目录（与 <see cref="CleanupTaskWorkspaceDirectory"/>
        /// 同一道边界：出现别的子目录说明它不是我们造的那个目录，一个字节都不碰）；</description></item>
        /// <item><description>删不掉（被占用 / 权限）只写 WARN —— 一次清理失败绝不该改写任务结论；
        /// 源包、已经定稿搬出去的内容物、<c>&lt;数据根&gt;</c>（日志 / 密码列表 / 设置）一个字节都不动。</description></item>
        /// </list>
        /// </summary>
        /// <param name="keepWorkspace">
        /// true = 这一单**不许清**（部分完成发布时"有文件没能发布"的那一档：没搬走的内容只在这里，
        /// 删掉等于丢用户的产物）。判据由 <see cref="TryPublishPartialProductsAsync"/> 给
        /// （<c>PartialPublisher.Result.AllMoved</c>），⛔ 不在这里自己猜。
        /// </param>
        private void CleanupFailedTaskWorkspace(ArchiveTask task, bool keepWorkspace = false)
        {
            if (task == null)
            {
                return;
            }

            _taskWorkspaceDirectories.TryRemove(task, out string? recorded);

            // 递归核心的逐层工作区不在这个目录里，单独按同一份判据处理（见下面那个方法）。
            _taskRecursiveExtractors.TryRemove(task, out RecursiveExtractor? recursiveExtractor);

            bool concludedSuccess =
                string.Equals(task.Status, StatusText.ExtractSuccess, StringComparison.Ordinal) &&
                task.Outcome != TaskOutcome.PartiallyCompleted;

            if (concludedSuccess)
            {
                /*
                 * 成功：这一支的清理在 PostProcessSuccessAsync 里（按"校验通过"删），
                 * 递归核心的工作区也由它自己在 FinalizeRun 里清掉了。这里什么都不做。
                 */
                return;
            }

            if (keepWorkspace)
            {
                /*
                 * 部分完成发布时"有文件没能发布"的那一档：工作区原样留着。
                 * 两层理由：① 没搬走的内容只在这里（删掉就是丢用户的产物）；
                 * ② ③页的「失败时保留中间产物」本来就是"要留现场"，这一档比它更强 ——
                 *    现场里躺着的是**已经解出来、核对过**的真内容，不是中间垃圾。
                 * 递归逐层工作区同样不动（那些层的内容可能正是下一份要发布的）。
                 */
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        "有内容没能按「部分完成」发布，工作区与递归逐层工作区都原样留着（那是它们唯一的落点）"));

                return;
            }

            CleanupFailedRecursionWorkspace(task, recursiveExtractor);

            string taskDirectory = string.IsNullOrWhiteSpace(recorded)
                ? _pathService.BuildTaskWorkDirectory(task)
                : recorded;

            if (string.IsNullOrWhiteSpace(taskDirectory) || !Directory.Exists(taskDirectory))
            {
                return;
            }

            string workRoot = _pathService.WorkDirectory;

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string guardReason))
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：要清的工作区目录不在工作区根之下，已跳过 —— {guardReason}：{taskDirectory}");

                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                /*
                 * 第二道容器内校验：目录里只许有我们自己造的子目录（判据本体在 Extraction 层的纯函数里，
                 * 递归那条路转调同一个 —— 2026-10-05 只读审计）。
                 */
                string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
                    subdirectories,
                    IsOurWorkspaceSubdirectory);

                if (foreign != null)
                {
                    AppendLog(
                        "WARN",
                        $"{task.FileName}：工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清：{taskDirectory}");

                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：读不了工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");

                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            /*
             * 有东西 + 用户要求留现场 → 保留，并且说清它在哪、几个、多大：
             * 这一行既是"东西还在不在"的答案，也是 ③ 页那条"工作区残留"的来处。
             * 零文件空壳不走这一支（空目录不占空间、也没有内容可看，留着只会在 ③ 页里多一行噪声）。
             */
            if (fileCount > 0 && Settings?.KeepFailedWorkspace == true)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceKeptOnFailureFormat,
                        task.FileName,
                        fileCount,
                        TaskSpaceEstimate.FormatSize(totalSize),
                        taskDirectory));

                return;
            }

            try
            {
                Directory.Delete(taskDirectory, recursive: true);

                AppendLog(
                    "INFO",
                    fileCount == 0
                        ? string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceEmptyShellRemovedFormat,
                            task.FileName,
                            taskDirectory)
                        : string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceClearedOnFailureFormat,
                            task.FileName,
                            fileCount,
                            TaskSpaceEstimate.FormatSize(totalSize),
                            taskDirectory));
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    fileCount == 0
                        ? string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceEmptyShellRemoveFailedFormat,
                            task.FileName,
                            ex.Message,
                            taskDirectory)
                        : string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceClearOnFailureFailedFormat,
                            task.FileName,
                            ex.Message,
                            taskDirectory));
            }
        }

        /// <summary>
        /// 没成功（失败 / 取消 / 部分完成）时，按同一份设置清掉**递归核心的逐层工作区**
        /// （<c>&lt;work&gt;\recursive\&lt;taskId&gt;</c>，双层包里最占地方的那一份）。
        ///
        /// <para><b>为什么必须单独处理这一处</b>：它与任务工作区是两个目录 —— 递归核心的根是
        /// <see cref="RecursiveExtractor.ConfiguredWorkspaceRoot"/> 之下再挂一层 <c>recursive</c>。
        /// 只清任务工作区的话，双层包失败之后那几百 MB / 几十 GB 的逐层产物还会留在盘上，
        /// ③ 页那句"默认不会留下东西"就成了假话。</para>
        ///
        /// <para><b>什么时候不动</b>：① 这一单成功了（递归核心自己在 <c>FinalizeRun</c> 里清）；
        /// ② 用户打开了「失败时保留中间产物」；③ 这个任务根本没走递归（字典里没有它）。
        /// 删除本身的安全校验（容器内校验 + 只认本实例持有的那一个目录 + 删不掉只写 WARN）
        /// 全在 <see cref="RecursiveExtractor.TryDiscardCurrentWorkspaceOnFailure"/> 里，
        /// 这里只负责"该不该清"这一个判断。</para>
        /// </summary>
        private void CleanupFailedRecursionWorkspace(ArchiveTask task, RecursiveExtractor? recursiveExtractor)
        {
            if (recursiveExtractor == null || Settings?.KeepFailedWorkspace == true)
            {
                return;
            }

            recursiveExtractor.TryDiscardCurrentWorkspaceOnFailure(task.FileName);
        }

        /// <summary>
        /// 这个落点目录里**除了工作区自己那棵树之外**还有没有东西。
        ///
        /// <para>⛔ 判据必须排除工作区（用户 2026-09-30）：工作区默认就建在目标目录里面
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），于是目标目录**永远是"非空"的** ——
        /// 不排除的后果是每一个任务都被判成"输出目录已存在且非空"：按「询问」档会停下来问用户，
        /// 按自动改名档则把成品落进 <c>包名(1)</c>（用户看到的是一堆莫名其妙的 (1) 目录）。
        /// 判据唯一出口 <see cref="WorkspaceTree"/>。</para>
        /// </summary>
        private bool HasProductEntriesOutsideWorkspace(string directory)
        {
            /*
             * 扫描期在批中，工作区根已经由批首定好；不过这里刻意**不只在有根时才排**：
             * WorkspaceTree 的名字那一条（点开头的 .ArchiveFixer.work）与根无关，
             * 所以哪怕读不到当前根，默认档那一层照样被排除。
             */
            string workRoot = _pathService.WorkDirectory;

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (!WorkspaceTree.ShouldSkipEntry(entry, workRoot))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                /*
                 * 读不了目录：按"有东西"处理（与既有各处同一口径）—— 那一档只会走冲突询问 / 自动改名，
                 * 绝不覆盖、绝不删任何东西，落在安全的一侧。
                 */
                return true;
            }
        }

        private async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken, bool oneClickRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            // 每个任务开始：清掉"这一个"的冲突记账（"全部 X"是本批级的，不清）。
            ResetTaskConflictState();

            /*
             * 上一轮的"提示类"结论先清掉。
             *
             * 为什么必须清：任务对象是**复用**的（同一个包重试、或用户再点一次），
             * 上一轮算出来的"本包含 N 个可执行文件""路径过长"如果不擦掉，
             * 这一轮换了密码/换了输出目录之后会继续挂着 —— 用户看到的是一句**已经不成立**的结论。
             * 它们是"提示"不是状态，所以只重置这两个字段，不碰 Status / ErrorMessage（那是下面按流程写的）。
             */
            task.DangerousEntriesWarning = string.Empty;
            task.PathLengthWarning = string.Empty;

            /*
             * ===== 分卷名被伪装时，解压前先把整组改回标准名 =====
             *
             * 用户 2026-09-28 的口径：「一键处理的功能是啥，就是我按一下你全部搞定，这些必要的操作肯定是要的」。
             * 7-Zip **只认 `<基名>.001/.002`… 这套精确名字**（实测：只把第二卷改成 `.002删除`，
             * `7z x` 直接 `Unexpected end of archive`），名字不标准就必须先改，否则这一单必然失败。
             *
             * 位置在**任何引擎调用之前**，且早于源快照（改完名字再拍快照，后面各层比对的都是新名字）。
             * 只改名字、内容一个字节不动；凑不成组 / 目标名被占 → 什么都不做，并写清为什么。
             * 手动档「修复分卷名并重试」调的是**同一对**判据与执行体（VolumeNameRepair）。
             */
            await NormalizeDisguisedVolumeNamesAsync(task, cancellationToken).ConfigureAwait(true);

            /*
             * ===== 一组分卷 = 一个任务 = 只从**首卷**启动 =====
             *
             * 用户 2026-10-01 真机（`AAA` 那批）：盘上出现三棵一模一样的产物树，外加
             * `111(1)` / `333-Rar4(1)`。根因不在解压，在**归组只做过一次** ——
             * 扫描期 `FileScanService` → `VolumeGroupingService.ApplyVolumeGrouping` 按
             * **文件名**把同组的多卷合成一行，可这一批的名字是被网盘改坏的
             *（`111.parts1.racr` / `111.part删2.ra除r`），那次归组认不出来 ⇒ 同一个包的两卷各占一行。
             * 等开工前的自动改名把它们改成标准名（`111.part1.rar` / `111.part2.rar`）之后，
             * **没有任何一处重新归组** —— 于是两行都去解压：
             *
             *   ① 后续卷那一单必然失败（7-Zip：`Missing volume` / 引擎：`这是分卷压缩包的后续卷`）；
             *   ② 它失败以后，整组的**链尾处理**被"链上有任务没成功"拦住 ⇒ 源包一个都不删；
             *   ③ 两个任务各自算落点 ⇒ 第二棵产物树落成 `名字(1)`（"绝不覆盖"那条路是给
             *      **不同来源**准备的，同一个包的两卷不该走到那里）。
             *
             * 这里落的口径与手动档「我手动指定缺失卷所在目录」那一段**完全相同**
             *（同样是 `ApplyGroupInfo` + 起点用 `group.FirstVolumePath`）：
             * 一组分卷 = 一个任务 = 从首卷启动，其余卷跟着整组一起被处理。
             *
             * 位置：改名之后（名字才是判据）、快照与任何引擎调用之前（跳过的这一单不拍快照、
             * 不碰盘、不产生任何结论）。
             */
            if (SkipWhenAnotherTaskOwnsThisVolumeGroup(task))
            {
                return;
            }

            /*
             * ===== 不变量 11 的**第一道**（也是唯一收口的那一道）：源文件变化 = 立刻停下 =====
             *
             * 位置刻意放在本方法**最前面**，理由有三条，缺一条都会漏：
             * ① 这里是所有解压入口的唯一收口（一键处理 / 手动「只解压」/ 单包重试 / 危险模式自测）；
             * ② 必须在**任何引擎调用之前** —— 下面的"分卷缺失"里会请引擎帮忙列目录、
             *    "格式未知"那条路也会让引擎试列一次，一旦引擎碰过这个包，
             *    后面再拦就已经"用过旧识别结果"了（不变量 11 要防的正是这个）；
             * ③ 必须在**任何写盘之前**：此刻还没有暂存目录、没有输出目录、没有其余物，
             *    拦下来就是"什么都没发生"（不变量 1 的红线在这种情形下照旧成立）。
             *
             * 没有基准的老任务在这里补拍（见 EnsureSourceSnapshot），不会被误拦。
             */
            EnsureSourceSnapshot(task);

            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * 分卷缺失时**不许开始**（AGENTS.md §6 第 7 条）。
             * 理由：分卷包里每一卷都是必需的数据片，缺一卷 7z 必然失败，
             * 让它跑一遍只会浪费用户时间、还可能留下半截输出目录；
             * 直接说清"缺哪几个卷"才是用户能行动的信息。
             *
             * 「我手动指定缺失卷所在目录」（WinRAR 参考 §2 G 组 / §3 第 2 条采纳项）：
             * 先给用户**一次**补救机会 —— 卷常常就散在隔壁文件夹或另一个盘，
             * 选中那个目录后重新归组再判定。
             * ⛔ 不变量 7 **一个字都不放松**：重新归组之后仍然缺，就仍然**不启动**。
             * 出口只是"帮你把卷找齐"，绝不是"缺卷也允许开始"。
             */
            if (task.IsVolumeGroup && !task.IsVolumeComplete)
            {
                /*
                 * 一键处理期间**不问**"缺失卷在哪个目录"（用户 2026-09-27：一键解压不许弹窗）。
                 * 手动档问一次（用户就在旁边），一键档直接按"不问"往下走 ——
                 * ⚠ 但结论**不再当场落死**了，见下面那两段（用户 2026-10-05 口径 2）。
                 */
                bool repaired = !oneClickRun
                    && await TryRepairMissingVolumesAsync(task, cancellationToken);

                if (!repaired)
                {
                    /*
                     * ===== 补齐了就先跑（用户 2026-10-05：「补齐了就收拢 + 正常跑这一组」）=====
                     *
                     * `task.IsVolumeComplete` 是**扫描期**（或者上一轮）算出来的账，可能已经过期：
                     * 同一批里另一个包刚把缺的那一卷解出来、或者开工前那次跨目录收卷刚把整组收齐 ——
                     * 那种时候照旧报"缺卷"就是拿一份过期读数把本来能解的包挡下来。
                     * 所以这里**现读一次盘上事实**（既有出口 `RebuildVolumeGroup`）：
                     * 真齐了就把账更新掉、照常往下解（⛔ 不变量 7 没被绕过 —— 这一组现在真的齐了）。
                     */
                    VolumeGroup? regrouped = ResolveCompleteGroupFromDisk(task);

                    if (regrouped != null)
                    {
                        string regroupedFirst = regrouped.FirstVolumePath;

                        new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, regrouped);

                        if (!string.Equals(
                                SafePathHelper.GetFullPathSafe(task.CurrentPath),
                                SafePathHelper.GetFullPathSafe(regroupedFirst),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                            task.CurrentPath = regroupedFirst;
                        }

                        task.CaptureSourceSnapshot();

                        AppendLog(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeDeficitRecheckResolvedFormat,
                                task.FileName,
                                task.VolumeInfoText));

                        // 补齐了 ⇒ **照常往下解**（不 return：下面还有快照重拍与真正的解压）。
                    }
                    else
                    {
                        /*
                         * ===== 缺卷**不许在批首一次判死**（用户 2026-10-05 口径 2）=====
                         *
                         * 旧写法在这里当场写一句"分卷不完整 …… 本次不开始"并把这一单落成失败 —— 真机上
                         * `.002` 就是这么在 16:29:33（批首那一刻）被判死的，而它的 `.001` 正躺在同一棵树里、
                         * 由同一批的另一个包解出来。
                         *
                         * 现在的口径：这一单**现在确实没有开始解压**（一个字节都没动，不变量 7 一个字节没放松），
                         * 但"还缺哪几卷"这句话留到最后再说 —— 先记进名单，等这一批的解压都跑完之后由
                         * `RecheckDeferredVolumeDeficits` 再判一次（那时缺的那几卷可能已经被解出来 / 收拢到位）。
                         *
                         * ⚠ 兜底：批末补判**必须**给名单里每一条一个结论（补齐 ⇒ 跑；仍然缺 ⇒ 如实报缺卷）。
                         * 唯一"没有结论"的出口是"整条链跑到一半被用户停下 / 撞上轮数上限" —— 那时它留在
                         * 「未处理」那一档，如实反映"这一批没轮到它"。
                         */
                        if (!_volumeDeficitFinalPass.Contains(task))
                        {
                            RecordDeferredVolumeDeficit(task, DescribeMissingVolumeNames(task));
                            return;
                        }

                        /*
                         * ===== ① 解前预检：**缺卷就别开解，而且要直接说清缺哪一卷 / 为什么这样判** =====
                         *
                         * 走到这里 = 批末补判已经跑过它一遍、盘上仍然凑不齐 ⇒ **这才如实报缺卷**。
                         *
                         * 判据走唯一出口 <see cref="VolumeGroupResolver"/>（分卷组装判定器）：
                         * 它把六条证据（基名 / 卷号 / 体积 / 位置 / 物理同一性 / 试开）收在一处，
                         * 只吐一个结论。试开那一档在**这里**做（有引擎、且是只读的硬链接试开）——
                         * 于是"末卷被改名成 一只顶美.z删除ip"这种情形能被认出来是**齐的**，
                         * 而不是像老判据那样一律报「分卷缺失」把用户指去满盘找卷。
                         *
                         * ⛔ 不管判出什么，**都不改变"这一单开不开"**：名字不标准 ⇒ 7-Zip 按原名打不开 ⇒
                         * 照旧不开始（不变量 7）。这里换掉的只是**诊断**：说得对不对、依据在不在。
                         */
                        VolumeGroupResolution volumeVerdict = await ResolveVolumeGroupAsync(task, cancellationToken)
                            .ConfigureAwait(true);

                        string diagnosis = BuildVolumeVerdictMessage(task, volumeVerdict);

                        /*
                         * 判据走**开工前拦下的统一收口**（与"源文件已变化"同一个出口）：
                         * 状态照旧是「分卷缺失」（中文一个字不改），但"这一单已经结束"这件事必须一起落 ——
                         * 少了结束时间，机器终态的唯一收口补不了 Failed，它就一直停在 Pending：
                         * 批末汇总说「未处理 1」、批末诊断说「分卷缺失」，两处口径当场打架
                         * （2026-09-30 真机 `222.z01`）。
                         */
                        MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, diagnosis);

                        AppendLog("ERROR", $"分卷缺失，未开始解压：{task.FileName}，{diagnosis}");
                        LogVolumeGroupEvidence(task, volumeVerdict);
                        return;
                    }
                }

                /*
                 * 用户刚把缺的卷找回来了（或者刚由"现读一次盘上事实"确认它已经齐了）：
                 * 基准必须**跟着重拍**（不变量 11 与这条补救出口的和解）。
                 *
                 * 不重拍的话，新找回来的那一卷不在快照里，比对时它会被算成"新出现的文件" →
                 * 用户按提示把卷补齐了，反而被"源文件已变化"拦下，而且再补多少次都一样 ——
                 * 那句提示就成了死循环。补齐分卷是**改变源文件组**的正当操作，
                 * 所以基准从这里重新开始（与"重新扫描"同一条道理）。
                 */
                task.CaptureSourceSnapshot();

                AppendLog("INFO", $"{task.FileName}：分卷已补齐，源文件快照重新记录（{task.VolumeCount} 卷）。");
            }

            bool tryExtractUnknown = Settings.UnknownFormatAction == "TryExtract";

            if (!task.IsArchive || task.DetectedFormat == "Unknown")
            {
                if (tryExtractUnknown)
                {
                    AppendLog("WARN", $"格式未知：{task.FileName}，已开启“尝试解压未知格式”，继续尝试。");
                }
                else
                {
                    /*
                     * 魔数说不认识，**不等于"它不是压缩包"** —— 只说明文件头不在我的签名表里。
                     * 基线里早就写下了正确原则：最终以 7-Zip 的结论为准。
                     *
                     * 所以这里让引擎亲自试一次列目录（只对用户勾选的任务做，代价是一次进程调用）：
                     *   · 列得出来 → 就是能解的容器（自解压安装器、签名表里没有的格式都算），继续处理；
                     *   · 列不出来 → 才跳过，并把"7-Zip 也打不开"写进原因，而不是含糊的"格式未知"。
                     */
                    ArchiveListResult probe = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(task.CurrentPath, string.Empty),
                        cancellationToken);

                    if (probe.Success && probe.FileCount > 0)
                    {
                        task.IsArchive = true;
                        task.EngineVerdict = $"{_archiveEngine.DisplayName} {_archiveEngine.Version} 能打开：{probe.FileCount} 个文件";

                        AppendLog("INFO", $"{task.FileName}：文件头不在签名表里，但 7-Zip 能打开（{probe.FileCount} 个文件），继续处理。");
                    }
                    else
                    {
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressCompleted;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(probe.Message)
                            ? "不是压缩包：7-Zip 也打不开"
                            : $"不是压缩包：7-Zip 打不开（{probe.Message}）";

                        AppendLog("WARN", $"跳过：{task.FileName} —— {task.ErrorMessage}");
                        return;
                    }
                }
            }

            if (!File.Exists(task.CurrentPath))
            {
                /*
                 * 与"缺卷预检 / 源文件已变化"同一个收口：这一单根本没开始解压，
                 * 所以状态、原因、**结束时间**一次落全（少了它，机器终态永远停在 Pending，
                 * 汇总把它算进「未处理」——与缺卷那一档同一个缺陷）。
                 */
                MarkStoppedBeforeExtract(task, StatusText.ExtractFailed, "文件不存在");

                AppendLog("ERROR", $"文件不存在：{task.CurrentPath}");
                return;
            }

            task.StartTime = DateTime.Now;
            task.EndTime = null;
            task.ElapsedText = "-";
            task.Operation = StatusText.OpPrepare;
            task.Status = StatusText.WaitingExtract;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;

            /*
             * 上一轮的**校验结论与机器终态**也必须清干净（任务对象是复用的：重试 / 再点一次）。
             *
             * 为什么不能只清 ErrorMessage：它们现在是"删源 / 搬源 / 续解 / 危险模式删除"的判据。
             * 一个上一轮成功过的任务，如果这一轮在开工前就被拦下（分卷缺失 / 源文件变化），
             * 留着上一轮的 `Passed` + `Succeeded` 会让那四条裁决读到一个**已经不成立的事实** ——
             * 那正是本次要根治的那类错误（拿旧结论决定不可逆动作）。
             */
            task.OutputVerification = OutputVerificationOutcome.NotAttempted;
            task.Outcome = TaskOutcome.Pending;
            task.VerifyMessage = string.Empty;

            // 上一轮"定稿搬运失败"那个事实位也要清掉（批末诊断读它，留着会把这一轮指错方向）。
            task.CommitMoveFailed = false;

            // 同理：上一轮"其余物为什么留着"那句话也要清掉（这一轮它会重新写）。
            task.RestKeptReason = string.Empty;

            task.LastUpdatedTime = DateTime.Now;

            // 选项构造收口在 BuildExtractOptions：确认框的"内容物会生成在…"必须与这里用**同一份字段**算。
            ExtractOptions extractOptions = BuildExtractOptions(tryExtractUnknown);

            /*
             * 「本次选项」的运行期覆盖（规格 §9.2 硬要求①/⑥）。
             *
             * 位置刻意放在**这里**：ExtractOptions 是落点推导的入口，覆盖写在它上面，
             * 下面那条唯一的推导链（BuildOutputPath → OutputPlacement.FromLegacyFlags →
             * ResolveDestinationDirectory）一个字都不用改 —— 面板不新增第二条拼路径的实现。
             * 内层包（IsContinuationTask）的落点由父任务给定，覆盖对它没有影响。
             */
            if (RunOptions != null)
            {
                RunOptions.ApplyTo(extractOptions);
            }

            /*
             * 任务上留一份"为什么落这儿"（§9.2 硬要求⑥ / 特定解压的用户 2026-09-24 要求）：
             * 失败清单第二级与「复制任务信息」读它。
             *
             * ⚠ 特定解压那一句**不能只在有本次选项快照时才写**：手动「只解压」路径没有快照
             * （RunOptions 为 null），但用户开了特定解压时同样要能回答"这次用的是哪条特定规则"。
             * 两段都空时写空串（把上一轮留在同一个任务对象上的旧依据清掉 —— 任务对象是复用的，
             * 留着旧依据会让用户读到一条已经不成立的解释）。
             */
            string runOptionsNote = RunOptions?.Describe() ?? string.Empty;
            string specialExtractionNote = SpecialExtractionPlan.FromSettings(Settings).Describe();

            task.RunOptionsNote = specialExtractionNote.Length == 0
                ? runOptionsNote
                : runOptionsNote.Length == 0
                    ? specialExtractionNote
                    : runOptionsNote + "；" + specialExtractionNote;

            /*
             * 落点（唯一实现：PathService.ResolveOutputPlacement → OutputPlacement）。
             *
             * ⚠ 2026-09-27 起两件事变了：
             * ①**场景 B 塌缩退役**（用户："如果 111\222\ 那层里还有很多别的东西呢，不就混乱了吗"）→
             *   不再扫目录、不再塌缩，一律多一层包名目录
             *   （顺带：原来为它服务的那次"目录里还有没有别的包"的扫描与 `SourceFolderScanService`
             *   一起删掉了 —— 落点解析重新是纯函数，UI 线程上也没有磁盘活了）；
             * ②手动档「解压到当前文件夹」走 `extractOptions.ExtractIntoSourceFolder`（内容物直接落源包那一层）。
             */
            OutputPlacementResult placement = _pathService.ResolveOutputPlacement(
                task,
                extractOptions);

            string requestedOutputPath = placement.DestinationDirectory;
            string outputPath = requestedOutputPath;
            string outputRedirectNote = string.Empty;

            /*
             * 落点为什么在这一层（用户 2026-10-05 口径："落在**第一卷的位置**"，而且他点名了现场：
             * "你又把 `111.zip` 移到 `111(4)`，然后你当时又在 `111(4)` 里面解压的"）。
             *
             * 只在"这一组第 1 卷与这一单自己的文件不在同一层"时补这一行 —— 那一档正是用户会问
             * "为什么解到这里"的形状（真机 `CCCC`：第 1 片 `111.z0删除1` 在 `111(2)\`，而这一单自己的文件
             * 还散在别处；产物因此落在 `111(2)\111`）。判据与落点推导共用同一个出口
             * （`PathService.ResolveOutputPlacement` → `Detection.GroupVolumeDirectory`），⛔ 这里不另算一遍。
             */
            if (task.IsVolumeGroup
                && placement.Success
                && !string.IsNullOrWhiteSpace(outputPath))
            {
                var groupMembers = new List<string> { task.CurrentPath };
                groupMembers.AddRange(task.VolumePaths.Where(path => !string.IsNullOrWhiteSpace(path)));

                Detection.GroupVolumeDirectory.Entry entry =
                    Detection.GroupVolumeDirectory.Resolve(groupMembers);

                if (entry.Path.Length > 0
                    && entry.Directory.Length > 0
                    && !SafePathHelper.PathEquals(entry.Directory, FileNameHelper.GetDirectoryName(task.CurrentPath)))
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeGroupFirstVolumeLandingFormat,
                            task.FileName,
                            outputPath,
                            Path.GetFileName(entry.Path),
                            Path.GetFileName(entry.Directory)));
                }
            }

            /*
             * 落点算不出来时必须**当场停下**（必修项，2026-09-21）。
             *
             * 旧实现会把空的"自定义输出目录"回落成程序安装目录，于是用户的文件被解进 exe 所在的那个目录 ——
             * 既找不到、又可能污染程序自己的 data。现在 BuildOutputPath 返回空串，
             * 这里给出明确状态与一句话原因，让用户去设置里选一个输出目录。
             */
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                // 同上面两处：这一单到这儿就结束了（引擎一次都没被调用），结束时间必须一起落。
                MarkStoppedBeforeExtract(
                    task,
                    StatusText.ExtractFailed,
                    "输出目录无效（未设置或不可用），请在设置里选择输出目录后重试");

                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            /*
             * 落点是不是"源包自己的目录"。
             * 现在只剩一种成因：手动档「解压到当前文件夹」（用户 2026-09-27）——
             * 场景 B 塌缩已经退役，别的落法都会多一层包名目录。
             * 这条事实决定"目录已存在且非空就改名 xxx(1)"要不要让开，
             * 理由见下面那个分支上的说明；判定本身只有一处实现（OutputPlacement）。
             */
            bool landsInSourceDirectory = OutputPlacement.LandsInSourceDirectory(task.CurrentPath, outputPath);

            // 共用落点（"添加文件夹 + 指定位置"）：别的包也会落进这一层，所以两处"改名让开"都要算上它。
            bool sharedOutputRoot = placement.SharesDestinationWithOtherPackages;

            try
            {
                /*
                 * 「输出目录已存在且非空 → 自动改名成 xxx(1)」只对**最外层源包**做。
                 *
                 * 为什么续解出来的内层包必须跳过这一步：它的落点是父任务**正在用**的那个目录
                 * （内容物 + 其余物都在里面），对它改名等于把第二层解到旁边的 xxx(1) 去 ——
                 * 那正是用户抱怨的"多弄了四个文件夹、文件一多根本分不清"。
                 * 同名冲突也不会因此丢东西：内层产物进目录时由定稿搬运的 AutoRename 兜住（绝不覆盖）。
                 */
                if (task.IsContinuationTask)
                {
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：内层包，产物归入父任务输出目录 {outputPath}（不再另建目录）。");
                }
                else if (landsInSourceDirectory)
                {
                    /*
                     * 落点就是**源包所在目录**（手动档「解压到当前文件夹」）
                     * → 上面那条"已存在且非空就改名 xxx(1)"必须让开。
                     *
                     * 为什么：那个目录**必然非空** —— 源包自己就躺在里面。照旧规则一改名，
                     * 用户点的"解压到当前文件夹"当场被抵消，产物落到旁边的 名字(1)\，
                     * 正是这一轮要根治的"凭空多一层目录"。
                     *
                     * 让开之后会不会和既有文件混在一起：不会丢东西。定稿搬运对同名条目一律
                     * AutoRename（绝不覆盖），而且这条规则的本意是"别把产物倒进一个已有内容的目录"，
                     * 可这里那个目录**本来就是这次要整理的目标**。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：落点就是源包所在目录 {outputPath}（解压到当前文件夹 / 就地整理），不套用“目录已存在就改名”的规则。");
                }
                else if (sharedOutputRoot)
                {
                    /*
                     * 「添加文件夹 + 指定位置」那一档：同一个文件夹里的包都落进 BBB\222\（用户 2026-09-24 第 13 条）。
                     *
                     * 第一个包定稿之后这个目录就"存在且非空"了，照旧规则会把第二个包改名成 222(1)\ ——
                     * 用户要的"都放进 BBB\222\"当场被拆开。所以这里同样让开：
                     * 同名条目由定稿搬运的 AutoRename / 用户选的冲突档兜住，绝不覆盖（不变量 3）。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：落点是本次导入共用的目标目录 {outputPath}（添加文件夹 + 指定位置），" +
                        "不套用“目录已存在就改名”的规则。");
                }
                else if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    HasProductEntriesOutsideWorkspace(outputPath))
                {
                    /*
                     * 目标目录已存在且非空 = 第一次撞上"目标已存在"的冲突。
                     *
                     * ⚠ 这里以前**无条件**自动改名成 xxx(1) —— 于是设置里那一档「询问」在落点上
                     * 从来没有兑现过：用户选了询问，程序静默改名。现在一律走 ResolveConflictAsync：
                     * · 非 Ask 档：按档位直接算（默认 AutoRename = 老行为，绝不覆盖）；
                     * · Ask 档：第一次冲突时暂停该任务、聚合问一次（覆盖 / 跳过 / 自动重命名，可对整批生效）。
                     */
                    ConflictResolution resolution = await ResolveConflictAsync(
                        task,
                        ConflictTargetKind.Directory,
                        outputPath,
                        $"输出目录已存在且非空，{task.FileName} 的产物会落进一个已经有内容的目录里。" +
                        "请选择同名时怎么处理。",
                        "落点：" + outputPath,
                        cancellationToken);

                    if (resolution.Choice == ConflictChoice.Skip)
                    {
                        // 「跳过」= 这个包这次不处理：一个字节都不写，也不动目录里已有的东西。
                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressSkipped;
                        task.ErrorMessage = $"输出目录已存在且非空，按你的选择跳过：{outputPath}";
                        task.EndTime = DateTime.Now;
                        task.ElapsedText = task.StartTime.HasValue
                            ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                            : "-";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("WARN", $"{task.FileName}：同名冲突按你的选择跳过，本任务不解压（输出目录：{outputPath}）。");
                        return;
                    }

                    if (resolution.Choice == ConflictChoice.Overwrite)
                    {
                        /*
                         * 「覆盖」= 沿用这个已有目录（把产物合并进去）。
                         *
                         * 目录本身不会被删：同名条目在**定稿搬运**那一步才真正相撞，
                         * 到那一刻仍然按同一个决定处理（覆盖走"先挪到临时名 → 落位 → 再删"两阶段）。
                         */
                        AppendLog(
                            "WARN",
                            $"{task.FileName}：输出目录已存在且非空，按你的选择「覆盖」沿用该目录：{outputPath}" +
                            "（目录里已有的同名条目会在定稿时按同一决定处理）。");

                        outputRedirectNote = $"原定输出目录 {outputPath} 已存在且非空，按你的选择覆盖进该目录";
                    }
                    else
                    {
                        string newOutputPath = resolution.TargetPath;

                        AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                        /*
                         * 实际落点与"打算的落点"不一致这件事**不许静默**：
                         * ① 下面会把实际落点写回 task.OutputPath（界面"输出目录"列看得见）；
                         * ② 校验结论里也带一句（task.VerifyMessage）；
                         * ③ 日志里额外提醒续解的影响 —— 一键处理的续解是按**解压前**的目录快照找内层包的，
                         *    内层包落进新目录时它找不到，用户看到的现象就是"第二层没解"。
                         *    这里的提示是让人一眼知道该去哪找，而不是让程序假装没发生。
                         */
                        outputRedirectNote = $"原定输出目录 {requestedOutputPath} 已存在且非空，本次实际输出到 {newOutputPath}";

                        AppendLog("WARN", $"{task.FileName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

                        outputPath = newOutputPath;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消必须原样抛上去（落成「已取消」，不变量 6）。
                 *
                 * 同名冲突询问里的「取消本批」也走这条路：下面的兜底 catch 会把任何异常变成
                 * 一句"检查输出目录失败，继续使用原输出目录"—— 那会让取消被吞掉、任务接着跑。
                 */
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            /*
             * ===== 并发下的"最终目录"占位（2026-09-22：默认并发提到 4 之后暴露的竞态）=====
             *
             * 场景：同一个目录里 `pipe.rar` 与 `pipe.zip` 的**包基名都是 `pipe`**，
             * 默认落点都是 `<out>\pipe`。串行时第二个会看到目录已存在、按冲突档落成 `pipe(1)`；
             * 并发时两个任务会在同一瞬间认定"这个目录还不存在"，于是双双往同一个新目录里定稿 ——
             * 后到的那个 `File.Move` 撞上已存在的目标，整条内容物都没搬成，任务报「解压失败」。
             * 用户什么错都没犯，却因为并发丢了一次解压。
             *
             * 修法：把"这个还不存在的最终目录"在**进程内先占下来**（只占不建，用户目录里不留空壳），
             * 占不到就按同一套 `名字(1)` 约定换一个。等这个任务收尾时释放（见 ProcessExtractTaskAsync）。
             *
             * 只对"目录还不存在"的情况占位：
             * · 目录已存在时，上面的冲突档已经基于一个**看得见的事实**做完了决定（沿用 / 改名 / 跳过），
             *   再插一手会把用户显式选的「覆盖」变成"改名"，那是另一回事；
             * · 落点就是源目录（**手动档「解压到当前文件夹」**；旧场景 B 塌缩已退役）时**绝不占位**：
             *   那个目录本来就是这次要整理的目标，给它改名等于把用户点的那次就地整理甩到旁边的 `名字(1)` 去；
             * · 落点是**本次导入共用的目标目录**时同样绝不占位（用户 2026-09-24 第 13 条）：
             *   旧版"添加文件夹 + 指定位置"下同一个文件夹里的包都落进 BBB\222\，第一个包占了位，
             *   后面那些包就会各自换成 222(1)、222(2)…（抢占只对"每包一个目录"的落点有意义）。
             *   ⚠ 2026-09-27 落点模型 v2 之后那一档已经**每包一个目录**（`sharedOutputRoot` 恒为 false），
             *   这条判据留着当兜底，不再有生产路径会命中它。
             */
            if (!task.IsContinuationTask &&
                !landsInSourceDirectory &&
                !sharedOutputRoot &&
                !string.IsNullOrWhiteSpace(outputPath) &&
                !Directory.Exists(outputPath))
            {
                outputPath = ClaimOutputDirectory(outputPath, task, ref outputRedirectNote);
            }

            /*
             * 回写**实际最终输出目录**（P1）。
             *
             * 这是"产物到底在哪"的唯一权威来源：上面可能刚把目录从 xxx 改成了 xxx(1)，
             * 解压后校验、结果归集、清理源包、界面"输出目录"列、以及一键处理的续解全都以它为准
             * （续解那边要读的就是这个值，见报告里的移交项）。
             * 绝不允许只把改名写进日志、却让 task.OutputPath 停在"打算输出到哪"。
             *
             * ⚠ 它**始终指向最终目录**，不指向暂存目录：暂存区只是过程，
             * 续解逻辑（OneClickCoordinator）读的必须是"内容物最终在哪"。
             */
            task.OutputPath = outputPath;

            // 每个任务都明确说一次实际落点：目录被改名时用户必须能立刻看出产物去了哪。
            // 有「本次选项」时带上依据（§9.2 硬要求⑥）：光有落点回答不了"这次为什么解到这里"。
            AppendLog(
                "INFO",
                RunOptions == null
                    ? $"{task.FileName}：本次实际输出目录 {outputPath}"
                    : $"{task.FileName}：本次实际输出目录 {outputPath}（依据 → {RunOptions.Describe()}）");

            /*
             * ===== 阶段一：入仓（stage，契约 §2.1）=====
             *
             * 从这一刻起，本任务**所有**中间动作（抠内嵌归档之后的解压、第一层、内层分卷、递归）
             * 都在私有暂存目录 <c>&lt;work&gt;\&lt;taskId&gt;\stage</c> 里完成，
             * 源目录与最终目录一个字节都不会被写。
             *
             * 这条就是用户那句"分卷文件你居然又解压到外面来了"的根治点：
             * 旧实现把引擎的输出目录直接设成 destDir，内层分卷于是和内容物一起躺在用户目录里；
             * 一旦失败，用户目录里还留着半成品。
             */
            string stageDirectory = _pathService.BuildTaskStageDirectory(task);

            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
                AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                return;
            }

            // 记下"这个任务真正用的工作区目录"：收尾时的空壳清理要按它来，
            // **不能事后重算**（taskId 含源路径哈希，而成功路径会回写 CurrentPath，见字段说明）。
            _taskWorkspaceDirectories[task] = Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            string? stageFailure = await PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(stageFailure))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = stageFailure;
                AppendLog("ERROR", $"{task.FileName}：{stageFailure}");
                return;
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            string engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.FileName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

            /*
             * 内嵌归档（双面文件）：先按偏移把尾部那段真正的 ZIP 抠出来，
             * 再拿它去列目录 / 预检 / 解压 / 解压后校验。
             *
             * 为什么不能直接把源文件交给 7z：这种文件的 ZIP 内部偏移是**相对 ZIP 自己**的。
             * 7-Zip 会尝试用"文件末尾 EOCD 反推的基准偏移"容忍这种错位，但只容忍 8 MiB 以内
             * （本机 26.01 实测：前置 8,388,608 字节可以，8,388,609 字节就报
             * "Cannot open the file as archive"）；用户那个文件垫了 17,031,321 字节，必然被拒。
             * 改后缀也没用（资源管理器能打开，只是因为它的 ZIP 读取器容忍这种整体错位）。
             * 把 [偏移, 归档终点) 原样复制出来是唯一可靠、且完全不改源文件的做法。
             * 终点是识别阶段算出来的 EOCD + 22 + 注释长度 —— 真实文件在 EOCD 之后还有十几 KB
             * 正常数据（实测 14,350–17,424 字节），那截不属于归档，抠取时按终点截断。
             *
             * 换成局部变量 engineArchivePath，而**不去改 task.CurrentPath**：
             * 后者是源文件路径，改名、清理源包、结果统计、报告全都依赖它 ——
             * 一旦被换成工作区里的临时文件，"清理源包"会去删我们自己的中间产物，
             * 而真正的源文件永远得不到处理，报告里指向的也不再是用户给的那个文件。
             */
            string engineArchivePath = task.CurrentPath;

            /*
             * ===== 一组分卷的第一卷名字被改坏 → 在我们自己的产物里把它摆正（用户 2026-09-25 第 37 条）=====
             *
             * 必须在**第一次 list / 解压之前**做完：改完名字引擎才认得出这一组，
             * 后面的清单、预算、路径预检、输出校验、定稿全都按"真正的归档内容"走。
             * 判据见 BrokenVolumeChainRepair：名字带卷号 1 + 后续卷 2、3… 连续 + 标准基名推得出来。
             *
             * ⛔ 只改**我们自己产出的**那一卷（续解任务：它本来就在 `其余物` 里）；
             * 用户自己添加的坏名字分卷一个字节都不动 —— 那种情况留给下面那道
             * 「通用分片」闸门如实报「分卷缺失」+ 给改名建议。
             */
            engineArchivePath = TryRepairBrokenVolumeChainName(task, engineArchivePath);

            /*
             * 内嵌 ZIP 的**直读**结论（非 null = 这一次走直读，不产生那份等大的临时副本）。
             *
             * 用户 2026-09-24 拍板：这条路先试直读，直读说"不支持"就**原地回落到今天的"抠取 + 7z"**。
             * 两条路的结论口径完全一致 —— 用的都是既有那套 ArchivePathGuard / ResourceBudget /
             * OutputVerifier / 定稿 / 其余物 / 源包处理，只是"谁来把字节解出来"换了个人。
             */
            EmbeddedZipProbeResult? directZip = null;

            /*
             * 密码候选表：**必须在直读探查之前**算出来（2026-09-25 第 29 条）。
             *
             * 为什么顺序重要：内嵌归档可能是**加密的**（百度网盘那种分享包就是 AES-256），
             * 而 7-Zip 对 AES 包的密码只按 ANSI 字节派生 —— 它永远打不开 UTF-8 打包的中文密码。
             * 所以直读那一步要拿着候选密码自己去解（内置 ZIP-AES 读取器，两种字节编码都试），
             * 解开了就**不用**再抠一份等大的副本、也不用去问 7z。
             */
            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                task,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst,
                Settings.EnableSidecarPassword);

            if (candidates.Count == 0)
            {
                candidates.Add(new PasswordItem
                {
                    Value = string.Empty,
                    Source = "Empty",
                    IsEnabled = true,
                    Remark = "空密码"
                });
            }

            /*
             * 本批手动输入的密码（如果用户答过）插进候选里。
             *
             * 位置：空密码之后、密码本之前 —— 见 InsertManualPasswordCandidate 的说明。
             * 它天然参与下面的"每层尝试上限"，所以不会让加密分卷的候选循环变成无限循环（不变量 8）。
             */
            InsertManualPasswordCandidates(candidates, _manualBatchPasswords);

            /*
             * 直读/抠取两条路共用的候选值表。⛔ 只活在内存里：日志里只说"第 N 个候选 + 哪种字节编码"，
             * 绝不出密码明文（AGENTS.md §8）。
             */
            List<string> candidatePasswords = candidates.Select(c => c.Value ?? string.Empty).ToList();

            if (task.EmbeddedArchiveOffset > 0)
            {
                long carveBytes = 0;

                try
                {
                    /*
                     * 区间长度 = 终点 − 起点，**不是**"文件长度 − 起点"。
                     * 真实资源包在 EOCD 之后还有十几 KB 正常数据（实测 14,350–17,424 字节），
                     * 那截不属于归档；终点缺失（0 或 ≤ 起点）时退回文件末尾，与旧行为一致。
                     */
                    long sourceLength = new FileInfo(task.CurrentPath).Length;

                    carveBytes = task.EmbeddedArchiveEnd > task.EmbeddedArchiveOffset &&
                                 task.EmbeddedArchiveEnd <= sourceLength
                        ? task.EmbeddedArchiveEnd - task.EmbeddedArchiveOffset
                        : sourceLength - task.EmbeddedArchiveOffset;
                }
                catch
                {
                    // 量不出大小不影响能不能抠，只是日志与空间预检里少个数字。
                }

                if (!IsSingleLayerRecursion())
                {
                    /*
                     * 递归模式：第 0 层必须交给引擎（见 DirectReadAppliesTo 的说明），照旧抠取。
                     * 这一行是**如实说明**，免得用户看到"直读"两个字却发现空间没省下来。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.FileName}：递归模式（{Settings.RecursionMode}）下第 0 层要交给 7-Zip 逐层展开，" +
                        $"本次不走 ZIP 直读，仍按偏移取出内嵌归档（需要约 {carveBytes / 1024 / 1024} MB 临时空间）。");
                }
                else
                {
                    /*
                     * 只读探查（一个字节都不写）：结构、压缩方法、加密位、ZIP64 字段、条目名一次问清。
                     *
                     * 为什么在这里再探一次、而不是信识别阶段那个标记：中间隔着冲突询问、占位、暂存目录准备，
                     * 源文件完全可能已经换了一份（不变量 11 的快照只挡"大小/修改时间变了"）。
                     * 真正拍板必须用**此刻**这一份的结论。
                     */
                    directZip = EmbeddedZipStreamExtractor.Probe(
                        task.CurrentPath,
                        task.EmbeddedArchiveOffset,
                        task.EmbeddedArchiveEnd,
                        engineOutputPath,
                        candidatePasswords,
                        cancellationToken);

                    if (directZip.Supported)
                    {
                        // 上一次回落留下的抠取副本：本次直读用不到它，先清掉（见方法说明）。
                        DiscardStaleCarvedArtifact(task);

                        if (directZip.ResolvedPasswordIndex > 0)
                        {
                            /*
                             * 加密包（AES）解锁成功：这句话必须说清"是哪一档候选、按哪种字节解开的"，
                             * 因为**7-Zip 在同样一个包上只会报"密码错误"**（它只按 ANSI 字节派生密码）——
                             * 用户拿着正确的中文密码却反复失败，根因就在这里（2026-09-25 第 29 条）。
                             * ⛔ 只说序号与编码，不说密码（AGENTS.md §8）。
                             */
                            AppendLog(
                                "INFO",
                                $"{task.FileName}：内嵌归档是加密包（AES），直读已用第 {directZip.ResolvedPasswordIndex} 个候选密码" +
                                $"（按{ZipAesCrypto.DescribeEncoding(directZip.ResolvedPasswordEncoding)}）通过校验与认证码 —— " +
                                "7-Zip 对这类包只按 ANSI 字节派生密码，交给它会一律报密码错误，所以本次由内置读取器解。");
                        }

                        AppendLog(
                            "INFO",
                            $"{task.FileName}：内嵌归档可以 ZIP 直读（{directZip.List?.FileCount ?? 0} 个文件 / " +
                            $"{TaskSpaceEstimate.FormatSize(directZip.TotalBytes)}）—— 本次不需要那份等大的临时副本" +
                            $"（原本要抠 {TaskSpaceEstimate.FormatSize(directZip.ArchiveLength)}）。");

                        // 直读的"引擎"是内置读取器：结果可追溯（不变量 14）里不能写成 7-Zip。
                        if (_archiveEngine is EngineRouter directReadRouter)
                        {
                            directReadRouter.RememberEmbeddedZipDirectRead(task.CurrentPath);
                        }
                    }
                    else if (directZip.PathRejected)
                    {
                        /*
                         * 条目名越界：这是**硬失败**，不是回落信号。
                         * 抠出来交给 7z 会被同一套路径预检拒掉（AGENTS.md §6 第 4 条），
                         * 白白拷一份等大的副本没有任何意义。文案与既有那条逐字一致。
                         */
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage = directZip.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.FileName}：{directZip.Message}");
                        return;
                    }
                    else if (directZip.PasswordRejected)
                    {
                        /*
                         * 加密包、候选密码一个都没通过：**硬失败，同样不回落**。
                         *
                         * 为什么这次连"抠出来交给 7z 试试"都不做：7-Zip 对 AES 包的密码只按 ANSI 字节派生
                         * （实测把归档的 UTF-8 声明手工清掉/置上都一样），百度网盘那种按 UTF-8 打包的包
                         * 在它那里**必然**再报一次密码错误 —— 回落只会白拷一份等大的副本（用户明说过
                         * "我不希望有这么多的失败残留"）。而内置读取器两种字节都试过了，试不出来就是真没有。
                         */
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = directZip.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.FileName}：{directZip.Message}");
                        return;
                    }
                    else
                    {
                        AppendLog("INFO", $"{task.FileName}：内嵌归档直读不支持（{directZip.Reason}），已回落到抠取。");
                        directZip = null;
                    }
                }

                /*
                 * 直读不通（不支持 / 递归模式）才走抠取。抠取那一整套原样保留 ——
                 * 它仍是所有"直读覆盖不到的形态"的唯一出路（加密条目、bzip2、分卷 ZIP…）。
                 */
                if (directZip == null)
                {
                    string carveTarget = BuildEmbeddedArchivePath(task);

                    /*
                     * 临时空间预检：抠出来的中间文件落在工作区（<c>&lt;程序目录&gt;\data\work</c>，即程序所在的那个盘）。
                     * 一个 780MB 的双面文件要在那里占掉 760MB —— 空间不够会写到一半失败，
                     * 还会把那个盘挤满。取不到空间就不拦（宁可试也不误拒），取到了才判。
                     *
                     * 这也是"空间核算里乐观地不记那笔副本"的兜底：真回落时由这一道把关，
                     * 所以账面省掉的那一笔不会变成"写到一半盘满"。
                     */
                    long? carveFree = SpaceChecker.GetAvailableFreeSpace(Path.GetDirectoryName(carveTarget));

                    if (carveFree.HasValue && carveBytes > 0 && carveFree.Value < carveBytes + (256L * 1024 * 1024))
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.ErrorMessage =
                            $"取出内嵌归档需要约 {carveBytes / 1024 / 1024} MB 临时空间，" +
                            $"但系统盘只剩 {carveFree.Value / 1024 / 1024} MB。请先清理空间再试。";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                        return;
                    }

                    AppendLog(
                        "INFO",
                        $"{task.FileName}：正在取出内嵌归档（约 {carveBytes / 1024 / 1024} MB），这一步在后台做，界面不会卡住。");

                    /*
                     * 关键修复：抠出是**同步磁盘拷贝**（几百 MB 量级），必须扔到后台线程。
                     * 之前直接在这里同步调用，拷贝期间整个界面线程被占住 ——
                     * 表现就是"点了一键处理之后程序卡死、窗口未响应"，只能强杀进程
                     * （实测：780MB 的双面文件拷 763MB，界面全程无响应）。
                     */
                    int lastPercent = -1;

                    // 每前进 20% 报一次：够密到能看出在动，又不至于把日志刷爆。
                    var carveProgress = new Progress<int>(percent =>
                    {
                        if (percent >= lastPercent + 20 || percent >= 100)
                        {
                            lastPercent = percent;
                            AppendLog("INFO", $"{task.FileName}：取出内嵌归档 {percent}%");
                        }
                    });

                    CarveResult carve = await Task.Run(
                        () => EmbeddedArchiveCarver.Carve(
                            task.CurrentPath,
                            task.EmbeddedArchiveOffset,
                            carveTarget,
                            task.EmbeddedArchiveEnd),
                        cancellationToken);

                    if (!carve.Success || !File.Exists(carve.OutputPath))
                    {
                        task.Status = StatusText.ExtractFailed;
                        task.Operation = StatusText.OpWaiting;
                        task.ProgressText = StatusText.ProgressFailed;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(carve.Message)
                            ? "取出内嵌归档失败"
                            : "取出内嵌归档失败：" + carve.Message;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                        return;
                    }

                    engineArchivePath = carve.OutputPath;

                    AppendLog(
                        "INFO",
                        $"检测到内嵌归档：已从偏移 {task.EmbeddedArchiveOffset} 处取出 {carve.BytesWritten} 字节" +
                        $"（区间 {task.EmbeddedArchiveOffset}–{task.EmbeddedArchiveOffset + carve.BytesWritten}），" +
                        $"实际使用 {engineArchivePath} 解压。");
                }
            }

            /*
             * 密码候选的**硬上限**（AGENTS.md §9.2：每层、每任务、每批次都要有尝试上限）。
             *
             * 解压模式下一个候选 = 一次完整解压：几百条密码本的包会被逐个候选整包重解一遍，
             * 用户看到的是"几十分钟不动、磁盘一直在写"。所以本层只试前 N 个，
             * 到上限时状态说"达到密码尝试上限"（**不是**"密码错误"）——
             * 包可能完全没问题，只是密码不在这批候选里。
             */
            // 上限读一次就定住：设置是用户随时可改的，同一单任务里不许"前半段按一个上限、后半段按另一个"。
            int attemptLimit = MaxPasswordAttemptsPerLayer;
            int maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
            bool candidatesTruncated = candidates.Count > maxPasswordAttempts;

            if (candidatesTruncated)
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：密码候选共 {candidates.Count} 个，超过单层上限 {attemptLimit} 个，" +
                    $"本层只试前 {maxPasswordAttempts} 个（到上限会明确报“{StatusText.PasswordAttemptLimitReached}”，不会报成密码错误）。");
            }

            /*
             * M5 解压前预检：路径安全 + 资源预算。
             *
             * 为什么必须先"列目录"再解压：真正写盘的是外部 7z.exe，进程外拦不住 Zip Slip。
             * 能做的只有两件事 —— ①解压前把危险条目挑出来，拒绝这一单；
             * ②解压后再校验落点（见 PostProcessSuccessAsync）。这里做的是 ①。
             *
             * 加密头（-mhe）的包不给密码是列不出目录的，所以先拿密码候选去试列表：
             * 试成功的那一个同时也是最可能的解压密码，后面解压循环还会再用一遍。
             */
            ArchiveListResult? preflightList = null;

            // 最后一次 list 失败的结构化原因（成功时用不到）。下面两个"只 list 失败的 WARN 分支"要靠它出结论：
            // 加密文件名与"文件损坏"的文本**分不开**，能分开的是错误类型（见 SevenZipOutputParser 的实测记录）。
            string lastListErrorType = string.Empty;
            string lastListMessage = string.Empty;

            // 预检给出的"文件名已加密"结论（含给用户的那句话）。null = 预检没下这个结论。
            // 它必须活到这一层结束，见下面 preflightSaidEncryptedHeaders 的恢复。
            string? encryptedHeadersMessage = null;

            if (directZip != null)
            {
                /*
                 * 直读路线：清单是**刚解析出来的**，不再问引擎 ——
                 * 源文件 7z 根本打不开（前缀远超 8 MiB 的容忍上限），问它只会得到一次失败。
                 * 更关键的是：这份清单必须与真正解出来的东西是同一份，否则后面的校验就是两套口径。
                 */
                preflightList = directZip.List;

                AppendLog(
                    "INFO",
                    $"{task.FileName}：直读清单 {preflightList?.FileCount ?? 0} 个文件 / " +
                    $"{TaskSpaceEstimate.FormatSize(preflightList?.TotalUncompressedSize ?? 0)}（未调用 7-Zip 列目录）。");
            }
            else
            {
                foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
                {
                    ArchiveListResult attempt = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(engineArchivePath, candidate.Value),
                        cancellationToken);

                    if (attempt.Success)
                    {
                        preflightList = attempt;
                        break;
                    }

                    lastListErrorType = attempt.ErrorType ?? string.Empty;
                    lastListMessage = attempt.Message ?? string.Empty;
                }
            }

            /*
             * ==================== 第 42 条：容器里装的是第 1 卷时，把外面的后续卷接上 ====================
             *
             * 用户 2026-09-25 选的方案 A：**做，但默认关**（②页那个开关打开才走这条路）。
             * 三种形状长得很像，判据必须是**引擎自己说的话**，不能靠猜：
             * · 容器里装的是**完整**归档 → 上面那次 list 就成功了（preflightList != null），走不到这里；
             * · 容器里装的是**分卷第 1 卷**、后续卷在外面 → list 报的正是「分卷缺失」这一类 ← 只有这一档才拼；
             * · 容器里装的是**后续卷**（真的缺首卷） → 外面那一组里**有**标准首卷名 → 不是"缺首卷的组"，
             *   SplitVolumeAssembler 的一组都挑不出来，一个字都不动。
             * 所以这里要求"引擎刚说过分卷缺失"这一条**硬证据**，拼完再用引擎验一次：验不过就退回原路。
             */
            if (preflightList == null
                && directZip == null
                && task.EmbeddedArchiveOffset > 0
                && Settings.AssembleSplitVolumesFromContainer
                && IsVolumeMissingErrorType(lastListErrorType))
            {
                (string? assembledFirstVolume, ArchiveListResult? assembledList) =
                    await TryAssembleSplitVolumesAsync(task, engineArchivePath, candidates, cancellationToken);

                if (assembledFirstVolume != null && assembledList != null)
                {
                    // 引擎自己认了这一套：换成它，后面预检 / 解压全都用拼好的那一套。
                    engineArchivePath = assembledFirstVolume;
                    preflightList = assembledList;
                }
            }

            if (preflightList == null)
            {
                /*
                 * 列不出内容：要么加密头（RAR -hp / 7z -mhe），要么归档真的坏了。
                 *
                 * ⚠ 这里**只认引擎给的错误类型**，绝不在这条路径上"猜"：
                 * 解压 / 测试路径上的 WrongPassword 是**密码候选循环的驱动信号**
                 * （循环见到非 WrongPassword 就 break），在那里把它改成"文件名已加密"
                 * 会让第一个候选（常是空密码）就打断循环 —— 我们自己 -mhe 的内层分卷会全部解不开。
                 * 判定本身只在 SevenZipOutputParser.LooksLikeEncryptedHeaders 里，而且只在列目录操作上成立
                 * （有反向回归测试钉着），这里只是把那个结论落到任务状态上。
                 */
                if (string.Equals(
                        lastListErrorType,
                        SevenZipOutputParser.EncryptedHeadersErrorType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    task.Status = StatusText.EncryptedHeaders;
                    task.PasswordStatus = StatusText.PasswordNeed;

                    /*
                     * 这份文案要**留到这一层结束**（见下面 preflightSaidEncryptedHeaders 的恢复）：
                     * 密码候选循环会把 task.ErrorMessage 覆盖成泛泛的"密码错误或缺少正确密码"，
                     * 而用户真正需要知道的是"这个包连文件列表都读不出来，得先给对密码"。
                     */
                    encryptedHeadersMessage =
                        $"这个包可能加密了文件名（RAR -hp / 7z -mhe），所以连内容清单都读不出来，" +
                        $"需要正确密码才能列出内容。{lastListMessage}";

                    task.ErrorMessage = encryptedHeadersMessage;

                    AppendLog(
                        "WARN",
                        $"{task.FileName}：{StatusText.EncryptedHeaders} —— 连内容清单都读不出来（需要正确密码），" +
                        $"本次跳过路径预检与资源预算，解压后仍会校验落点。原因：{lastListMessage}");
                }
                else
                {
                    AppendLog("WARN", $"{task.FileName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
                }
            }
            else
            {
                /*
                 * 「一组分卷的第一卷名字被改坏」这一档（用户 2026-09-25 第 36 条追加，取证见 RawSplitStreamDetector）。
                 *
                 * 为什么必须在**写盘之前**拦：7-Zip 找不到同组的后续卷时会退化成"通用分片"，
                 * 把这一卷**照解不误**（退出码 0）—— 解出来是一个与它等大的垃圾文件，
                 * 而结果校验拿"清单那一条"比"盘上那一个文件"会判**通过**，
                 * 用户拿到 5 GB 垃圾却看到「解压成功」。这就是"成功"被用错，必须判「分卷缺失」。
                 *
                 * ⛔ 判据全在 RawSplitStreamDetector 里（纯函数、可测）：引擎说通用分片 + 清单只有"文件自己"一条
                 * + 魔数认得出是归档 + 这一组只有自己一卷。名字正常且卷齐的那一组**不拦**（拼起来正是用户要的）。
                 */
                if (RawSplitStreamDetector.IsBrokenVolumeChain(
                        preflightList,
                        task.CurrentPath,
                        task.IsArchive && !string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase),
                        task.VolumeCount))
                {
                    string volumeDirectory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

                    /*
                     * 改名建议**只有一处推法**（Extraction/VolumeNameRepair）：
                     * 失败清单里写给用户的名字、与①页「按建议改名并重试」真正会改成的名字，
                     * 必须是同一个 —— 两边各推一次就会出现"按它说的改完还是解不开"。
                     */
                    VolumeNameRepairPlan repair = VolumeNameRepair.Plan(
                        task.CurrentPath,
                        CollectVolumeFilesInDirectory(volumeDirectory).Select(Path.GetFileName));

                    task.VolumeRenameSuggestion = repair.CanRepair ? repair.SuggestedFileName : string.Empty;

                    string siblingText = repair.Siblings.Count > 0
                        ? $"同目录里有像后续卷的文件：{string.Join("、", repair.Siblings)}"
                          + "（它们与这一卷的名字对不上，所以引擎找不到它们）。"
                        : "同目录里也没有找到像后续卷的文件。";

                    string advice = repair.CanRepair
                        ? $"把这一卷改回标准命名（{repair.SuggestedFileName}）就能解开 —— "
                          + "勾上它点①页「修复分卷名并重试」，程序只改名字（内容一个字节都不动）后立刻重试；"
                          + "也可以自己改完右键「重新扫描此文件」。"
                        : $"这一步没给出改名建议：{repair.Reason}。";

                    task.Status = StatusText.VolumeMissing;
                    task.ErrorMessage =
                        "分卷缺失：这一卷是「一组分卷的第一卷」，但它的名字被改坏了 —— "
                        + "引擎按名字找不到同组的后续卷，只会把它当成一段通用分片"
                        + "（那样「解出来」的是一个与它等大的垃圾文件，不是包里的内容），所以不开始。"
                        + siblingText + advice
                        + "程序不会自己改源文件（不变量 1）：只有你点那个按钮，它才会改这一个名字。";

                    AppendLog("ERROR", $"{task.FileName}：{task.ErrorMessage}");
                    return;
                }

                PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

                if (!pathReport.IsSafe)
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                    AppendLog("ERROR", $"{task.FileName}：路径预检未通过 —— {pathReport.Summary}");
                    return;
                }

                long archiveSize = 0;

                try
                {
                    /*
                     * 量的是**真正交给引擎的那个文件**：内嵌归档抠出来之后它比源文件小得多，
                     * 拿源文件大小当分母会把展开比算小，压缩炸弹就更容易蒙混过关。
                     *
                     * 直读路线没有"那个文件"：归档就是虚拟区间 [Offset, ArchiveEnd)，
                     * 分母取它的长度（源文件含几百 MB 视频前缀，拿它当分母同样会把展开比算小）。
                     */
                    archiveSize = directZip != null
                        ? directZip.ArchiveLength
                        : new FileInfo(engineArchivePath).Length;
                }
                catch
                {
                    // 取不到大小就不做展开比判断，其余预算照常。
                }

                // 预算里的"落点"用暂存目录：引擎真正写盘的地方是它，最终目录此时还不存在。
                // 上限取用户设置（第 36 条）：四条上限只有 BudgetLimits 这一个出口。
                BudgetCheckResult budget = new ResourceBudget(BudgetLimits)
                    .CheckBeforeExtract(preflightList, archiveSize, engineOutputPath);

                /*
                 * 精确空间需求（用户 2026-09-22 需求第 1 条：核算必须含内容物 + 过程物 + 去重）。
                 *
                 * 用的是**手上这一份 list**（绝不为此再跑一次 7z：加密包每多列一次目录就多一次失败机会），
                 * 而流程预算只算了"内容物"那一项 —— 抠出来的内嵌过程物、内层包再展开的增量、
                 * 以及并发下别的任务已经占下的份额，都要在这一步一起算进来。
                 *
                 * ⚠ 源包**不算**进这个需求：它已经在盘上、本来就不在"可用空间"里
                 * （判据是 FreeSpaceDemandBytes = 内容物 + 过程物；见 TaskSpaceEstimate 里的说明）。
                 */
                TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(
                    SpaceEstimator.FromSourceFiles(task, DirectReadAppliesTo(task), PartialPublishActive),
                    preflightList,
                    directZip != null ? 0 : SpaceEstimator.EstimateCarvedBytes(task, archiveSize),
                    carvedBytesNotNeeded: directZip != null)
                    .WithVolumeLayout(ResolveWorkspaceVolumeShare(outputPath));

                RecordRefinedEstimate(task, refined);

                if (!budget.Allowed)
                {
                    if (budget.IsSpaceShortage)
                    {
                        /*
                         * 空间不够（精确值算出来的）：**不启动**，报具体数字与建议动作。
                         * 顺手用 ReconcileReservation 把账本调准 —— 它给出的信息比预算那句话更全
                         * （含"差多少"与三条建议）；万一它反而放行（两边估算口径不同），
                         * 就退回预算那句话，绝不把"放行"当结论（宁可保守）。
                         */
                        SpaceGateDecision spaceGate = ReconcileReservation(task, refined);
                        string spaceReason = spaceGate.Allowed ? budget.Reason : spaceGate.ToLogLine();

                        MarkSpaceBlockedCore(
                            task,
                            spaceReason + $"（{refined.Describe()}；依据：{refined.Basis}）",
                            refined.FreeSpaceDemandBytes,
                            budget.FreeSpaceBytes ?? SpaceReservationLedger.UnknownAvailable,
                            spaceGate.Allowed ? 0L : spaceGate.ShortfallBytes);

                        return;
                    }

                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "资源预算未通过：" + budget.Reason;

                    AppendLog("ERROR", $"{task.FileName}：资源预算未通过 —— {budget.Reason}");
                    return;
                }

                /*
                 * 空间门（精确值）：够了就放行、不够就**在这里停下**（引擎一个字节都还没写）。
                 * 它同时把本任务的预留从"粗估"调整成精确峰值 —— 并发下这一步很关键：
                 * 估小了会让几个任务一起把盘写满，估大了会白白拦下本来能跑的任务。
                 */
                SpaceGateDecision preciseGate = ReconcileReservation(task, refined);

                if (!preciseGate.Allowed)
                {
                    MarkSpaceBlockedCore(
                        task,
                        preciseGate.ToLogLine() + $"（{refined.Describe()}；依据：{refined.Basis}）",
                        refined.FreeSpaceDemandBytes,
                        preciseGate.AvailableBytes,
                        preciseGate.ShortfallBytes);

                    return;
                }

                AppendLog("INFO", $"{task.FileName}：空间门通过（精确） —— {preciseGate.Reason}；{refined.Basis}");

                if (!string.Equals(
                        RestHandlingModes.Normalize(_restHandlingThisBatch),
                        RestHandlingModes.Keep,
                        StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * "预计可回收多少"必须**跟着源包那一档算**（2026-09-27 用户真机）：
                     * `ReclaimableBytes` = 源包 + 过程物（那是"危险模式能收回多少"的口径），
                     * 可源包选的是「留在原地」时它一个字节都不会被碰 —— 报 6.8 MiB 就是在骗人
                     * （日志里那个 6.8 MiB 正是源包自己的大小）。
                     */
                    SourceHandlingMode sourceMode = EffectiveSourceHandling;

                    long reclaimable = sourceMode == SourceHandlingMode.KeepInPlace
                        ? refined.ProcessArtifactBytes
                        : refined.ReclaimableBytes;

                    string amount = reclaimable > 0
                        ? $"预计可回收 {TaskSpaceEstimate.FormatSize(reclaimable)}"
                        : "这次没有可回收的过程物";

                    AppendLog(
                        "INFO",
                        $"{task.FileName}：定稿 + 校验通过之后会按「删除操作」"
                        + (string.Equals(RestHandlingModes.Normalize(_restHandlingThisBatch), RestHandlingModes.RecycleBin, StringComparison.OrdinalIgnoreCase)
                            ? "把其余物移入回收站"
                            : "彻底删除其余物")
                        + $"（范围：{DescribeRestScope(sourceMode)}；{amount}）");
                }

                if (!string.IsNullOrWhiteSpace(budget.Reason))
                {
                    /*
                     * 这一条是**提示**（"预检通过：N 个文件 / 约 X MiB，展开比 N 倍"），不是警告 ——
                     * 以前记成 WARN，后果有两个：①日志里每个任务都顶着一条黄字，看着像出了事；
                     * ②第 44 条的日志瘦身策略里"出现 WARN 就把该任务的细节全部解锁"，
                     *    于是 68 个包每个都把十几行样板全吐了出来（实测就是被这一条挡住的）。
                     * 真正被预算挡下时走的是 ERROR（"安全上限"/"磁盘空间不足"那几支），一个字都不少。
                     */
                    AppendLog("INFO", $"{task.FileName}：资源预算提示 —— {budget.Reason}");
                }

                /*
                 * 长路径提示落地（WinRAR 参考 §2 I 组采纳项）。
                 *
                 * 以前它只在上面那行日志里（ResourceBudget 已经把它拼进 Reason），任务详情里看不到 ——
                 * 而"路径过长导致少了几个文件"恰恰是用户事后才来查的问题。落到任务字段之后，
                 * 失败清单逐归档的第二级（TaskSummaryService）也会带上这一行。
                 * 数字与文案都由 Security/PathLengthPreflight 备好，这里只搬运，不重算。
                 */
                if (!string.IsNullOrWhiteSpace(budget.PathLengthWarning))
                {
                    task.PathLengthWarning = budget.PathLengthWarning;
                }

                /*
                 * 危险条目统计（WinRAR 参考 §2 F 组 / §3 第 5 条采纳项，设置项 ReportDangerousEntries 默认开）。
                 *
                 * ⚠ 复用**手上这一份** list（preflightList），绝不为它再跑一次 7z：
                 * 加密包每多列一次目录就多一次失败机会，重复 list 是把失败概率成倍放大。
                 * **只提示不阻断**：安装器 / 补丁经常就是内容物，不做全局硬排除掩码。
                 */
                PublishDangerousEntriesHint(
                    task,
                    AnalyzeDangerousEntries(preflightList.Entries, Settings.ReportDangerousEntries),
                    "INFO");
            }

            /*
             * 预检给出的"文件名已加密"结论必须**留住**。
             *
             * 为什么不能只写在预检那一支里：后面的密码候选循环无论如何都会覆盖 task.Status
             * （解压失败 → WrongPassword）。于是预检算出来的那个**更具体**的结论会被一句泛泛的
             * "密码错误"吃掉 —— 用户拿到的还是错的那句话，这一处接线就等于白做。
             *
             * 只在"这一层最后仍然只是密码没通过"时把它恢复：
             * · 解压成功（有的包加密头也能解）→ 不动；
             * · 达到尝试上限 → 那是更具体的结论，不动；
             * · 文件损坏 / 权限不足这类别的失败 → 更具体的结论，不动。
             */
            bool preflightSaidEncryptedHeaders = task.Status == StatusText.EncryptedHeaders;

            /*
             * ===== 不变量 11 的**第二道**：就在引擎真正开工之前再比一次 =====
             *
             * 第一道在本方法开头（挡住"扫描之后就被改过"的包）。这一道挡的是**中间那段窗口**：
             * 从第一道到这里，程序已经做过冲突询问、目录占位、坍缩扫描、暂存目录准备
             * —— 弹窗可以停在屏幕上等用户点，一秒到几分钟都正常，
             * 而这期间用户完全可能自己把那个包换掉（"我先看看里面是什么"→ 解压 → 覆盖回去）。
             *
             * 两道之间只做 stat（不读内容、不写任何东西），所以代价可以忽略；
             * 而漏掉这一道的代价是**拿旧识别结果解新文件**，那正是不变量 11 要防的事。
             */
            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * 递归模式（M4）：不是"只解当前层"时，整条解压交给 RecursiveExtractor。
             * 它自己会解第 0 层、探测内层、按模式决定继续还是问用户，并受硬上限约束。
             * 放在预检之后：预检已经把暂存目录算好，而且非归档 / 格式未知在前面已经分流走了。
             *
             * 落点传的是**暂存目录**：递归核心本来就在自己的工作区里逐层解、最后才 Publish，
             * 让它直接 Publish 到最终目录等于跳过了"入仓 → 定稿"这道门（失败时还会在用户目录里留下半成品）。
             * 现在它发布进暂存区，再由下面的定稿一次性搬进最终目录 —— 与单层路径同一口径。
             *
             * ⚠ 这条分支里的第 0 层同样受不变量 11 保护：递归核心**每一层**开工之前都会回头问一次
             * "源文件还是原来那一份吗"（见 CheckRootSourceUnchangedAsync），不必在这里再写一遍。
             */
            if (!string.Equals(Settings.RecursionMode, "SingleLayer", StringComparison.OrdinalIgnoreCase))
            {
                RecursionResult? recursion = await RunRecursiveAsync(
                    task, engineArchivePath, engineOutputPath, oneClickRun, cancellationToken);

                if (task.Status == StatusText.ExtractSuccess)
                {
                    // 递归产物同样要走"校验 → 定稿 → 归集 → 源包处理"，与单层路径一个字都不差。
                    bool recursionConclusionStands = await PostProcessSuccessAsync(
                        task,
                        engineArchivePath,
                        string.Empty,
                        engineOutputPath,
                        outputRedirectNote,
                        sharedOutputRoot,
                        oneClickRun,
                        cancellationToken,
                        knownList: null,
                        recursion: recursion);

                    if (recursionConclusionStands && task.Status == StatusText.ExtractSuccess)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }

                /*
                 * ===== 递归这条路的密码类失败也要登记到本批（2026-10-05 只读审计）=====
                 *
                 * 现场：<see cref="RecordPasswordFailure"/> 过去只有单层路径的两个调用点，而这一支解完就 return ——
                 * 于是出厂默认档（展开所有分支 ⇒ 内层包走递归）下，用户 2026-10-02 明确要的批末指路
                 * （「可能本来就没有密码 / 也可能对的密码排在候选更靠后 ⇒ 去④页调顺序或调大上限」）
                 * 与"本批 N 个包没能解开"整句**一次都不出现**。
                 *
                 * 判据**不在**这里重写：<see cref="RecordPasswordFailure"/> 自己就只认三个状态常量
                 * （密码错误 / 密码可能不对也可能数据坏了 / 达到密码尝试上限），不是那一档就什么都不做 ——
                 * 转调它 = 登记判据与批末分档仍然只有一处（§9.5）。
                 */
                RecordPasswordFailure(task);

                return;
            }

            ArchiveOperationResult? lastResult = null;
            string selectedPassword = string.Empty;
            bool passwordConfirmed = false;

            /*
             * 密码类结论上要补的两句话（**方法级**，因为要在"文件名已加密"那一支之后才落到 ErrorMessage 上）：
             * · 换引擎兜底跑过 / 没跑成（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）；
             * · 候选被每层上限截断时点名"哪几条没试到"。
             * 判据不成立时它**一直是空串** ⇒ 正常路径一个字都不改。
             */
            string passwordConclusionNote = string.Empty;

            /*
             * 两义那一档（引擎既说密码不对、又说数据坏了）出现过的那一次：
             * 收尾时优先用它，结论与日志同时保留两种可能（判据在引擎解析层那一处）。
             */
            ArchiveOperationResult? ambiguousTestResult = null;

            /*
             * 进度可见（D5）的接线点：**每个任务一个接收端**。
             *
             * 为什么一个任务只建一个：它记着"上一个写进日志的 10% 档位"，
             * 每换一次密码候选就新建一个的话，"达到 10% 就写一行"这条限制会失效
             * （每个候选都从头数一遍档位）。
             *
             * 清一次残留：同一个任务可能被重跑（换密码候选、用户再点一次），
             * 上一轮的百分比绝不能带到这一轮来。
             */
            TaskProgressSink progressSink = new(this, task);
            task.ClearProgress();

            /*
             * 关键修复：
             *
             * 旧逻辑：
             * candidates.Count > 1 就强制先执行 7z t。
             *
             * 问题：
             * 7z t 只是测试，不会产生解压文件。
             * 所以用户会看到 7-Zip Console 一直运行，但输出目录没有产物。
             *
             * 新逻辑：
             * 只有用户明确开启 TestBeforeExtract，或者任务明确标记 IsEncrypted，
             * 才先测试密码。
             */
            bool shouldTestPasswordBeforeExtract =
                Settings.TestBeforeExtract;

            if (directZip != null)
            {
                /*
                 * 直读路线：不走密码候选循环、不调引擎。
                 *
                 * 为什么可以完全跳过密码：能走到这里就说明探查已经确认"没有加密条目"
                 * （通用位标志 bit0 的检查在 EmbeddedZipStreamExtractor 里，一处定义）。
                 * 收尾仍然走**同一个** PostProcessSuccessAsync（校验 → 定稿 → 归集 → 其余物 → 源包处理），
                 * 绝不另起一条收尾路径 —— 两条路的结论口径必须一致。
                 */
                await RunEmbeddedZipDirectExtractionAsync(
                    task,
                    directZip,
                    candidatePasswords,
                    preflightList,
                    engineOutputPath,
                    outputRedirectNote,
                    sharedOutputRoot,
                    oneClickRun,
                    progressSink,
                    cancellationToken);
            }
            else if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = StatusText.OpTest;
                task.Status = StatusText.Testing;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.FileName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    ArchiveOperationResult testResult = await _archiveEngine.TestAsync(
                        BuildTrackedRequest(engineArchivePath, password, null, progressSink),
                        cancellationToken);

                    lastResult = testResult;

                    // 详细档：测试这一次的命令行与原话（与解压路径同一出口、同一措辞）。
                    if (VerboseTaskLogEnabled)
                    {
                        EngineOutputLog.LogVerbose(AppendLog, task.FileName, testResult);
                    }

                    if (testResult.DetectedErrorType == "Cancelled" ||
                        testResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (testResult.Success)
                    {
                        selectedPassword = password;
                        passwordConfirmed = true;

                        task.Status = StatusText.TestPassed;
                        task.PasswordStatus = string.IsNullOrEmpty(password) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("INFO", $"{task.FileName}：测试通过。");
                        break;
                    }

                    if (testResult.DetectedErrorType == "WrongPassword")
                    {
                        task.PasswordStatus = StatusText.WrongPassword;

                        /*
                         * ⛔ 这里**不是** WARN（用户 2026-09-26 第 45 条实测）：候选错一个是**预期之内**的事，
                         * 而 WARN 会（按第 44 条的规矩）把该任务攒着的细节**立刻全吐出来**并让后续 INFO 不再攒 ——
                         * 真机上就是这一行让"成功就丢"整条失效：38 个包每个都因此把入仓 / 空间门 / 定稿 /
                         * 本次内容物 / 结果校验几十行样板全写了出来（也是他看到的 246 KB 的主要来源）。
                         * 用 INFO 之后：这个候选错了就错了，任务最后**成功** → 细节照样丢；**失败** → 细节照样全留。
                         */
                        AppendLog("INFO", $"{task.FileName}：这个密码候选不对，继续试下一个。");
                        continue;
                    }

                    /*
                     * 两义那一档（引擎既说密码不对、又说数据坏了）在这里同样是**继续试下一个候选**，
                     * 不许 break —— 判据与处置只有引擎解析层那一处（见 UnRarOutputParser /
                     * EngineErrorTypes.PasswordOrCorrupted），三条候选循环口径一致。
                     */
                    if (testResult.DetectedErrorType == EngineErrorTypes.PasswordOrCorrupted)
                    {
                        ambiguousTestResult ??= testResult;
                        task.PasswordStatus = StatusText.PasswordNeed;

                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidatePasswordOrCorruptedLogFormat,
                                task.FileName));

                        continue;
                    }

                    task.Status = testResult.Status;
                    task.ErrorMessage = testResult.Message;
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：测试失败，原因：{testResult.Message}");
                    break;
                }

                if (!passwordConfirmed)
                {
                    /*
                     * "试到上限停了"和"候选全都试过、都说密码错"是两件事（AGENTS.md §9.2）：
                     * 前者的状态是"达到密码尝试上限"，不能让用户以为密码本里就一定没有正确密码。
                     * lastResult 不是 WrongPassword 时说明停因另有其人（损坏/权限…），照旧原样上报。
                     */
                    bool stoppedByAttemptLimit = candidatesTruncated &&
                        (lastResult == null || lastResult.DetectedErrorType == "WrongPassword");

                    if (stoppedByAttemptLimit)
                    {
                        task.Status = StatusText.PasswordAttemptLimitReached;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage =
                            $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                    }
                    else if (ambiguousTestResult != null)
                    {
                        // 同"普通模式"那一支：引擎自己说过两种可能，结论就必须把两种可能都留着。
                        task.Status = ambiguousTestResult.Status;
                        task.PasswordStatus = StatusText.PasswordNeed;
                        task.ErrorMessage = ambiguousTestResult.Message;
                    }
                    else if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = StatusText.TestFailed;
                        task.ErrorMessage = "没有可用密码或测试失败";
                    }

                    task.EndTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    /*
                     * 密码类失败**登记到批次**，由 ShowPasswordFailuresSummaryAsync 合并成一次提示。
                     * 原来这里（以及下面的直接解压分支）各弹一次模态框、而且用的是同步 Dispatcher.Invoke：
                     * 一批几十个错包就要点几十次，还会阻塞调用线程。
                     */
                    RecordPasswordFailure(task);

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = StatusText.OpExtract;
                task.Status = StatusText.Extracting;
                task.ProgressText = StatusText.ProgressProcessing;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：测试通过，开始正式解压。");

                ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                lastResult = extractResult;

                if (extractResult.DetectedErrorType == "Cancelled" ||
                    extractResult.Status == StatusText.Cancelled ||
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (extractResult.Success)
                {
                    /*
                     * 引擎说成功 ≠ 产物是好的（同"普通模式"那条分支的真机铁证）：先校验。
                     *
                     * 这一支没有"下一个候选"可试 —— 密码是 7z t 亲自确认过的，
                     * 产物不完整只可能是盘满 / 权限 / 数据本身的问题。校验判否时**不落成功**，
                     * 但仍然走一次收尾：半截产物落在用户看得见的地方，0 字节产物什么都不会搬，
                     * 源包一个字节都不动（收尾里的校验闸门负责把结论落成失败）。
                     */
                    StageProductVerification stage = await VerifyStageProductsAsync(
                        task,
                        engineArchivePath,
                        selectedPassword,
                        engineOutputPath,
                        cancellationToken);

                    if (stage.Verification.Verified)
                    {
                        task.Status = StatusText.ExtractSuccess;
                        task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                        task.ErrorMessage = string.Empty;

                        // 密码成功记录仍然挂在**源文件**上：下次用户再导入这个文件时要能直接命中，
                        // 而工作区里抠出来的临时文件活不过这次任务。
                        _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
                    }
                    else
                    {
                        AppendLog(
                            "WARN",
                            $"{task.FileName}：结果校验 —— {stage.Verification.Message}" +
                            "（密码是对的，但产物不完整，按失败收场）");
                    }

                    // 收尾可能把"解压成功"顶掉（产物越界 / 超预算 / 定稿失败 / 校验未通过）：只有结论仍然成立时才敢这么写日志。
                    bool conclusionStands = await PostProcessSuccessAsync(
                        task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                    if (conclusionStands)
                    {
                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                    }
                }
                else
                {
                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = StatusText.WrongPassword;
                        task.PasswordStatus = StatusText.WrongPassword;
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }

                    // 「容器里装的是分卷第一卷」那一档：把引擎那句与事实相反的话补正（第 42 条）。
                    string volumeHint = BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

                    if (!string.IsNullOrEmpty(volumeHint))
                    {
                        task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                }
            }
            else
            {
                /*
                 * 普通模式：
                 * 不先测试，直接解压。
                 *
                 * 好处：
                 * 1. 普通无密码压缩包会立刻产生解压产物。
                 * 2. 不会让用户误以为卡住。
                 * 3. 如果遇到密码错误，再尝试下一个密码。
                 */
                bool extractSuccess = false;
                bool hasWrongPassword = false;

                /*
                 * 两义那一档（引擎既说密码不对、又说数据坏了）这一层出现过没有：
                 * 过它的时候**不停候选循环**，收尾时状态与原因取它（引擎原话里两种可能都写着），
                 * 只有它不存在时才退回"最后一次引擎结果"（老口径，行为不变）。
                 */
                ArchiveOperationResult? ambiguousResult = null;

                /*
                 * 「密码已经证实、坏的是数据」这一档（用户 2026-10-01 真机 `giu910`，17.7 GiB 解了 13 分钟）：
                 * true = 某一趟**真解出了内容**之后才失败的 ⇒ 密码不是问题，不再往下试候选。
                 * 判据见下面候选循环里那两处 `TryMeasureProducedContent`。
                 */
                bool passwordProvenDataFailed = false;
                (int Files, long Bytes) provenProducts = (0, 0);

                /*
                 * ===== 主引擎在这一层的候选上写出过字节没有（2026-10-05；换引擎兜底那条判据的输入）=====
                 *
                 * true = 某一趟失败（或"引擎说成功但校验不过"）在暂存目录里**留下了非零字节** ⇒
                 * 这个引擎**进得去这一份的数据流**。它与"换引擎兜底"的关系见
                 * `PasswordFallbackTriggerFacts.MainEngineWroteBytes`：WinRAR 用错密码时也会按原大小把
                 * 解密出来的乱码写进同一个目录（名字 / 大小 / 时间戳与真产物一样，实测）⇒ 主引擎写出过
                 * 字节的那些档，两个引擎给不出**可分辨**的证据 ⇒ 判不出 ⇒ 不换引擎、结论照旧。
                 *
                 * ⚠ 只数非零字节：0 字节桩文件不算"写出过字节"（错密码打 AES ZIP 的典型签名就是一堆 0 字节桩，
                 * 真机那一档照旧要允许换引擎）—— 口径与 `ProducedContentGate` / `ProducedBytesInLayerOutput` 一致。
                 */
                bool anyCandidateWroteBytes = false;

                /*
                 * ===== 先试密码，再解整包（用户 2026-09-25 第 37 条）=====
                 *
                 * 两条纪律，缺一条他那个 12 GiB 的包就又要白跑一整包：
                 * ① **加密包不试空密码**：7-Zip / WinRAR 都造不出"用空密码加密"的包（`-p""` 等于不加密），
                 *    拿空密码去打一个已加密的包必然白跑 —— 他真机那 10 分 18 秒就是这么来的；
                 * ② **候选先过"最小条目"这一关**：探针解开了才去解整包（见 Extraction/PasswordProbe）。
                 *
                 * 为什么以前躲不过：`-mhe=off` 的包**列目录不需要密码**（任何候选都列得出来），
                 * 而 `Copy` 方法 + AES-CBC 的数据要**整条读完**才能比对 CRC —— 错密码的代价 = 解一遍整包。
                 * ⚠ 空密码那一档只在"还有别的候选可试"时才跳过：一个候选都不剩会让下面的收场分支
                 * 落到"未知解压失败"，那比多跑一次更难懂（见 skippedOnlyEmptyPasswordBecauseEncrypted 那一支）。
                 */
                string? probeEntryPath = PasswordProbe.IsWorthProbing(
                        preflightList,
                        preflightList?.TotalUncompressedSize ?? 0)
                    ? PasswordProbe.ChooseProbeEntry(preflightList)
                    : null;
                long probeEntrySize = 0;
                bool skippedOnlyEmptyPasswordBecauseEncrypted = false;

                if (probeEntryPath != null)
                {
                    probeEntrySize = preflightList!.Entries
                        .FirstOrDefault(e => e != null && string.Equals(e.Path, probeEntryPath, StringComparison.Ordinal))?.Size ?? 0;
                }

                if (PasswordProbe.ShouldSkipEmptyPassword(preflightList))
                {
                    int removedEmpty = candidates.RemoveAll(c => string.IsNullOrEmpty(c.Value));
                    skippedOnlyEmptyPasswordBecauseEncrypted = removedEmpty > 0 && candidates.Count == 0;

                    if (removedEmpty > 0)
                    {
                        AppendLog(
                            "INFO",
                            $"{task.FileName}：整包已加密 —— 跳过「空密码」这一档（引擎造不出用空密码加密的包，"
                            + "试它必然白跑一整包）。"
                            + (candidates.Count == 0 ? "这次没有任何可用密码。" : $"还有 {Math.Min(candidates.Count, maxPasswordAttempts)} 个候选可试。"));
                    }
                }
                else if (preflightList == null
                         && string.Equals(
                             lastListErrorType,
                             SevenZipOutputParser.EncryptedHeadersErrorType,
                             StringComparison.OrdinalIgnoreCase))
                {
                    /*
                     * 文件名被加密（RAR `-hp` / 7z `-mhe=on`）时**清单根本读不出来**（preflightList == null），
                     * 上面那条判据看不到任何条目 —— 可"这一包要密码"是引擎**已经说过**的事实。
                     *
                     * 用户 2026-09-26 第 45 条真机（38 个同源包，内层是加密 RAR）里那行
                     * `开始解压，密码候选 1/10，尝试空密码` 就是从这条路来的：
                     * 空密码必然失败，白跑一轮，还顺手把"成功就丢"的日志瘦身整条打掉（那一行以前记 WARN）。
                     *
                     * ⚠ **只在还有别的候选可试时**才跳过（与上面那一支同一条边界）：一个候选都不剩会让
                     * 下面的收场分支落到"未知解压失败"，那比多跑一次更难懂 ——
                     * `PipelineWiringTests.列目录失败且判为加密头_任务状态是文件名已加密` 钉着这一点。
                     */
                    int removedEmpty = candidates.Count > 1
                        ? candidates.RemoveAll(c => string.IsNullOrEmpty(c.Value))
                        : 0;

                    if (removedEmpty > 0)
                    {
                        AppendLog(
                            "INFO",
                            $"{task.FileName}：这一包连文件名都加密（引擎说 {lastListErrorType}）—— 同样跳过「空密码」这一档。");
                    }
                }

                /*
                 * ===== 先试密码：只读「开头 64 字节」给候选排序（用户 2026-09-25 第 38 条）=====
                 *
                 * 这一档专门解决"包里没有小文件"（他这个 12 GiB 的包正是三个 5 GB 的大条目 ——
                 * 小样预检挑不出探针）。7z 的 AES **没有密码校验位**（不像 WinZip AES 有 2 字节校验值），
                 * 所以"只验密码、不读数据"在协议上做不到；但可以**只读开头几十字节**：
                 * 密码对，解出来的就是文件真正的开头（7z/zip/rar/mp4… 的魔数）；密码错，解出来是随机字节。
                 * 代价 = 7-Zip 的密钥派生（约 0.1–0.3 秒），**与包多大无关**、一个字节都不落盘。
                 *
                 * ⛔ 只用来**排序**、绝不丢候选：看不出文件开头**不等于**密码错
                 * （有些真实文件就是没有魔数的裸数据）—— 那些排到后面再试，一个都不会少。
                 */
                string? prefixEntryPath = PasswordProbe.IsWorthProbing(
                        preflightList,
                        preflightList?.TotalUncompressedSize ?? 0)
                    ? PasswordProbe.ChoosePrefixEntry(preflightList)
                    : null;

                if (prefixEntryPath != null &&
                    candidates.Count > 1 &&
                    _archiveEngine is SevenZipEngine prefixEngine)
                {
                    candidates = await RankCandidatesByDecryptedPrefixAsync(
                        task,
                        engineArchivePath,
                        prefixEngine,
                        prefixEntryPath,
                        candidates,
                        maxPasswordAttempts,
                        cancellationToken);
                }

                /*
                 * 本轮候选循环的两笔账（用户 2026-09-24 要求，缺一不可）：
                 * · lastVerification —— 最后一个"解出来了但产物没通过校验"的结论。
                 *   循环跑完还没成功时，它就是**最准确**的失败理由（比泛泛的"密码错误"多出三个数字）；
                 * · attemptedCandidates —— 到底试了几个候选，写进最终原因（"已试 N 个候选，产物始终不完整"）。
                 */
                OutputVerificationResult? lastVerification = null;
                int attemptedCandidates = 0;

                /*
                 * 这一层的**逐条清单**（部分完成发布的尺子）：优先取"带着密码列出来的那一份"
                 * （校验里那次 list），没有就退回解压前那一次预检清单 —— 未加密的包两者本来就一样。
                 * ⛔ 只留最近一次成功的：候选循环里每个候选都会重列一次，留错哪一份就对着错的尺子量。
                 */
                IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries =
                    PartialPublishPlanner.ToManifestEntries(preflightList);

                /*
                 * 2026-09-27 真机（`rar-android-722.132.apk`）加的两笔账：
                 *
                 * · previousVerification —— 上一个候选的校验数字。**连续两个候选结果一模一样**时，
                 *   换密码显然不会改变任何东西，再试下去只是白烧时间（真机白试了 9 次 / 5 分钟）；
                 * · passwordIrrelevant —— 结论是"产物不完整、与密码无关"（包没加密 / 结果不变 / 试到上限之外）。
                 *   收尾时据此**不许**再把它说成「密码错误」——那句话把用户指去核对密码本，方向全错。
                 *
                 * · stoppedByStopRequest —— 用户点了「停止后续」，候选循环在**候选之间**停下的那一档
                 *   （用户 2026-09-27："停止后续就是停止所有的东西"）。
                 */
                OutputVerificationResult? previousVerification = null;
                bool passwordIrrelevant = false;
                string? passwordIrrelevantReason = null;
                bool stoppedByStopRequest = false;

                /*
                 * ⛔ 上限必须在**动过候选表之后**重算（2026-09-26 真机逮到的缺陷）：
                 * 上面"跳过空密码"那一档会 `RemoveAll` 掉空密码候选，候选表因此少一个 ——
                 * 而 `maxPasswordAttempts` 是**列目录之前**算的（`Math.Min(候选数, 每层上限)`）。
                 * 候选数 ≤ 上限时两者相等，于是循环最后会多跑一次、`candidates[i]` 越界：
                 * 真机现场（加密包 + 候选 10 个）就是 `开始解压，密码候选 9/10` 之后直接
                 * `任务失败：… Index was out of range`，结论落成一句看不懂的"未知错误"。
                 *
                 * 顺带把"有没有被上限截断"一起重算：少了那一个之后可能已经"全都试过了"，
                 * 仍然按旧值判就会把"密码都不对"误报成「达到密码尝试上限」。
                 */
                maxPasswordAttempts = Math.Min(candidates.Count, attemptLimit);
                candidatesTruncated = candidates.Count > maxPasswordAttempts;

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    /*
                     * ===== 「停止后续」在候选之间生效（用户 2026-09-27 真机）=====
                     *
                     * 现场：08:25:32 他点了「停止后续」，08:25:33 程序照旧开始"密码候选 10/10"，
                     * 又跑了 32 秒才结束 —— 他原话："我都暂停了，你还在尝试新的密码"。
                     *
                     * 为什么判在这里：`StopAfterCurrent` 取消的是**批级** token（只阻止启动后续任务），
                     * 每个任务持有自己的取消源（不变量 9：停止后续 ≠ 强杀当前）。所以任务内部要自己看
                     * `IsStopping` 这个"用户想让一切都停下来"的信号 —— **候选之间**是天然的落点：
                     * 正在解的那一个不打断（不留半个产物），下一个密码一个都不再试。
                     */
                    if (IsStopping && attemptedCandidates > 0)
                    {
                        stoppedByStopRequest = true;

                        AppendLog(
                            "WARN",
                            $"{task.FileName}：已按「停止后续」中断 —— 剩余 "
                            + $"{maxPasswordAttempts - attemptedCandidates} 个候选密码不再尝试。");

                        break;
                    }

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;
                    attemptedCandidates++;

                    task.Operation = StatusText.OpExtract;
                    task.Status = StatusText.Extracting;
                    task.ProgressText = StatusText.ProgressProcessing;
                    task.LastUpdatedTime = DateTime.Now;

                    /*
                     * ===== 候选先过"最小条目"这一关，通过了才去解整包（第 37 条）=====
                     *
                     * 探针只解清单里最小的那个文件（≤16 MiB，见 PasswordProbe）；解不开 = 这个候选不对，
                     * 而**整包一个字节都没动**。挑不出探针时 probeEntryPath 为 null，这里整段跳过（退回老路）。
                     */
                    if (probeEntryPath != null)
                    {
                        PasswordProbeOutcome probe = await ProbePasswordAsync(
                            engineArchivePath,
                            extractOptions,
                            engineOutputPath,
                            probeEntryPath,
                            selectedPassword,
                            progressSink,
                            cancellationToken);

                        if (probe == PasswordProbeOutcome.Rejected)
                        {
                            hasWrongPassword = true;
                            task.PasswordStatus = StatusText.WrongPassword;

                            AppendLog(
                                "WARN",
                                $"{task.FileName}：密码预检不通过（只解了 {PasswordProbe.Describe(probeEntryPath, probeEntrySize)}，"
                                + "整包一个字节都没动）—— 这个候选不对，试下一个。");

                            continue;
                        }

                        if (probe == PasswordProbeOutcome.Verified)
                        {
                            AppendLog(
                                "INFO",
                                $"{task.FileName}：密码预检通过（{PasswordProbe.Describe(probeEntryPath, probeEntrySize)} 解开了）"
                                + "—— 这个候选是对的，开始解整包。");
                        }
                    }

                    AppendLog("INFO", $"{task.FileName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    /*
                     * ===== 每换一个候选，先把暂存产物清空（用户 2026-09-25 真机铁证）=====
                     *
                     * 现场（日志 8:27:53–54 十条候选）：偶数候选全是"引擎说成功但产物 2 个 / 0 字节"，
                     * 奇数候选全是"密码错误" —— 奇偶交替。查下来是**上一次尝试的垃圾把下一次废掉**：
                     *
                     * ① 7z 用**错密码**解一个 AES ZIP 时，会先在输出目录里建出 0 字节的桩文件，**然后**才报
                     *    Wrong password（退出码 2）—— 实测如此（用故意错的密码跑，目录里照样留下 2 个 0 字节文件）；
                     * ② 旧代码只在"引擎说成功但校验不过"那一支清产物，"密码错误"这一支**直接 continue**，
                     *    于是桩文件留在了暂存目录里；
                     * ③ 下一次尝试带着 `-aos`（已存在就跳过）跑：7z 看到那两个文件已存在，**直接跳过、退出码 0**，
                     *    我们随后校验到"2 个文件 / 0 字节"，把这个候选当成废票扔掉并清掉产物；
                     * ④ 于是**真正正确的那个候选，只要排在"错密码候选"后面，就永远没机会真正解一次** ——
                     *    用户密码本里的第一条正是这样被废掉的。
                     *
                     * 修法：每个候选都从**空目录**开始。宁可每次多删一次（暂存目录本来就是我们的、内容都是垃圾），
                     * 也不能让上一次的桩文件把这一次的正确密码判成废票。
                     */
                    await DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);

                    ArchiveOperationResult extractResult = await _archiveEngine.ExtractAsync(
     BuildTrackedRequest(engineArchivePath, selectedPassword, engineOutputPath, progressSink),
     extractOptions,
     cancellationToken);

                    lastResult = extractResult;

                    /*
                     * 详细档：这一次候选的命令行与原话（**无论成败**）。
                     *
                     * ⚠ 失败的"关键行"刻意**不在这里**写：候选循环每试一个错密码就要报一次错，
                     * 十个候选就是十条 ERROR —— 那是噪声，不是信号。失败原话在**结论那一处**
                     * 写一次（见下面 `conclusionResult` 那一段）。
                     */
                    if (VerboseTaskLogEnabled)
                    {
                        EngineOutputLog.LogVerbose(AppendLog, task.FileName, extractResult);
                    }

                    if (extractResult.DetectedErrorType == "Cancelled" ||
                        extractResult.Status == StatusText.Cancelled ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (extractResult.Success)
                    {
                        /*
                         * ===== 关键修复（用户 2026-09-24 真机铁证）======
                         *
                         * 引擎说"成功"**不等于**产物是好的：7z 会在密码不对 / 数据不全时写出 0 字节的桩文件，
                         * 而退出码仍然是 0。老逻辑拿到 Success 就直接落「解压成功」并 break ——
                         * 用户看到的是一句"解压成功"，目录里却什么都没有，而且剩下的候选密码再也没被试过。
                         *
                         * 现在：**先校验，再决定这个候选算不算数**。
                         * 判否 → 当成"这个候选不行"：清掉它的垃圾产物、continue 试下一个候选。
                         * 只有真的校验通过，才落成功；无论哪种情况，收尾都走同一个 PostProcessSuccessAsync
                         * （校验未通过时它会**顶掉成功结论**、不动源包、也不会把 0 字节产物搬出去）。
                         */
                        StageProductVerification stage = await VerifyStageProductsAsync(
                            task,
                            engineArchivePath,
                            selectedPassword,
                            engineOutputPath,
                            cancellationToken);

                        // 这一层的尺子换成"用这个候选的密码列出来的那一份"（部分完成发布要用它逐条对账）。
                        if (stage.List is { Success: true })
                        {
                            lastLayerManifestEntries = PartialPublishPlanner.ToManifestEntries(stage.List);
                        }

                        if (stage.Verification.Verified)
                        {
                            extractSuccess = true;

                            task.Status = StatusText.ExtractSuccess;
                            task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? StatusText.PasswordNotNeeded : StatusText.PasswordCorrect;
                            task.ErrorMessage = string.Empty;

                            // 同"先测试再解压"那条分支：密码成功记录挂源文件，不挂工作区里的临时文件。
                            _passwordService.RecordPasswordSuccess(task.CurrentPath, selectedPassword);
                        }
                        else
                        {
                            lastVerification = stage.Verification;
                            task.PasswordStatus = StatusText.WrongPassword;

                            /*
                             * 还有候选可试吗？有 → 这个候选不算数，清掉它的产物接着试。
                             *
                             * 例外一（**产物越界**，不变量 4）：它比"候选不行"更具体，交给收尾去说。
                             * 越界的现场往往"暂存目录里什么都没有"（产物被写到了目标根之外），
                             * 在这里判成"这个候选不行"会把真正的原因换成一句泛泛的"没有产物"，
                             * 用户就看不到那条唯一能行动的信息。
                             *
                             * 例外二（**最后一个候选**）：没有下一个可试了，此时走收尾而不是把产物删掉 ——
                             * 半截产物（真的解出了一部分的那种）能落在用户看得见的地方，
                             * 而 0 字节产物在那一步天然什么都不会搬（PlanFinalLayout 的零字节规则）。
                             * 两种情况的结论都一样：**不许成功**（收尾里的校验闸门会落成失败）。
                             */
                            string? landingViolation = await Task.Run(
                                () => RecursiveExtractor.FindLandingViolation(engineOutputPath),
                                cancellationToken);

                            bool hasMoreCandidates = i < maxPasswordAttempts - 1;

                            /*
                             * ===== 这次失败到底是不是"密码不对"？（2026-09-27 真机铁证）=====
                             *
                             * 现场：`rar-android-722.132.apk` 用**空密码**解出 1238 个文件（校验说"少 33 个"），
                             * 程序把这句话当成"这个候选不行"，于是把 10 个候选密码全试了一遍（5 分钟），
                             * 最后报「密码错误」—— 而那个包**根本没有加密**。那次"少 33 个"的真因是
                             * **32 组仅大小写不同的同名条目**（Windows 物理上放不下），已在校验口径里修掉；
                             * 这里修的是**另一半**：结论不能想当然地归给密码。
                             *
                             * 三条判据全部来自事实（⛔ 不比对文案）：
                             *  ① 包**没有加密**（清单里一个加密条目都没有）→ 密码与产物无关；
                             *  ② 与**上一个候选的结果完全相同**（同文件数、同字节数）→ 换密码不会改变结果；
                             *  ③ 用户已经点了「停止后续」→ 一个都不再试。
                             * 命中任一条就不再烧候选，并把这个结论带进收尾（⛔ 不许说成「密码错误」）。
                             */
                            /*
                             * ⛔ 前置门槛：**必须真的解出了内容**（字节数 > 0）才谈得上"与密码无关"。
                             *
                             * 为什么这一条不能省（2026-09-25 那条修复正是它）：**错密码**的典型签名就是
                             * "引擎退出码 0、只写出一堆 0 字节桩文件"，而那种结果**必须继续试下一个候选** ——
                             * 密码本里正确的那个可能就排在后面。只有"解出了真东西、只是比清单少"这种形状，
                             * 换密码才不可能有帮助（真机那个 apk：1238 个真文件 / 13.5 MB）。
                             */
                            bool realContentProduced = stage.Verification.ActualTotalSize > 0;

                            // 同一件事的事实位（换引擎兜底那条判据要它）：引擎**说成功**但产物没通过校验，
                            // 而盘上确实留下了非零字节 ⇒ 这个引擎进得去这一份的数据流。
                            anyCandidateWroteBytes |= realContentProduced;

                            bool listSaysNoEncryption =
                                realContentProduced && stage.List is { Success: true, IsEncrypted: false };

                            bool sameAsPrevious =
                                realContentProduced &&
                                previousVerification != null &&
                                previousVerification.ActualFileCount == stage.Verification.ActualFileCount &&
                                previousVerification.ActualTotalSize == stage.Verification.ActualTotalSize;

                            previousVerification = stage.Verification;

                            string? stopReason = !hasMoreCandidates
                                ? "没有更多候选可试，按失败收场"
                                : stoppedByStopRequest
                                    ? "已按「停止后续」中断，剩余候选不再尝试"
                                    : listSaysNoEncryption
                                        ? $"这个包没有加密（清单里没有加密条目），换密码不会改变结果，"
                                          + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                        : sameAsPrevious
                                            ? $"与上一个候选的结果完全相同，换密码不会改变结果，"
                                              + $"剩余 {maxPasswordAttempts - attemptedCandidates} 个候选不再尝试"
                                            : null;

                            if (landingViolation == null && stopReason == null)
                            {
                                AppendLog(
                                    "WARN",
                                    $"{task.FileName}：结果校验 —— {stage.Verification.Message}" +
                                    "（这个候选不算数，继续尝试下一个候选密码）");

                                await DiscardStageProductsAsync(task, engineOutputPath, cancellationToken);
                                continue;
                            }

                            /* 结论是"与密码无关"时，收尾必须按这个走（否则会给用户指错方向）。 */
                            passwordIrrelevant = listSaysNoEncryption || sameAsPrevious;

                            /*
                             * 理由要**原样带进收尾**（2026-09-27 自查逮到的措辞 bug）：
                             * 两句结论对应的现场完全不同 —— 一个是"包根本没加密"，
                             * 一个是"加密了但换哪个候选结果都一样"。收尾那句如果一律写"没有加密"，
                             * 用户拿着一个明明加密的包会以为程序搞错了。
                             */
                            passwordIrrelevantReason = listSaysNoEncryption
                                ? "这个包没有加密（清单里没有加密条目）"
                                : sameAsPrevious
                                    ? "连续两个候选的结果完全相同"
                                    : null;

                            AppendLog(
                                "WARN",
                                $"{task.FileName}：结果校验 —— {stage.Verification.Message}" +
                                "（" + (landingViolation != null
                                    ? "产物越界，按越界收场，不再换密码"
                                    : stopReason ?? "按失败收场") + "）");
                        }

                        // 收尾否掉结论（越界 / 超预算 / 定稿失败 / 校验未通过 / 源包没有搬成）时不许写"解压成功"。
                        // knownList 传刚校验过的那份清单：定稿那一步用**同一份**再校验，两条路不许各列一次目录。
                        bool conclusionStands = await PostProcessSuccessAsync(
                            task, engineArchivePath, selectedPassword, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken, stage.List);

                        if (conclusionStands && task.Status == StatusText.ExtractSuccess)
                        {
                            AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        }

                        break;
                    }

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        /*
                         * ===== 「密码其实已经对了，坏的是数据」这一档（用户 2026-10-01 真机 `giu910`）=====
                         *
                         * 现场：一组 17.7 GiB 的 7z `-mhe`（19 GB 源包）——
                         * 候选 1/2 秒级失败（头没打开），**候选 3 把整个包解到 98%**（13 分钟），
                         * 最后 `CRC Failed in encrypted file. Wrong password? : giu\V\fulibl.net (15).mp4`。
                         * 老口径把这一句当成"这个候选不对"，于是**解压完又回去试密码**（候选 4..10），
                         * 13 分钟的产物全部丢弃、最后报「密码错误」。用户原话：
                         * 「**为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候，而不是解压完，
                         * 这才 20G，大一点你这就是在浪费用户时间**」。
                         *
                         * 判据**只读事实**（⛔ 不比任何文案）：这一趟在暂存目录里**真解出来的东西**有多少。
                         * 「密码错」的签名是"第一份数据就过不了 ⇒ 只留 0 字节桩 / 至多一个文件"；
                         * 「密码对、有个别文件坏了」的签名是"整个包基本都解出来了、只死在那一个坏文件上"。
                         * 后者再往下试密码**没有任何意义**（密码已经被这天趟解压本身证明了）。
                         */
                        bool wrongPwProvenByThisAttempt =
                            TryMeasureProducedContent(engineOutputPath, out int wrongPwFiles, out long wrongPwBytes);

                        // 同一件事的事实位（换引擎兜底那条判据要它）：这一趟盘上留下了**非零字节** ⇒
                        // 引擎进得去这一份的数据流（0 字节桩不算，见上面 `anyCandidateWroteBytes` 的说明）。
                        anyCandidateWroteBytes |= wrongPwBytes > 0;

                        if (wrongPwProvenByThisAttempt)
                        {
                            passwordProvenDataFailed = true;
                            provenProducts = (wrongPwFiles, wrongPwBytes);
                            break;
                        }

                        hasWrongPassword = true;
                        task.PasswordStatus = StatusText.WrongPassword;

                        // 同上：候选不对是预期之内，⛔ 不许记 WARN（那会让"成功就丢"整条失效）。
                        AppendLog("INFO", $"{task.FileName}：这个密码候选不对，继续试下一个。");
                        continue;
                    }

                    /*
                     * ===== 两义那一档：引擎自己说"密码可能不对、也可能数据坏了"（用户 2026-09-30 真机）=====
                     *
                     * 与"密码错误"同一处置：**继续试下一个候选**（与 7-Zip 侧那句
                     * `CRC Failed in encrypted file. Wrong password?` 同一口径，见 Item37SafetyTests）。
                     * ⛔ 不许像"已损坏"那样当场 break —— 那正是真机"11 个候选只试了第 1 个"的成因；
                     * 也⛔ 不许落成"密码错误"（那是把真损坏说成密码错）。
                     * 收尾时状态与原因都取 `lastResult`（= 这一次的原话），于是结论里两种可能都在。
                     */
                    if (extractResult.DetectedErrorType == EngineErrorTypes.PasswordOrCorrupted)
                    {
                        // 与上面"密码错误"那一支同一条判据：**真解出了东西** ⇒ 密码已经证实，别再试候选。
                        bool ambiguousProvenByThisAttempt =
                            TryMeasureProducedContent(engineOutputPath, out int ambiguousFiles, out long ambiguousBytes);

                        // 同上：这也是"引擎写出过非零字节"的一次。
                        anyCandidateWroteBytes |= ambiguousBytes > 0;

                        if (ambiguousProvenByThisAttempt)
                        {
                            passwordProvenDataFailed = true;
                            provenProducts = (ambiguousFiles, ambiguousBytes);
                            break;
                        }

                        ambiguousResult ??= extractResult;
                        task.PasswordStatus = StatusText.PasswordNeed;

                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.CandidatePasswordOrCorruptedLogFormat,
                                task.FileName));

                        continue;
                    }

                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    // 同上面那处：内嵌归档 + 分卷缺失时补一句事实（第 42 条）。
                    string volumeHint = BuildEmbeddedVolumeMissingHint(task, extractResult.DetectedErrorType);

                    if (!string.IsNullOrEmpty(volumeHint))
                    {
                        task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");

                    break;
                }

                if (!extractSuccess)
                {
                    /*
                     * ===== 换引擎再试一遍（用户 2026-10-05 拍板"补"；唯一实现 PasswordEngineFallback）=====
                     *
                     * <para><b>真机现场</b>（AGENTS.md §11.5 最后一条）：同一个包、同一条密码 ——
                     * WinRAR 6.11 退出码 0、两卷原样解出；内置 7-Zip 26.03 退出码 2、
                     * <c>Wrong password</c>、一个字节都没出来。而候选循环只用 7-Zip，
                     * <c>WrongPassword</c> 又落在"不换引擎"那一档（AGENTS.md §3）⇒ 程序把
                     * "这个引擎吃不下这个 ZIP"如实报成了「达到密码尝试上限」——一条假结论。</para>
                     *
                     * <para>⛔ **正常路径一个字不改**：候选循环**内部**遇到 <c>WrongPassword</c>
                     * 照旧换下一个候选、照旧不换引擎（省时间）。这里只在"循环已经跑完 + 失败是密码类 +
                     * 归档确实加密 + 本机有 WinRAR"四条同时成立时才动，用的还是**同一批候选**。</para>
                     *
                     * <para>位置：紧跟候选循环之后、失败结论之前 —— 命中就按"这个密码是对的"
                     * 直接走收尾（与循环里成功那一支**同一个出口**），否则落到下面那段原样的失败处理。</para>
                     */
                    PasswordFallbackTriggerFacts fallbackFacts = BuildPasswordFallbackFacts(
                        extractSuccess: false,
                        hasWrongPassword,
                        ambiguousResult != null,
                        passwordProvenDataFailed,
                        passwordIrrelevant,
                        stoppedByStopRequest,
                        lastResult,
                        attemptedCandidates,
                        maxPasswordAttempts,
                        candidates.Count,
                        task.IsEncrypted || PasswordProbe.ShouldSkipEmptyPassword(preflightList) || preflightSaidEncryptedHeaders,
                        anyCandidateWroteBytes);

                    if (PasswordEngineFallback.ShouldFallBack(fallbackFacts))
                    {
                        FallbackCandidateSlice fallbackSlice = BuildFallbackCandidateSlice(candidates, maxPasswordAttempts);

                        PasswordEngineFallbackOutcome fallback = await PasswordEngineFallback
                            .TryAsync(new PasswordFallbackRequest
                            {
                                ArchivePath = engineArchivePath,
                                Candidates = fallbackSlice.Values,
                                TargetDirectory = engineOutputPath,
                                Label = task.FileName,
                                FailedEngineName = ResolveFailedEngineName(lastResult),
                                FailedErrorType = lastResult?.DetectedErrorType,

                                // 与循环里同一个清法（⛔ 不在这里另写一份"怎么清"）。
                                ResetTarget = token => DiscardStageProductsAsync(task, engineOutputPath, token),

                                // 与循环里同一个描述器（⛔ 绝不出明文）。
                                DescribeCandidate = (_, ordinal) => fallbackSlice.Describe(ordinal),

                                Log = AppendLog,
                                MainEngine = _archiveEngine,
                                CancellationToken = cancellationToken
                            })
                            .ConfigureAwait(true);

                        if (fallback.Succeeded)
                        {
                            /*
                             * 兜底报"命中"只说明 WinRAR 的退出码是 0 且产物目录里多出了东西 ——
                             * **不说明那些东西是对的**。所以收尾**先过这一层的结果校验**，
                             * 校验不过 ⇒ 这个候选不算数（不发布、不翻结论、现场留着）。
                             */
                            bool accepted = await FinishFallbackSuccessAsync(
                                    task,
                                    engineArchivePath,
                                    fallback.HitPassword ?? string.Empty,
                                    engineOutputPath,
                                    outputRedirectNote,
                                    sharedOutputRoot,
                                    oneClickRun,
                                    cancellationToken)
                                .ConfigureAwait(true);

                            /*
                             * ⛔ 这里**只置位、不 return**：下面那段"失败收尾"整块跳过（`extractSuccess` 就是它的
                             * 闸门），而方法**尾部**那段收尾（EndTime / 进度文案「完成」/ Operation）照旧要跑 ——
                             * 早退会让成功的一单永远停在"处理中"（显示口径那条口径见 AGENTS.md §11.5）。
                             */
                            extractSuccess = accepted;
                        }
                        else
                        {
                            passwordConclusionNote += fallback.Note;
                        }
                    }
                    else if (PasswordEngineFallback.ShouldReportMissingEngine(fallbackFacts))
                    {
                        /*
                         * 判据成立、本机没有 WinRAR ⇒ 不跑，**但必须说清**（用户 2026-10-05 口径：
                         * ⛔ 不许静默跳过、⛔ 不许把没跑过的兜底说成跑过了）。
                         */
                        passwordConclusionNote += PasswordEngineFallback
                            .ReportMissingEngine(task.FileName, ResolveFailedEngineName(lastResult), AppendLog)
                            .Note;
                    }
                    else if (PasswordEngineFallback.ShouldReportIndistinguishableEvidence(fallbackFacts))
                    {
                        /*
                         * 判据成立、但**主引擎已经写出过字节** ⇒ 不换引擎（证据分辨不出来，
                         * 见 `PasswordFallbackTriggerFacts.MainEngineWroteBytes`）。
                         * ⛔ 什么都不做、⛔ 结论一个字都不改，只写一行如实说明（不许静默跳过）。
                         */
                        PasswordEngineFallback.ReportIndistinguishableEvidence(
                            task.FileName,
                            ResolveFailedEngineName(lastResult),
                            AppendLog);
                    }
                }

                if (!extractSuccess)
                {
                    /*
                     * ===== 登记"失败时盘上还剩什么"（部分完成发布的输入，用户 2026-10-02）=====
                     *
                     * 位置刻意选在**候选循环已经跑完、结论已经定下来之后**：
                     * · 产物目录里此刻留着的是**最后一个候选**留下的东西（每个候选开工前会清一次），
                     *   所以尺子与现场必须是同一次候选的 —— `lastLayerManifestEntries` 与
                     *   `lastResult` 都跟着循环滚动，天然对齐；
                     * · 密码取 `selectedPassword`（最后一个候选的）：密码不对时收尾那次懒取清单
                     *   照样列不出来 ⇒ 判"没有清单" ⇒ 一个字节都不发布。
                     *
                     * ⛔ 这里不判断③页开关（收尾时才读）—— 详见 RecordPartialPublishCandidate 的说明。
                     */
                    RecordPartialPublishCandidate(
                        task,
                        engineOutputPath,
                        engineArchivePath,
                        selectedPassword,
                        lastLayerManifestEntries,
                        lastResult?.FailedEntryNames,
                        lastResult?.ReportedSubItemErrors ?? 0,
                        engineReportedFailure: lastResult != null && !lastResult.Success);

                    /*
                     * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
                     *
                     * 位置刻意选在**结论已经定下来之后**（`lastResult` / `task.Status` 都写好了），
                     * 而且整层只写一次：
                     * · 候选循环里每个错密码都记一条 ERROR 是噪声（十个候选十条），
                     *   而真正要看的恰恰是**最后那次**（走得最远的那一次，与失败原因同一份）；
                     * · 详细档在循环里已经逐候选写过命令行与原话了，这里不重复。
                     *
                     * ⛔ 出口只有 <see cref="EngineOutputLog"/> 一个（递归路径同一个）。
                     */
                    EngineOutputLog.LogFailure(AppendLog, task.FileName, lastResult);

                    /*
                     * 产物校验始终没过的收场（用户 2026-09-24 要求）：
                     * 状态取**更准确**的那一个 —— 试过密码的用「密码错误」语义（用户会先去核对密码本），
                     * 原因里必须同时给出三件事：校验的数字、试了几个候选、以及"产物始终不完整"。
                     *
                     * 判据里的第二个条件（`task.OutputVerification == Failed`）是**结构化**的，不是比对文案：
                     * 收尾可能给出了**更具体**的原因（产物越界 / 超预算 / 定稿失败 / 同名冲突跳过），
                     * 那些分支都会把校验结论落回 NotAttempted —— 只有"收尾的结论就是校验未通过"这一种，
                     * 才该在这里被换成带候选数量的那版措辞。否则"越界"那句唯一能行动的信息会被吃掉。
                     *
                     * ⛔ 绝不允许落到成功：这是不变量 6 在这一条路上的落点。
                     */
                    if (lastVerification is { Verified: false } failedVerification &&
                        task.OutputVerification == OutputVerificationOutcome.Failed)
                    {
                        /*
                         * 结论归给谁？三档互斥，全部读**事实**（⛔ 不比对文案）：
                         * · passwordIrrelevant（包没加密 / 候选结果始终一样）→ 是**产物不完整**，与密码无关；
                         * · stoppedByStopRequest（用户中途喊停）→ 是**没试完**，不能说"密码错了"；
                         * · 其余才是真的"试过密码都不对"。
                         */
                        bool verdictIsPasswordProblem =
                            (hasWrongPassword || attemptedCandidates > 1) && !passwordIrrelevant && !stoppedByStopRequest;

                        task.Status = verdictIsPasswordProblem ? StatusText.WrongPassword : StatusText.ExtractFailed;

                        task.PasswordStatus = verdictIsPasswordProblem
                            ? StatusText.WrongPassword
                            : passwordIrrelevant
                                ? StatusText.PasswordNotNeeded
                                : task.PasswordStatus;

                        string verdictTail = passwordIrrelevant
                            ? $"（已试 {attemptedCandidates} 个候选，产物始终不完整；{passwordIrrelevantReason}，问题不在密码上）"
                            : stoppedByStopRequest
                                ? $"（已试 {attemptedCandidates} 个候选，产物始终不完整；已按「停止后续」中断，剩余候选未尝试）"
                                : $"（已试 {attemptedCandidates} 个候选，产物始终不完整）";

                        task.ErrorMessage = $"{failedVerification.FailureMessage}{verdictTail}";

                        /*
                         * 机器可判的两个事实必须一起落（用户 2026-09-24 要求）：
                         * · OutputVerification = Failed —— 汇总/失败清单据此把它算进失败侧
                         *   （⛔ 不是靠比对"校验未通过"这句中文）；
                         * · Outcome = Failed —— 删源 / 搬源 / 续解 / 危险模式删除四条路都读它，
                         *   这一档一律不放行。
                         */
                        task.OutputVerification = OutputVerificationOutcome.Failed;
                        task.VerifyMessage = failedVerification.FailureMessage;
                        task.Outcome = TaskOutcome.Failed;

                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                    }
                    else if (task.Outcome is TaskOutcome.Failed or TaskOutcome.Skipped or TaskOutcome.PartiallyCompleted)
                    {
                        /*
                         * 收尾（或上面那条候选收尾）**已经给出了结论**：产物越界 / 超预算 / 定稿失败 /
                         * 同名冲突按用户选择跳过 —— 那些分支各自写好了状态与原因，这里一个字都不许覆盖。
                         *
                         * 为什么必须挡这一下（实测踩到的坑）：不挡的话下面那支会拿 `lastResult`（**引擎**那次的
                         * "成功"）把状态改回「解压成功」—— 引擎说成功、产物不行的那一整类结论就被抹掉了，
                         * 那正是本次要修的 bug 在收尾处的翻版。判据是**机器终态枚举**，不是比对文案。
                         */
                    }
                    else
                    {
                        // 与"先测试再解压"分支同一条规则：试到上限 ≠ 候选全都试过、更 ≠ 密码错误（AGENTS.md §9.2）。
                        bool stoppedByAttemptLimit = candidatesTruncated && hasWrongPassword;

                        if (stoppedByAttemptLimit)
                        {
                            task.Status = StatusText.PasswordAttemptLimitReached;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage =
                                $"已达到密码尝试上限：本层试了 {maxPasswordAttempts} 个候选（共 {candidates.Count} 个），未能确认密码。";
                        }
                        else if (passwordProvenDataFailed)
                        {
                            /*
                             * 密码**已经证实**（这一趟真解出了内容）之后的失败 = 数据层面。
                             * 收场落「文件损坏」而⛔ 不是「密码错误」（用户会拿着密码本白核对半天），
                             * 并如实点名"解出了多少、失败的是数据"——用户 2026-10-01 真机那一单就是
                             * 「1 个文件 CRC 失败、另外 557 个都好好的」。
                             */
                            task.Status = StatusText.Corrupted;
                            task.PasswordStatus = StatusText.PasswordCorrect;
                            task.ErrorMessage = string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.PasswordProvenDataCorruptedFormat,
                                provenProducts.Files,
                                provenProducts.Bytes);
                            task.Outcome = TaskOutcome.Failed;

                            AppendLog(
                                "WARN",
                                $"{task.FileName}：{task.ErrorMessage}"
                                + "（密码已经由这一趟解压本身证实 —— 不再往下试密码候选，"
                                + "也不再把已经解出来的内容当成'密码候选不对'的牺牲品）");
                        }
                        else if (ambiguousResult != null)
                        {
                            /*
                             * 两义那一档优先于"密码错误"（用户 2026-09-30 真机）：引擎自己说过
                             * "密码可能不对、也可能数据坏" —— 这一层只要出现过这种原话，
                             * 结论就必须同时保留两种可能，⛔ 不许被后面某个候选的"密码错误"
                             * 盖成一句断言（那正是"单独定原因"）。原话在 ErrorMessage 里带着。
                             */
                            task.Status = ambiguousResult.Status;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage = ambiguousResult.Message;

                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                        else if (hasWrongPassword)
                        {
                            task.Status = StatusText.WrongPassword;
                            task.PasswordStatus = StatusText.WrongPassword;
                            task.ErrorMessage = "密码错误或缺少正确密码";
                        }
                        else if (skippedOnlyEmptyPasswordBecauseEncrypted)
                        {
                            /*
                             * 加密包 + 一个候选都没有（只跳过的那一档空密码）：如实报"没有可用密码"，
                             * ⛔ 不许落到"未知解压失败"（那是"我们不知道"的意思，这里我们很清楚）。
                             */
                            task.Status = StatusText.WrongPassword;
                            task.PasswordStatus = StatusText.PasswordNeed;
                            task.ErrorMessage =
                                "整包已加密，但这次没有任何可用密码（空密码对已加密的包不可能成立，已跳过，"
                                + "没有浪费一次整包解压）。";

                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                        else if (lastResult != null)
                        {
                            /*
                             * 候选循环里那条失败结论会走到这里**再落一次**（状态与原因都按最后那次引擎结果来）。
                             *
                             * ⚠ 为什么"容器里装的是分卷第一卷"那句补充必须**在这里也补一遍**（第 42 条实测踩到）：
                             * 上面那个分支里我们已经把补充说明接在 `task.ErrorMessage` 后面了，
                             * 但这里会把整条消息**重新赋值**（= 覆盖），于是失败清单里又只剩引擎那句
                             * "后续卷、缺少首卷"——与事实正好相反。日志里有、清单里没有，用户看到的还是错的。
                             */
                            task.Status = lastResult.Status;
                            task.ErrorMessage = lastResult.Message;

                            string volumeHint = BuildEmbeddedVolumeMissingHint(task, lastResult.DetectedErrorType);

                            if (!string.IsNullOrEmpty(volumeHint))
                            {
                                task.ErrorMessage = task.ErrorMessage + " " + volumeHint;
                            }
                        }
                        else
                        {
                            task.Status = StatusText.ExtractFailed;
                            task.ErrorMessage = "未知解压失败";
                        }

                        if (task.Status == StatusText.WrongPassword || task.Status == StatusText.PasswordAttemptLimitReached)
                        {
                            AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                        }
                    }
                }

                /*
                 * ===== 收尾那行要点名"哪几条候选没试到"（用户 2026-10-05 真机第七批，要求 ①）=====
                 *
                 * 现场：候选共 12 个（空密码 + 密码本 11 条）、每层上限 10 ⇒ **第 11 条从没被试过**，
                 * 而日志只说「达到密码尝试上限」—— 用户看不出"是哪一条没试"，也就无从决定
                 * 要不要调上限 / 调顺序。点名用的是**既有描述器**（形如「尝试密码列表第 11 项」），
                 * ⛔ 绝不出明文（判据与折行都在 `PasswordCandidateGap`，两条解压路共用）。
                 *
                 * ⚠ 只在**真被每层上限截断**时才说（`candidatesTruncated`）：用户点了「停止后续」时
                 * 剩余候选同样是"没试到"，但那不是上限卡住的 —— 说成"上限卡住的"会指错方向
                 * （那一条的现场已经在候选循环里写过「已按「停止后续」中断」）。
                 */
                if (!extractSuccess && candidatesTruncated && !stoppedByStopRequest)
                {
                    PasswordCandidateGap.Gap? untriedCandidates = PasswordCandidateGap.Describe(
                        candidates,
                        maxPasswordAttempts,
                        maxPasswordAttempts + 1,
                        (item, ordinal) => _passwordService.BuildTryPasswordLogText(item, ordinal));

                    if (untriedCandidates.HasValue)
                    {
                        AppendLog(
                            "WARN",
                            PasswordCandidateGap.BuildLogLine(task.FileName, attemptLimit, untriedCandidates.Value));

                        passwordConclusionNote += PasswordCandidateGap.BuildConclusionSuffix(untriedCandidates.Value);
                    }
                }
            }

            /*
             * 把预检的"文件名已加密"结论恢复回来（见上面 preflightSaidEncryptedHeaders 的说明）。
             * 条件收得最紧：预检说过、而且这一层最后得到的**只是**"密码没通过"。
             */
            if (preflightSaidEncryptedHeaders &&
                (task.Status == StatusText.WrongPassword || task.Status == StatusText.ExtractFailed))
            {
                task.Status = StatusText.EncryptedHeaders;
                task.PasswordStatus = StatusText.PasswordNeed;

                if (!string.IsNullOrWhiteSpace(encryptedHeadersMessage))
                {
                    // 连原因也一起恢复：泛泛的"密码错误"会让用户去翻密码本，
                    // 而这里的结论是"先给它一个密码，它才肯把内容清单给你看"。
                    task.ErrorMessage = encryptedHeadersMessage;
                }

                AppendLog(
                    "WARN",
                    $"{task.FileName}：这次拿到的是「{StatusText.EncryptedHeaders}」的结论" +
                    "（不是泛泛的密码错误）—— 连内容清单都读不出来，需要正确密码才能列出内容。");
            }

            /*
             * ===== 密码类结论上那两句补充（位置刻意在"文件名已加密"恢复之后）=====
             *
             * 两件事都只在这条路上才有值（判据不成立时是空串 ⇒ 正常路径一个字都不改）：
             * ① **换引擎兜底**跑过 / 没跑成（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）；
             * ② **候选被每层上限截断**时点名"哪几条没试到"。
             *
             * ⛔ 必须排在上面那一支之后：它会把整条 ErrorMessage **重新赋值**（= 覆盖），
             * 写在它前面等于白写。
             */
            if (!string.IsNullOrEmpty(passwordConclusionNote) && !string.IsNullOrEmpty(task.ErrorMessage))
            {
                task.ErrorMessage += passwordConclusionNote;
            }

            // 密码类失败登记到本批，批次结束后合并成一次提示（不再在任务循环里逐个弹模态框）。
            RecordPasswordFailure(task);

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            /*
             * 实时进度到此为止：把百分比与"长时间无响应"提示一起清掉。
             *
             * 光靠 EndTime 已经能让 HasLiveProgress 判否（界面上不会再显示百分比），
             * 但字段本身留着会变成一颗定时炸弹：将来任何一处把 EndTime 清空（重跑、重置），
             * 上一轮的 87% 就会莫名其妙地重新出现在界面上。收尾时清干净是唯一稳妥的做法。
             */
            task.ClearProgress();

            task.Operation = StatusText.OpWaiting;

            /*
             * 进度文案只在"这一单没有失败 / 取消结论"时才写"完成"。
             * 越界、超预算、递归部分完成这些分支自己写了 ProgressFailed，取消写的是"已取消"，
             * 无条件覆盖会让任务详情窗口出现"状态：解压失败 / 进度：完成"这种自相矛盾的显示。
             */
            if (task.ProgressText != StatusText.ProgressFailed &&
                task.ProgressText != StatusText.ProgressCancelled)
            {
                task.ProgressText = StatusText.ProgressCompleted;
            }

            task.LastUpdatedTime = DateTime.Now;
        }

        // ================================================================
        // 换引擎兜底（用户 2026-10-05 拍板"补"）—— 单层路这一侧的接线
        // ================================================================
        //
        // 判据与执行体都在 `Extraction/PasswordEngineFallback.cs`（唯一实现，递归路转调**同一个**）。
        // 这里只负责把这一条路上的事实凑齐、把结果接回既有管线。⛔ 不在这里另写一份判据。

        /// <summary>
        /// 把"该不该换引擎再试一遍"要的事实凑齐（⛔ 判据本身不在这里，见
        /// <see cref="PasswordEngineFallback.ShouldFallBack"/>）。
        ///
        /// <para>每一条都只读**这一条路上已有的事实**（结构化错误类型、清单里的加密位、循环自己的计数），
        /// ⛔ 不比中文文案、⛔ 不新加计数器：</para>
        /// <list type="bullet">
        /// <item><description><b>密码类失败</b> = 最后一次引擎结果落在 <c>WrongPassword</c> /
        /// <c>NeedPassword</c> / <c>PasswordOrCorrupted</c>，**或**"引擎说成功、产物没通过校验"
        /// （那一次的结论同样是"所有候选都没解开"）；⛔ 排除"密码已证实、坏在数据上"
        /// （{@code passwordProvenDataFailed}）与"与密码无关"（{@code passwordIrrelevant}）两档 ——
        /// 它们的结论**不是**密码类，换引擎也不该改口。</description></item>
        /// <item><description><b>归档加密</b> = 识别阶段判过加密 / 预检清单里有加密条目 / 引擎报过加密头，
        /// 三条任一条成立（都读结构化事实）。</description></item>
        /// <item><description><b>候选跑完</b> = 循环把该试的都试了（{@code attemptedCandidates >= maxPasswordAttempts}，
        /// 且上限大于 0）—— ⛔ 不是"循环还在跑"、也⛔ 不是"失败就换引擎"。</description></item>
        /// </list>
        /// </summary>
        private PasswordFallbackTriggerFacts BuildPasswordFallbackFacts(
            bool extractSuccess,
            bool hasWrongPassword,
            bool sawAmbiguousPasswordFailure,
            bool passwordProvenDataFailed,
            bool passwordIrrelevant,
            bool stoppedByStopRequest,
            ArchiveOperationResult? lastResult,
            int attemptedCandidates,
            int maxPasswordAttempts,
            int candidateCount,
            bool archiveEncrypted,
            bool anyCandidateWroteBytes)
        {
            bool lastResultPasswordClass =
                lastResult != null &&
                (lastResult.IsWrongPassword || lastResult.IsNeedPassword || lastResult.IsPasswordOrCorrupted);

            /*
             * "引擎说成功、产物却没通过校验"那一档也算：候选循环跑完时它的结论同样是「密码错误」
             * （试过密码都不对 / 产物始终不完整），而**最后一次引擎结果**不是密码类错误 ——
             * 只看最后那次结果就会漏掉这一档。⛔ 但硬失败（退出码类错误）不算：那说明停因另有其人。
             */
            bool passwordConclusion =
                !passwordProvenDataFailed &&
                !passwordIrrelevant &&
                !stoppedByStopRequest &&
                (hasWrongPassword || sawAmbiguousPasswordFailure) &&
                (lastResultPasswordClass || lastResult is { Success: true });

            return new PasswordFallbackTriggerFacts
            {
                AlreadySucceeded = extractSuccess,
                PasswordClassFailure = passwordConclusion,

                // 候选循环跑完 = 该试的都试了（上限内）。⛔ 一条都没试（上限 0 / 没有候选）不算。
                CandidatesFinished = maxPasswordAttempts > 0 && attemptedCandidates >= maxPasswordAttempts,

                ArchiveEncrypted = archiveEncrypted,
                UserAskedToStop = stoppedByStopRequest || IsStopping,
                HasCandidates = candidateCount > 0,
                EngineAvailable = PasswordEngineFallback.IsEngineAvailable,

                /*
                 * 主引擎在这一层的候选上写出过非零字节 ⇒ **进得去这一份的数据流** ⇒ 不换引擎
                 * （WinRAR 用错密码时也会按原大小把乱码写进同一个目录，两个引擎给不出可分辨的证据）。
                 * 真机那一档（引擎吃不下这一份、一个字节都没出来）照旧允许换引擎。
                 * 详见 `PasswordFallbackTriggerFacts.MainEngineWroteBytes`。
                 */
                MainEngineWroteBytes = anyCandidateWroteBytes
            };
        }

        /// <summary>
        /// 结论里点名"是哪个引擎报的密码错"。
        /// 优先取**引擎自己盖的戳**（<see cref="ArchiveOperationResult.EngineDisplayName"/>，不变量 14），
        /// 取不到才退回注入的那个引擎实例的显示名。
        /// </summary>
        private string ResolveFailedEngineName(ArchiveOperationResult? lastResult)
        {
            if (!string.IsNullOrWhiteSpace(lastResult?.EngineDisplayName))
            {
                return lastResult!.EngineDisplayName;
            }

            return string.IsNullOrWhiteSpace(_archiveEngine.DisplayName)
                ? EngineIds.SevenZip
                : _archiveEngine.DisplayName;
        }

        /// <summary>
        /// 兜底要试的那一批候选（**同一批**：候选循环刚才在这个上限内试过的那些，保序）。
        /// 值 + 描述器一起给出，⛔ 描述器仍然只有既有那一个（<c>PasswordService.BuildTryPasswordLogText</c>）。
        /// </summary>
        private FallbackCandidateSlice BuildFallbackCandidateSlice(List<PasswordItem> candidates, int maxPasswordAttempts)
        {
            int take = Math.Max(0, Math.Min(maxPasswordAttempts, candidates.Count));

            var values = new List<string>(take);

            for (int index = 0; index < take; index++)
            {
                values.Add(candidates[index].Value ?? string.Empty);
            }

            return new FallbackCandidateSlice(candidates, values, _passwordService);
        }

        /// <summary>兜底候选的**值 + 来源说明**（说明只走既有描述器，⛔ 绝不出明文）。</summary>
        private readonly struct FallbackCandidateSlice
        {
            private readonly IReadOnlyList<PasswordItem> _candidates;
            private readonly PasswordService _passwordService;

            public FallbackCandidateSlice(
                IReadOnlyList<PasswordItem> candidates,
                IReadOnlyList<string> values,
                PasswordService passwordService)
            {
                _candidates = candidates;
                Values = values;
                _passwordService = passwordService;
            }

            /// <summary>交给 WinRAR 的候选值（保序）。</summary>
            public IReadOnlyList<string> Values { get; }

            /// <summary>第 <paramref name="ordinal"/> 项（1 起）的来源说明。</summary>
            public string Describe(int ordinal)
            {
                if (ordinal >= 1 && ordinal <= _candidates.Count)
                {
                    return _passwordService.BuildTryPasswordLogText(_candidates[ordinal - 1], ordinal);
                }

                return $"尝试密码候选第 {ordinal} 项：******";
            }
        }

        /// <summary>
        /// 兜底命中之后的收尾：**与候选循环里成功那一支逐字同一条路**
        /// （<see cref="VerifyStageProductsAsync"/> → <see cref="PostProcessSuccessAsync"/>）。
        ///
        /// <para>⛔ 这里不许另造一套"成功"的口径：产物此刻已经落在**同一个暂存目录**里
        /// （WinRAR 直接写进去的），所以校验、定稿、发布、源包处理全部照旧。</para>
        ///
        /// <para>⚠ 与循环里那一支的唯一差别：清单**没有**跟着 WinRAR 的密码重列一遍 ——
        /// 列目录本来就是与密码无关的一步（能列出来的包，任何候选都列得出来；
        /// <c>-mhe</c> 那种列不出来的包，我们手上那份就是 <c>Unavailable</c>）。
        /// 判"可证完整"的那一步因此照旧落在"判不出 ⇒ 什么都不做"上（不变量 12 的收尾口径一个字没放宽）。</para>
        ///
        /// <para><b>⛔ 顺序：先校验，再说话</b>（2026-10-05）。兜底那一句"命中"只说明
        /// <b>WinRAR 退出码 0 且产物目录里多出了东西</b>（见 <see cref="PasswordEngineFallback.TryAsync"/>），
        /// **不说明那些东西就是这一层的产物**。与候选循环里"引擎说成功也得先过校验"那一支逐字同一处置：
        /// 校验判否 ⇒ **这个候选不算数** —— 不落「解压成功」、不定稿发布、不改结论，
        /// 现场（这一趟写下的东西）原样留着交给调用方那段失败处理（③页「工作区残留」那一档看的就是它）。</para>
        /// </summary>
        /// <returns>true = 校验通过、已经按成功收尾；false = 这个候选不算数（调用方必须保持"没成功"）。</returns>
        private async Task<bool> FinishFallbackSuccessAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string engineOutputPath,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            /*
             * 先校验（⚠ 这里**还不许**动 task.Status / PasswordStatus / ErrorMessage）：
             * 判否时那些字段必须保持调用方原来那条密码类结论，一个字都不许被"成功"盖掉。
             */
            StageProductVerification stage = await VerifyStageProductsAsync(
                    task,
                    engineArchivePath,
                    password,
                    engineOutputPath,
                    cancellationToken)
                .ConfigureAwait(true);

            if (!stage.Verification.Verified)
            {
                /*
                 * 这个候选不算数（⛔ 不发布、⛔ 不翻结论）。实测会走到这里的形状：
                 * 换引擎兜底"解开"的东西与这一层的清单对不上（数目 / 字节对不上，或者干脆是空的）。
                 * 现场保留：这是最后一次尝试，不该再清一次盘（那是循环里"换下一个候选"那一支的事）。
                 */
                AppendLog(
                    "WARN",
                    $"{task.FileName}：换引擎兜底解出来的产物没有通过结果校验 —— {stage.Verification.Message}"
                    + "（这个候选不算数：不发布、结论照旧）。");

                return false;
            }

            task.Status = StatusText.ExtractSuccess;
            task.PasswordStatus = string.IsNullOrEmpty(password)
                ? StatusText.PasswordNotNeeded
                : StatusText.PasswordCorrect;
            task.ErrorMessage = string.Empty;

            // 与循环里同一处：密码成功记录挂源文件，不挂工作区里的临时文件。
            _passwordService.RecordPasswordSuccess(task.CurrentPath, password);

            bool conclusionStands = await PostProcessSuccessAsync(
                    task,
                    engineArchivePath,
                    password,
                    engineOutputPath,
                    outputRedirectNote,
                    sharedOutputRoot,
                    oneClickRun,
                    cancellationToken,
                    stage.List)
                .ConfigureAwait(true);

            if (conclusionStands && task.Status == StatusText.ExtractSuccess)
            {
                AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
            }

            return true;
        }

        /// <summary>
        /// 一个候选密码解出来的**产物校验**（进入定稿之前的那一道门）。
        ///
        /// <para>
        /// 用户 2026-09-24 的真机铁证（本案的核心修复）：7z 用候选 2 解出来 **0 字节**的
        /// <c>*.7z.001/.002</c>、校验当场判否，程序却①落「解压成功」②把 0 字节文件当内层包继续解
        /// ③把它们搬进 <c>其余物</c> ④**候选循环就此收场**（候选 3–10 一个都没试）。
        /// </para>
        /// <para>
        /// 所以校验必须在**候选循环内部**做，而且结论只有两种用法：
        /// <list type="bullet">
        /// <item><description>通过 → 才进定稿 / 归集 / 源包处理（<see cref="PostProcessSuccessAsync"/>）；</description></item>
        /// <item><description>判否 → **这个候选不算数**：清掉它的垃圾产物、继续试下一个候选。</description></item>
        /// </list>
        /// 清单（<see cref="ArchiveListResult"/>）跟着一起回传：定稿那一步要用**同一份**清单再校验一次，
        /// 不能让两条路各列一次目录各说各话（重复列目录还会成倍放大加密包的失败概率）。
        /// </para>
        ///
        /// <para><b>只允许在后台线程上跑</b>：列目录是引擎进程调用，量产物是整棵目录树的磁盘活。</para>
        /// </summary>
        private async Task<StageProductVerification> VerifyStageProductsAsync(
            ArchiveTask task,
            string engineArchivePath,
            string password,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArchiveListResult list = await _archiveEngine.ListAsync(
                ArchiveRequest.For(engineArchivePath, password),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            /*
             * ⚠ 2026-09-30：与 RunPostProcessWork 同一口径 —— **不把"取不到清单"折成 null**
             * （null 会把"为什么取不到"一起丢掉；Verify 内部对 null 与 Success=false 的处理逐字相同）。
             */
            OutputVerificationResult verification = await Task.Run(
                () => OutputVerifier.Verify(stageDirectory, list),
                cancellationToken);

            return new StageProductVerification(list, verification);
        }

        /// <summary>
        /// 用"解密后的开头 64 字节"给候选密码**排序**（用户 2026-09-25 第 38 条）。
        ///
        /// <para>形状对得上的（看得出文件魔数 / 像文本）排前面；读不出来的（引擎不支持、超时）保持原位；
        /// 读出**随机字节**的排最后 —— 那基本就是"密码不对"，但**一个候选都不丢**：
        /// 万一那个密码其实是对的、而这个文件恰好没有魔数，它仍然会被试到（只是排在后面）。
        /// 检查的候选数封顶在"每层尝试上限"个（每个约 0.1–0.3 秒，十来个也就一两秒）。</para>
        /// </summary>
        private async Task<List<PasswordItem>> RankCandidatesByDecryptedPrefixAsync(
            ArchiveTask task,
            string archivePath,
            SevenZipEngine engine,
            string entryPath,
            List<PasswordItem> candidates,
            int maxPasswordAttempts,
            CancellationToken cancellationToken)
        {
            var plausible = new List<PasswordItem>();
            var unknown = new List<PasswordItem>();
            var implausible = new List<PasswordItem>();
            var uncheckedCandidates = new List<PasswordItem>();

            int limit = Math.Min(candidates.Count, Math.Max(1, maxPasswordAttempts));

            for (int i = 0; i < candidates.Count; i++)
            {
                if (i >= limit)
                {
                    uncheckedCandidates.Add(candidates[i]);
                    continue;
                }

                PasswordItem candidate = candidates[i];

                byte[]? prefix = await engine.TryReadDecryptedPrefixAsync(
                    archivePath,
                    entryPath,
                    candidate.Value ?? string.Empty,
                    PasswordProbe.PrefixProbeBytes,
                    cancellationToken);

                if (prefix == null)
                {
                    unknown.Add(candidate);
                }
                else if (PasswordProbe.LooksLikeFileStart(prefix))
                {
                    plausible.Add(candidate);
                }
                else
                {
                    implausible.Add(candidate);
                }
            }

            var ordered = new List<PasswordItem>(candidates.Count);
            ordered.AddRange(plausible);
            ordered.AddRange(unknown);
            ordered.AddRange(implausible);
            ordered.AddRange(uncheckedCandidates);

            AppendLog(
                "INFO",
                $"{task.FileName}：密码预检 —— 只读「{entryPath}」解密后的开头 {PasswordProbe.PrefixProbeBytes} 字节"
                + $"（与包多大无关，一个字节都不落盘）：看着像正常文件开头 {plausible.Count} 个"
                + $"（先试），说不准 {unknown.Count} 个，看不出是文件开头 {implausible.Count} 个（排到最后再试）。"
                + "⛔ 一个候选都不会被丢掉。");

            return ordered;
        }

        /// <summary>
        /// 密码预检的三种结论（用户 2026-09-25 第 37 条）。
        /// </summary>
        private enum PasswordProbeOutcome
        {
            /// <summary>说不准（引擎给了别的错误 / 超时 / 探针目录写不进去）—— 调用方按老路解整包。</summary>
            Inconclusive,

            /// <summary>探针解开了：这个候选是对的，可以去解整包。</summary>
            Verified,

            /// <summary>探针明说密码不对：这个候选作废，整包一个字节都不用动。</summary>
            Rejected
        }

        /// <summary>
        /// 用候选密码**只解一个条目**（清单里最小的那个），验证这个密码对不对（用户 2026-09-25 第 37 条）。
        ///
        /// <para>真机代价对比：他那个 12.22 GiB 的包（名字可见、条目加密）用空密码解整包花了 **10 分 18 秒**
        /// 并写出 12 GiB 垃圾，最后才报密码错误；本方法解一个 1 KiB 的说明文件，**毫秒级**就能否掉同一个候选。</para>
        ///
        /// <para>落点：探针产物写在**产物（暂存）目录外面的兄弟位置**，判据只有一处
        /// （<see cref="PasswordProbe.ResolveProbeDirectory"/>，递归那条路用同一个出口）——
        /// 产物目录里的东西要参与校验与发布，探针文件一个字节都不许混进去；
        /// 无论成败都在 finally 里删掉（不变量 12：绝不写工作区之外）。</para>
        ///
        /// <para>三种结论的边界刻意保守：**只有引擎明说"密码错误"才判 Rejected**；
        /// 别的错误（不支持单条目解、读不了、超时）一律 Inconclusive，退回整包试解 ——
        /// 预检是省时间的手段，⛔ 不许因为它的误判把一个对的密码判死。</para>
        /// </summary>
        private async Task<PasswordProbeOutcome> ProbePasswordAsync(
            string archivePath,
            ExtractOptions extractOptions,
            string stageDirectory,
            string entryPath,
            string password,
            TaskProgressSink progressSink,
            CancellationToken cancellationToken)
        {
            /*
             * ⚠ 2026-10-05：落点与递归那条路**统一成一个出口**（`PasswordProbe.ResolveProbeDirectory`）——
             * 以前单层路把它建在**暂存（产物）目录里面**，而暂存目录里的东西要参与结果校验与发布；
             * 万一 finally 那次删除失败（占用 / 权限），探针文件会被当成本层的产物一起数、一起搬。
             * 现在两条路都是"产物目录的**兄弟位置**"（还在本任务的工作区里，不变量 12 不变）。
             */
            string probeDirectory = PasswordProbe.ResolveProbeDirectory(stageDirectory);

            try
            {
                Directory.CreateDirectory(probeDirectory);
            }
            catch
            {
                // 建不出目录（权限 / 盘）：预检做不了，让调用方走老路。
                return PasswordProbeOutcome.Inconclusive;
            }

            IReadOnlyList<string> previousEntries = extractOptions.IncludeEntries;

            try
            {
                extractOptions.IncludeEntries = new[] { entryPath };

                ArchiveOperationResult result = await _archiveEngine.ExtractAsync(
                    BuildTrackedRequest(archivePath, password, probeDirectory, progressSink),
                    extractOptions,
                    cancellationToken);

                if (result.DetectedErrorType == "Cancelled" || result.Status == StatusText.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Success)
                {
                    return PasswordProbeOutcome.Verified;
                }

                if (result.DetectedErrorType == "WrongPassword" || result.Status == StatusText.WrongPassword)
                {
                    return PasswordProbeOutcome.Rejected;
                }

                return PasswordProbeOutcome.Inconclusive;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 预检本身出错不是"这个包失败"：退回整包试解，结论仍然由整包那一步说了算。
                return PasswordProbeOutcome.Inconclusive;
            }
            finally
            {
                extractOptions.IncludeEntries = previousEntries;

                try
                {
                    if (Directory.Exists(probeDirectory))
                    {
                        Directory.Delete(probeDirectory, recursive: true);
                    }
                }
                catch
                {
                    // 删不掉只影响暂存目录的整洁（那一份马上会被 DiscardStageProductsAsync 整份清掉）。
                }
            }
        }

        /// <summary>
        /// 把上一轮候选留在暂存目录里的产物清干净，让下一个候选从零开始。
        ///
        /// <para>
        /// 为什么必须清：不清的话下一轮的校验会把**上一个候选的残留**一起算进去
        /// （0 字节桩 + 这一轮的产物 → 数字对不上，判出来的结论是错的），
        /// 而定稿也可能把两份候选的东西一起搬进最终目录。
        /// </para>
        /// <para>
        /// 清不掉时只写一行 WARN，**不打断候选循环**：最坏的结果是下一个候选校验不过，
        /// 那也会被如实报出来；为了"清残留失败"把整单判死只会让用户更难办。
        /// </para>
        /// </summary>
        private async Task DiscardStageProductsAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(
                    () =>
                    {
                        if (Directory.Exists(stageDirectory))
                        {
                            Directory.Delete(stageDirectory, recursive: true);
                        }

                        Directory.CreateDirectory(stageDirectory);
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：清理候选产物失败（{ex.Message}），继续尝试下一个候选。");
            }
        }

        /// <summary>一个候选的产物校验结论（清单 + 校验，见 <see cref="VerifyStageProductsAsync"/>）。</summary>
        private sealed class StageProductVerification
        {
            public StageProductVerification(ArchiveListResult list, OutputVerificationResult verification)
            {
                List = list;
                Verification = verification;
            }

            /// <summary>这一轮列目录拿到的清单（定稿那一步复用，避免再列一次）。</summary>
            public ArchiveListResult List { get; }

            /// <summary>产物校验结论（判否时 <c>FailureMessage</c> 里带着预期与实际的三个数字）。</summary>
            public OutputVerificationResult Verification { get; }
        }

        /// <summary>
        /// 准备本任务的暂存目录（入仓的第一步）：建出来，并把**上一次留下的残留**清掉。
        ///
        /// 为什么要清残留：暂存目录的 taskId 带源路径哈希，所以同一个包重试时落回同一个目录。
        /// 上一次失败/取消留下的产物如果不清，这一次的定稿会把它们一起当成"本次产物"搬进最终目录 ——
        /// 用户会看到上一轮的垃圾文件混进结果里（比"多几个文件夹"更难查）。
        ///
        /// 为什么必须在后台线程：<see cref="Directory.Delete(string, bool)"/> 删的是几十万个文件的树时
        /// 是秒级到分钟级的同步磁盘活，放在 UI 线程上就是"点了没反应"。
        ///
        /// 返回 null = 准备好了；返回非空字符串 = 失败原因（调用方落成失败状态并把原因写给用户）。
        /// </summary>
        private async Task<string?> PrepareStageDirectoryAsync(
            ArchiveTask task,
            string stageDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (Directory.Exists(stageDirectory))
                        {
                            bool hasLeftover = false;

                            try
                            {
                                hasLeftover = Directory.EnumerateFileSystemEntries(stageDirectory).Any();
                            }
                            catch
                            {
                                hasLeftover = true;
                            }

                            if (hasLeftover)
                            {
                                // 上一轮的残留：先清掉再解。清不掉就如实失败，绝不"接着往里解"。
                                Directory.Delete(stageDirectory, recursive: true);
                            }
                        }

                        return SafePathHelper.EnsureDirectoryExists(stageDirectory)
                            ? null
                            : $"暂存目录不存在且创建失败：{stageDirectory}";
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"准备暂存目录失败（{ex.Message}）：{stageDirectory}";
            }
        }

        /// <summary>
        /// 直读路线开跑之前，把**上一次回落**留下的抠取副本清掉。
        ///
        /// <para><b>为什么必须有这一条</b>：抠取副本的落点是
        /// <c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>，而 taskId 只取决于**源文件全路径** ——
        /// 所以"上一次走到回落、抠出来了一份，这一次直读成功"时，那份**等大**的旧副本还留在工作区里。
        /// 定稿那一步会把它当成本任务的其余物搬进用户目录，等于把这次省下来的空间又还回去了
        /// （用户要的正是"别再有这份副本"）。</para>
        ///
        /// <para>它是本任务**私有工作区**里的派生过程物（不是源文件，也不是别人的东西），
        /// 而本次已经确定不用它 —— 删掉是唯一不误导的做法。边界与
        /// <see cref="CleanupTaskWorkspaceDirectory"/> 同一口径：只在"确实在工作区根之下"时动手，
        /// 删不掉只记 WARN（绝不让一次清理失败影响解压结论）。</para>
        /// </summary>
        private void DiscardStaleCarvedArtifact(ArchiveTask task)
        {
            string carvedPath = BuildEmbeddedArchivePath(task);

            try
            {
                if (string.IsNullOrWhiteSpace(carvedPath) || !File.Exists(carvedPath))
                {
                    return;
                }

                if (!ArchivePathGuard.IsInsideRoot(_pathService.WorkDirectory, carvedPath, out string reason))
                {
                    AppendLog("WARN", $"{task.FileName}：上次留下的抠取副本不在工作区根之下，已跳过清理 —— {reason}");
                    return;
                }

                File.Delete(carvedPath);

                AppendLog(
                    "INFO",
                    $"{task.FileName}：已清掉上一次回落留下的抠取副本（本次走直读，不需要它）：{Path.GetFileName(carvedPath)}");
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    $"{task.FileName}：上次留下的抠取副本没清掉（{ex.Message}），它会在定稿时被当成其余物搬走。");
            }
        }

        /// <summary>
        /// 内嵌 ZIP 直读的**执行**那一步（用户 2026-09-24 需求第 6 条）。
        ///
        /// <para>只做三件事：把状态标成"解压中"、在后台线程流式解出条目、把结论交给
        /// <see cref="PostProcessSuccessAsync"/>（**同一个**收尾：校验 / 定稿 / 归集 / 其余物 / 源包处理）。
        /// 所以直读与"抠取 + 7z"两条路在用户眼里是同一套状态与同一套结论。</para>
        ///
        /// <para>取消原样抛出去：<c>ProcessExtractTaskAsync</c> 会落成「已取消」（不变量 6），
        /// 而直读器在抛之前已经把它写出去的东西全删掉了 —— 不留半成品、也不发布。</para>
        /// </summary>
        private async Task RunEmbeddedZipDirectExtractionAsync(
            ArchiveTask task,
            EmbeddedZipProbeResult plan,
            IReadOnlyList<string> passwordCandidates,
            ArchiveListResult? knownList,
            string stageDirectory,
            string outputRedirectNote,
            bool sharedOutputRoot,
            bool oneClickRun,
            TaskProgressSink progressSink,
            CancellationToken cancellationToken)
        {
            task.Operation = StatusText.OpExtract;
            task.Status = StatusText.Extracting;
            task.ProgressText = StatusText.ProgressProcessing;
            task.LastUpdatedTime = DateTime.Now;

            AppendLog(
                "INFO",
                $"{task.FileName}：开始 ZIP 直读解压（{plan.List?.FileCount ?? 0} 个文件 / " +
                $"{TaskSpaceEstimate.FormatSize(plan.TotalBytes)}，不生成等大临时副本）。");

            var progress = new DirectReadProgressBridge(progressSink);

            /*
             * 掐一下表（用户 2026-09-25 第 38 条："为什么要直读这么久…有没有什么办法改进"）。
             *
             * 直读 = 从源文件把这一段**读出来再写到成品**（等于一次同盘拷贝），所以它天然是**磁盘速度**：
             * 实测他那块 H 盘顺序读写只有 18–22 MB/s（同一个 473 MB 文件在 H: 内复制一次 24.9 秒），
             * 442 MB 的直读花 20 秒正好等于这个速度 —— **瓶颈在盘，不在读取器**。
             * 把实测速度写进日志，用户一眼就能判断"是盘慢还是程序慢"，不用猜。
             */
            var directReadWatch = System.Diagnostics.Stopwatch.StartNew();

            EmbeddedZipExtractResult extract = await Task.Run(
                () => EmbeddedZipStreamExtractor.Extract(
                    task.CurrentPath,
                    task.EmbeddedArchiveOffset,
                    task.EmbeddedArchiveEnd,
                    stageDirectory,
                    progress,
                    cancellationToken,
                    // 直读与 7z 两条路共用同一套上限（AGENTS.md §9.6"口径不许分叉"）：
                    // 传 null 会让直读退回硬编码那一份，用户在设置里调大的值就只对 7z 生效。
                    budgetOptions: BudgetLimits,
                    passwords: passwordCandidates),
                cancellationToken);

            directReadWatch.Stop();

            if (!extract.Success)
            {
                /*
                 * 直读失败（结构在两次探查之间变了 / 盘写不进去 / 解出来的字节数与清单不符）：
                 * **不回落**。回落要把几百 MB 到 2 GB 再拷一份，而失败原因（磁盘、权限、数据损坏）
                 * 拷一遍照样存在 —— 那只会让用户多等一场、还多占一份空间。如实报失败。
                 */
                task.Status = StatusText.ExtractFailed;
                task.ErrorMessage = extract.Message;
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("ERROR", $"{task.FileName}：ZIP 直读失败 —— {extract.Message}");
                return;
            }

            AppendLog(
                "INFO",
                $"{task.FileName}：ZIP 直读完成 —— {extract.Message}"
                + $"；用时 {directReadWatch.Elapsed.TotalSeconds:0.0} 秒 ≈ {DescribeThroughput(extract.WrittenBytes, directReadWatch.Elapsed)}"
                + "（直读就是「读出来再写进去」的一次拷贝，速度上限由这块盘决定）");

            task.Status = StatusText.ExtractSuccess;
            /*
             * 密码状态按**实际发生的事**写：加密包是被内置读取器用候选密码解开的，
             * 写成"不需要密码"会让报告与失败清单误导人（不变量 14 的同一口径）。
             */
            task.PasswordStatus = extract.WasEncrypted ? StatusText.PasswordCorrect : StatusText.PasswordNotNeeded;
            task.ErrorMessage = string.Empty;

            /*
             * 溯源（不变量 14）：这一单不是 7-Zip 解的，报告里不能写成 7-Zip。
             * 身份已经在探查那一步盖进引擎门面（RememberEmbeddedZipDirectRead），
             * 这里再把"谁干的、干出多少"落到任务自己的字段上。
             */
            task.EngineVerdict =
                $"{EmbeddedZipStreamExtractor.ReaderDisplayName} 读出 {extract.FileCount} 个文件 / {extract.WrittenBytes} 字节" +
                "（未调用 7-Zip，未生成临时副本）" +
                (extract.WasEncrypted
                    ? $"；密码：第 {extract.ResolvedPasswordIndex} 个候选用{ZipAesCrypto.DescribeEncoding(extract.ResolvedPasswordEncoding)}解开"
                    : string.Empty);

            bool conclusionStands = await PostProcessSuccessAsync(
                task,
                task.CurrentPath,
                string.Empty,
                stageDirectory,
                outputRedirectNote,
                sharedOutputRoot,
                oneClickRun,
                cancellationToken,
                knownList);

            if (conclusionStands && task.Status == StatusText.ExtractSuccess)
            {
                AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
            }
        }

        /// <summary>
        /// 直读的百分比 → 既有进度口径的桥（<see cref="TaskProgressSink"/>）。
        ///
        /// <para>为什么要桥：直读器是纯逻辑、不认识 <c>ArchiveProgress</c>（那是引擎层类型），
        /// 而界面上的百分比 / "当前条目" / "跨 10% 写一行日志" 全都由 sink 统一处理 ——
        /// 桥过来之后，直读那条路在界面上的表现与 7z 那条**完全一样**（同样能看出在动、同样能取消）。</para>
        /// </summary>
        private sealed class DirectReadProgressBridge : IProgress<int>
        {
            private readonly TaskProgressSink _sink;

            public DirectReadProgressBridge(TaskProgressSink sink)
            {
                _sink = sink;
            }

            public void Report(int percent)
            {
                // 不带"当前条目"：日志里"xxx.mp4：进度 20%"已经说清了是哪个任务，
                // 再挂一遍文件名只是噪声（引擎路径给的是**归档内**的条目名，这里没有对应物）。
                _sink.Report(new ArchiveProgress { Percent = percent });
            }
        }

        /// <summary>
        /// 内嵌归档抠出来之后落在哪：<c>&lt;work&gt;\&lt;taskId&gt;\&lt;包基名&gt;.zip</c>。
        ///
        /// 三条刻意的选择：
        /// ① 放工作区（<see cref="PathService.WorkDirectory"/>）而不是源目录旁边 ——
        ///    中间产物不得写进源目录（AGENTS.md §6 第 12 条）。抠出来的这一段只是为了能解压，
        ///    不是用户要的东西；定稿时它会被归入 <c>其余物</c>（契约 §3.2），取消/失败时留在工作区；
        /// ② 文件名沿用**源文件的包基名**，不改成随机临时名 ——
        ///    密码本"名称:密码"的映射匹配用的就是包基名，随机名会让本来能命中的密码全部落空，
        ///    用户看到的现象会是"同一个包以前能解开，现在说密码错误"；
        /// ③ 它是**暂存目录的兄弟**（不在 stage 里面）：stage 里的东西会被整体定稿搬走，
        ///    而它是解压的输入，不该被自己搬走。
        /// </summary>
        private string BuildEmbeddedArchivePath(ArchiveTask task)
        {
            string baseName = FileNameHelper.SanitizeFileName(FileNameHelper.GetArchiveBaseName(task.CurrentPath));

            /*
             * ⚠ 后缀**刻意**还是 `.zip`，哪怕抠出来的其实是 7z / RAR（用户 2026-09-25 第 42 条实测过才留下的）：
             *
             * · 完整的归档（容器里就是一整个 7z / RAR）**不看后缀**：两个引擎都按魔数认格式
             *   （本机实测：完整 7z 与完整 RAR 各自改名成 `封面.zip`，`7z l` / `UnRAR l` 都退出码 0 并列出条目）；
             * · 而"容器里装的是分卷的第一卷"时，后缀会改变引擎的判决：
             *   叫 `封面.zip` → 引擎报的是「分卷缺失」这一类（成因说得对）；
             *   叫 `封面.7z` → 引擎改口说「**文件损坏**」（把"缺后续卷"说成了"包坏了"，用户会去怀疑文件）。
             *   实测三种命名（同一段第一卷字节）：`.zip` → Is not archive / 分卷缺失一类；
             *   `.7z` 与 `.7z.001` → Unexpected end of archive（被归到"文件损坏"）。
             *   所以这里**不许**"顺手改成真实格式的后缀" —— 那会把最需要说清的那一档说歪。
             *   回归 = `SplitInsideContainerTests` 里那两条引擎行为证据。
             */
            return Path.Combine(_pathService.BuildTaskWorkDirectory(task), baseName + ".zip");
        }

        /// <summary>
        /// 内嵌归档 + 引擎报「分卷缺失」时补一句事实（用户 2026-09-25 第 42 条）。
        ///
        /// <para>真 7z 实测的现场：`封面.jpg` 里装着的**正是** `set.7z.001`，后续卷 `set.7z.002/.003`
        /// 就在同一个目录里。抠出来交给引擎之后，引擎按**名字**找同组的其他卷 ——
        /// 抠出来的那一段没有卷名，于是报"这是后续卷、缺少首卷"，而事实正好相反：
        /// 首卷在我们手上，缺的是"名字对得上的后续卷"。用户拿着那句话只会去满盘找首卷。</para>
        ///
        /// <para>判据全部来自文件系统（<see cref="OrphanVolumeSetDetector"/> 是纯函数）：
        /// 任务确实有内嵌归档 + 引擎报的确实是分卷缺失类 + 同目录里确实存在"缺首卷的那一组"。
        /// 三条缺一条就返回空串（⛔ 不猜、不拼凑）。</para>
        /// </summary>
        private string BuildEmbeddedVolumeMissingHint(ArchiveTask task, string? detectedErrorType)
        {
            if (task == null || task.EmbeddedArchiveOffset <= 0)
            {
                return string.Empty;
            }

            if (!IsVolumeMissingErrorType(detectedErrorType))
            {
                return string.Empty;
            }

            try
            {
                string directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return string.Empty;
                }

                IReadOnlyList<OrphanVolumeSet> orphans = OrphanVolumeSetDetector.Find(
                    CollectVolumeFilesInDirectory(directory).Select(Path.GetFileName));

                if (orphans.Count == 0)
                {
                    return string.Empty;
                }

                /*
                 * 只有一组时才敢说——两组以上说明这个目录里缺首卷的组不止一个，
                 * 我们无法确定容器里装的是哪一组（⛔ 宁可不说，也不指错组）。
                 */
                if (orphans.Count > 1)
                {
                    AppendLog(
                        "WARN",
                        $"{task.FileName}：同目录里有 {orphans.Count} 组缺首卷的分卷，无法确定容器里装的是哪一组；" +
                        "这一条补充说明就不给了（失败原因仍是引擎报的那一条）。");

                    return string.Empty;
                }

                string hint = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.EmbeddedFirstVolumeHintFormat,
                    orphans[0].Describe(),
                    task.FileName,
                    orphans[0].FirstVolumeName);

                /*
                 * 开关打开时补一句"这次已经试过了" —— 否则用户会以为程序没试
                 * （方案 A 落地后，失败原因里的建议必须与"当前这一档到底试没试"一致）。
                 */
                if (Settings.AssembleSplitVolumesFromContainer)
                {
                    hint += StatusText.EmbeddedFirstVolumeHintAssemblyTriedSuffix;
                }

                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.EmbeddedFirstVolumeHintLogFormat,
                        task.FileName,
                        orphans[0].Describe()));

                return hint;
            }
            catch
            {
                // 补充说明拿不到就算了：失败原因本身仍然成立，绝不因为这句让结论变化。
                return string.Empty;
            }
        }

        /// <summary>引擎报的是不是"分卷缺失"这一类（7-Zip 与 UnRAR 两套错误码都认）。</summary>
        private static bool IsVolumeMissingErrorType(string? detectedErrorType)
        {
            if (string.IsNullOrWhiteSpace(detectedErrorType))
            {
                return false;
            }

            return string.Equals(detectedErrorType, EngineErrorTypes.VolumeMissing, StringComparison.Ordinal)
                || string.Equals(detectedErrorType, Engines.SevenZip.SevenZipOutputParser.MissingFirstVolumeErrorType, StringComparison.Ordinal)
                || string.Equals(detectedErrorType, "VolumeMissing", StringComparison.Ordinal);
        }

        /// <summary>
        /// **第 42 条的全自动拼装**（用户 2026-09-25 选的方案 A：做，默认关）：
        /// 容器里装的是分卷第 1 卷、后续卷在容器外面时，在**工作区**里接出一套"名字成套、同在一个目录"的卷。
        ///
        /// <para>调用时机是**引擎已经说过"分卷缺失"之后**（不是抠完就拼）—— 这是这一档最重要的安全阀：
        /// 容器里装的是**完整**归档时 list 早就成功了，根本走不到这里；真的装的是"后续卷"时，
        /// 外面那一组里**有**标准首卷名，<see cref="SplitVolumeAssembler"/> 一组都挑不出来。
        /// 拼完还要再让引擎自己验一次，验不过就**退回原路**（结论一个字不变）。</para>
        ///
        /// <para>返回 <c>(null, null)</c> = 没拼 / 拼了但引擎不认 —— 调用方照旧走今天那条路。</para>
        /// </summary>
        private async Task<(string? FirstVolumePath, ArchiveListResult? List)> TryAssembleSplitVolumesAsync(
            ArchiveTask task,
            string carvedPath,
            IReadOnlyList<PasswordItem> candidates,
            CancellationToken cancellationToken)
        {
            try
            {
                string sourceDirectory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
                string workDirectory = _pathService.BuildTaskWorkDirectory(task);

                if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(workDirectory))
                {
                    return (null, null);
                }

                string assemblyDirectory = Path.Combine(workDirectory, SplitVolumeAssembler.AssemblyDirectoryName);

                /*
                 * 同一个任务被重试时这里可能有上一轮的残留：只清**我们自己造的那一个目录**
                 * （名字与父目录都对得上才清；对不上就一个字节都不动）。
                 */
                if (!TryResetAssemblyDirectory(assemblyDirectory, workDirectory))
                {
                    AppendLog("WARN", $"{task.FileName}：拼装目录里已经有程序没造过的东西，本次不拼（一个字节都不动）。");
                    return (null, null);
                }

                SplitVolumeFamily family = SplitVolumeAssembler.DetectFamilyByMagic(carvedPath);

                SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                    carvedPath,
                    family,
                    sourceDirectory,
                    assemblyDirectory,
                    CollectVolumeFilesInDirectory(sourceDirectory).Select(Path.GetFileName));

                if (!plan.CanAssemble)
                {
                    AppendLog("INFO", $"{task.FileName}：没有自动把两边的分卷接起来 —— {plan.Reason}");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.FileName}：{plan.Reason}正在工作区里接起来（你的源文件只读，一个字节都不动）。");

                /*
                 * 进度用 Progress<long>（回调回到捕获的上下文，与"抠取内嵌归档"那处的写法一致）——
                 * ⛔ 绝不在复制循环里直接碰界面/日志：那段代码跑在线程池上。
                 * 每 1 GiB 报一次：跨盘复制几个 5 GB 的卷时，用户要能看出"还在动"。
                 */
                long logged = 0;

                IProgress<long> copyProgress = new Progress<long>(copied =>
                {
                    if (copied - logged >= 1024L * 1024 * 1024)
                    {
                        logged = copied;
                        AppendLog("INFO", $"{task.FileName}：正在复制后续卷 …已复制 {TaskSpaceEstimate.FormatSize(copied)}");
                    }
                });

                SplitVolumeAssemblyResult assembled = await Task.Run(
                    () => SplitVolumeAssembler.TryAssemble(
                        plan,
                        availableSpace: null,
                        progress: copied => copyProgress.Report(copied),
                        cancellationToken),
                    cancellationToken);

                if (!assembled.Success)
                {
                    AppendLog("WARN", $"{task.FileName}：自动拼装没做成 —— {assembled.Reason}照旧按「分卷缺失」处理。");
                    return (null, null);
                }

                foreach (string line in assembled.LogLines)
                {
                    AppendLog("INFO", $"{task.FileName}：{line}");
                }

                /*
                 * 记在任务上：默认档的日志"成功就丢"，而"接过分卷"是**动过盘**的事实，
                 * 用户不改设置也要能看见 → 收尾那一行摘要会带上它（见 BuildTaskSummaryLine）。
                 */
                task.SplitVolumeAssemblyNote =
                    $"接上分卷（硬链接 {assembled.HardLinkCount} / 复制 {assembled.CopyCount}"
                    + (assembled.CopiedBytes > 0 ? $"，{TaskSpaceEstimate.FormatSize(assembled.CopiedBytes)}" : string.Empty)
                    + "）";

                // 拼完**必须让引擎自己验一次**：它认了才算数（与上面那次 list 用的是同一套候选顺序）。
                ArchiveListResult? assembledList = null;

                foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
                {
                    ArchiveListResult attempt = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(assembled.FirstVolumePath, candidate.Value),
                        cancellationToken);

                    if (attempt.Success)
                    {
                        assembledList = attempt;
                        break;
                    }
                }

                if (assembledList == null)
                {
                    AppendLog(
                        "WARN",
                        $"{task.FileName}：接上后续卷之后引擎仍然打不开这一套（可能这一组本身就是坏的），" +
                        "退回原路，照旧按「分卷缺失」报。");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.FileName}：接上后续卷之后引擎能打开了（{assembledList.FileCount} 个文件），" +
                    "本次就用工作区里那一套解压（源目录里一个字节都没动）。");

                return (assembled.FirstVolumePath, assembledList);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 拼装是"锦上添花"：出任何错都退回原路，⛔ 绝不让它改变任务的结论。
                AppendLog("WARN", $"{task.FileName}：自动拼装时出错（{ex.Message}），退回原路（结论不受影响）。");
                return (null, null);
            }
        }

        /// <summary>
        /// 清掉本任务上一轮留下的拼装目录（**只认我们自己造的那一个**：父目录 = 本任务工作区、
        /// 目录名 = <see cref="SplitVolumeAssembler.AssemblyDirectoryName"/>，两条都对得上才删）。
        /// </summary>
        private static bool TryResetAssemblyDirectory(string assemblyDirectory, string workDirectory)
        {
            try
            {
                if (!Directory.Exists(assemblyDirectory))
                {
                    return true;
                }

                string? parent = Path.GetDirectoryName(assemblyDirectory);

                if (parent == null
                    || !SafePathHelper.PathEquals(parent, workDirectory)
                    || !string.Equals(
                        Path.GetFileName(assemblyDirectory),
                        SplitVolumeAssembler.AssemblyDirectoryName,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                Directory.Delete(assemblyDirectory, recursive: true);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清理**本任务自己**的中间工作区目录：<c>&lt;work&gt;\&lt;taskId&gt;\</c>。
        ///
        /// 为什么必须有（端到端验收实测）：双面文件要先按偏移把它尾部那段真正的 ZIP 抠进工作区再解压，
        /// 一次**成功**的一键处理就在那个目录里留下近 1 GB 过程物（实测 733 MB + 167 MB 两个包）。
        /// 这些是从源文件按偏移可再生的派生数据，成功后留着纯属垃圾：跑几批就把盘吃掉，
        /// 而且 MainViewModel 启动时会把工作区根下的每个子目录都报成"未完成的工作区"，留着还会造成假警报。
        ///
        /// 入仓（stage）之后这一条覆盖的范围变大了：暂存目录也在这个工作区里，
        /// 所以**每个任务**（不只是抠过内嵌归档的）成功后都要清 —— 条件反而更简单：
        /// 定稿把内容物搬走之后，这里剩下的全是可再生的过程物。
        ///
        /// 只在这三件事同时成立时才删（与 AGENTS.md §9.5 的清理语义对齐）：
        /// ① 解压成功；② 输出校验通过；③ 没有被取消、没有越界结论、没有超预算、没有定稿失败。
        /// 取消 / 部分完成 / 校验失败 / 越界一律不删 —— 产物可能没落全，工作区里的过程物是用户唯一的线索。
        ///
        /// 安全边界（这是"删目录"，每一条都要有）：
        /// · 目录按**暂存目录的父目录**算出（不再按 CurrentPath 重算，见下），
        ///   再规范化确认它确实在工作区根**之下**（容器内校验，越界就什么都不删）；
        /// · 目录里只允许出现**我们自己造的那些**子目录（<c>stage</c>、第 42 条拼装用的 <c>volumes</c>，
        ///   见 <see cref="IsOurWorkspaceSubdirectory"/>）与本任务的过程物文件；
        ///   出现别的子目录说明这不是我们造的那个目录（最典型：任务名撞上了递归工作区的 <c>recursive</c>），
        ///   为安全起见一个字节都不碰；
        /// · 删失败（被占用 / 权限不足）只写日志，绝不让已经成功的任务变成失败。
        ///
        /// ⚠ 目录**必须**由 <paramref name="stageDirectory"/> 反推，不能再调
        /// <see cref="PathService.BuildTaskWorkDirectory"/> 重算：它的 taskId 含源路径哈希，
        /// 而一键处理成功后会按设置把源包搬进其余物并回写 <c>task.CurrentPath</c> ——
        /// 重算出来的就是另一个目录，清理会静默地什么都不做（实测口径：近 1 GB 过程物留在工作区）。
        /// </summary>
        private void CleanupTaskWorkspaceDirectory(ArchiveTask task, string stageDirectory)
        {
            if (task == null)
            {
                return;
            }

            string workRoot = _pathService.WorkDirectory;
            string taskDirectory = string.IsNullOrWhiteSpace(stageDirectory)
                ? _pathService.BuildTaskWorkDirectory(task)
                : Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(taskDirectory))
            {
                return;
            }

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string reason))
            {
                AppendLog("WARN", $"{task.FileName}：中间工作区目录不在工作区根目录之下，已跳过清理 —— {reason}");
                return;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                // 判据本体同上（一处纯函数，两条路径转调）。
                string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
                    subdirectories,
                    IsOurWorkspaceSubdirectory);

                if (foreign != null)
                {
                    AppendLog("WARN", $"{task.FileName}：中间工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清理：{taskDirectory}");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.FileName}：读不了中间工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");
                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            try
            {
                // 删除是**不可逆**的：动手之前先把"删什么、为什么"写进日志（AGENTS.md §9.5 的同一要求）。
                AppendLog(
                    "INFO",
                    $"{task.FileName}：解压成功且输出校验通过，清理本任务的中间工作区（{fileCount} 个文件 / {totalSize} 字节）：{taskDirectory}");

                Directory.Delete(taskDirectory, recursive: true);

                AppendLog("INFO", $"{task.FileName}：中间工作区已清理：{taskDirectory}");
            }
            catch (Exception ex)
            {
                // 删不掉只是"垃圾多留一会儿"，不影响任务结论（同 ExtractionWorkspace.Cleanup 的口径）。
                AppendLog("WARN", $"{task.FileName}：清理中间工作区失败（{ex.Message}），目录保留：{taskDirectory}");
            }
        }

        /// <summary>
        /// 这个子目录名是不是"我们自己造的"（决定清工作区时敢不敢整份删）。
        ///
        /// <para>现在认三个：<c>stage</c>（暂存/入仓）、<c>volumes</c>（第 42 条的拼装目录）、
        /// <c>_密码预检</c>（密码探针 —— 2026-10-05 起单层路也建在**产物目录外面**的兄弟位置，
        /// 名字与递归那侧同一个常量；⛔ 漏了它，异常退出留下的探针目录会把整份清理拦下）。
        /// ⛔ 出现别的子目录一律不删 —— 那最典型的情况是"任务名撞上了递归工作区的 <c>recursive</c>"，
        /// 顺着删会把别人的东西删掉。以后每加一个我们自己造的子目录，都要加到这里。</para>
        ///
        /// <para><c>internal</c>（2026-10-05）：白名单本身要能被守门用例直接钉住
        /// （它决定"删不删"，⛔ 不能只靠间接行为验）。</para>
        /// </summary>
        internal static bool IsOurWorkspaceSubdirectory(string name) =>
            string.Equals(name, PathService.StageDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, SplitVolumeAssembler.AssemblyDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, RecursiveExtractor.ProbeDirectoryName, StringComparison.OrdinalIgnoreCase);

        public void StopAfterCurrent()
        {
            try
            {
                IsStopping = true;

                /*
                 * 说明：
                 * 这里取消的是批量操作 token，用来阻止启动后续任务。
                 *
                 * 正在运行的任务各自持有独立的取消源，
                 * 所以这里只停止后续，不强制结束当前任务。
                 *
                 * 如果用户想立即取消正在运行的任务，需要再点“取消当前任务”。
                 */
                _operationCts?.Cancel();

                LogStopRequestOnce();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "停止后续任务失败：" + ex.Message);
            }
        }


        public void CancelCurrentTask()
        {
            try
            {
                // 先取快照再逐个 Cancel：Cancel 会同步触发回调，不能拿着列表的锁做这件事。
                List<CancellationTokenSource> running = SnapshotRunningTasks();

                if (running.Count == 0)
                {
                    AppendLog("WARN", "当前没有正在执行的任务可取消。");
                    return;
                }

                foreach (CancellationTokenSource cts in running)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }

                AppendLog("WARN", "已请求取消当前正在执行的任务，正在结束 7-Zip 进程。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "取消当前任务失败：" + ex.Message);
            }
        }
    }
}
