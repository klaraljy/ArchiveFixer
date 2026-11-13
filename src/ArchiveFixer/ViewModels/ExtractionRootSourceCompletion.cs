using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>链结束后的根源包补搬/处理（从 ExtractionCoordinator.Source.cs 搬出）。</summary>
    internal sealed class ExtractionRootSourceCompletion
    {
        internal readonly ExtractionCoordinator _host;

        internal ExtractionRootSourceCompletion(ExtractionCoordinator host)
        {
            _host = host;
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
            await _host.FinalizeDeferredVolumeDeficitsAsync(cancellationToken).ConfigureAwait(false);

            /*
             * 链尾再扫一遍已验证组里的跟班片：批末结算（跟班/接手/消费方终态）可能在
             * `FinalizeDeferredVolumeDeficitsAsync` 之后才全部落定（`22222222` 现场：
             * `part1869删除2` 的跟班事实在批末才写，早一步扫它会漏）。幂等：Done 的跳过。
             */
            _host.SettleVerifiedGroupFollowers();

            /*
             * 分卷组的**原包**留到链尾按源包档处理（用户 2026-11-01 口径）：
             * 过程物片在收片时已删；原包片与"只出过程物"的包壳**不许先动** —— 这里以
             * 「owner 成功 + 入口包落点里已有内容物」为事实（不是包壳自己的落点目录），
             * 再走既有 `PurgeLayerSourcePackage` 唯一出口。留下档一个字节都不动。
             */
            _host.SettleSucceededGroupSourcesAtChainEnd(runOptions);

            if (rootTasks == null || rootTasks.Count == 0)
            {
                _host.SettleVerifiedContentGroupSources(runOptions);
                return;
            }

            // 设置在进后台之前读一次：设置是用户随时可改的，别让后台线程读到半路改掉的值。
            // 本次选项优先（同上：只对这一次生效）。
            SourceHandlingMode sourceHandling = runOptions?.SourceHandling
                ?? AppSettings.ParseSourceHandling(_host.Settings.SourceHandling);

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
                /*
                 * 链尾唯一顺序由 CleanupPlanner 给出：
                 * 收过程物 → 按档处理其余物 → 过路层 → 源包最后补搬。
                 * 空间不足档的源包已在各层当场处理，规划器不会给出 ProcessUserSource。
                 */
                if (!string.IsNullOrWhiteSpace(rootTask.CurrentPath))
                {
                    _host._cleanupLedger.Add(new CleanupItem
                    {
                        Path = rootTask.CurrentPath,
                        Kind = CleanupItemKind.UserSource,
                        ChainId = rootTask.LogName,
                        OwnerTaskId = rootTask.LogName
                    });
                }

                if (!string.IsNullOrWhiteSpace(rootTask.RestDirectoryPath))
                {
                    _host._cleanupLedger.Add(new CleanupItem
                    {
                        Path = rootTask.RestDirectoryPath,
                        Kind = CleanupItemKind.RestArtifact,
                        ChainId = rootTask.LogName,
                        OwnerTaskId = rootTask.LogName
                    });
                }

                ChainCleanupPlan cleanupPlan = CleanupPlanner.BuildChainPlan(
                    CleanupPlanner.ResolveAllowedRoot(rootTask.OutputPath),
                    sourcePending: rootTask.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd);

                await CleanupExecutor.ExecuteAsync(
                    cleanupPlan,
                    async (step, stepCancellation) =>
                    {
                        switch (step)
                        {
                            case ChainCleanupStep.CollectProcessArtifacts:
                                await _host.CollectChainInnerPackagesIntoRestAsync(
                                    rootTask, chainTasks, stepCancellation, cleanupPlan.AllowedRoot).ConfigureAwait(false);
                                break;

                            case ChainCleanupStep.ApplyRestHandling:
                                _host.ReportLeftBehindInnerPackages(rootTask, chainTasks);
                                await _host.ApplyRestHandlingAfterChainAsync(
                                    rootTask, chainTasks, stepCancellation).ConfigureAwait(false);
                                break;

                            case ChainCleanupStep.SweepPassThrough:
                                await _host.SweepPassThroughRestDirectoriesAsync(
                                    rootTask, stepCancellation).ConfigureAwait(false);
                                break;

                            case ChainCleanupStep.ProcessUserSource:
                                {
                                    DeferredSourceMoveWork work = await Task.Run(
                                            () => _host.RunDeferredSourceMoveWorkAsync(
                                                rootTask,
                                                chainTasks,
                                                sourceHandling,
                                                stepCancellation),
                                            stepCancellation)
                                        .ConfigureAwait(false);

                                    foreach ((string level, string message) in work.LogEntries)
                                    {
                                        _host.AppendLog(level, message);
                                    }

                                    if (!string.IsNullOrWhiteSpace(work.Failure))
                                    {
                                        rootTask.Status = StatusText.PartiallyCompleted;
                                        rootTask.ErrorMessage = work.Failure;

                                        rootTask.VerifyMessage = string.IsNullOrWhiteSpace(rootTask.VerifyMessage)
                                            ? work.Failure
                                            : $"{rootTask.VerifyMessage}；{work.Failure}";
                                    }

                                    break;
                                }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

            }

            /*
             * 组级最后一道：只有组最终内容 Verified 才处理组内所有贡献源根。
             * 旧的逐 rootTask 循环可能只认入口那一单；这里按组补齐，组未证则一个字节不动。
             */
            _host.SettleVerifiedContentGroupSources(runOptions);
        }
    }
}

