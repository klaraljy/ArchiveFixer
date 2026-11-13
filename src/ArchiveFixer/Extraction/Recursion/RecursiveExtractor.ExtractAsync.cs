using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public sealed partial class RecursiveExtractor
    {        /// <summary>
             /// 从 <see cref="ArchiveTask.CurrentPath"/> 开始递归展开。
             /// </summary>
             /// <param name="previousDecision">
             /// 非 null 表示"用户已经就上一次的多分支询问做出了选择：继续"。
             /// 这时只处理 <see cref="RecursionDecisionRequest.CandidateArchives"/> 里那几个归档，
             /// **不重新全盘扫描** —— 用户看到的候选清单可能已经被别的东西改过，重扫等于偷偷扩大范围。
             /// </param>
             /// <param name="progress">
             /// 进度接收端（可空）。**直接转发给引擎**（写进每层的 <see cref="ArchiveRequest.Progress"/>），
             /// 节流由引擎层的 <see cref="ArchiveProgressReporter"/> 负责 —— 递归核心**不自己再节流一次**，
             /// 也不自己造一套进度类型（AGENTS.md §3.1：统一进度只有 <see cref="ArchiveProgress"/> 一个）。
             ///
             /// <para>
             /// 为什么补这个口子（2026-09-22）：递归内层包过去完全没挂进度，界面上那一段是"处理中"死等，
             /// 而递归恰恰是最容易久的一段（一层套一层）。挂上之后每层的百分比 / 当前条目都会照常上报，
             /// 于是"当前解的是哪一层里的哪个文件"用户看得见。
             /// </para>
             /// </param>
             /// <param name="stalled">
             /// "长时间没有任何引擎输出"的通知（可空，默认阈值 90 秒）。与 <paramref name="progress"/> 同一个口径：
             /// 只提示、不杀进程，由上层决定怎么显示（不变量 9）。
             /// </param>
        public async Task<RecursionResult> ExtractAsync(
            ArchiveTask task,
            string finalOutputDirectory,
            RecursionDecisionRequest? previousDecision = null,
            CancellationToken cancellationToken = default,
            IProgress<ArchiveProgress>? progress = null,
            Action<ArchiveStallNotice>? stalled = null)
        {
            var layers = new List<RecursionLayerReport>();
            var stopReason = RecursionStopReason.None;

            /*
             * 逐层回收那本账**每次运行从零开始**：这个实例可能被同一个包的续跑复用
             * （多分支询问 → 用户确认继续，见 `_supersededWorkspaces` 的说明），
             * 上一趟记下的路径在这一趟就是别人的事了 —— 带着它只会让发布侧把
             * 一份**还好好在盘上**的包当成"我们自己回收过"，从而不再如实点名（假话）。
             */
            ReclaimedProcessArtifacts.Clear();

            /*
             * 这一趟的两本账也从零开始（同一个实例可能被续跑复用）：临时链接（必须删干净）与
             * "借来用过的用户源片"（给协调器记"这一组已由谁解出"）。
             */
            _crossBoundaryLinks.Clear();
            _consumedVolumeSources.Clear();
            _unresolvedVolumePieces.Clear();

            /*
             * 换引擎兜底那句结论也**每次运行从零开始**：同一个实例可能被同一个包的续跑复用
             * （多分支询问 → 用户确认继续），上一趟留下的"已换 WinRAR 再试过一遍"在这一趟
             * 就是一句不成立的话（这一趟压根没兜底）。
             */
            _passwordFallbackNote = string.Empty;

            /*
             * 这个字段只表示"这一次运行有没有产生新的、还没回答的询问"。
             * 刻意不从 previousDecision 起手：传了 previousDecision 就说明用户已经回答过了，
             * 把它原样塞回结果会让调用方以为"还得再问一次"（RecursionResult.Decision 的约定是
             * "非 null = 需要用户就多分支做选择"）。
             */
            RecursionDecisionRequest? decision = null;

            int totalFiles = 0;
            long totalSize = 0;

            /*
             * 第 1 层起停下来时要能说清"还有**哪几个**内层归档没展开"（不变量 8：多分支默认不展开，
             * 必须问；用户 2026-10-04 真机：只报个数时他看不出是哪一个）。
             */
            IReadOnlyList<string> unexpandedNames = Array.Empty<string>();

            /*
             * 因"源文件已变化"停下时，那句话就是本次的结论（不变量 11）。
             * 与别的停因分开记：它的**理由来自调用方**（快照与状态都在协调器那边），
             * 这里只负责原样带进 RecursionResult.Summary，不许在这里自己编一句。
             */
            string sourceChangedReason = string.Empty;

            // 参数校验放在建工作区之前：路径都没有就没什么可展开的，
            // 这里也**不抛异常**（递归核心对外的约定是"用返回值说清楚"，不是"炸给调用方"）。
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return new RecursionResult
                {
                    StopReason = RecursionStopReason.EngineFailed,
                    Completed = false,
                    PartiallyCompleted = false,
                    Decision = decision,
                    Layers = Array.Empty<RecursionLayerReport>(),
                    FinalOutputPath = string.Empty,
                    Summary = "任务没有可处理的归档路径，未开始递归解压"
                };
            }

            // 工作区必须在循环之前就建出来：取消 / 立刻失败时也要留下"产物在哪"的线索。
            ExtractionWorkspace workspace = CreateWorkspace(task);

            /*
             * 本次运行的工作区由**本实例自己持有**（见 CurrentWorkspace 的说明）：
             * 无论后面是清理还是发布，用的都是这一个对象 —— 不去扫目录、不去按名字猜，
             * 所以并发跑的两个任务永远碰不到对方的目录。
             *
             * 顺带判断"这一次是不是同一个归档的续跑"（多分支询问 → 用户确认继续）：
             * 续跑会把第 0 层在新工作区里**重解一遍**，上一次那份产物因此变成纯垃圾
             * （往往是几百 MB）。它当时没被清是对的（"等用户决定"属于部分完成，线索要留住）；
             * 现在用户已经决定、整条递归也真的走完了，那个理由不再成立 —— 见 CleanupSupersededWorkspaces。
             *
             * 判据里必须比对**归档路径**：调用方若把同一个实例拿去跑另一个任务，
             * 上一个任务的工作区属于"部分完成"，不能因为别的任务成功就被删掉。
             */
            string runArchivePath = SafePathHelper.GetFullPathSafe(task.CurrentPath);
            ExtractionWorkspace? previousWorkspace = CurrentWorkspace;

            /*
             * 这一趟"找卷窗口"的基准 = 最外层源包（用户 2026-10-05 口径）。每趟重算一次：
             * 续跑时源包可能已经被改名 / 搬走，拿上一趟的路径去枚举就是拿一份过期读数。
             */
            _rootSourcePath = runArchivePath;
            _rootSourceCandidates = null;

            bool continuesSameArchive = previousDecision != null &&
                previousWorkspace != null &&
                runArchivePath.Length > 0 &&
                SafePathHelper.PathEquals(_lastRunArchivePath, runArchivePath);

            if (continuesSameArchive)
            {
                _supersededWorkspaces.Add(previousWorkspace!);
            }
            else
            {
                // 不是同一个包的续跑：上一轮攒下的那几份属于**别的**任务（部分完成、要保留），
                // 不能再由本次的成功去清它们。
                _supersededWorkspaces.Clear();
            }

            CurrentWorkspace = workspace;
            _lastRunArchivePath = runArchivePath;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pending = new Queue<WorkItem>();

                /*
                 * previousDecision 的语义（规则 4）：用户已经就上次的多分支询问选了"继续"。
                 * 把候选清单压成**文件名**挂在第 0 层的执行项上 —— 后面每一层都拿它做过滤，
                 * 于是"只处理用户点名的这几个归档、不重新全盘扫描"这件事在代码里只有一处实现。
                 *
                 * 为什么不能直接比完整路径：决策里带的是**上一次运行的工作区**里的路径，
                 * 而本次续跑会新建一个工作区（taskId 带时间戳 + 随机码），绝对前缀必然不同 ——
                 * 拿完整路径比对会把用户点过的每一个分支都判成"这一层里没有"，选择被静默丢掉。
                 * 层内部的文件名是两次运行之间唯一稳定的定位键（同一个归档、同一个引擎解出来）。
                 */
                IReadOnlyList<string>? decidedNames = previousDecision == null
                    ? null
                    : previousDecision.CandidateArchives
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .ToList();

                pending.Enqueue(new WorkItem
                {
                    Depth = 0,
                    ArchivePath = task.CurrentPath,
                    Layer = workspace.CreateNextLayer(task.CurrentPath),
                    IsRoot = true,
                    DecisionNames = decidedNames
                });

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    WorkItem item = pending.Dequeue();

                    /*
                     * 上限检查必须在**真正动手之前**做（规则 5），而且顺序固定：
                     * 层数 → 累计文件数 → 累计大小 → 展开比。
                     * 顺序固定是为了让"命中哪一条"可预期、可复现、可写进测试；
                     * 密码尝试上限不在这里查，它是**每层内部**的事（见 ExtractLayerAsync）。
                     *
                     * 展开比要问引擎"这个包解压后多大"（第 0 层）或量磁盘（内层），
                     * 两者都可能要等 I/O —— 所以它是 async 的，**绝不在这里同步等待**：
                     * 同步 GetResult() 会把 UI 线程占住，而 7z 的续体要回到同一个线程，
                     * 两边互等就是历史"界面永久无响应"的真凶（无弹窗、无子进程、CPU 不忙）。
                     */
                    RecursionStopReason blocked = CheckLimitsBeforeLayer(item, layers, totalFiles, totalSize);

                    if (blocked != RecursionStopReason.None)
                    {
                        ReportStopReason(blocked, $"{item.ArchivePath}：上限/安全闸门拦下（{blocked}）");
                        stopReason = blocked;
                        break;
                    }

                    if (layers.Count < MaxExpansionRatioChecks &&
                        await IsExpansionRatioExceededAsync(item, layers, pending).ConfigureAwait(false))
                    {
                        ReportStopReason(RecursionStopReason.ExpansionRatioExceeded, $"{item.ArchivePath}：展开比超限");
                        stopReason = RecursionStopReason.ExpansionRatioExceeded;
                        break;
                    }

                    /*
                     * 不变量 11：**本层开工之前**再问一次"源文件还是原来那一份吗"。
                     *
                     * 位置就在上限检查之后、真正动手（列目录 / 调引擎）之前：
                     * 上限检查只算数、展开比那一步虽然会问引擎"这个包解压后多大"、
                     * 但那是只读的探查；真正的写盘发生在 ExtractLayerAsync 里面，
                     * 所以拦在这里 = 一个字节都没写过。
                     *
                     * 只对第 0 层问（见 _sourceCheck 的说明）：第 1 层起解的是我们自己
                     * 从内层抠出来的过程物，拿源包的快照去比它们只会得出一句错误的结论。
                     */
                    if (item.IsRoot && _sourceCheck != null)
                    {
                        string? sourceChanged = await _sourceCheck(task).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(sourceChanged))
                        {
                            ReportStopReason(RecursionStopReason.SourceChanged, sourceChanged);
                            stopReason = RecursionStopReason.SourceChanged;
                            sourceChangedReason = sourceChanged;
                            break;
                        }
                    }

                    LayerOutcome outcome = await ExtractLayerAsync(item, cancellationToken, progress, stalled)
                        .ConfigureAwait(false);

                    /*
                     * 这一层用完就把"跨盘 ZIP 末片"那一档建的**临时链接**删掉（删名字不动数据）：
                     * 留着会被发布侧当成品搬进用户目录，还会把「半套分卷」闸门自己绊倒。
                     * ⛔ 位置就在这里（层一跑完，不管成没成）—— 排在发布与逐层回收之前。
                     */
                    RemoveCrossBoundaryLinks();

                    if (outcome.Report != null)
                    {
                        layers.Add(outcome.Report);

                        /*
                         * 层成功与否只在这里标一次（就地替换发布只搬"真的解开了"的层）：
                         * 失败的报告会带着 StopReason 让循环 break，所以下面这行是**唯一**的标记点，
                         * ⛔ 不许在别处再标一遍 —— 两个地方各标一次迟早漂移。
                         */
                        item.Layer.Successful = outcome.Report.Success;
                    }

                    if (outcome.StopReason != RecursionStopReason.None)
                    {
                        if (!_exactStopReasonReported)
                        {
                            ReportStopReason(
                                outcome.StopReason,
                                $"{item.ArchivePath}：{outcome.StopReason}");
                        }

                        _exactStopReasonReported = false;
                        stopReason = outcome.StopReason;
                        break;
                    }

                    /*
                     * ⛔ **先记事实，再动手删任何东西**：发布侧要回答"这一层除了交给下一层的内层包，
                     * 还有没有别的东西"（判据 <see cref="PackageLayerRules.ProducedOwnContent"/>，
                     * 决定该不该给它留一个包名目录），而它的过程物在**下面那一支**里就要被删掉了 ——
                     * 发布时再读盘只会读到空目录（实测：该留的层目录被摊掉，
                     * 两遍对照的产物路径因此不一致，而这不是"逐层回收"该改变的东西）。
                     *
                     * 位置在**停因检查之后、回收之前**，而且对**每一层**都记（第 0 层也要）：
                     * 记的时机是"这一层刚跑成、它的产物还没被动过"那一刻 —— 与判据原来的读法
                     * （发布时读一次盘）语义完全相同，只是把那一刻的读数留住了。判据一个字没改。
                     */
                    item.Layer.EntryCountBeforeReclaim ??=
                        PackageLayerRules.CountEntriesOrUnknown(item.Layer.OutputPath);

                    /*
                     * ===== 递归路「逐层回收」：这一层跑成了，就把**上一层交出来的那个内层包**还回去 =====
                     *
                     * 位置是刻意的，四条理由缺一不可（AGENTS.md §11.3 的口径 + 两条红线）：
                     *
                     * ① **排在 `ExtractLayerAsync` 之后**：这一刻这一层的输入包才真的用完了。
                     *    ⚠ 2026-10-05 实测踩过：写到它**之前**时，回调会把 `level2.7z` 删掉，
                     *    紧接着本层就要解 `level2.7z` ⇒ 引擎报「压缩包文件不存在」、
                     *    整条链停在"已完成 1 层"（第一次跑 R1 就是这个形状）。
                     * ② **排在停因检查之后**：本层失败 / 被上限拦下 ⇒ 链就停在这里，后面全不跑 ——
                     *    那一刻上一层的包**留着**才是对的（红线：失败 / 部分完成 / 取消 ⇒ 一个字节都不动）。
                     * ③ **排在"上限 / 展开比 / 不变量 11"之后**：那三关任意一关拦下就是"这一层没跑"，
                     *    同样属于"这一趟没成"，一样不许动字节。
                     * ④ **第 0 层（用户给的源包）永远没有回收项** —— 它的输入是源包，
                     *    口径是"最外层源包只留到链尾"，所以入队时压根不会给它建回收项；
                     *    这里再查一次 `!item.IsRoot` 是兜底：谁哪天把回收项挂到第 0 层上，
                     *    最坏也只是这一层不回收，绝不会碰到源包。
                     *
                     * ⛔ 回调自己不删任何东西（判据与执行体都在协调器那一个出口里）；
                     * 回调为 null（没接 / 不是「彻底删除」档）= 一次都不调。
                     */
                    if (!item.IsRoot && item.Reclaims.Count > 0 && LayerReclaim != null)
                    {
                        foreach (RecursiveLayerReclaimRequest reclaimRequest in item.Reclaims)
                        {
                            /*
                             * 先登记、再回调：回调可能把这一份整份删掉，而发布那一步要在
                             * "它已经不在了"时知道"那是我们自己决定的"（见 ReclaimedProcessArtifacts）。
                             * ⛔ 登记不是判据 —— 真删没删由发布侧读盘上事实回答。
                             */
                            foreach (string groupFile in reclaimRequest.GroupFiles)
                            {
                                ReclaimedProcessArtifacts.Add(groupFile);
                            }
                            if (_limits.KeepDeepestProcessArtifact
                                && outcome.Report is { InnerArchives.Count: 0 })
                            {
                                string? outputRoot = ResolveWorkspaceOutputRoot(workspace.RootDirectory);

                                if (!string.IsNullOrWhiteSpace(outputRoot) && Directory.Exists(outputRoot))
                                {
                                    foreach (string groupFile in reclaimRequest.GroupFiles)
                                    {
                                        if (!File.Exists(groupFile))
                                        {
                                            continue;
                                        }

                                        string? retainedRoot = ResolveRetainedArtifactRoot(outputRoot);

                                        if (string.IsNullOrWhiteSpace(retainedRoot))
                                        {
                                            continue;
                                        }

                                        string retained = Path.Combine(
                                            retainedRoot,
                                            "其余物",
                                            Path.GetFileName(groupFile));

                                        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);

                                        if (!File.Exists(retained))
                                        {
                                            try
                                            {
                                                File.Copy(groupFile, retained, overwrite: false);
                                            }
                                            catch
                                            {
                                                // 留不下就按原逻辑继续。
                                            }
                                        }
                                    }
                                }

                                continue;
                            }

                            LayerReclaim(reclaimRequest);
                        }
                    }

                    RecursionLayerReport report = outcome.Report!;

                    totalFiles += report.OutputFileCount;
                    totalSize += report.OutputSize;

                    var enqueueState = new EnqueueState
                    {
                        Pending = pending
                    };

                    if (!TryEnqueueNextLayers(
                            item,
                            report,
                            workspace,
                            enqueueState,
                            out RecursionDecisionRequest? askUser,
                            out RecursionStopReason enqueueStopReason))
                    {
                        unexpandedNames = enqueueState.UnexpandedNames.ToList();

                        if (askUser != null)
                        {
                            decision = askUser;
                            stopReason = RecursionStopReason.NeedsDecision;
                        }
                        else
                        {
                            stopReason = enqueueStopReason;
                        }

                        break;
                    }
                }

                if (stopReason == RecursionStopReason.None)
                {
                    // 队列空了 = 没有更多可展开的内层归档。
                    stopReason = RecursionStopReason.Completed;
                }

                bool publish = stopReason == RecursionStopReason.Completed;

                return FinalizeRun(
                    BuildResult(
                        stopReason,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        // "源文件已变化"那句话由调用方给（快照与状态都在协调器那边），原样带进结论。
                        sourceChangedReason,
                        publish,
                        unexpandedNames,
                        passwordFallbackNote: _passwordFallbackNote),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                /*
                 * 取消不是失败（AGENTS.md §6 第 6 条）：保留工作区，让用户能看到已经解出来的部分，
                 * 也能从那里接着处理。绝不在这里抛异常给上层。
                 */
                return FinalizeRun(
                    BuildResult(
                        RecursionStopReason.UserCancelled,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        string.Empty,
                        published: false,
                        unexpandedNames),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // 兜底：一个包炸了不能把整批任务带下水（AGENTS.md §6 第 9 条）。
                return FinalizeRun(
                    BuildResult(
                        RecursionStopReason.EngineFailed,
                        layers,
                        decision,
                        workspace,
                        finalOutputDirectory,
                        PasswordMasker.Sanitize(ex.Message),
                        published: false,
                        unexpandedNames,
                        failureException: ex),
                    workspace,
                    task.FileName,
                    cancellationToken);
            }
        }

        /// <summary>
        /// 解一层：逐个密码候选试，直到成功或确定停因。
        /// </summary>
        /// <param name="progress">
        /// 进度接收端（可空）：原样挂到本层每一次引擎调用上。
        /// ⚠ 一次"解一层"可能真的跑好几遍引擎（每个密码候选一次），进度因此会**从 0 重新开始** ——
        /// 这是如实反映（上一遍确实白跑了），上层按"进度回退 = 新的一轮"处理（见 TaskProgressSink）。
        /// </param>
        /// <summary>
        /// 把**这一层在工作区里的产物目录**清空并重建（换密码候选之前调用）。
        ///
        /// <para>为什么必须清：7z 用错密码时会先建出 0 字节的桩文件再报错，而本层的提取参数是
        /// `SkipExisting`（`-aos`）—— 下一次尝试会被这些桩文件跳过，正确的密码就永远解不出东西
        /// （用户 2026-09-25 真机日志里"偶数候选产物为空、奇数候选密码错误"的奇偶交替就是这个）。</para>
        ///
        /// <para>安全边界：只动传进来的这个目录（它是 <see cref="ExtractionWorkspace"/> 造的层产物目录）；
        /// 清不掉（被占用 / 权限 / 路径过长）**不抛异常** —— 清不动最多让这一次尝试按旧垃圾判废，
        /// 由结果校验兜底，不该因为一次清理失败就把整层判死。</para>
        /// </summary>
        internal static void TryResetLayerOutputDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                Directory.CreateDirectory(directory);
            }
            catch
            {
                // 见上面的说明：清理失败只影响这一次尝试的质量，不影响正确性（校验那道闸门还在）。
            }
        }

    }
}