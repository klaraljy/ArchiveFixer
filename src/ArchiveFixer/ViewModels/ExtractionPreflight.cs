using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 单任务解压的**清单/预算/路径闸门**：容器分卷拼装 → 加密头识别 → 改名坏卷拦截 → 路径安全预检
/// → 资源预算 → 精确空间门 → 回收/长路径/危险条目提示。
///
/// <para>只读这一份 list（⛔ 不再跑 7z）；停下就是停下（不再写盘），返回 false 让调用方直接 return。
/// 跨阶段只回传 preflightList / engineArchivePath / encryptedHeadersMessage 三个值。</para>
/// </summary>
internal sealed class ExtractionPreflight
{
    internal async Task<bool> RunAsync(
        ExtractionCoordinator host,
        ExtractionRunState state,
        ArchiveTask task,
        CancellationToken cancellationToken)
    {
        ArchiveListResult? preflightList = state.PreflightList;
        EmbeddedZipProbeResult? directZip = state.DirectZip;
        string lastListErrorType = state.LastListErrorType;
        string lastListMessage = state.LastListMessage;
        string? encryptedHeadersMessage = state.EncryptedHeadersMessage;
        string engineArchivePath = state.EngineArchivePath;
        string engineOutputPath = state.EngineOutputPath;
        string outputPath = state.OutputPath;
        List<PasswordItem> candidates = state.Candidates;

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
            && host.Settings.AssembleSplitVolumesFromContainer
            && ExtractionCoordinator.IsVolumeMissingErrorType(lastListErrorType))
        {
            (string? assembledFirstVolume, ArchiveListResult? assembledList) =
                await host.TryAssembleSplitVolumesAsync(task, engineArchivePath, candidates, cancellationToken);

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

                host.AppendLog(
                    "WARN",
                    $"{task.LogName}：{StatusText.EncryptedHeaders} —— 连内容清单都读不出来（需要正确密码），" +
                    $"本次跳过路径预检与资源预算，解压后仍会校验落点。原因：{lastListMessage}");
            }
            else
            {
                host.AppendLog("WARN", $"{task.LogName}：没能列出归档内容（可能是加密头或文件损坏），本次跳过路径预检与资源预算，解压后仍会校验落点。");
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
                    ExtractionCoordinator.CollectVolumeFilesInDirectory(volumeDirectory).Select(Path.GetFileName));

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

                /*
                 * ⚠ 2026-10-24（第一大步造包器当场逮到）：这一支原来只写中文状态、不落机器终态 ⇒
                 * 批末汇总把它算进「未处理」。⇒ 走统一收口（`MarkStoppedBeforeExtract` +
                 * `FinalizeOutcomeIfPending`）：状态仍是「分卷缺失」，终态按既有口径（缺卷 ⇒ 部分完成）。
                 */
                string volumeMissingMessage =
                    "分卷缺失：这一卷是「一组分卷的第一卷」，但它的名字被改坏了 —— "
                    + "引擎按名字找不到同组的后续卷，只会把它当成一段通用分片"
                    + "（那样「解出来」的是一个与它等大的垃圾文件，不是包里的内容），所以不开始。"
                    + siblingText + advice
                    + "程序不会自己改源文件（不变量 1）：只有你点那个按钮，它才会改这一个名字。";

                host.MarkStoppedBeforeExtract(task, StatusText.VolumeMissing, volumeMissingMessage, TaskReason.VolumeMissing);
                ExtractionCoordinator.FinalizeOutcomeIfPending(task);

                host.AppendLog("ERROR", $"{task.LogName}：{volumeMissingMessage}", DiagnosticCodes.VolumeIncomplete, "ExtractionCoordinator.VolumePreflight");

