using ArchiveFixer.BindingModels;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一个任务解压前的**准备与入仓**：落点解析 → 最终目录占位 → 暂存目录（stage）→ 交给引擎的那一份路径。
///
/// <para>为什么单独成类：这一段只决定"产物最终放哪、中间产物先放哪、交给引擎的入口是哪一个"，
/// 与密码候选、引擎调用、定稿无关；它把结果写进 <see cref="ExtractionRunState"/> 交给后面的阶段。</para>
/// </summary>
internal sealed class ExtractionRunPreparation
{
    internal async Task<ExtractionRunState> PrepareAsync(
        ExtractionCoordinator host,
        ArchiveTask task,
        ExtractOptions extractOptions,
        CancellationToken cancellationToken)
    {
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
        OutputPlacementResult placement = host.PathService.ResolveOutputPlacement(
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
        if (host.StagedGroupOutputPaths.TryGetValue(task, out string? stagedOutputPath)
            && !string.IsNullOrWhiteSpace(stagedOutputPath))
        {
            outputPath = stagedOutputPath;
            outputRedirectNote = "试开定序暂存组入口";

            host.AppendLog(
                "INFO",
                $"{task.LogName}：产物落点回到试开之前的原落点 —— {outputPath}");
        }

        /*
         * 落点为什么在这一层（用户 2026-10-05 口径："落在**第一卷的位置**"）：
         * 这行日志属于第二大步的解释，已停用（用户 2026-10-24）—— 实现随
         * `VolumeHandoffStep.cs` 一起搬到 `SecondStepResidue/`，重建第二大步时去那儿挑。
         */

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
            host.MarkStoppedBeforeExtract(
                task,
                StatusText.ExtractFailed,
                "输出目录无效（未设置或不可用），请在设置里选择输出目录后重试");

            host.AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
            return new ExtractionRunState();
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
                host.AppendLog(
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
                host.AppendLog(
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
                host.AppendLog(
                    "INFO",
                    $"{task.LogName}：落点是本次导入共用的目标目录 {outputPath}（添加文件夹 + 指定位置），" +
                    "不套用“目录已存在就改名”的规则。");
            }
            else if (!string.IsNullOrWhiteSpace(outputPath) &&
                Directory.Exists(outputPath) &&
                host.HasProductEntriesOutsideWorkspace(outputPath))
            {
                /*
                 * 目标目录已存在且非空 = 第一次撞上"目标已存在"的冲突。
                 *
                 * ⚠ 这里以前**无条件**自动改名成 xxx(1) —— 于是设置里那一档「询问」在落点上
                 * 从来没有兑现过：用户选了询问，程序静默改名。现在一律走 ResolveConflictAsync：
                 * · 非 Ask 档：按档位直接算（默认 AutoRename = 老行为，绝不覆盖）；
                 * · Ask 档：第一次冲突时暂停该任务、聚合问一次（覆盖 / 跳过 / 自动重命名，可对整批生效）。
                 */
                ConflictResolution resolution = await host.ResolveConflictAsync(
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
                    task.EndTime = ArchiveFixer.Helpers.Clock.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";
                    task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

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

                    host.AppendLog("WARN", $"{task.LogName}：同名冲突按你的选择跳过，本任务不解压（输出目录：{outputPath}）。");
                    return new ExtractionRunState();
                }

                if (resolution.Choice == ConflictChoice.Overwrite)
                {
                    /*
                     * 「覆盖」= 沿用这个已有目录（把产物合并进去）。
                     *
                     * 目录本身不会被删：同名条目在**定稿搬运**那一步才真正相撞，
                     * 到那一刻仍然按同一个决定处理（覆盖走"先挪到临时名 → 落位 → 再删"两阶段）。
                     */
                    host.AppendLog(
                        "WARN",
                        $"{task.LogName}：输出目录已存在且非空，按你的选择「覆盖」沿用该目录：{outputPath}" +
                        "（目录里已有的同名条目会在定稿时按同一决定处理）。");

                    outputRedirectNote = $"原定输出目录 {outputPath} 已存在且非空，按你的选择覆盖进该目录";
                }
                else
                {
                    string newOutputPath = resolution.TargetPath;

                    host.AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                    /*
                     * 实际落点与"打算的落点"不一致这件事**不许静默**：
                     * ① 下面会把实际落点写回 task.OutputPath（界面"输出目录"列看得见）；
                     * ② 校验结论里也带一句（task.VerifyMessage）；
                     * ③ 日志里额外提醒续解的影响 —— 一键处理的续解是按**解压前**的目录快照找内层包的，
                     *    内层包落进新目录时它找不到，用户看到的现象就是"第二层没解"。
                     *    这里的提示是让人一眼知道该去哪找，而不是让程序假装没发生。
                     */
                    outputRedirectNote = $"原定输出目录 {requestedOutputPath} 已存在且非空，本次实际输出到 {newOutputPath}";

                    host.AppendLog("WARN", $"{task.LogName}：{outputRedirectNote}。自动续解按解压前的目录查找内层包，若内层包落在新目录里可能不会被继续解开。");

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
            host.AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
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
        (outputPath, outputRedirectNote) = host.Auxiliary.ClaimFinalOutputDirectoryIfNeeded(
            task, outputPath, landsInSourceDirectory, sharedOutputRoot, outputRedirectNote);

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
        host.AppendLog(
            "INFO",
            host.RunOptions == null
                ? $"{task.LogName}：本次实际输出目录 {outputPath}"
                : $"{task.LogName}：本次实际输出目录 {outputPath}（依据 → {host.RunOptions.Describe()}）");

        (bool stagingOk, string stageDirectory, string engineOutputPath) staging =
            await host.Auxiliary.TryPrepareStagingAsync(task, outputPath, cancellationToken);

        if (!staging.stagingOk)
        {
            return new ExtractionRunState();
        }

        string stageDirectory = staging.stageDirectory;
        string engineOutputPath = staging.engineOutputPath;

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

        return new ExtractionRunState
        {
            Prepared = true,
            RequestedOutputPath = requestedOutputPath,
            OutputPath = outputPath,
            OutputRedirectNote = outputRedirectNote,
            LandsInSourceDirectory = landsInSourceDirectory,
            SharedOutputRoot = sharedOutputRoot,
            StageDirectory = stageDirectory,
            EngineOutputPath = engineOutputPath,
            EngineArchivePath = engineArchivePath
        };
    }
}

