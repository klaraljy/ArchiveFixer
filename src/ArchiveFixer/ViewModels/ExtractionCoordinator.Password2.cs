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
        /// 本批（或本单）生效的「源包怎么处理」——**唯一出口**：
        /// `RunOptions`（一键处理的「本次选项」）优先，否则读设置。
        /// </summary>
        private SourceHandlingMode EffectiveSourceHandling
            => RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

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
                // ⚠ 2026-10-15：回收站那一档整条下线 ⇒ 这里只剩两档。
                RestHandlingModes.Delete =>
                    $"其余物：任务成功后彻底删除（{scope}，不可恢复）",
                _ => "其余物：保留（不动其余物）"
            };

            // ⚠ 2026-10-15：源包那一档只剩「留下 / 彻底删除（就地）」——「移入其余物」已随用户口径删除。
            string source = sourceMode == SourceHandlingMode.KeepInPlace
                ? "源包：留下（原地不动）"
                : "源包：彻底删除（就地删，不搬进其余物）";

            return source + "；" + rest;
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
                                $"{task.LogName}：同一个名字的包正在并行解压，为避免两个任务写进同一个目录，" +
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
            }

            _spaceBlockedTasks.Clear();
            _unrecognizedMaybePiece.Clear();

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
        /// <summary>
        /// 批首**第二大步点名**（2026-10-23）：只读组分析（基名=第一个点之前 + 大小分布），
        /// 把「可证完整」的组的入口那份点名为第二大步入口任务（登记任务级事实位 ⇒ 只解一层）。
        /// ⛔ 判不出/缺卷 ⇒ 什么都不做（照旧第一大步跳过、原地不动）。
        /// </summary>
        private void MarkVolumeStepEntriesForBatch(IReadOnlyList<ArchiveTask> tasks)
        {
            var seeds = new List<string>();
            var roots = new List<string>();

            foreach (ArchiveTask task in tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath) || !File.Exists(task.CurrentPath))
                {
                    continue;
                }

                seeds.Add(task.CurrentPath);

                string? directory = Path.GetDirectoryName(task.CurrentPath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    roots.Add(directory);
                }

                if (!string.IsNullOrWhiteSpace(task.ParentOutputDirectory))
                {
                    roots.Add(task.ParentOutputDirectory);
                }
            }

            if (seeds.Count == 0)
            {
                return;
            }

            List<GroupAnalysis.GroupCandidate> groups = GroupAnalysis.Analyze(seeds, roots);

            foreach (GroupAnalysis.GroupCandidate group in groups)
            {
                if (group == null || group.Verdict != GroupAnalysis.GroupAnalysisVerdict.Complete
                    || string.IsNullOrWhiteSpace(group.EntryPath))
                {
                    continue;
                }

                ArchiveTask? entryTask = null;

                foreach (ArchiveTask candidate in tasks)
                {
                    if (candidate != null
                        && string.Equals(candidate.CurrentPath, group.EntryPath, StringComparison.OrdinalIgnoreCase))
                    {
                        entryTask = candidate;
                        break;
                    }
                }

                if (entryTask == null)
                {
                    continue;
                }

                RememberVolumeStepTask(entryTask);

                AppendLog(
                    "INFO",
                    $"[第二大步·组分析] {group.BaseName}：可证完整（{group.Pieces.Count} 片）—— 入口={FileNameHelper.GetFileName(group.EntryPath)}，这一单只解这一层。");
            }
        }

        /// <summary>
        /// **预判入口包将来落在哪** —— 落点跟"产出它的那一单"那条链（真机 `CCCC` 2026-10-06）。
        ///
        /// <para>现场（用户连着追问的那一条）：任务表里第一单是散在 `CCCC\111(4)` 的 `111.z0删除3`，
        /// 而这一组的**入口包**（跨盘 ZIP 的末片 `111.zip`）正压在 `CCCC\111\111.rar` 里、还没被解出来 ⇒
        /// <see cref="Detection.GroupVolumeDirectory.Resolve"/> 如实回空 ⇒ 落点退回"源包自己那一层 + 包名"
        /// ⇒ 产物与"其余物"全挂到 `111(4)` 上（而它们该在 `CCCC\111\111`）。</para>
        ///
        /// <para><b>怎么预判</b>：① 这一组那几片里任意一份 → `VolumeGroupDetector.TryGetFirstVolumeName`
        /// 直接给出**入口包的标准名**（跨盘 ZIP 族：`111.z0删除2` ⇒ `111.zip`；7z 族 ⇒ `X.7z.001`；
        /// RAR 族 ⇒ 第 1 卷）—— ⛔ 不靠后缀猜、不另写按族的规则；② 在**任务表**里找"同包基名、又不是本组成员、
        /// 自己也不是在等入口包的那一单"（真机 = `111.rar`）；③ 它的落点就是这个包的落点 ⇒
        /// 拼出"入口包将来会落在哪"（`&lt;产出方落点&gt;\&lt;入口包名&gt;`），再交给**同一个**落点实现。</para>
        ///
        /// <para>⛔ 判不出（找不到产出方 / 产出方自己也在等入口包 / 名字推不出标准名）⇒ 空串
        /// ⇒ 落点与改动前逐字相同（兜底落在"什么都不做"那一档）。⛔ 深度上限 2 层，防互相等成环。</para>
        /// </summary>
        private string ResolveProducerEntryArchivePathCore(
            ArchiveTask task,
            ExtractOptions options,
            string archivePath,
            int depth)
        {
            if (depth > 2)
            {
                return string.Empty;
            }

            string entryName = VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(archivePath))
                ?? string.Empty;

            if (entryName.Length == 0)
            {
                return string.Empty;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath);

            if (baseName.Length == 0)
            {
                return string.Empty;
            }

            ArchiveTask? producer = FindProducerTaskForPackage(task, archivePath, entryName);

            if (producer == null)
            {
                return string.Empty;
            }

            /*
             * ===== 落点**一层一层往上叠**（用户 2026-10-06 拍板）=====
             *
             * 现场：`111.rar` → 里面是 `111.zip` → 里面是 `111.rar`（2 卷）→ 里面才是那一组跨盘 ZIP 的
             * 入口 `111.zip`。用户原话：「**我问你，你要想得到 `111.zip` 你是不是得按规则解压得到，
             * 之前的规矩就是一层层解压**，人工如果想得到 `111.zip` 就得中间有这么多的文件夹」。
             *
             * ⇒ 判据只有一条、只用一个出口：**落点 = 入口包所在那一层 + 一层包名目录**
             * （`ResolveOutputPlacement` 自己就是这条规则），于是：
             *   ① 找到"产出这一份的那一单"（同一个包基名、又不是本组成员）；
             *   ② 算**它**的落点（递归 —— 它自己也可能还在等它的入口包）；
             *   ③ 这一份就落在**它的落点**目录里（那正是"它解出来之后会待着的那一层"）；
             *   ④ 拿这一层当新的起点，继续往下追，直到追到"盘上真有这一份"或追不动为止。
             * ⛔ 不是"产出方落点 + 包名"那种一步猜测（那一步在真机上算出来正好差一层）。
             * ⛔ 追不动 / 判不出 ⇒ 原样退回旧口径（什么都不做那一档）。
             */
            OutputPlacementResult producerPlacement = ResolveOutputPlacementForProducer(producer, options, depth);

            if (!producerPlacement.Success || string.IsNullOrWhiteSpace(producerPlacement.DestinationDirectory))
            {
                return string.Empty;
            }

            string landingLayer = producerPlacement.DestinationDirectory;
            string? nextPackage = FindNextPackageInProducerChain(producer, archivePath);
            int guard = 0;

            while (nextPackage != null && guard++ < MaxProducerChainDepth)
            {
                string nextEntryName = TryGetEntryPackageName(nextPackage);

                if (nextEntryName.Length == 0)
                {
                    break;
                }

                // 入口包**已经真的在盘上**了（真机第二次及以后重算时会走到这里）⇒ 落点就是它自己那一层。
                string onDisk = Path.Combine(landingLayer, nextEntryName);

                if (File.Exists(onDisk))
                {
                    return onDisk;
                }

                ArchiveTask? nextProducer = FindProducerTaskForPackage(task, nextPackage, nextEntryName);

                if (nextProducer == null)
                {
                    return Path.Combine(landingLayer, nextEntryName);
                }

                OutputPlacementResult nextPlacement = ResolveOutputPlacementForProducer(nextProducer, options, depth + 1);

                if (!nextPlacement.Success || string.IsNullOrWhiteSpace(nextPlacement.DestinationDirectory))
                {
                    return Path.Combine(landingLayer, nextEntryName);
                }

                landingLayer = nextPlacement.DestinationDirectory;
                nextPackage = FindNextPackageInProducerChain(nextProducer, nextPackage);
            }

            string predicted = Path.Combine(landingLayer, entryName);

            AppendLog(
                "INFO",
                $"{task.LogName}：这一组的入口包（{entryName}）现在还没被解出来 —— 落点按"
                + $"「产出它的那一单」（{producer.FileName}）那条链「一层一层」推：{predicted}。");

            return predicted;
        }

        /// <summary>
        /// 产出方的落点（唯一实现仍是 <see cref="PathService.ResolveOutputPlacement"/>）。
        ///
        /// <para>⚠ 这一调会再次走进本方法所在的这条预测链（产出方自己也可能在等它的入口包）——
        /// 递归由 `depth` 与 <see cref="MaxProducerChainDepth"/> 双重兜住，⛔ 不会自环。</para>
        /// </summary>
        private OutputPlacementResult ResolveOutputPlacementForProducer(
            ArchiveTask producer,
            ExtractOptions options,
            int depth)
        {
            _ = depth;

            return _pathService.ResolveOutputPlacement(producer, options);
        }

        /// <summary>产出方那条链最多追几层（防环、防病态输入；真机这一条链是 3 层）。</summary>
        private const int MaxProducerChainDepth = 6;

        /// <summary>这一份的**入口包名字**（跨盘 ZIP = 末片 `X.zip`、7z = `X.7z.001`、ZIP 通用分片 = `X.zip.001`、RAR = 第 1 卷）；判不出 ⇒ 空串。</summary>
        private static string TryGetEntryPackageName(string packagePath) =>
            VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(packagePath)) ?? string.Empty;

        /// <summary>
        /// **产出方那一单**：同一个包基名、又不是本组成员、自己也不是"在等入口包"的那一单（真机 = `111.rar`）。
        /// 找不到 ⇒ null（调用方退回旧口径）。
        /// </summary>
        private ArchiveTask? FindProducerTaskForPackage(ArchiveTask task, string packagePath, string entryName)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(packagePath);

            if (baseName.Length == 0)
            {
                return null;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || ReferenceEquals(candidate, task))
                {
                    continue;
                }

                string candidatePath = candidate.CurrentPath;

                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                string candidateName = FileNameHelper.GetFileName(candidatePath);

                // 本组成员（收在同一组里的那几片）跳过 —— 它们自己就在等入口包。
                if (VolumeGroupDetector.BelongsToSameGroup(candidateName, entryName))
                {
                    continue;
                }

                if (!string.Equals(
                        FileNameHelper.GetArchiveBaseName(candidatePath),
                        baseName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 产出方自己也在等它那一组的入口包 ⇒ 它算不出真落点，别拿它当锚点。
                if (candidate.IsVolumeGroup && IsUnresolvedVolumeGroupMember(candidate))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// **这一条链上的下一份**（产出方交出来的那一份）。
        ///
        /// <para>判据 = 产出方手上那份的**入口包规范名**（`TryGetFirstVolumeName`）拼回它自己那一层：
        /// `111.rar` ⇒ `…\111\111.zip`；`111(2)_.zip` ⇒ `…\111(2)\111(2)_\111.z01`。
        /// ⛔ 与手上这一份同名（或名字推不出来）⇒ 返回 null ⇒ **链到此为止**（⛔ 不许自己套自己）。</para>
        /// </summary>
        private static string? FindNextPackageInProducerChain(ArchiveTask producer, string currentPackagePath)
        {
            string name = TryGetEntryPackageName(producer.CurrentPath ?? string.Empty);

            if (name.Length == 0)
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(producer.CurrentPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string next = Path.Combine(directory, name);

            return SafePathHelper.PathEquals(next, currentPackagePath) ? null : next;
        }

        /// <summary>
        /// 这一单是不是**分卷组的成员、而这一组的入口包现在还没被解出来**（真机 `CCCC` 2026-10-06）。
        ///
        /// <para>判据与落点实现**同一个事实位**：<see cref="Detection.GroupVolumeDirectory.Resolve"/>
        /// 在这一单自己的成员名单里找"引擎要打开、要对它输密码的那一份"（跨盘 ZIP 族 = 本体 <c>X.zip</c>、
        /// 7z 族 = <c>X.001</c>、RAR 族 = 第 1 卷）；判不出 ⇒ 它的落点只是**占位值**
        /// （退回"源包自己那一层 + 包名"那个旧口径），不足以当整批工作区根的锚点。</para>
        ///
        /// <para>⛔ 只有"分卷组"才问这一句；⛔ 判不出不等于失败 —— 调用方只是把它排到候选末尾，
        /// 实在没有别的单时照旧用它（⛔ 绝不因为挑不出根就整批停手）。</para>
        /// </summary>
        private bool IsUnresolvedVolumeGroupMember(ArchiveTask task)
        {
            if (!task.IsVolumeGroup)
            {
                return false;
            }

            var members = new List<string>();

            if (!string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                members.Add(task.CurrentPath);
            }

            foreach (string volumePath in task.VolumePaths)
            {
                if (!string.IsNullOrWhiteSpace(volumePath))
                {
                    members.Add(volumePath);
                }
            }

            return members.Count > 0 && Detection.GroupVolumeDirectory.Resolve(members).Path.Length == 0;
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

            return IsSingleLayerRecursion(task);
        }

        /// <summary>这一批是不是单层模式（递归模式下第 0 层要交给引擎，见 <see cref="DirectReadAppliesTo"/>）。</summary>
        private bool IsSingleLayerRecursion() =>
            false; // 递归三档已删除（默认全展开）；重建中

        /// <summary>
        /// **这一单这一趟是不是"第二大步"**（用户 2026-10-11 定的轮次模型，见 `RoundProductClassifier` 的说明）：
        /// 第二大步 = **分卷**的解压、**只解一层** —— 解出来的东西一律交回**轮判定**，
        /// 里面又有分卷也**不就地接着解**（下一轮从第二大步再来一遍）。
        ///
        /// <para>为什么必须是"任务级的事实位"而不是改用户的设置：递归档
        /// （<c>AppSettings</c>）是**用户选的**，⛔ 这一档不许动它；
        /// 而"这一单是第二大步"是**程序自己在批末判出来的**（那一组齐了 ⇒ 照常跑这一组，见 `toRun` 那一处）。</para>
        ///
        /// <para>⚠ 没点名 / 传 null ⇒ 与改动前**逐字相同**（只看设置档）。</para>
        /// </summary>
        private bool IsSingleLayerRecursion(ArchiveTask? task) =>
            (task != null && _volumeStepTasks.Contains(task)) || IsSingleLayerRecursion();

        /// <summary>把这一单记成"第二大步任务"（批末那一组齐了、要照常跑这一组的那一刻）。</summary>
        private void RememberVolumeStepTask(ArchiveTask task)
        {
            if (task != null)
            {
                _volumeStepTasks.Add(task);
            }
        }

        /// <summary>
        /// 同 <see cref="RememberVolumeStepTask(ArchiveTask)"/>，供轮次那一层调（`OneClickCoordinator` 在轮边界
        /// 把"判成是分卷的那几单"点成第二大步）—— ⛔ 只有一个写点语义，不另开第二套。
        /// </summary>
        internal void RememberVolumeStepTaskForRound(ArchiveTask task) => RememberVolumeStepTask(task);

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
                            ? $"{task.LogName}：这一轮没有产生其余物（过程物就是内容物里的那个内层包）；"
                              + "整条续解链跑完后按「删除操作」处理内层包。"
                            : $"{task.LogName}：其余物先留着（里面还有要接着解的内层包），"
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

            // ⚠ 2026-10-15：回收站档整条下线 ⇒ 这里（Keep 已在上面挡掉）只剩永久删。
            DeleteMode deleteMode = DeleteMode.Permanent;

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
                    Message = $"{task.LogName}：处理其余物时出现意外错误：{ex.Message}"
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
             * ⛔ **2026-10-15 口径变更：逐层回收与③页「删除操作」档位解绑**（用户 2026-10-14 口径逐字：
             * 「删除操作只针对**最后一层过程物**……**其他的过程物统一彻底删除**（因为他们都是成功的情况下），
             * 统一彻底删除」—— 见 `docs\大改动-2026-10-14-口径与冲突.md` §2.1 / §2.2 / §18.1）。
             *
             * 老口径（2026-10-03 的「B 档位分叉」）是"只有彻底删除档才逐层回收、不动其余物档集中进其余物、回收站档
             * 逐层进回收站"。回收站那一档已整条下线，而**"成功过的过程物"在哪个档下都不该留着**：
             * 用户 2026-10-14 的论证是「删掉的是**成功过的那几层**——它们可以从留下的那一层重做，所以不是证据」。
             * ⇒ 这一位恒为 true（普通档），动作恒为**当场永久删**。
             * ⛔ 那四道门槛（定稿 + 可证完整 + 未取消 + 「半套分卷」）一条都没放宽，改的只是"要不要做"。
             *
             * ⚠ **「最后一层过程物」不在这一位管**（用户口径：那一份默认**留下**，归③页「删除操作」那一档）：
             * 链停下来的那一层，它的输入包是"这一层要解的那个东西"，按红线**一个字节都不动** ——
             * 见 `PurgeLayerSourcePackage` 的入口判据与 `PostProcessSuccessAsync` 里"只有跑成了才回收"。
             * ⇒ 「彻底删除」档目前对"最后一层过程物"还没有动作（**已知实现缺口**，下一刀）。
             */
            _layerReclaimThisBatch = true;

            // 链内记账每批一清（它只服务"这一批这一条链"）。
            _chainInnerLayerInputs.Clear();
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
        /// 设置项（`MaxRecursionDepth`，默认 7、夹在 1~10）。这里只把它取出来说明白"最多几层"，
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
                /*
                 * ⛔ 第一大步只做「单一完整包」：分卷成员只登记交接事实、原地不动，交给第二大步。
                 * 判据只读内容卡（FileNature.Inspect），⛔ 不按名字猜；判不出（Unknown）照旧放行。
                 */
                ArchiveFixer.Detection.FileNatureCard entryCard =
                    ArchiveFixer.Detection.FileNature.Inspect(task.CurrentPath ?? string.Empty);

                /*
                 * 第二大步入口（2026-10-23）：内容卡证明这一份就是分卷组的**入口卷** ⇒ 这一单就是
                 * 第二大步的入口任务：登记任务级事实位（让递归只解一层），交给单层路解这一组。
                 * ⛔ 仍只读内容卡，不按名字猜；非入口卷照旧跳过、原地不动。
                 */

                if (entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                    || entryCard.BodyKind == ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece)
                {
                    RegisterVolumeHandoff(task, entryCard);
                    task.Status = StatusText.Skipped;
                    task.Outcome = TaskOutcome.Skipped;
                    task.ProgressText = StatusText.ProgressSkipped;
                    task.EndTime = DateTime.Now;
                    task.LastUpdatedTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    AppendLog(
                        "WARN",
                        $"{task.LogName}：这一份是分卷成员（{entryCard.BodyKind}）—— 第一大步不解它，只登记交接事实、原地不动，交给第二大步。");

                    return;
                }

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
                /*
                 * 「这一单整体有没有内容物解出」这条事实**必须在收口之前读**：
                 * 候选表在下面那次 `TryPublishPartialProductsAsync` 里会被 `TryRemove` 掉。
                 */
                bool chainProducedContent =
                    _partialPublishCandidates.TryGetValue(task, out PartialPublishCandidate? registeredCandidate)
                    && registeredCandidate.ChainProducedContent;

                FinalizeOutcomeIfPending(task, chainProducedContent);
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

                /*
                 * ⛔ **任务收尾：实时进度清零**（`ArchiveTask.ProgressPercent` 的契约逐字：「它是**瞬时状态**：
                 * 任务一收尾就必须清掉，否则已完成/失败的任务还会挂着"解压中 45%"（不变量 6…）」）。
                 *
                 * 位置选在**所有路径都经过的那一个 finally**（⛔ 不是某条分支里）：
                 * 实测（守门 `ProgressVisibilityTests.解压进度_落到任务上并且只在跨10个百分点时写日志`）
                 * 递归那条路上收尾**没有清** ⇒ 跑完的任务 `ProgressPercent` 仍停在 100
                 * （逐字 `Expected: -1 / Actual: 100`）。另外两处清**都不是统一出口**：开工前清掉上一轮残留那一处
                 * （本方法中段建 `TaskProgressSink` 时）、递归路 `RecursionStopReason.SourceChanged` 那一支自己那一处。
                 */
                task.ClearProgress();

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
        /// **「这一单算不算『损坏压缩包』」的唯一出口**（用户 2026-10-14 口径：部分完成发布从"用户开关"
        /// 降到底层 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，**而且用户不能操作**」）。
        ///
        /// <para>判据只看**结构化事实**：引擎明说 <see cref="EngineErrorTypes.CorruptedArchive"/>
        /// （"归档损坏 / 校验和不符 / 数据不全"）。⛔ 不比中文文案、⛔ 不看体积/名字一类推断。</para>
        ///
        /// <para><b>为什么不含"两义"那一档</b>（<see cref="EngineErrorTypes.PasswordOrCorrupted"/>）：
        /// 引擎在那里同一句话里既说密码不对又说数据坏了，**它自己都判不出**；而这一类最常见的成因是
        /// 密码不对（旧式 RAR 加密包）⇒ 按项目铁律「判不出 ⇒ 什么都不做」，这一档**不发布**
        /// （要发布得先让用户看到那两种可能、由他决定，⛔ 不是我们替他挑一个）。</para>
        /// </summary>
        private static bool IsDamagedArchive(string? detectedErrorType) =>
            string.Equals(detectedErrorType, EngineErrorTypes.CorruptedArchive, StringComparison.OrdinalIgnoreCase);

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
            /// **这一单的失败是"引擎明说包损坏"吗**（<see cref="IsDamagedArchive"/> 是唯一判据出口）。
            ///
            /// <para>它是这一档的**触发事实**：登记那一刻从结构化结果里读一次、之后不再变
            /// （收尾时才决定发不发，但"坏没坏"这件事在失败那一刻就已经定了）。</para>
            /// </summary>
            public bool Damaged { get; init; }

            /// <summary>
            /// **这一单整体到底有没有内容物解出来**（用户 2026-10-16 口径：「部分完成 = 一部分成功 + 一部分失败」：
            /// 「密码错了但是其他的地方有内容物这个**肯定是判部分完成**，但是如果只有一个压缩包，而且密码错误，
            /// 那么就不可能有内容物，因为密码都没有试出来」）。
            ///
            /// <para>它是**终态分类**那一档的判据输入（与"要不要发布"是两件事：发布只认"损坏"，见
            /// <see cref="Damaged"/>）—— 单层路 = 产物目录里真解出来的非空文件够不够
            /// （唯一出口 <c>ProducedContentGate</c>，与"密码已被证实"用同一把尺子，⛔ 不另定阈值）；
            /// 递归路 = 链上**任何一层**产出过文件（<c>RecursionLayerReport.OutputFileCount</c>）。</para>
            /// </summary>
            public bool ChainProducedContent { get; init; }

            /// <summary>
            /// 引擎给的结构化错误类型（<c>EngineErrorTypes</c> 那一套词表里的名字；空串 = 没给）。
            /// 只为**如实写清"为什么没发布"**：那一行要点名这一单到底属于哪一档。
            /// </summary>
            public string DetectedErrorType { get; init; } = string.Empty;

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
            bool engineReportedFailure,
            string? detectedErrorType = null,
            bool chainProducedContent = false)
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
                EngineReportedFailure = engineReportedFailure,
                Damaged = IsDamagedArchive(detectedErrorType),
                DetectedErrorType = detectedErrorType ?? string.Empty,
                ChainProducedContent = chainProducedContent
            };
        }

        /// <summary>
        /// **「链停在哪一层」的唯一出口**：报告里**最后一个没跑成的层**（都在跑成 / 没跑过任何一层 ⇒ null）。
        ///
        /// <para>为什么要抽出来：这一件事现在有**两个**消费方 —— ① 部分完成发布的登记（那一层的产物就是
        /// "解了一半的现场"）；② 「最后一层过程物」按「删除操作」那一档留 / 删（那一层**要解的那个包**
        /// 就是口径里说的「最后一层过程物」）。⛔ 两处各判一遍迟早会分叉。</para>
        /// </summary>
        private static RecursionLayerReport? ResolveStoppedLayer(RecursionResult? result)
        {
            if (result == null)
            {
                return null;
            }

            RecursionLayerReport? stopped = null;

            foreach (RecursionLayerReport layer in result.Layers)
            {
                if (!layer.Success)
                {
                    stopped = layer;
                }
            }

            return stopped;
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

            RecursionLayerReport? failedLayer = ResolveStoppedLayer(result);

            if (failedLayer == null || string.IsNullOrWhiteSpace(failedLayer.OutputPath))
            {
                return;
            }

            /*
             * 密码传空串：这一层的候选是递归核心自己挑的，它只把**脱敏**的占位符交出来
             * （报告里绝不出现明文密码）。所以这一档要么拿现成的逐条清单（正常情形：
             * 解压前列目录那一次就是拿对的密码列的），要么什么都不发布 —— ⛔ 不去猜密码。
             */
            /*
             * 链上**任何一层**产出过文件 ⇒ "整体已有内容物解出"（用户 2026-10-16 口径）。
             *
             * ⚠ 还必须加上**停下那一层自己的产物目录**：失败层的报告里 `OutputFileCount` 按定义是 0
             * （`BuildLayerReport` 写的就是 0 —— 那一层没跑成），可它的产物目录里**可能真有东西**。
             * 实测（假引擎 + 5 个产物文件 + 密码错误）：第一版只读报告 ⇒ 判成"没有内容物"⇒ 终态落 `Failed`，
             * 与用户口径相反。判据仍走**唯一出口** `ProducedContentGate`，⛔ 不另定阈值。
             */
            bool chainProducedContent =
                result.Layers.Any(layer => layer.OutputFileCount > 0)
                || (ProducedContentGate.TryMeasure(
                        failedLayer.OutputPath,
                        failedLayer.ReportedSubItemErrors,
                        failedLayer.FailedEntryNames.Count,
                        out int failedLayerFiles,
                        out _)
                    && failedLayerFiles >= ProducedContentGate.MinimumFiles);

            RecordPartialPublishCandidate(
                task,
                failedLayer.OutputPath,
                failedLayer.ArchivePath,
                string.Empty,
                failedLayer.ManifestEntries,
                failedLayer.FailedEntryNames,
                failedLayer.ReportedSubItemErrors,
                engineReportedFailure: true,
                detectedErrorType: failedLayer.DetectedErrorType,
                chainProducedContent: chainProducedContent);
        }

        /// <summary>
        /// **「最后一层过程物」按③页「删除操作」那一档处置**（用户 2026-10-14 口径，链条模型 `A→B→C→D→a`）：
        /// 「**删除操作（失败和部分完成）**只**管"最后一层过程物"**……默认留下」
        /// ＋「`D` 肯定会留下，而且是完整的……前面的 `B` 和 `C` 都是成功的」（见
        /// <c>docs\大改动-2026-10-14-口径与冲突.md</c> §2.1 / §2.2）。
        ///
        /// <para><b>哪一份是"最后一层过程物"</b>：**链停下来的那一层要解的那个包** =
        /// <see cref="ResolveStoppedLayer"/> 那一层的 <c>ArchivePath</c>（唯一出口，两个消费方共用）。</para>
        ///
        /// <para><b>为什么必须有这一档</b>（2026-10-16 运行期实测，不是推断）：递归路每一层的产物都留在
        /// 这一单的**工作区**里，而"失败 / 部分完成 ⇒ 工作区整份删掉" ⇒ 停下来的那一层要解的包**连同一份
        /// 完整的过程物一起被清掉**了 —— 与用户口径「默认留下」正好相反（实测：一条四层链在第三层失败，
        /// 跑完盘上只剩源包 `outer.7z`，`level3.7z` 一个字节都没留）。</para>
        ///
        /// <para><b>落法</b>（⛔ 只做"留下"这一半，"彻底删除"那一半的主动删还没做，见 §20.4）：
        /// 「删除操作 = 留下」（默认档）= 在这一单的其余物里**给这一份另起一个名字（硬链接）** ——
        /// ⛔ **不是搬走**：原件留在原处（别处还在用它），而工作区收尾把那一棵树整份删掉之后，
        /// 这一份仍然活在其余物那个名字上（硬链接就是"同一份字节的第二个实名"）。
        /// 「彻底删除」档 = **什么都不做**（它随工作区一起清掉，今天就是这个行为），只如实写一行说清。</para>
        ///
        /// <para>⚠ <b>为什么必须是"链接"而不是"搬"</b>（2026-10-16 实测踩到，写死）：第一版用的是
        /// <c>TryMoveIntoRest</c>（搬走），当场把两条既有守门打红 ——
        /// ① `GroupLandingFollowsEntryPackageTests` 两条：「批末收拢 / 接片 / 落地」那套机制要在**原地**
        /// 找到那一片，搬走它 = 那一组永远收不拢（实测日志：`pack3.rar：链在「111_.part3.rar」这一层停下……
        /// 留在其余物里`，随后三条片各自落在自己那一层、没并进入口包那一层）；
        /// ② `WorkspaceRootTests.打开保留开关_失败但有文件_一律保留且说清在哪`：搬走工作区里**唯一那个文件**
        /// 会让工作区变成"零文件空壳"⇒ 空壳无论如何都删 ⇒ 用户要的"留现场"没了。
        /// ⇒ 判据与动作都改层：**原件一律原地不动**，只多一个名字。</para>
        ///
        /// <para>⛔ <b>分卷片不进这一档</b>：那一片归"批末收拢 / 接片 / 落地"那套机制管（它们本来就会在落地层
        /// 留下自己的名字、活得比工作区长），这里再给它起名字只会多一个看不懂的名字。判据 = 唯一出口
        /// <c>VolumePieceFacts</c>（卡说它是某组的一片 **或** 名字读得出卷号 —— 两者取并，宁可少动）。</para>
        ///
        /// <para>⛔ <b>红线</b>：只碰**递归工作区里**的东西（用户拖进来的源包永远不在那儿 —— 与逐层回收
        /// 那条用的是同一道保险）；建不了链接 / 推不出其余物目录 ⇒ 一行 WARN + 什么都不做。
        /// ⛔ 取消这一档不在这里判：取消的判定在 <c>TryPublishPartialProductsAsync</c> 与调用方，
        /// 而"留下"本身是**非破坏性**动作（比删安全），失败/部分完成/取消都该留下（不变量 1）。</para>
        /// </summary>
        private void PreserveStoppedLayerProcessArtifact(ArchiveTask task, RecursionResult? result)
        {
            if (task == null)
            {
                return;
            }

            RecursionLayerReport? stopped = ResolveStoppedLayer(result);

            if (stopped == null || string.IsNullOrWhiteSpace(stopped.ArchivePath))
            {
                return;
            }

            string artifact = stopped.ArchivePath;

            if (!File.Exists(artifact))
            {
                // 已经不在了（没解到那一层 / 前面某一步收走了）⇒ 没有什么可留。
                return;
            }

            if (!IsInsideRecursionWorkspace(artifact))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：停下的那一层要解的「{Path.GetFileName(artifact)}」不在递归工作区里"
                    + "（它可能是用户拖进来的源包）—— 判不出它是不是本链的过程物，所以一个字节都不动。");

                return;
            }

            string fileName = Path.GetFileName(artifact);

            if (string.Equals(_restHandlingThisBatch, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：链在「{fileName}」这一层停下，它是这一层的「最后一层过程物」；"
                    + "按③页「删除操作 = 彻底删除」处理 —— 它随这一单的工作区一起清掉（⛔ 不搬进其余物、"
                    + "不另起名字；链上方那些成功过的过程物本来就已经各层当场删掉了）。");

                return;
            }

            FileNatureCard card = FileNature.Inspect(artifact);

            if (VolumePieceFacts.CardSaysItIsPiece(card)
                || VolumePieceFacts.ResolveSlotIndex(artifact, card) is not null
                || VolumeGroupDetector.TryGetVolumeIndex(fileName) is not null)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：链在「{fileName}」这一层停下，但它是「分卷的一片」 —— 这一片归"
                    + "「批末收拢 / 接片 / 落地」那套机制管（它会带着自己的名字落在该落的那一层），"
                    + "所以这一档不动它（⛔ 不给它另起名字）。");

                return;
            }

            string? restDirectory = ResolveRecursionRestDirectory(task, artifact);

            if (string.IsNullOrWhiteSpace(restDirectory))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但推不出这一单的其余物目录"
                    + " —— 它留在原处（工作区收尾会把它清掉）。");

                return;
            }

            try
            {
                SafePathHelper.EnsureDirectoryExists(restDirectory);
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」本该留下，但其余物目录建不出来 —— {ex.Message}"
                    + "（它留在原处；工作区收尾会把它清掉）。");

                return;
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string target = Path.Combine(restDirectory, fileName);

            /*
             * ⚠ 撞名才让位（与 <see cref="TryMoveIntoRest"/> 同一个模式，⛔ 不自己另写一套）：
             * `ProcessArtifactLayout.MakeUniqueTarget` **只在"已经有人占了"时才该调** —— 它本身
             * 只会给出 `名字(1).ext` 这种形式（没有"空着就原样返回"那一支）。第一版无条件调它 ⇒
             * 明明空着也写成 `level3(1).7z`（运行期实测逮到的）。
             */
            if (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
            {
                target = ProcessArtifactLayout.MakeUniqueTarget(
                    target,
                    isDirectory: false,
                    reserved,
                    FileSystemArtifactTargetProbe.Instance);
            }

            if (!HardLinkHelper.TryCreateHardLink(target, artifact))
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：链停下的那一层的过程物「{fileName}」没能留下第二个名字"
                    + "（硬链接建不出来 —— 多半与其余物不在同一个卷）—— 它留在原处（工作区收尾会把它清掉）。");

                return;
            }

            // 其余物记到这一单上（其余物那一档收尾时才有对象可处理）。
            task.RestDirectoryPath = restDirectory;

            AppendLog(
                "INFO",
                $"{task.LogName}：链在「{fileName}」这一层停下 —— 它是这一层的「最后一层过程物」，"
                + $"按③页「删除操作 = 留下」（默认档）在其余物里留了一个名字：{target}"
                + "（硬链接：原件一个字节都没搬、也没改名；链上方成功过的那些过程物已经各层当场删掉了；"
                + "最外层源包一律原地不动）。");
        }

        /// <summary>
        /// **这一单的其余物目录**（递归路：从"这一份在递归工作区里的路径"反推落点那一层）。
        ///
        /// <para>判据只读事实：路径里那一段工作区标记（<see cref="VolumeContentInference.WorkDirectoryName"/>，
        /// ⛔ 这里不另写标记名）**前面的那一层**就是落点，其余物就在它下面。</para>
        ///
        /// <para>推不出（标记那一段找不到 / 前面没有东西 / 路径形态怪）⇒ 返回 null ⇒ 调用方什么都不做 + 一行 WARN。
        /// ⚠ 2026-10-16：这个方法在「逐层回收与档位解绑」那一刀里被删过一次（当时它唯一的调用者
        /// "集中进其余物"那支被删了）；现在「最后一层过程物按档留下」重新需要它 —— 同一份判据，
        /// ⛔ 不另写一份。</para>
        /// </summary>
        private static string? ResolveRecursionRestDirectory(ArchiveTask task, string pathInsideWorkspace)
        {
            if (task != null && !string.IsNullOrWhiteSpace(task.RestDirectoryPath))
            {
                return task.RestDirectoryPath;
            }

            if (string.IsNullOrWhiteSpace(pathInsideWorkspace))
            {
                return null;
            }

            try
            {
                var directory = new DirectoryInfo(Path.GetDirectoryName(pathInsideWorkspace) ?? string.Empty);

                while (directory != null)
                {
                    if (string.Equals(directory.Name, VolumeContentInference.WorkDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? landing = directory.Parent?.FullName;

                        return string.IsNullOrWhiteSpace(landing)
                            ? null
                            : Path.Combine(landing, ProcessArtifactLayout.ArtifactDirectoryName);
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
                // 路径形态怪 / 权限 ⇒ 判不出（⛔ 不猜）。
            }

            return null;
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

            /*
             * ⛔ **触发判据 = 结构化事实，不是③页那个开关**（用户 2026-10-14 口径：这个功能淘汰，
             * 做成底层设计 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，
             * **而且用户不能操作**」）。
             *
             * 老实现读 `Settings.PartialPublishEnabled`：那个开关的 UI 在九页签那一刀已经删掉、
             * 三处值都是 false ⇒ **用户再也开不了"把已经解出来的留下"**（这一档等于死掉了）。
             * 现在：只有"引擎明说包损坏"（<see cref="IsDamagedArchive"/>，唯一出口）才自动发布；
             * 别的失败（密码错 / 缺卷 / 取消 / 越界…）照旧一个字节都不发布，并如实写一行为什么。
             */
            if (!candidate.Damaged)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishNotDamagedFormat,
                        task.FileName,
                        string.IsNullOrWhiteSpace(candidate.DetectedErrorType)
                            ? "引擎没给出结构化错误类型"
                            : candidate.DetectedErrorType));

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

            /*
             * ⛔ **必须明说"压缩包损坏"**（用户 2026-10-14 口径：「这个损坏的压缩包我们肯定是要在日志里面
             * 去报一个压缩包损坏的情况，然后不完整的内容物就留下来」）。
             *
             * 为什么写成 WARN 而不是 INFO：成功任务只留一行摘要（第 44 条），而这一单是"失败 + 救回一部分"
             * ⇒ 只写 INFO 的话用户按"失败"去查，看不到"哪些是坏的、哪些已经救出来了"这件事。
             */
            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PartialPublishDamagedArchiveFormat,
                    task.FileName,
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
             * ⛔ **机器终态跟着走：损坏 + 已经救回一部分 = 「部分完成」**（用户 2026-10-14 口径逐字：
             * 「这个损坏的压缩包……不完整的内容物就留下来，如果用户选择删除，那就删除……
             * 留下就留下，然后**最后的汇总肯定是报部分完成，橙色**」）。
             *
             * 位置：只能在**真发布成功之后**改（前面每一条 return false 都保持原终态）
             * ⇒ "损坏但一个字节都没救回来"照旧落 `Failed`（红），那是对的。
             * 色带与计数不用另写判据：`BatchSummarySeverity.FromOutcomes` / `BatchOutcomeTally`
             * 读的就是这一位（橙 = 有部分完成）。⚠ 需要先过 `FinalizeOutcomeIfPending`（它在收尾更早处，
             * 已经把 `Pending` 兜底成 `Failed`）—— 这里改的是**已经定下来的失败**，不是绕过兜底。
             */
            if (task.Outcome == TaskOutcome.Failed)
            {
                task.Outcome = TaskOutcome.PartiallyCompleted;
            }

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
                    $"{task.LogName}：要清的工作区目录不在工作区根之下，已跳过 —— {guardReason}：{taskDirectory}");

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
                        $"{task.LogName}：工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清：{taskDirectory}");

                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.LogName}：读不了工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");

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
             * ===== ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）=====
             *
             * 用户原话：「现在我觉得禁止解压真正的 apk 文件，因为有些是用户真的要转移到手机上进行安装的」。
             * 判据 = 内容证据（ZIP 里同时有 AndroidManifest.xml 与 classes.dex，唯一出口
             * `AndroidPackageDetector`，⛔ 不看后缀）；判不出 ⇒ 照旧照常处理。
             *
             * 位置：排在**任何引擎调用与改名之前**（与它下面那一档同一个位置口径）——
             * 真机 `魔方.apk` 的现场就是"被当普通 ZIP 解开、还在它内部建工作区、继续递归探 `.so`"，
             * 所以这一档必须在"开工"之前就返回。改名那条线本来就有（`.apk` 在
             * `ExtensionHelper.ZipContainerExtensions` 里 ⇒ 不会被改成 `.zip`），这里只管"解"与"递归"。
             *
             * 三条路的另外两处落点：递归层的内层包候选（`RecursiveExtractor`）与一键处理的产物扫描
             * （`OneClickCoordinator`）—— 判据都转调同一个出口。
             */
            if (AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
            {
                task.IsArchive = false;
                task.Status = StatusText.Skipped;
                task.Operation = StatusText.OpSkip;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = StatusText.AndroidPackageSkipped;

                // 机器终态显式落（与"未识别 ⇒ 跳过"那条同一个口径：只补 EndTime 会让终态收口按失败接手）。
                task.Outcome = TaskOutcome.Skipped;
                task.EndTime ??= DateTime.Now;

                AppendLog(
                    "WARN",
                    $"{task.LogName}：这是 Android 应用安装包（ZIP 里同时有 {AndroidPackageDetector.ManifestEntryName} 与 "
                    + $"{AndroidPackageDetector.DexEntryName}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。"
                    + "要处理它请用专门的 APK 工具。");

                return;
            }

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
            // 第一大步不改分卷名（改名归第二大步）

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

                            // ⛔ 同上：起点改了，列表那一行仍旧显示"用户自己那个文件"（只改显示）。
                            task.ShowUserFileIdentity(regroupedFirst, FindUserOwnFileBesideItsDirectory(task, regroupedFirst));
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

                AppendLog("INFO", $"{task.LogName}：分卷已补齐，源文件快照重新记录（{task.VolumeCount} 卷）。");
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

                        AppendLog("INFO", $"{task.LogName}：文件头不在签名表里，但 7-Zip 能打开（{probe.FileCount} 个文件），继续处理。");
                    }
                    else
                    {
                        /*
                         * ⚠ 2026-10-14（官方环台 `10101010` / `19191919` 逐字，第二大步那一层）：
                         * **"7-Zip 打不开"不等于"它是垃圾"** —— 它可能是某个分卷组的一片。
                         *
                         * <para>现场逐字：`跳过：pack111-L2.7z —— 不是压缩包：7-Zip 打不开（7-Zip 无法识别或不支持该格式）`，
                         * 那一单落「已跳过」、**从没进过分卷那一套** ⇒ 组 `pack111-L2.7z.001` 永远凑不齐 ⇒ 判据 ① 红。
                         * 为什么既有的两道尺子都瞎：这一份**内容判不出**（7z 族的中间片/末片按定义没有魔数，
                         * 卡上 `格式=Unknown 分卷身份=Unknown`）、**名字也不像后续卷**
                         * （`pack111-L2.7z` = 卷标记被伪装吃掉，正刑"伪装成单一包"那一法）。</para>
                         *
                         * <para>判据（⛔ 只用既有事实，⛔ 不猜卷号、⛔ 不动盘上任何东西）：**同批任务里有同族的入口包 + 体积规律**
                         * —— ① 有另一单的名字读得出"第 1 卷"（`TryGetVolumeIndex == 1`）；
                         * ② 它的基名（唯一出口 <see cref="FileNameHelper.GetArchiveBaseName"/>）与这一份**相同或互为前缀**；
                         * ③ 这一份的体积 ≤ 那一份的体积（"除末卷外等大"⇒ 任何一片都不会比满片还大）。
                         * 命中 ⇒ 结论是**缺卷**（`VolumeMissing`），⛔ 不是「跳过」、⛔ 不是「失败」；片原样留着，
                         * 交给批末/跨链那一套去凑 —— 与单层路那两处**同一个结论口径**（只是证据多了一档）。</para>
                         */

                        /*
                         * ⛔ 2026-10-16 **这条判据终于落地**（它 2026-10-14 只写了注释、没进代码 —— 见上面那段）：
                         * 现场复核逐字（`10101010`，`result.json` + 盘面）：那一单落「已跳过 / Skipped」、
                         * 输出层 `…\pack111(3)\pack111(3)\pack111(3)\pack111-L2`，而它的三个兄弟片
                         * （`.001` / `.002` 载荷）分别在另外两条链的产出层里 ⇒ 组永远凑不齐 ⇒ 判据 ① 红。
                         */
                        /*
                         * ⛔⛔ 2026-10-16 实测纠正（探针逐字）：「批首就用同族入口包当判据」**判不出来** ——
                         * 那一刻批里**还没有 `.001` 那一单**（它由同批另一条链稍后才产出），
                         * 于是判据只看到 `pack111-W1-0.zip`（基名不同）⇒ 门不开 ⇒ 照旧落「已跳过」。
                         *
                         * 用户口径本来就写着这一步该怎么办（2026-10-09 逐字）：
                         * 「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」——
                         * 也就是说**第一趟不判死**：先记名单，等这一批的解压都跑完（那时同族入口包要么已经产出、
                         * 要么已经收拢到位）**再判一次**；批末才用严格判据落结论。
                         *
                         * ⇒ 与缺卷预检**同一条口径、同一个事实位**（`_volumeDeficitFinalPass`）。
                         */
                        /*
                         * ⚠ 2026-10-16 **第二版**（第一版被既有守门纠正过，如实记）：
                         * 第一版把"未识别"塞进了**缺卷名单**（`RecordDeferredVolumeDeficit`），
                         * 于是批末把一个纯文本文件改判成「分卷缺失」——
                         * `DeferredVolumeStatusTests.未识别的东西_只跳过_机器终态必须收口不许算未处理`
                         * 当场逮住（逐字 `Expected: "已跳过" / Actual: "分卷缺失"`）。
                         * ⇒ 正确形状（两个契约都要守）：
                         *   ① 当场**照旧落「已跳过」+ 机器终态收口**（那条契约一个字不改）；
                         *   ② 只**另记一笔**（`_unrecognizedMaybePiece`），批末再用**严格判据**
                         *      （同族第 1 卷 + 基名相同或互为前缀 + 体积 ≤ 满片）把"确实是某组一片"的
                         *      那种**升级**成缺卷 —— 那时同批该产出的都产出了，判据才看得见。
                         */
                        _unrecognizedMaybePiece.Add(task);

                        task.Status = StatusText.Skipped;
                        task.Operation = StatusText.OpSkip;
                        task.ProgressText = StatusText.ProgressCompleted;
                        task.ErrorMessage = string.IsNullOrWhiteSpace(probe.Message)
                            ? "不是压缩包：7-Zip 也打不开"
                            : $"不是压缩包：7-Zip 打不开（{probe.Message}）";

                        /*
                         * ⚠ 2026-10-09（B 前半「未识别的东西只跳过 + 记账」、用户原话：
                         * 「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」）：
                         * **机器终态必须显式落 `Skipped`** —— 老写法这一支只写状态、不写终态，
                         * 而这一支走的是**正常 `return`**（只有取消 / 异常那条 `finally` 才补 `EndTime`，
                         * 见 `RunScheduledTaskAsync` 的 catch）⇒ 终态收口 `FinalizeOutcomeIfPending`
                         * 的判据（要求 `EndTime` 有值）不成立 ⇒ 这一位永远是 `Pending`
                         * ⇒ ①页那一行写着「已跳过」、批末汇总却把它算进**「未处理」**（同一件事两处口径打架）。
                         *
                         * ⛔ **不能只补 `EndTime`**：那样终态收口会接手，而它的 else 支按
                         * `Status != VolumeMissing` 一律落 `Failed` ⇒ 一个"跳过"当场变成**失败**。
                         * ⇒ 显式写 `Skipped` 之后收口那一处自然 no-op（判据第一句就是 `Outcome == Pending`）。
                         * ⚠ 与 `ArchiveTask.MarkSkipped` 的关系：这里保持既有的四处赋值**一个字节不改**
                         * （`ProgressText` 那一格是「完成」，改了会动显示口径），只补终态与结束时间。
                         */
                        task.Outcome = TaskOutcome.Skipped;
                        task.EndTime = DateTime.Now;
                        task.LastUpdatedTime = DateTime.Now;

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
             * 试开定序的组任务：`CurrentPath` 被改到硬链接暂存组入口，按路径算出来的落点会落进
             * `.ArchiveFixer.work`（产物还会被当成过程物）。⇒ 回**试开之前**那一份落点；⛔ 只对
             * 我们自己登记过的组任务生效，其它任务一个字不改。
             */
            if (_stagedGroupOutputPaths.TryGetValue(task, out string? stagedOutputPath)
                && !string.IsNullOrWhiteSpace(stagedOutputPath))
            {
                outputPath = stagedOutputPath;
                outputRedirectNote = "试开定序暂存组入口";

                AppendLog(
                    "INFO",
                    $"{task.LogName}：产物落点回到试开之前的原落点 —— {outputPath}");
            }

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

                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
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
                        $"{task.LogName}：内层包，产物归入父任务输出目录 {outputPath}（不再另建目录）。");
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
                        $"{task.LogName}：落点就是源包所在目录 {outputPath}（解压到当前文件夹 / 就地整理），不套用“目录已存在就改名”的规则。");
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
                        $"{task.LogName}：落点是本次导入共用的目标目录 {outputPath}（添加文件夹 + 指定位置），" +
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

                        /*
                         * ⚠ 2026-10-09：**用户自己选的"跳过"不许被算成失败**。
                         *
                         * 老写法这一支写了 `EndTime` 却**没写终态** ⇒ 终态收口 `FinalizeOutcomeIfPending`
                         * 的判据（`Outcome == Pending` + `EndTime` 有值 + 校验没通过）**三条全中**，
                         * 而它的 else 支按"不是缺卷 ⇒ `Failed`"落结论（`:457-459`）
                         * ⇒ 用户在冲突框里点的那一下「跳过」，批末汇总按**失败**数、批末色带变**红**
                         * （用户点名过「为什么跳过要标红，这是非常错误的行为」）。
                         *
                         * ⇒ 显式落 `Skipped`：收口那一处随即 no-op（它第一句就是 `Outcome == Pending`）。
                         * ⛔ 与上面"未识别"那一条同一个病、同一个修法 —— 两处都不是"补 EndTime 就行"。
                         */
                        task.Outcome = TaskOutcome.Skipped;

                        AppendLog("WARN", $"{task.LogName}：同名冲突按你的选择跳过，本任务不解压（输出目录：{outputPath}）。");
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
                            $"{task.LogName}：输出目录已存在且非空，按你的选择「覆盖」沿用该目录：{outputPath}" +
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

                        AppendLog("WARN", $"{task.LogName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

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
                    ? $"{task.LogName}：本次实际输出目录 {outputPath}"
                    : $"{task.LogName}：本次实际输出目录 {outputPath}（依据 → {RunOptions.Describe()}）");

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
                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
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
                AppendLog("ERROR", $"{task.LogName}：{stageFailure}");
                return;
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            string engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.LogName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

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
             * ⚠ 2026-10-16（困难批 `21212121`，运行期实测）：**交给引擎的那一份，名字得是它自己的规范名**。
             *
             * <para>位置说明（⚠ 用词：这一档**不是**被删掉的那个用户档位 —— 那个是"三档递归模式"里的
             * 「只解当前这一层」，用户 2026-10-14 拍板删掉、出厂钉死为"展开所有分支"，见
             * <c>AppSettings.递归档位（已删除）</c> 那段说明。这里是 <c>ExtractSingleTaskAsync</c>：**把一份包交给引擎解**
             * 那一段；默认档解完接着走递归核心（下面那两处 `IsSingleLayerRecursion(task)` 闸门），
             * 而"第二大步"（分卷组只解一层）是**任务级事实位** `_volumeStepTasks` 让它只解这一层。）</para>
             *
             * <para>现场逐字（那一批日志）：批末补判已经判出「这一组现在齐了（共 3 卷）」并把这一单重跑，
             * 可引擎拿到的仍是伪装名 ⇒ `7z x … aaa-L2.part0788839中文1.rar` ⇒ 引擎原话
             * `ERROR = Missing volume : aaa-L2.part0788839中文2.rar` —— 它是按**手上这个名字**推兄弟卷名的，
             * 而这一组的三卷在盘上叫 `aaa-L2.part1/2/3.rar`（同一层、三卷全在）。</para>
             *
             * <para>实测（`fsutil hardlink list`）：`aaa-L2.part0788839中文1.rar` 与 `aaa-L2.part1.rar`
             * 是**同一个 File ID**（`…014d85`）⇒ 换个名字交给引擎**一个字节都不换**，只是让它认得出这一组。</para>
             *
             * <para>⛔ 判据全在读事实（唯一出口 <see cref="RecursiveExtractor.PreferCanonicalSiblingName"/>）：
             * 卡上的规范名非空且与当前文件名不同 + 同目录里真有那个名字 + 磁盘身份证明是同一份；
             * 任何一条不成立 / 判不出 ⇒ 原样返回（老口径一个字节不改）。⛔ 只影响"交给引擎的那一份"：
             * `task.CurrentPath` 与源包那一档一个字都不动（改名、删源包、统计、报告读的都是它）。</para>
             */
            engineArchivePath = RecursiveExtractor.PreferCanonicalSiblingName(engineArchivePath);

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

                if (!IsSingleLayerRecursion(task))
                {
                    /*
                     * 递归展开：第 0 层必须交给引擎（见 DirectReadAppliesTo 的说明），照旧抠取。
                     * 这一行是**如实说明**，免得用户看到"直读"两个字却发现空间没省下来。
                     *
                     * ⚠ 2026-10-15：这里原来打印**用户设置里那一档**（真机日志逐字：
                     * `递归模式（SingleChain）下第 0 层要交给 7-Zip 逐层展开…`）—— 屏幕上不该再出现
                     * 那个档位名（用户口径：「我不是将单层路给删掉了吗，现在就是全面解压所有分路」），
                     * 而这句本身要说的只是"没有走 ZIP 直读"，于是改成那句白话。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：递归展开（有压缩包就解压）下第 0 层要交给 7-Zip 逐层展开，"
                        + $"本次不走 ZIP 直读，仍按偏移取出内嵌归档（需要约 {carveBytes / 1024 / 1024} MB 临时空间）。");
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
                                $"{task.LogName}：内嵌归档是加密包（AES），直读已用第 {directZip.ResolvedPasswordIndex} 个候选密码" +
                                $"（按{ZipAesCrypto.DescribeEncoding(directZip.ResolvedPasswordEncoding)}）通过校验与认证码 —— " +
                                "7-Zip 对这类包只按 ANSI 字节派生密码，交给它会一律报密码错误，所以本次由内置读取器解。");
                        }

                        AppendLog(
                            "INFO",
                            $"{task.LogName}：内嵌归档可以 ZIP 直读（{directZip.List?.FileCount ?? 0} 个文件 / " +
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

                        AppendLog("ERROR", $"{task.LogName}：{directZip.Message}");
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

                        AppendLog("ERROR", $"{task.LogName}：{directZip.Message}");
                        return;
                    }
                    else
                    {
                        AppendLog("INFO", $"{task.LogName}：内嵌归档直读不支持（{directZip.Reason}），已回落到抠取。");
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

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                        return;
                    }

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：正在取出内嵌归档（约 {carveBytes / 1024 / 1024} MB），这一步在后台做，界面不会卡住。");

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
                            AppendLog("INFO", $"{task.LogName}：取出内嵌归档 {percent}%");
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

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                        return;
                    }

                    engineArchivePath = carve.OutputPath;

                    /*
                     * 抠出来那一份的名字按**内容**定（用户 2026-10-11 大测试 run-5555 实测的缺口）：
                     * 一整个 RAR5 尾巴被抠成 `X.zip` 时，`7z x` 按扩展名挑 zip 处理器直接拒绝
                     * （退出码 2、产物 0 个），上层还会把这次失败归到「分卷缺失」⇒ 整单跳过。
                     * 判据与唯一写点都在 AlignCarvedArchiveExtension 里（⛔ 不看后缀、⛔ 不猜）。
                     */
                    engineArchivePath = AlignCarvedArchiveExtension(task, engineArchivePath);

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
                    $"{task.LogName}：密码候选共 {candidates.Count} 个，超过每层上限 {attemptLimit} 个，" +
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
                    $"{task.LogName}：直读清单 {preflightList?.FileCount ?? 0} 个文件 / " +
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
                        $"{task.LogName}：{StatusText.EncryptedHeaders} —— 连内容清单都读不出来（需要正确密码），" +
                        $"本次跳过路径预检与资源预算，解压后仍会校验落点。原因：{lastListMessage}");
                }
                else
                {
                    AppendLog("WARN", $"{task.LogName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
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

                    AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}");
                    return;
                }

                PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

                if (!pathReport.IsSafe)
                {
                    task.Status = StatusText.ExtractFailed;
                    task.ErrorMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                    AppendLog("ERROR", $"{task.LogName}：路径预检未通过 —— {pathReport.Summary}");
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

                    AppendLog("ERROR", $"{task.LogName}：资源预算未通过 —— {budget.Reason}");
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

                AppendLog("INFO", $"{task.LogName}：空间门通过（精确） —— {preciseGate.Reason}；{refined.Basis}");

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
                        $"{task.LogName}：定稿 + 校验通过之后会按「删除操作」"
                        // ⚠ 2026-10-15：回收站档整条下线 ⇒ 走到这里（Keep 已挡掉）只剩彻底删除。
                        + "彻底删除其余物"
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
                    AppendLog("INFO", $"{task.LogName}：资源预算提示 —— {budget.Reason}");
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
            if (!IsSingleLayerRecursion(task))
            {

                RecursionResult? recursion;

                try
                {
                    recursion = await RunRecursiveAsync(
                        task, engineArchivePath, engineOutputPath, oneClickRun, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", $"{task.LogName}：递归解压异常 —— {ex.GetType().Name}：{ex.Message}");
                    throw;
                }

                // 这一趟有没有**可发布的产物**（用户 2026-10-06：「上一步得到的产物下一步就有可能成为
                // 所需要解压的文件」）—— ⛔ 发布与否只由产物事实回答，⛔ 不再由终态标签兼任开关：
                // `111.rar` 解出 `111.zip` 后因缺 `.z01` 停下时，旧写法把状态记成「部分完成」⇒
                // 校验 / 定稿 / 发布整条链一次都不跑 ⇒ 已经解出来的 `111.zip` 一个字节都不落地。
                bool hasPublishableProducts = recursion != null
                    && recursion.Layers.Any(layer => layer.Success)
                    && !string.IsNullOrWhiteSpace(recursion.FinalOutputPath);

                /*
                 * ⚠ **一拿到递归结果就搬**（位置三条硬要求，全是实测踩出来的）：
                 * ① **必须在工作区被清掉之前**：那一片（`111.zip`）就活在工作区的递归层目录里，
                 *    收尾一清它就没了 ⇒ 放到最后再搬会静默搬不到（守门用例当场逮到：
                 *    "这一步解出来的"那一行压根没出现、"结果校验 —— 输出目录是空目录"照旧）。
                 * ② **必须在结果校验之前**：校验读的是**暂存目录**，那一片不搬进去 ⇒ 校验看到空目录 ⇒
                 *    「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+「完整性：可证不完整」
                 *    ⇒ 源包一个字节都不处理（真机 2026-10-06 11:24 那两个 bug 就是这一处）。
                 * ③ **必须在"成功才进"那一支的外面**：`111.rar` 这类是「部分完成」⇒ 进不去那一支。
                 * ⇒ 三条一起满足的唯一位置就是这里（拿到结果之后、进任何收尾分支之前）。
                 */
                if (recursion != null && recursion.UnresolvedVolumePieces.Count > 0)
                {
                    List<string> carried = PublishUnresolvedVolumePieces(recursion, task);
                    AdoptUnresolvedVolumePieces(recursion, task, carried);

                    // 接片接不进去的那几份，才在产出它的那一单自己的落点里补一份（顺序不许反过来）。
                    PublishUnresolvedPieceLandingCopies(carried, task);
                }

                if (task.Status == StatusText.ExtractSuccess || hasPublishableProducts)
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
                 * ⚠ 落地那一档**已经在上面搬过了**（拿到递归结果就搬 —— 必须在工作区被清掉之前）。
                 * ⛔ 这里不要再搬一遍。
                 */

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

                AppendLog("INFO", $"{task.LogName}：开始解压前测试。");

                for (int i = 0; i < maxPasswordAttempts; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.LogName}：测试密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

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

                        AppendLog("INFO", $"{task.LogName}：测试通过。");
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
                        AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
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

                    AppendLog("ERROR", $"{task.LogName}：测试失败，原因：{testResult.Message}");
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

                    /*
                     * ⚠ 2026-10-13（困难批 `run-4444`，运行期定案）：**单层这条路也要问一次"这一份是什么"**
                     * —— 与递归路那三处**同一个唯一出口**（<see cref="RecursiveExtractor.IsPieceOfIncompleteGroup"/>）。
                     *
                     * <para>现场逐字：那一单（`bbb-L2.rar` = 程序从包装里救出来的那一片）在单层路上
                     * 10 条候选全试完、一条都没解开 ⇒ 落 `PasswordAttemptLimitReached` ⇒ 机器终态 `Failed`
                     * （日志逐字 `解压失败：bbb-L2.rar，原因：已达到密码尝试上限：本层试了 10 个候选（共 12 个），
                     * 未能确认密码。`）。而卡上逐字 `格式=Rar 分卷身份=VolumeMember 卷号=判不出 首卷标志=False`
                     * ⇒ 它**可证是某个还没凑齐的分卷组的一员**，同组入口不在旁边 ⇒ 结论应当是**缺卷**。</para>
                     *
                     * <para>⚠ `requireNothingElseBeside: true`：单层路这一档还要求**同层里没有别的名字也属于这一组的东西**
                     * ——`EncryptedVolumePipelineTests` 那两条守门（三片同层、名字都缀了「删除」）钉的就是这一条：
                     * 不加它会把"片都在、只是名字都坏了"错判成「缺卷」（实测逐字
                     * `Expected: "密码错误" Actual: "分卷缺失"`）。⛔ 递归路那三处**不用**这一条。</para>
                     */
                    if (RecursiveExtractor.IsPieceOfIncompleteGroup(task.CurrentPath, requireNothingElseBeside: true))
                    {
                        task.Status = StatusText.VolumeMissing;
                        task.ErrorMessage =
                            "分卷缺失：这一片属于某个分卷组，而同组的入口包不在这一层 —— "
                            + "按「缺卷」处理（⛔ 不是「密码不对」，也⛔ 不是「解压失败」）；"
                            + "片原样留着，交给批末/跨链那一套去凑。";

                        /*
                         * ⚠ 2026-10-14（走查 + 日志实测，`10101010` 那一组）：这一支原来**只写状态**、
                         * 不登记进"缺卷留到最后再判"那份名单 ⇒ 批末重判**一次都不跑**
                         * （日志里"批末补判 / 这一组现在齐了"整段是空的）⇒ 三片都在盘上也没人把它们收到一层
                         * ⇒ 判据 ① 红。⇒ 结论是缺卷就必须进名单（唯一出口 <see cref="RememberDeferredVolumeDeficit"/>，
                         * 它只管"进名单 + 点亮事实位"，⛔ 不碰 Status / ErrorMessage，⛔ 幂等）。
                         */
                        RememberDeferredVolumeDeficit(task, task.CurrentPath);

                        AppendLog("WARN", $"{task.LogName}：{task.ErrorMessage}");
                    }
                    else if (stoppedByAttemptLimit)
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

                AppendLog("INFO", $"{task.LogName}：测试通过，开始正式解压。");

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
                            $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
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
                            $"{task.LogName}：整包已加密 —— 跳过「空密码」这一档（引擎造不出用空密码加密的包，"
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
                            $"{task.LogName}：这一包连文件名都加密（引擎说 {lastListErrorType}）—— 同样跳过「空密码」这一档。");
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
                            $"{task.LogName}：已按「停止后续」中断 —— 剩余 "
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
                                $"{task.LogName}：密码预检不通过（只解了 {PasswordProbe.Describe(probeEntryPath, probeEntrySize)}，"
                                + "整包一个字节都没动）—— 这个候选不对，试下一个。");

                            continue;
                        }

                        if (probe == PasswordProbeOutcome.Verified)
                        {
                            AppendLog(
                                "INFO",
                                $"{task.LogName}：密码预检通过（{PasswordProbe.Describe(probeEntryPath, probeEntrySize)} 解开了）"
                                + "—— 这个候选是对的，开始解整包。");
                        }
                    }

                    AppendLog("INFO", $"{task.LogName}：开始解压，密码候选 {i + 1}/{maxPasswordAttempts}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

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
                                    $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
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
                                $"{task.LogName}：结果校验 —— {stage.Verification.Message}" +
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
                            TryMeasureProducedContent(engineOutputPath, extractResult, out int wrongPwFiles, out long wrongPwBytes);

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
                        AppendLog("INFO", $"{task.LogName}：这个密码候选不对，继续试下一个。");
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
                            TryMeasureProducedContent(engineOutputPath, extractResult, out int ambiguousFiles, out long ambiguousBytes);

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
                    /*
                     * "这一单整体有没有内容物解出"（终态分类那一档的判据输入，用户 2026-10-16 口径）：
                     * 用**唯一出口** `ProducedContentGate`（与"密码已被证实"同一把尺子，⛔ 不另定阈值）
                     * 数一次产物目录里真解出来的非空文件。
                     */
                    bool producedContent =
                        ProducedContentGate.TryMeasure(
                            engineOutputPath,
                            lastResult?.ReportedSubItemErrors ?? 0,
                            lastResult?.FailedEntryNames?.Count ?? 0,
                            out int producedFiles,
                            out _)
                        && producedFiles >= ProducedContentGate.MinimumFiles;

                    RecordPartialPublishCandidate(
                        task,
                        engineOutputPath,
                        engineArchivePath,
                        selectedPassword,
                        lastLayerManifestEntries,
                        lastResult?.FailedEntryNames,
                        lastResult?.ReportedSubItemErrors ?? 0,
                        engineReportedFailure: lastResult != null && !lastResult.Success,
                        detectedErrorType: lastResult?.DetectedErrorType,
                        chainProducedContent: producedContent);

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

                        /*
                         * ⚠ 与上面"先测试再解压"那条分支**同一个唯一出口**（说明见那一处 2026-10-13 那条注释）：
                         * "这一份可证是某个还没凑齐的分卷组的一员" ⇒ 结论是**缺卷**，⛔ 不是密码问题、⛔ 不是失败。
                         * ⚠ `requireNothingElseBeside: true`（单层路专用，理由同上）。
                         */
                        if (RecursiveExtractor.IsPieceOfIncompleteGroup(task.CurrentPath, requireNothingElseBeside: true))
                        {
                            task.Status = StatusText.VolumeMissing;
                            task.ErrorMessage =
                                "分卷缺失：这一片属于某个分卷组，而同组的入口包不在这一层 —— "
                                + "按「缺卷」处理（⛔ 不是「密码不对」，也⛔ 不是「解压失败」）；"
                                + "片原样留着，交给批末/跨链那一套去凑。";

                            // 与另一条收尾链同一口径：**结论是缺卷就必须进"缺卷留到最后再判"那份名单**
                            // （唯一出口，见那一处的说明；⛔ 不碰 Status / ErrorMessage，⛔ 幂等）。
                            RememberDeferredVolumeDeficit(task, task.CurrentPath);

                            AppendLog("WARN", $"{task.LogName}：{task.ErrorMessage}");
                        }
                        else if (stoppedByAttemptLimit)
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
                                $"{task.LogName}：{task.ErrorMessage}"
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
                    $"{task.LogName}：这次拿到的是「{StatusText.EncryptedHeaders}」的结论" +
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
                    $"{task.LogName}：换引擎兜底解出来的产物没有通过结果校验 —— {stage.Verification.Message}"
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
                AppendLog("WARN", $"{task.LogName}：清理候选产物失败（{ex.Message}），继续尝试下一个候选。");
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
            /*
             * 候选名字 = 任务上记着的那一个 + 内容的三种标准后缀各一个
             * （`AlignCarvedArchiveExtension` 只可能把名字改成 `.7z` / `.rar` / `.zip` 之一）。
             *
             * 为什么要都试：这一份是**上一次进程**留下的，那时任务上记的还不是这次的结论；
             * 只按 `.zip` 找 ⇒ 上一次被按内容改名成 `.rar` 的那一份永远清不掉，
             * 定稿时会被当成本任务的其余物搬进用户目录（正是这个方法要防的那件事）。
             */
            var candidates = new List<string> { BuildEmbeddedArchivePath(task) };

            foreach (string extension in new[] { "7z", "rar", "zip" })
            {
                string candidate = Path.ChangeExtension(candidates[0], extension);

                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }

            try
            {
                foreach (string carvedPath in candidates)
                {
                    if (string.IsNullOrWhiteSpace(carvedPath) || !File.Exists(carvedPath))
                    {
                        continue;
                    }

                    if (!ArchivePathGuard.IsInsideRoot(_pathService.WorkDirectory, carvedPath, out string reason))
                    {
                        AppendLog("WARN", $"{task.LogName}：上次留下的抠取副本不在工作区根之下，已跳过清理 —— {reason}");
                        continue;
                    }

                    File.Delete(carvedPath);

                    AppendLog(
                        "INFO",
                        $"{task.LogName}：已清掉上一次回落留下的抠取副本（本次走直读，不需要它）：{Path.GetFileName(carvedPath)}");
                }
            }
            catch (Exception ex)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：上次留下的抠取副本没清掉（{ex.Message}），它会在定稿时被当成其余物搬走。");
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
                $"{task.LogName}：开始 ZIP 直读解压（{plan.List?.FileCount ?? 0} 个文件 / " +
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

                AppendLog("ERROR", $"{task.LogName}：ZIP 直读失败 —— {extract.Message}");
                return;
            }

            AppendLog(
                "INFO",
                $"{task.LogName}：ZIP 直读完成 —— {extract.Message}"
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
             * ⚠ 后缀**默认**还是 `.zip`，哪怕抠出来的其实是 7z / RAR（用户 2026-09-25 第 42 条实测过才留下的）：
             *
             * · 而"容器里装的是分卷的第一卷"时，后缀会改变引擎的判决：
             *   叫 `封面.zip` → 引擎报的是「分卷缺失」这一类（成因说得对）；
             *   叫 `封面.7z` → 引擎改口说「**文件损坏**」（把"缺后续卷"说成了"包坏了"，用户会去怀疑文件）。
             *   实测三种命名（同一段第一卷字节）：`.zip` → Is not archive / 分卷缺失一类；
             *   `.7z` 与 `.7z.001` → Unexpected end of archive（被归到"文件损坏"）。
             *   所以**分卷那一档**不许"顺手改成真实格式的后缀" —— 那会把最需要说清的那一档说歪。
             *   回归 = `SplitInsideContainerTests` 里那两条引擎行为证据。
             *
             * ⚠ 2026-10-11 大测试实测补上另一半（run-5555，8.4 MB 媒体前缀 + 一整个 RAR5 尾巴）：
             *   上面那个"完整归档不看后缀"的前提**对大的 RAR 尾巴不成立** —— 同一份 96 MB 字节，
             *   叫 `X.zip` 时 `7z x` 退出码 2、`Open ERROR: Cannot open the file as [zip] archive`、**产物 0 个**；
             *   叫 `X.rar` 时退出码 0、4 个产物。于是"完整归档"那一档的名字改由内容证据决定，
             *   写点只有一个：`AlignCarvedArchiveExtension`（见 `ArchiveTask.CarvedArchiveExtension`）。
             */
            string suffix = string.IsNullOrWhiteSpace(task.CarvedArchiveExtension)
                ? ".zip"
                : task.CarvedArchiveExtension;

            return Path.Combine(_pathService.BuildTaskWorkDirectory(task), baseName + suffix);
        }

        /// <summary>
        /// 抠出来的那一份**按内容起名**（用户 2026-10-11 大测试 run-5555 实测出来的缺口）。
        ///
        /// <para><b>缺口现场</b>：`222-L5.png`（8.4 MB 媒体前缀 + 一整个 RAR5 尾巴）抠出来的那份叫
        /// `222-L5.zip` ⇒ `7z x` 按扩展名挑 zip 处理器、直接拒绝（退出码 2、产物 0 个）⇒
        /// 上层把这次失败归到「分卷缺失」⇒ 整单跳过、内容物一个都没有。
        /// 而同一份字节改名成 `.rar` 就能正常解开（本机实测，两个名字只差后缀）。</para>
        ///
        /// <para><b>判据（只读内容事实，⛔ 不看后缀、⛔ 不猜）</b>：抠完拿底层那张卡
        /// <see cref="FileNature.Inspect"/> —— 内容**可证"不是分卷组的一员"** 且魔数认得出格式时才改名；
        /// 真分卷成员（`VolumeMember`）与 7z 那一族（判不出）**一个字节都不动**，
        /// 保持用户 2026-09-25 第 42 条要的 `.zip`（那一档的回归 = `SplitInsideContainerTests`）。</para>
        ///
        /// <para>改名只发生在**本任务自己的暂存目录**里（抠取副本是我们自己刚写出来的临时文件）：
        /// 拿不到"确实在工作区根之下"这个事实证明就什么都不做。</para>
        /// </summary>
        private string AlignCarvedArchiveExtension(ArchiveTask task, string carvedPath)
        {
            if (task == null || string.IsNullOrWhiteSpace(carvedPath) || !File.Exists(carvedPath))
            {
                return carvedPath;
            }

            FileNatureCard card = FileNature.Inspect(carvedPath);
            string realExtension = VolumeContentInference.ExtensionFor(card.Format);

            if (realExtension.Length == 0)
            {
                CriterionLog(
                    "内嵌归档：抠出来的那一份按内容起名",
                    $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：{Path.GetFileName(carvedPath)}",
                    "不按内容起名（魔数认不出格式）",
                    "保持现有名字",
                    carvedPath);

                return carvedPath;
            }

            if (card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember)
            {
                CriterionLog(
                    "内嵌归档：抠出来的那一份按内容起名",
                    $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：{Path.GetFileName(carvedPath)}",
                    "内容不能证明「不是分卷组的一员」⇒ 保持 .zip（用户 2026-09-25 第 42 条：这一档叫 .zip 引擎才报「分卷缺失」）",
                    "保持现有名字",
                    carvedPath);

                return carvedPath;
            }

            string wanted = "." + realExtension;

            if (string.Equals(Path.GetExtension(carvedPath), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return carvedPath;
            }

            if (!ArchivePathGuard.IsInsideRoot(_pathService.WorkDirectory, carvedPath, out string reason))
            {
                AppendLog("WARN", $"{task.LogName}：抠出来的那一份不在工作区根之下，没有按内容改名 —— {reason}");

                return carvedPath;
            }

            string target = Path.ChangeExtension(carvedPath, realExtension);

            try
            {
                File.Move(carvedPath, target, overwrite: true);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{task.LogName}：抠出来的那一份按内容改名失败（沿用原名继续）：{ex.Message}");

                return carvedPath;
            }

            task.CarvedArchiveExtension = wanted;

            AppendLog(
                "INFO",
                $"{task.LogName}：抠出来的那一份按内容起名（内容可证不是分卷组的一员）：" +
                $"{Path.GetFileName(carvedPath)} → {Path.GetFileName(target)}");

            CriterionLog(
                "内嵌归档：抠出来的那一份按内容起名",
                $"内容：格式={card.Format} 分卷身份={card.Membership}｜名字：原名 {Path.GetFileName(carvedPath)}",
                $"内容格式={card.Format} ⇒ 后缀该是 {wanted}",
                $"改名 {Path.GetFileName(target)}",
                target);

            return target;
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
                        $"{task.LogName}：同目录里有 {orphans.Count} 组缺首卷的分卷，无法确定容器里装的是哪一组；" +
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
                    AppendLog("WARN", $"{task.LogName}：拼装目录里已经有程序没造过的东西，本次不拼（一个字节都不动）。");
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
                    AppendLog("INFO", $"{task.LogName}：没有自动把两边的分卷接起来 —— {plan.Reason}");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：{plan.Reason}正在工作区里接起来（你的源文件只读，一个字节都不动）。");

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
                        AppendLog("INFO", $"{task.LogName}：正在复制后续卷 …已复制 {TaskSpaceEstimate.FormatSize(copied)}");
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
                    AppendLog("WARN", $"{task.LogName}：自动拼装没做成 —— {assembled.Reason}照旧按「分卷缺失」处理。");
                    return (null, null);
                }

                foreach (string line in assembled.LogLines)
                {
                    AppendLog("INFO", $"{task.LogName}：{line}");
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
                        $"{task.LogName}：接上后续卷之后引擎仍然打不开这一套（可能这一组本身就是坏的），" +
                        "退回原路，照旧按「分卷缺失」报。");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：接上后续卷之后引擎能打开了（{assembledList.FileCount} 个文件），" +
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
                AppendLog("WARN", $"{task.LogName}：自动拼装时出错（{ex.Message}），退回原路（结论不受影响）。");
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
