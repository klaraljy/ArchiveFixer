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
    /// <summary>递归器构建 / 逐层回收 / 递归上限（第 13 块，从主文件搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
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
        /// <para>⚠ 与 <see cref="RestHandlingThisBatch"/> 同样的寿命：批尾**不清**（链尾钩子在批次返回之后
        /// 才跑，清掉会让链尾以为"这一批没有逐层回收"而再去搬已经不存在的内层包）；
        /// 下一批开工时 <c>PrepareRestHandlingForBatch</c> 会重新写一次。</para>
        /// </summary>
        private bool _layerReclaimThisBatch;

    }
}