                host.CriterionLog(
                    "分卷完整性",
                    task.VolumeInfoText + "；missing=" + string.Join(",", task.MissingVolumeNames),
                    "缺卷/不完整",
                    "不解压；源包不动",
                    task.CurrentPath,
                    task.VolumeGroupKey,
                    level: "ERROR",
                    errorCode: DiagnosticCodes.VolumeIncomplete,
                    component: "ExtractionCoordinator.VolumePreflight");
                return false;
            }

            PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(preflightList.Entries);

            /*
             * 命名类（保留名 / 结尾点或空格 / 备用数据流）：只记一条 WARN 照常解压 ——
             * 引擎落盘时会把它们改成安全名（用户 2026-10-24 口径：清洗后再落盘，⛔ 不整包拒绝）。
             */
            if (pathReport.NamingHazards.Count > 0)
            {
                host.AppendLog("WARN", $"{task.LogName}：{pathReport.NamingSummary}");
            }

            if (!pathReport.IsSafe)
            {
                /*
                 * ⚠ 2026-10-24（造包器 G01/G02 当场逮到）：同上 —— 只写状态不落终态会让这一单停在
                 * `Pending`（批末汇总报「未处理」）。⇒ 统一收口；状态是「解压失败」⇒ 终态 `Failed`。
                 */
                string unsafeMessage = "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary;

                host.MarkStoppedBeforeExtract(task, StatusText.ExtractFailed, unsafeMessage);
                ExtractionCoordinator.FinalizeOutcomeIfPending(task);

                host.AppendLog("ERROR", $"{task.LogName}：路径预检未通过 —— {pathReport.Summary}", DiagnosticCodes.FileIo, "ArchivePathGuard");
                return false;
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
            BudgetCheckResult budget = new ResourceBudget(host.BudgetLimits)
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
                SpaceEstimator.FromSourceFiles(task, host.DirectReadAppliesTo(task), host.PartialPublishActive),
                preflightList,
                directZip != null ? 0 : SpaceEstimator.EstimateCarvedBytes(task, archiveSize),
                carvedBytesNotNeeded: directZip != null)
                .WithVolumeLayout(host.ResolveWorkspaceVolumeShare(outputPath));

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
                    SpaceGateDecision spaceGate = host.ReconcileReservation(task, refined);
                    string spaceReason = spaceGate.Allowed ? budget.Reason : spaceGate.ToLogLine();

                    host.MarkSpaceBlockedCore(
                        task,
                        spaceReason + $"（{refined.Describe()}；依据：{refined.Basis}）",
                        refined.FreeSpaceDemandBytes,
                        budget.FreeSpaceBytes ?? SpaceReservationLedger.UnknownAvailable,
                        spaceGate.Allowed ? 0L : spaceGate.ShortfallBytes);

                    return false;
                }

                /*
                 * 预检拒绝也是**任务终态**：状态不能单独写。
                 * 以前这里只写 Status / ErrorMessage，Outcome 停在 Pending ⇒ 批末汇总把
                 * 一个明确的“资源预算未通过”算成“没轮到/未处理”，用户看到的是“解压失败”
                 * 但统计里既不是失败也不是跳过（912 压缩炸弹现场）。
                 * 走统一收口：MarkStoppedBeforeExtract 落状态 + 结束时间，FinalizeOutcomeIfPending
                 * 按既有判据落 Failed（这里不是缺卷）。
                 */
                host.MarkStoppedBeforeExtract(
                    task,
                    StatusText.ExtractFailed,
                    "资源预算未通过：" + budget.Reason);
                ExtractionCoordinator.FinalizeOutcomeIfPending(task);

                host.AppendLog("ERROR", $"{task.LogName}：资源预算未通过 —— {budget.Reason}", DiagnosticCodes.LimitReached, "ResourceBudget");
                return false;
            }

            /*
             * 空间门（精确值）：够了就放行、不够就**在这里停下**（引擎一个字节都还没写）。
             * 它同时把本任务的预留从"粗估"调整成精确峰值 —— 并发下这一步很关键：
             * 估小了会让几个任务一起把盘写满，估大了会白白拦下本来能跑的任务。
             */
            SpaceGateDecision preciseGate = host.ReconcileReservation(task, refined);

            if (!preciseGate.Allowed)
            {
                host.MarkSpaceBlockedCore(
                    task,
                    preciseGate.ToLogLine() + $"（{refined.Describe()}；依据：{refined.Basis}）",
                    refined.FreeSpaceDemandBytes,
                    preciseGate.AvailableBytes,
                    preciseGate.ShortfallBytes);

                return false;
            }

            host.AppendLog("INFO", $"{task.LogName}：空间门通过（精确） —— {preciseGate.Reason}；{refined.Basis}");

            if (!string.Equals(
                    RestHandlingModes.Normalize(host.RestHandlingThisBatch),
                    RestHandlingModes.Keep,
                    StringComparison.OrdinalIgnoreCase))
            {
                /*
                 * "预计可回收多少"必须**跟着源包那一档算**（2026-09-27 用户真机）：
                 * `ReclaimableBytes` = 源包 + 过程物（那是"危险模式能收回多少"的口径），
                 * 可源包选的是「留在原地」时它一个字节都不会被碰 —— 报 6.8 MiB 就是在骗人
                 * （日志里那个 6.8 MiB 正是源包自己的大小）。
                 */
                SourceHandlingMode sourceMode = host.EffectiveSourceHandling;

                long reclaimable = sourceMode == SourceHandlingMode.KeepInPlace
                    ? refined.ProcessArtifactBytes
                    : refined.ReclaimableBytes;

                string amount = reclaimable > 0
                    ? $"预计可回收 {TaskSpaceEstimate.FormatSize(reclaimable)}"
                    : "这次没有可回收的过程物";

                host.AppendLog(
                    "INFO",
                    $"{task.LogName}：定稿 + 校验通过之后会按「删除操作」"
                    // ⚠ 2026-10-15：回收站档整条下线 ⇒ 走到这里（Keep 已挡掉）只剩彻底删除。
                    + "彻底删除其余物"
                    + $"（范围：{ExtractionCoordinator.DescribeRestScope(sourceMode)}；{amount}）");
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
                host.AppendLog("INFO", $"{task.LogName}：资源预算提示 —— {budget.Reason}");
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
            host.PublishDangerousEntriesHint(
                task,
                ExtractionCoordinator.AnalyzeDangerousEntries(preflightList.Entries, host.Settings.ReportDangerousEntries),
                "INFO");
        }

        state.PreflightList = preflightList;
        state.EngineArchivePath = engineArchivePath;
        state.EncryptedHeadersMessage = encryptedHeadersMessage;
        return true;
    }
}

