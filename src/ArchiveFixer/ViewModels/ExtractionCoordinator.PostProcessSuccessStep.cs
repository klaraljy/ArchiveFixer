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
    /// <summary>成功后的收尾（源包处理 / 其余物 / 发布结果；第 13 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
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

    }
}