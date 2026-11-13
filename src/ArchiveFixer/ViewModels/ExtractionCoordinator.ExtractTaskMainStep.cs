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
    /// <summary>任务解压主流程（调度执行/单任务处理/单任务解压主体；第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 跑一个任务：解压 → （空间不足模式下当场删源包）→ 其余物按档处理 → 释放预留 + 刷新可用空间。
        ///
        /// <para>⚠ 顺序是刻意的：删除发生在**任务收尾之后、释放预留之前**。
        /// 这样"删源包回收回来的空间"会出现在下一个任务的空间门判断里（这正是那个模式能解决空间不够的原因），
        /// 而账本上的预留仍然按"这些字节还在盘上"来算，绝不会提前把它许给别的任务。</para>
        /// </summary>
        // 任务生命周期（RunScheduledTaskAsync / ProcessExtractTaskAsync）已搬到 ExtractionTaskRunner。

        internal async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken, bool oneClickRun)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            DiagnosticLog.WriteTaskSnapshot(_vm.Logger, "task.begin", task);

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
             * 真正的 APK 不碰（判据与落点见 TrySkipAndroidPackage 的注释：内容证据、唯一出口）。
             */
            if (TrySkipAndroidPackage(task))
            {
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
            /*
             * 同一组的后续卷：入口已经定下来 ⇒ 这一单不再检查源文件、不再试密码、不再解压。
             * 它要报的是“随整组解开”，不是“源文件不见了”。
             */
            if (task.IsVolumeGroupFollower)
            {
                return;
            }

            EnsureSourceSnapshot(task);

            if (StopIfSourceChanged(task))
            {
                return;
            }

            /*
             * ===== 分卷开工前检查：已停用（用户 2026-10-24 拍板）=====
             * 原来这里对第二大步入口单调 `TryStopVolumeStepBeforeStartAsync`（缺卷不开工 / 现读盘 / 延期账）。
             * 第一大步不再碰分卷；实现仍在 `SecondStepResidue/ExtractionCoordinator.VolumeStepPreflightStep.cs`。
             */

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
                    /*
                     * 第二大步已把这一组按规范名链接好时，交给引擎的入口是 `.zip`/`7z.001`
                     * 那个**链接名**，不是任务账上的入口片原路径（真机 960 的 8888：owner 是
                     * 内容卷号 3 的片，直接拿它去探测必然 Headers Error）。这里必须先认 override，
                     * 否则“未知格式先探一次”会在链接生效前把整组判死。
                     */
                    string unknownProbePath =
                        !string.IsNullOrWhiteSpace(task.EngineEntryPathOverride)
                        && File.Exists(task.EngineEntryPathOverride)
                            ? task.EngineEntryPathOverride
                            : task.CurrentPath;

                    ArchiveListResult probe = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(unknownProbePath, string.Empty),
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
                         * 第一大步只做一件事：**7-Zip 打不开 ⇒ 这一单按「跳过」收场**（终态收口的理由见下）。
                         * "它是不是某个分卷组的一片"是**第二大步**的判定 —— 登记与批末升级都在分卷侧
                         * （`RegisterUnrecognizedSuspect` / `UpgradeUnrecognizedMaybePieceVerdicts`），
                         * 判据 = 同族第 1 卷 + 基名相同或互为前缀 + 体积 ≤ 满片。
                         */
                        RegisterUnrecognizedSuspect(task);

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
                        task.EndTime = ArchiveFixer.Helpers.Clock.Now;
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

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

            task.StartTime = ArchiveFixer.Helpers.Clock.Now;
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
            task.OutputManifestCrossChecked = false;
            task.OutputOnlyZeroByteEntriesMissing = false;
            task.Outcome = TaskOutcome.Pending;
            task.VerifyMessage = string.Empty;

            // 上一轮"定稿搬运失败"那个事实位也要清掉（批末诊断读它，留着会把这一轮指错方向）。
            task.CommitMoveFailed = false;

            // 同理：上一轮"其余物为什么留着"那句话也要清掉（这一轮它会重新写）。
            task.RestKeptReason = string.Empty;

            // 同理：上一轮"成品目录里还留着的内层包"清单也要清掉（这一轮链尾会重新清点）。
            task.LeftBehindInnerPackageNames.Clear();

            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

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

            task.RunOptionsNote = runOptionsNote;

            ExtractionRunState runState = await new ExtractionRunPreparation()
                .PrepareAsync(this, task, extractOptions, cancellationToken)
                .ConfigureAwait(true);

            if (!runState.Prepared)
            {
                return;
            }

            string outputPath = runState.OutputPath;
            string outputRedirectNote = runState.OutputRedirectNote;
            bool sharedOutputRoot = runState.SharedOutputRoot;
            string engineOutputPath = runState.EngineOutputPath;
            string engineArchivePath = runState.EngineArchivePath;

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
            engineArchivePath = ResolveEngineArchivePath(task, engineArchivePath);

            /*
             * ⚠ 2026-10-16（困难批 `21212121`，运行期实测）：**交给引擎的那一份，名字得是它自己的规范名**。
             *
             * <para>位置说明（⚠ 用词：这一档**不是**用户可选的档位 —— 那三个"递归模式"档位
             * 用户 2026-10-14 已拍板删除，产品只有完全递归。这里是 <c>ExtractSingleTaskAsync</c>：
             * **把一份包交给引擎解**那一段；第一大步解完接着走递归核心（下面那两处
             * `IsSecondStepTask(task)` 闸门），而"第二大步"（分卷组只解一层）是**任务级事实位**
             * `_volumeStepTasks` 让它只解这一层。）</para>
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

                if (IsSecondStepTask(task))
                {
                    /*
                     * 第二大步（分卷组）只解一层：直读是**第一大步**的动作，这里不做。
                     * 这一行如实说明为什么没走直读，免得用户看到"直读"两个字却发现空间没省下来。
                     */
                    AppendLog(
                        "INFO",
                        $"{task.LogName}：第二大步只解这一层，不在这里做 ZIP 直读；"
                        + $"仍按偏移取出内嵌归档（需要约 {carveBytes / 1024 / 1024} MB 临时空间）。");
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
                    directZip = await Task.Run(
                        () => EmbeddedZipStreamExtractor.Probe(
                            task.CurrentPath,
                            task.EmbeddedArchiveOffset,
                            task.EmbeddedArchiveEnd,
                            engineOutputPath,
                            candidatePasswords,
                            cancellationToken),
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
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

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
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

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
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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
                        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

                        AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
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

            ExtractionRunState preflightState = new()
            {
                PreflightList = preflightList,
                DirectZip = directZip,
                LastListErrorType = lastListErrorType,
                LastListMessage = lastListMessage,
                EncryptedHeadersMessage = encryptedHeadersMessage,
                EngineArchivePath = engineArchivePath,
                EngineOutputPath = engineOutputPath,
                OutputPath = outputPath,
                Candidates = candidates
            };

            if (!await new ExtractionPreflight()
                    .RunAsync(this, preflightState, task, cancellationToken)
                    .ConfigureAwait(true))
            {
                return;
            }

            preflightList = preflightState.PreflightList;
            engineArchivePath = preflightState.EngineArchivePath;
            encryptedHeadersMessage = preflightState.EncryptedHeadersMessage;

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
             */
            if (StopIfSourceChanged(task))
            {
                return;
            }

            if (!IsSecondStepTask(task))
            {
                await new ExtractionRecursiveStep()
                    .RunAsync(this, task, engineArchivePath, engineOutputPath, outputRedirectNote, sharedOutputRoot, oneClickRun, cancellationToken)
                    .ConfigureAwait(true);

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
            ProgressHeartbeatStep.Sink progressSink = _progressHeartbeat.CreateSink(task);
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

            ExtractionRunState firstAttemptState = new()
            {
                LastResult = lastResult,
                SelectedPassword = selectedPassword,
                PasswordConfirmed = passwordConfirmed
            };

            if (!await new ExtractionFirstAttempt()
                    .RunAsync(this, firstAttemptState, task, directZip, preflightList, candidatePasswords, progressSink,
                        shouldTestPasswordBeforeExtract, maxPasswordAttempts, candidates, candidatesTruncated, attemptLimit,
                        engineArchivePath, engineOutputPath, outputRedirectNote, sharedOutputRoot, extractOptions, oneClickRun,
                        cancellationToken)
                    .ConfigureAwait(true))
            {
                passwordConclusionNote = await new ExtractionNormalMode()
                    .RunAsync(this, task, maxPasswordAttempts, attemptLimit, candidatesTruncated, candidates,
                        preflightList, preflightSaidEncryptedHeaders, encryptedHeadersMessage, engineArchivePath,
                        engineOutputPath, outputRedirectNote, sharedOutputRoot, lastResult, selectedPassword,
                        passwordConfirmed, ambiguousTestResult, passwordConclusionNote, lastListErrorType,
                        extractOptions, progressSink, oneClickRun, cancellationToken)
                    .ConfigureAwait(true);
            }

            await new ExtractionNormalMode()
                .FinishAsync(this, task, preflightSaidEncryptedHeaders, encryptedHeadersMessage, passwordConclusionNote)
                .ConfigureAwait(true);
        }

        /// <summary>
        /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
        /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
        /// </summary>
        /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>
        /// <summary>
        /// ⛔ 真正的 APK 不碰：不解压、不递归、不改名（用户 2026-10-10 拍板）。判据 = 内容证据，
        /// 唯一出口 <c>AndroidPackageDetector</c>；判不出 ⇒ 照旧照常处理。
        /// </summary>
        /// <returns>true = 这一单到此为止（状态与终态已经写好）。</returns>
        internal bool TrySkipKnownContent(
            ArchiveTask task,
            bool oneClickRun,
            ArchiveFixer.Detection.FileNatureCard card)
        {
            if (!oneClickRun
                || string.IsNullOrWhiteSpace(task.CurrentPath)
                || !File.Exists(task.CurrentPath))
            {
                return false;
            }

            /*
             * 媒体前缀的完整归档 / 已证明的归档：头部像媒体也不能按内容物跳过。
             * 这里用 entryCard 已经取到的底层事实，避免对媒体前缀再猜一次。
             */
            if (card.ContentClass == ArchiveFixer.Detection.FileContentClass.Archive
                || card.EmbeddedOffset > 0
                || card.BodyKind is ArchiveFixer.Detection.ArchiveBodyKind.VolumeFirstPiece
                    or ArchiveFixer.Detection.ArchiveBodyKind.VolumePiece)
            {
                return false;
            }

            /*
             * 用户明确导入的内容容器（docx/epub/jar/apk…）保留既有“源包手动只解压”语义：
             * 它是用户点名要处理的文件，不按“自动扫到的内容物”跳过。
             * 自动递归/产物里的同类文件仍由递归闸门与 OneClick 产物扫描挡住。
             */
            if (!task.IsContinuationTask
                && ArchiveFixer.Security.ContentContainerProtection.ShouldProtect(task.CurrentPath))
            {
                return false;
            }

            ArchiveFixer.Detection.FileContentProbeResult content =
                ArchiveFixer.Detection.ContentAdmission.Probe(
                    task.CurrentPath,
                    ArchiveFixer.Detection.ContentAdmissionScope.UserSource,
                    deep: true);

            if (content.Class != ArchiveFixer.Detection.FileContentClass.KnownContent)
            {
                return false;
            }

            /*
             * APK 走已有的专用闸门（它有自己的文案/终态/守门用例），
             * 这里不要用“普通非压缩文件”把它抢先收掉。
             */
            if (ArchiveFixer.Detection.AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
            {
                return false;
            }

            task.IsArchive = false;
            task.Status = StatusText.Skipped;
            task.Operation = StatusText.OpSkip;
            task.ProgressText = StatusText.ProgressCompleted;
            task.ErrorMessage = $"这是非压缩文件（{content.Format}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。";
            task.Outcome = TaskOutcome.Skipped;
            task.EndTime ??= ArchiveFixer.Helpers.Clock.Now;

            AppendLog(
                "INFO",
                $"{task.LogName}：识别为非压缩文件（{content.Format}；{content.Evidence}）"
                + " —— 自动处理不碰，源文件原地不动。要拆请手动选中后用「只解压」。");

            return true;
        }

        private bool TrySkipAndroidPackage(ArchiveTask task)
        {
            if (!AndroidPackageDetector.IsAndroidPackage(task.CurrentPath))
            {
                return false;
            }

            task.IsArchive = false;
            task.Status = StatusText.Skipped;
            task.Operation = StatusText.OpSkip;
            task.ProgressText = StatusText.ProgressCompleted;
            task.ErrorMessage = StatusText.AndroidPackageSkipped;

            // 机器终态显式落（与"未识别 ⇒ 跳过"那条同一个口径：只补 EndTime 会让终态收口按失败接手）。
            task.Outcome = TaskOutcome.Skipped;
            task.EndTime ??= ArchiveFixer.Helpers.Clock.Now;

            AppendLog(
                "WARN",
                $"{task.LogName}：这是 Android 应用安装包（ZIP 里同时有 {AndroidPackageDetector.ManifestEntryName} 与 "
                + $"{AndroidPackageDetector.DexEntryName}）—— 按设置不解压、不递归、不改名，源文件一个字节都不动。"
                + "要处理它请用专门的 APK 工具。");

            return true;
        }

        /// <summary>
        /// 交给引擎的那一份归档路径：先修"坏名字的第一卷"（只改我们自己产出的那一卷），
        /// 再优先用盘上同一份的规范名（判据全在读事实，唯一出口 <c>RecursiveExtractor.PreferCanonicalSiblingName</c>）。
        /// ⛔ 只影响交给引擎的那一份：task.CurrentPath 与源包那一档一个字都不动。
        /// </summary>
        private string ResolveEngineArchivePath(ArchiveTask task, string engineArchivePath)
        {
            /*
             * 第二大步给了规范名入口（`X.7z.001` 这类硬链接别名）⇒ 引擎就用它。
             * ⛔ 任务账/源包/删除判据仍读 task.CurrentPath；这里只换“交给引擎的那一份”。
             */
            if (!string.IsNullOrWhiteSpace(task.EngineEntryPathOverride)
                && File.Exists(task.EngineEntryPathOverride))
            {
                return task.EngineEntryPathOverride;
            }

            string resolved = TryRepairBrokenVolumeChainName(task, engineArchivePath);

            return RecursiveExtractor.PreferCanonicalSiblingName(resolved);
        }

        /// <summary>
        /// 把预检的"文件名已加密"结论恢复回来：条件收得最紧 —— 预检说过、而且这一层最后得到的**只是**"密码没通过"。
        /// </summary>
        internal void RestoreEncryptedHeadersConclusion(
            ArchiveTask task,
            bool preflightSaidEncryptedHeaders,
            string? encryptedHeadersMessage)
        {
            if (!preflightSaidEncryptedHeaders ||
                (task.Status != StatusText.WrongPassword && task.Status != StatusText.ExtractFailed))
            {
                return;
            }

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

        /// <summary>
        /// 阶段一：入仓（契约 §2.1）。从这一刻起本任务所有中间动作都在私有暂存目录里完成，
        /// 源目录与最终目录一个字节都不会被写。返回 false = 这一单停下（原因已经写进任务与日志）。
        /// </summary>
        internal async Task<(bool Ok, string StageDirectory, string EngineOutputPath)> TryPrepareStagingAsync(
            ArchiveTask task,
            string outputPath,
            CancellationToken cancellationToken)
        {
            string stageDirectory = string.Empty;
            string engineOutputPath = string.Empty;
            stageDirectory = string.Empty;
            engineOutputPath = string.Empty;

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
            stageDirectory = _pathService.BuildTaskStageDirectory(task);

            if (string.IsNullOrWhiteSpace(stageDirectory))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = "无法确定暂存目录（工作区不可用）";
                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage}", DiagnosticCodes.TaskFailed, "ExtractionCoordinator.ExtractTask");
                return (false, string.Empty, string.Empty);
            }

            // 记下"这个任务真正用的工作区目录"：收尾时的空壳清理要按它来，
            // **不能事后重算**（taskId 含源路径哈希，而成功路径会回写 CurrentPath，见字段说明）。
            TaskWorkspaceDirectories[task] = Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            string? stageFailure = await PrepareStageDirectoryAsync(task, stageDirectory, cancellationToken);

            if (!string.IsNullOrWhiteSpace(stageFailure))
            {
                task.Status = StatusText.ExtractFailed;
                task.ProgressText = StatusText.ProgressFailed;
                task.ErrorMessage = stageFailure;
                AppendLog("ERROR", $"{task.LogName}：{stageFailure}");
                return (false, string.Empty, string.Empty);
            }

            // 引擎写盘的地方 = 暂存目录。**不是** outputPath —— 那是定稿的目标。
            engineOutputPath = stageDirectory;

            AppendLog("INFO", $"{task.LogName}：入仓目录 {stageDirectory}（中间产物先落这里，定稿时才搬进 {outputPath}）");

            return (true, stageDirectory, engineOutputPath);
        }

        /// <summary>
        /// 并发下的"最终目录"占位：只对"目录还不存在、且是每包一个目录的落点"占位（只占不建），
        /// 占不到就按同一套 `名字(1)` 约定换一个；收尾时由 ProcessExtractTaskAsync 释放。
        /// 返回占位后的输出目录 + 可能产生的改名说明。
        /// </summary>
        internal (string OutputPath, string RedirectNote) ClaimFinalOutputDirectoryIfNeeded(
            ArchiveTask task,
            string outputPath,
            bool landsInSourceDirectory,
            bool sharedOutputRoot,
            string outputRedirectNote)
        {
            if (task.IsContinuationTask ||
                landsInSourceDirectory ||
                sharedOutputRoot ||
                string.IsNullOrWhiteSpace(outputPath) ||
                Directory.Exists(outputPath))
            {
                return (outputPath, outputRedirectNote);
            }

            return (ClaimOutputDirectory(outputPath, task, ref outputRedirectNote), outputRedirectNote);
        }

        /// <summary>
        /// 登记"失败时盘上还剩什么"（部分完成发布的输入）：先按唯一出口 `ProducedContentGate` 数一次
        /// 真解出来的非空文件（终态分类的判据输入），再交候选表，最后把引擎原话落一次日志。
        /// </summary>
        internal void RecordPartialPublishFacts(
            ArchiveTask task,
            string engineOutputPath,
            string engineArchivePath,
            string selectedPassword,
            IReadOnlyList<(string Path, long Size)>? lastLayerManifestEntries,
            ArchiveOperationResult? lastResult)
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
            EngineOutputLog.LogFailure(AppendLogPair, task.FileName, lastResult);
        }

        /// <summary>本次任务执行器要读的账本（与空间门同一个对象，⛔ 不复制）。</summary>
        internal SpaceReservationLedger? SpaceLedger => _spaceLedger;

        /// <summary>准备/入仓段要读的路径服务与试开暂存账（与协调器同一个对象，⛔ 不复制）。</summary>
        internal PathService PathService => _pathService;

        internal Dictionary<ArchiveTask, string> StagedGroupOutputPaths => _stagedGroupOutputPaths;

        internal PasswordService PasswordService => _passwordService;

        internal MainViewModel Vm => _vm;

        internal ProgressHeartbeatStep ProgressHeartbeat => _progressHeartbeat;
    }
}

