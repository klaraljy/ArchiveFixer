using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using PostProcessWorkResult = ArchiveFixer.ViewModels.ExtractionCoordinator.PostProcessWorkResult;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 成功收尾里"碰盘那一大段"（校验 → 预算 → 定稿 → 归集 → 源包三档）—— 从
/// <see cref="ExtractionCoordinator"/> 的 <c>Source</c> partial 整体搬出的协作类。
///
/// <para>为什么单独成类：这段只编排"暂存区的产物怎么结案"，与协调器的调度/批次/任务表无关；
/// 留在 <see cref="ExtractionCoordinator"/> 里只会让那个类型继续长（结构守卫按行数记账）。
/// ⛔ 判据、终态、源包处理仍全部走协调器既有出口（<see cref="_host"/>）；需要碰的私有成员
/// 只有定稿那一处，访问器（<c>ExecuteFinalLayoutForSourceHandler</c>）登记在
/// <c>ExtractionCoordinator.Source.cs</c> 里，⛔ 不用反射、不复制判据。</para>
///
/// <para>无状态：<c>_host</c> 是唯一的持有物，所有中间结论都是方法内的局部变量 ——
/// 多个任务并发收尾时各自一个实例，也不会有共享状态。</para>
/// </summary>
internal sealed class SourceHandler
{
    private readonly ExtractionCoordinator _host;

    internal SourceHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

    /// <summary>
    /// 收尾重活本体：落点校验 → 结果校验 → 预算事后判定 → **定稿搬运** → 归集 → 处理源包。
    ///
    /// 校验与预算量的都是**暂存区**：那才是引擎真正写盘的地方（契约 §2.1）。
    /// 最终目录此时还是干净的，只有定稿那一步会往里面写东西。
    ///
    /// **只允许在后台线程上跑**（见 <see cref="ExtractionCoordinator.PostProcessSuccessAsync"/> 的线程规则）：
    /// 这里只碰文件系统，不写任务状态、不写界面日志集合，要说的都放进 LogEntries 回传。
    /// </summary>
    /// <param name="oneClickRun">
    /// 是不是「一键处理」发起的整理路径。**只影响一件事**：一键处理有续解链，
    /// 所以"本轮没有内容物"时源包搬运要记成"留到链结束后补搬"；
    /// 手动「只解压」是单层路径，没有链可等，当场按"定稿 + 校验通过"处理。
    /// </param>
    internal PostProcessWorkResult RunPostProcessWork(
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
        RecursionResult? recursion = null,
        string expectedRootPath = "")
    {
        var logEntries = new List<(string Level, string Message)>();

        cancellationToken.ThrowIfCancellationRequested();

        _host.PostProcessStartedForTests?.Invoke();

        /*
         * ⚠ 2026-09-30（用户真机报的 bug）：**不再把"取不到清单"折成 null**。
         * null 会把"为什么取不到"（<see cref="ChainManifestResolver"/> 点名的那一层与原因）
         * 一起丢掉，用户只能看到一句"未取得预期条目数"。
         * <see cref="OutputVerifier.Verify"/> 内部对 null 与 Success=false 的处理逐字相同
         * （两条规则都先判 Success），所以这一改**只多带一句话，不改任何判定**。
         */
        OutputVerificationResult verification = OutputVerifier.Verify(
            stageDirectory,
            expected,
            expectedRootPath);

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
        ResourceBudgetOptions budgetLimits = _host.BudgetLimits;

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
         * 空间不足模式：未完成内容物不发布。
         *
         * 普通模式这里可能仍会进入定稿，由后续失败分支给出结论；空间不足模式的目标是
         * “失败只留最后一层过程物”，所以必须在 ExecuteFinalLayout 之前截断，
         * 连半成品都不写进最终目录。失败由 PostProcessFailureWriter 统一落终态，源包不动。
         */
        if (_host._spaceTightPolicy.Enabled && !verification.Verified)
        {
            logEntries.Add((
                "WARN",
                $"{task.LogName}：空间不足模式 —— 内容未完成，未完成内容物不发布；"
                + "最后一层过程物保留，源包不动。"));

            return new PostProcessWorkResult
            {
                Verification = verification,
                LogEntries = logEntries
            };
        }

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
        StageCommitResult? commit = _host.ExecuteFinalLayoutForSourceHandler(
            task,
            stageDirectory,
            task.OutputPath,
            sharedOutputRoot,
            terminalLayout,
            conflictAction,
            conflictDecision,
            cancellationToken,
            verification.Verified,
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
         * 唯一的差别是"本轮没有内容物"时怎么办，见 源包移入其余物旧入口。
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

        if (_host._spaceTightPolicy.SourceOnLayerSuccessDeletes)
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
            sourceMoveFailure = _host.PurgeLayerSourcePackage(
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
            sourceMoveFailure = _host.PurgeLayerSourcePackage(
                task, verification, commit, logEntries, LayerPurgeTrigger.LayerReclaim);
        }
        else if (_host._spaceTightPolicy.Enabled && _host._spaceTightPolicy.KeepSource)
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
             * 两个分支合起来才是"删的那一档"，所以这里不再重复判一次 `_spaceTightPolicy.Enabled`。
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
            /*
             * 用户 2026-11-02 定案：**一键处理里原包永远不在单层成功后处理**。
             * 这一层看起来已经出了内容物，也可能只是后缀没露出来的内层包（真机 `7z.rar`
             * 吐出的 `1101.webp` 就是下一层 RAR）—— 后面还有没有层，这一刻无法证明。
             * ⇒ 一律记「延期到链尾」，由 `CompleteRootSourcePackagesAfterChainAsync`
             * 在整条链跑完后按最终成败处理。手动「只解压」没有链可等，仍走当场处理。
             */
            if (oneClickRun)
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
                sourceMoveFailure = _host.PurgeLayerSourcePackage(
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
}
