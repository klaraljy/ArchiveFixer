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
                recursion,
                stageFilesAreProducedContent: true);

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
                 * 用户 2026-10-26 口径：**并不是所有一键处理都延期**。
                 * 独立任务/整条链已经完整成功 ⇒ 当场删自己的源包；
                 * 只有链没跑完（递归未 Completed）、分卷组未完整、或还有未完成卷组时才延期。
                 * 这样一批里某个 300MB 失败，不会拖着 9G/5G/3G 那些已经成功任务的源包。
                 */
                if (oneClickRun && !CanDeleteRootSourceImmediately(task, recursion))
                {
                    task.SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd;

                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：源包先留在原地 —— 这一单所在的链还没跑完，"
                        + "等内容物完整确认后才按「源包操作 = 彻底删除」处理"
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
            bool ignoreDoneState = false,
            bool groupComplete = false)
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
                    && reclaimVerdict.AllowsSourceRemoval)
                || (groupComplete
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
                enabled: true,
                includeRestAliases: _vm.SpaceTightMode);

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

            foreach (string deletedPath in cleanup.DeletedFiles)
            {
                DiagnosticLog.WriteFileOperation(
                    _vm.Logger,
                    "delete",
                    deletedPath,
                    target: null,
                    result: "deleted",
                    detail: $"trigger={trigger}; subject=源包; fate=永久删除");
            }

            foreach (string failedPath in cleanup.FailedFiles)
            {
                DiagnosticLog.WriteFileOperation(
                    _vm.Logger,
                    "delete",
                    failedPath,
                    target: null,
                    result: "failed",
                    detail: $"trigger={trigger}; subject=源包; message={cleanup.Message}");
            }

            // 只删文件会把“其余物”留成空壳；这里只收拾已经被清空的那一层。
            foreach (string deletedPath in cleanup.DeletedFiles)
            {
                RemoveEmptyRestShell(deletedPath, logEntries);
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
        /// 删掉一个文件之后，如果它所在的“其余物”已经被彻底清空，就连空壳一起删。
        /// ⛔ 只删名字逐字是“其余物”的那一层；非空一律不动，不往上删父目录。
        /// </summary>
        private static void RemoveEmptyRestShell(
            string deletedPath,
            List<(string Level, string Message)> logEntries)
        {
            try
            {
                string? directory = Path.GetDirectoryName(deletedPath);

                for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(directory); depth++)
                {
                    if (string.Equals(
                            Path.GetFileName(directory),
                            ProcessArtifactLayout.ArtifactDirectoryName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(directory) &&
                            !Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory, recursive: false);
                            logEntries.Add((
                                "INFO",
                                $"其余物已清空，删除空壳目录：{Path.GetFileName(directory)}"
                                + $"（{directory}）"));
                        }

                        break;
                    }

                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch
            {
                // 空壳收拾失败不影响已完成的删除结论；下次运行还会再收拾。
            }
        }

        /// <summary>
        /// ⛔ 只读事实位，不按名字或中文猜。
        /// </summary>
        private static bool CanDeleteRootSourceImmediately(ArchiveTask task, RecursionResult? recursion)
        {
            if (task == null)
            {
                return false;
            }

            if (task.HasUnresolvedVolumeGroup)
            {
                return false;
            }

            if (task.IsVolumeGroup && !task.IsVolumeComplete)
            {
                return false;
            }

            if (recursion != null &&
                (!recursion.Completed ||
                 recursion.PartiallyCompleted ||
                 recursion.UnexpandedNames.Count > 0))
            {
                return false;
            }

            /*
             * 一键处理的“链”可能不是递归核心里展开的：第一层只把 inner.7z.001 当内容物落出来，
             * 再交给下一轮续解。源码层看不出“后面还有没有一层”，所以这里看盘上事实：
             * 产物树里还有归档/分卷候选 ⇒ 这一单还没到最终内容物，源包必须留着。
             */
            return !OutputLooksLikeIntermediateArchive(task.OutputPath);
        }

        private static bool OutputLooksLikeIntermediateArchive(string? outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                return true;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             outputDirectory,
                             "*",
                             SearchOption.AllDirectories).Take(2000))
                {
                    string name = Path.GetFileName(file);

                    if (FileNameHelper.IsVolumePartFileName(name))
                    {
                        return true;
                    }

                    string extension = Path.GetExtension(name);

                    if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".bz2", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".xz", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 读不出来就无法证明是最终内容物 ⇒ 保留源包（兜底方向是“不删”）。
                return true;
            }

            return false;
        }

        /// <summary>
        /// 这一份源包的内容证据是否**可证不是分卷成员**（只有这一档才让“半套分卷”闸门不适用）。
        /// 文件读不出 / 没有内容证据 ⇒ false（照旧过闸门，⛔ 不因判不出而放宽）。
        /// </summary>
        private static bool IsProvablyNotVolumeMemberSource(ArchiveTask task)
        {
            if (task == null)
            {
                return false;
            }

            foreach (string? path in new[] { task.CurrentPath, task.OriginalPath })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                try
                {
                    return !FileNature.Inspect(path).ParticipatesInVolumeHandling;
                }
                catch
                {
                    return false;
                }
            }

            return false;
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
            /*
             * 内容卡已经**可证不是分卷成员**（SingleComplete / ProvablyNotVolumeMember）⇒ 它不可能是
             * 半套分卷的一半，这道闸门根本不适用。真机 960 批 `27272727`：源包是一份完整 ZIP，
             * 成品目录里恰好有同基名的内层分卷片 `aaa-L2.中文z02`，旧判据只按基名+族就把它拦下，
             * 导致 DeleteInPlace 该删的源包没删。判据只读内容卡，⛔ 不按名字猜。
             */
            if (IsProvablyNotVolumeMemberSource(task))
            {
                return null;
            }

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
             * 这一次允许看的唯一一棵树（口径见 ResolveSplitGateScanBoundary）：
             * · 选文件夹 = 选中的文件夹；
             * · 选单个压缩包 = 它旁边/指定位置里那个以包基名命名的成品文件夹；
             * · 选单个文件且任务已算出 OutputPath = 那个实际成品文件夹。
             */
            string batchRoot = ResolveSplitGateScanBoundary(task);

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

                /*
                 * ⛔ 盘根永远不是"成品目录树"：候选不在这一批目标根里面时，上溯 3 层可能正好走到
                 * H:\ / D:\。把盘根交给下面的递归枚举 = 整盘扫一遍（真机 2026-10-26：H:\ 上
                 * System Volume Information 读不动，UI 线程卡到 Windows 判定 AppHang）。
                 * 判不出就什么都不做，绝不拿"扫不动"去换一次不可逆删除。
                 */
                string? driveRoot = Path.GetPathRoot(full);

                if (!string.IsNullOrWhiteSpace(driveRoot)
                    && string.Equals(
                        full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                /*
                 * 边界外的任何地方都不进扫描清单 —— 用户的源包/别的目录没有资格被扫。
                 */
                if (batchRoot.Length > 0
                    && !ArchivePathGuard.IsInsideRoot(batchRoot, full, out _))
                {
                    return;
                }

                if (full.Length > 0 && seen.Add(full))
                {
                    roots.Add(full);
                }
            }

            /*
             * 上溯的**上界 = 这一次真正允许看的那棵树**：
             * · 「未指定位置」+ 选文件夹 = 用户选中的文件夹；
             * · 「未指定位置」+ 选单个压缩包 = 这个包基名的成品文件夹；
             * · 「指定位置」+ 选单个压缩包 = 指定位置里这个包基名的成品文件夹；
             * · 「指定位置」+ 选文件夹 = 用户指定的成品根。
             * ⛔ 不再无条件拿 Settings.CustomOutputDirectory 当边界 —— 那一格在「未指定位置」时可能
             * 是上一次留下的旧地址（真机 2026-10-26：旧值是 `H:\BaiduNetdiskDownload\大测试-...`，
             * 而这一批实际在 `H:\BaiduNetdiskDownload\666` ⇒ 候选被判成"在边界外" ⇒ 上溯到 H:\ ⇒ 整盘扫）。
             */

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
             * 改法：候选**落在这一批的边界里面**时，从它自己那一层一路上溯，**直到边界为止**（含），
             * 不再有层数上限。⛔ 候选**不在边界里面**时，只允许看它自己所在的那一层 ——
             * 绝不顺着父目录爬到用户的上一层/盘根（2026-10-26 用户逐字：扫描范围只限于选中的文件夹
             * 或指定的成品位置，其他地方没有资格动手）。
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

                    /*
                     * 边界外的候选（例如单文件模式下的原包自己）不扫它的父目录：
                     * 用户明确说"外面只有原包，不用管其他地方"。只扫边界内那一棵成品树。
                     */
                    if (batchRoot.Length > 0 && !insideBatchRoot)
                    {
                        break;
                    }

                    Add(current);

                    if (batchRoot.Length > 0 &&
                        string.Equals(current, batchRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        break;   // 到允许看的边界了，再往上不是本次的地盘
                    }

                    /*
                     * 边界外 / 没有边界：只保留候选自己那一层，绝不上溯。
                     */
                    if (!insideBatchRoot || batchRoot.Length == 0)
                    {
                        break;
                    }

                    string? parent = Path.GetDirectoryName(current);

                    if (string.IsNullOrWhiteSpace(parent) ||
                        string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;   // 到盘根了（绝对兜底）
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
        /// 「半套分卷」闸门允许看的**唯一一棵树**。
        ///
        /// <para>用户 2026-10-26 口径：
        /// · 选文件夹 = 这个文件夹里面；
        /// · 选单个压缩包 = 它旁边/指定位置里那个**以包基名命名的成品文件夹**；
        /// · 指定位置 = 只在这个成品位置里面，绝不看源包外面的其他目录。</para>
        /// ⛔ 不读「未指定位置」时那份可能已经过期的 <see cref="AppSettings.CustomOutputDirectory"/>。
        /// </summary>
        private string ResolveSplitGateScanBoundary(ArchiveTask task)
            => ResolveSplitGateScanBoundary(task, Settings);

        /// <summary>上面那个边界的**纯函数版**（单测直接调，不 new 协调器）。</summary>
        internal static string ResolveSplitGateScanBoundary(ArchiveTask task, AppSettings? settings)
        {
            string Normalize(string? path) => SafePathHelper.GetFullPathSafe(path ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string FirstExistingOutput()
            {
                foreach (string? candidate in new[] { task.OutputPath, task.ContentDirectoryPath, task.CollectedPath })
                {
                    string normalized = Normalize(candidate);

                    if (normalized.Length > 0 && Directory.Exists(normalized))
                    {
                        return normalized;
                    }
                }

                return string.Empty;
            }

            string baseName = task.CurrentPath.Length > 0
                ? OutputPlacement.ResolveArchiveBaseName(task.CurrentPath)
                : string.Empty;

            bool fileSelection = task.SourceSelectionKind != SourceSelectionKind.Folder;
            bool customPlacement = settings != null && !settings.ExtractToOriginalDirectory;

            if (customPlacement)
            {
                string customRoot = Normalize(settings?.CustomOutputDirectory);

                /*
                 * 指定位置 + 选单个压缩包：只在「指定位置\<包基名>\」里扫
                 * （用户例子：指定位置\111\，绝不扫指定位置的上一层/别的包）。
                 */
                if (fileSelection)
                {
                    string actualOutput = FirstExistingOutput();

                    if (actualOutput.Length > 0)
                    {
                        return actualOutput;
                    }

                    if (customRoot.Length > 0 && baseName.Length > 0)
                    {
                        return Normalize(Path.Combine(customRoot, baseName));
                    }
                }

                if (customRoot.Length > 0)
                {
                    return customRoot;
                }

                string actualRoot = Normalize(task.OutputPath);

                if (actualRoot.Length > 0)
                {
                    return actualRoot;
                }
            }

            /*
             * 「未指定位置」+ 选文件夹：这个文件夹就是边界（每个包各自的成品文件夹都在它下面）。
             */
            if (task.SourceSelectionKind == SourceSelectionKind.Folder
                && !string.IsNullOrWhiteSpace(task.SourceSelectionRoot))
            {
                string selectedRoot = Normalize(task.SourceSelectionRoot);

                if (selectedRoot.Length > 0 && Directory.Exists(selectedRoot))
                {
                    return selectedRoot;
                }
            }

            /*
             * 「未指定位置」+ 选单个压缩包：H:\111.rar → H:\111\，只扫这个成品文件夹。
             * 源包自己就在外面，用户明确说"外面只有原包，不用管其他"。
             */
            string actualFileOutput = FirstExistingOutput();

            if (actualFileOutput.Length > 0)
            {
                return actualFileOutput;
            }

            string sourceDirectory = Normalize(Path.GetDirectoryName(task.CurrentPath));

            return baseName.Length > 0
                ? Normalize(Path.Combine(sourceDirectory, baseName))
                : sourceDirectory;
        }

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
                    DeferredSourceMoveWork work = await Task.Run(
                            () => RunDeferredSourceMoveWorkAsync(
                                rootTask,
                                chainTasks,
                                sourceHandling,
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);

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
        /// 本批（或本单）生效的「源包怎么处理」——**唯一出口**：
        /// `RunOptions`（一键处理的「本次选项」）优先，否则读设置。
        /// </summary>
        private SourceHandlingMode EffectiveSourceHandling
            => RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);


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
        /// **链内每一层"被用掉的那几个输入文件"**（递归钩子每层报上来的 `GroupFiles`）——
        /// 只记账、不动盘（用户 2026-10-12 口径：搬不能挂在"逐层"那一刻，见
        /// <see cref="CollectChainInnerPackagesIntoRestAsync"/> 末尾那一段"链尾集中"）。
        /// 写入点唯一 = <see cref="OnRecursiveLayerReclaim"/>；清空点 = 批首 `PrepareRestHandlingForBatch`。
        /// </summary>
        private readonly List<string> _chainInnerLayerInputs = new();

    }
}
