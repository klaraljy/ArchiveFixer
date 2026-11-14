using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一个候选**解压失败**之后的分类与处置：引擎原话落日志 → 记"走得最远的那次" →
/// 后续卷/损坏/密码证实/错密码/两义 → 双面文件补救 → 引擎错误停因。
///
/// <para><b>为什么单独成类</b>：这一段过去排在候选循环里近四百行，八条出口各自 return，
/// 与"成功那一支"（产物校验）共用同一批局部变量 —— 读的人分不清"这个候选为什么被否"。</para>
///
/// <para>⛔ 行为一个字不改：判据顺序、日志级别与文案、停因、缺卷记账、双面文件重解的挂点全部照旧；
/// 只有两笔账（<see cref="CandidateFailureResult.InformativeFailure"/> /
/// <see cref="CandidateFailureResult.AmbiguousPasswordFailure"/>）回写给循环。</para>
/// </summary>
internal static class CandidateFailureClassifier
{
    /// <summary>
    /// 判定这个候选失败之后这一层怎么走。
    /// 返回的 <see cref="CandidateFailureResult.Outcome"/> 为 null = 接着试下一个候选。
    /// </summary>
    internal static async Task<CandidateFailureResult> ClassifyFailureAsync(
        RecursiveExtractor host,
        CandidateFailureContext context,
        CancellationToken cancellationToken)
    {
        ArchiveOperationResult result = context.Result;

        var step = new CandidateFailureResult
        {
            InformativeFailure = context.InformativeFailure,
            AmbiguousPasswordFailure = context.AmbiguousPasswordFailure
        };

        /*
         * ===== 引擎原话落日志（用户 2026-09-27：「引擎原话从不落日志」）=====
         *
         * 所有 return / continue **之前**：三条出路（损坏停下 / 错密码继续 / 其它停下）都要留下
         * 这一次的原话 —— 但**每个候选只留这一份**：出口只有 <see cref="EngineOutputLog"/> 一个
         * （与单层路径同一个），⛔ 这里不许自己挑行、自己拼前缀。
         */
        if (host.VerboseLog)
        {
            EngineOutputLog.LogVerbose(host.Log, context.LayerLabel, result);
        }

        if (!host.VerboseLog)
        {
            /*
             * 默认档：**这个候选失败了**那一条（一行）—— 与单层路径的候选循环同一口径
             * （不变量 6：失败必须留痕）。
             *
             * ⚠ 2026-10-17（用户 2026-09-25 第 45 条）：**这一步是"这个候选被否、继续下一个"，
             * 不是终态** ⇒ 引擎原话按 **INFO** 写（进"每任务攒着、成功就丢"的缓冲；
             * 真失败的收场由协调器写 ERROR 并且 `EndTaskLogCapture` 会把细节全吐出来）。
             * 上一版写 ERROR ⇒ 两个错候选就把缓冲冲掉（`Item45LogAndPasswordTests` 实测）。
             */
            EngineOutputLog.LogFailure(host.Log, context.LayerLabel, result, levelOverride: "INFO");
        }

        /*
         * ===== 报错结论要取**走得最远的那一次**，不是最后一次（用户 2026-09-30 真机）=====
         *
         * 判据只用**事实**：这一次尝试在产物目录里留下了多少字节。留下过东西的那一次，
         * 信息量必然大于"连门都没进去"的那些 ⇒ 记下**第一次**留下产物的失败，收尾优先用它
         * （没有就退回最后一次失败，行为与以前逐字一致）。
         */
        if (step.InformativeFailure == null && RecursiveExtractor.ProducedBytesInLayerOutput(context.Item) > 0)
        {
            step.InformativeFailure = result;
        }

        /*
         * ===== 「单独一片后续卷」开失败 ⇒ 结论是**缺卷**，不是「损坏」（2026-10-13 困难批 `run-2222`）=====
         *
         * 判据（只读事实，两把既有尺子）：① 这一份是**后续卷**
         * （<see cref="FileNameHelper.IsVolumeContinuationPart"/>，能吃骨架档 `par@@@@t3`）；
         * ② 同组入口**不在它旁边**。两条同时成立 ⇒ 按"缺卷"收（记下这一片、交给跨链/批末去凑）。
         * ⛔ 位置必须在"损坏"与"密码已证实 ⇒ 数据坏了"那两支**之前**（它们会当场 return）；
         * ⛔ 判不出（目录读不了 / 名字解析不出）⇒ 照旧走引擎给的那一档。
         */
        if (!result.Success
            && !Detection.VolumePieceFacts.ContentProvesNotAVolumePiece(context.ArchivePath)
            && FileNameHelper.IsVolumeContinuationPart(Path.GetFileName(context.ArchivePath))
            && !RecursiveExtractor.IsGroupEntryPresentBeside(context.ArchivePath))
        {
            host.Log(
                "WARN",
                $"{context.LayerLabel}：这一片是这一组的「后续卷」、而同组的入口包不在这一层 ⇒ 按「缺卷」处理"
                + "（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

            host.RememberUnresolvedVolumePiece(context.ArchivePath);

            step.Outcome = LayerOutcome.Stop(
                host.BuildLayerReport(
                    context.Item,
                    result,
                    succeededPassword: null,
                    manifestEntries: context.LastListedEntries),
                RecursionStopReason.MissingVolume);

            return step;
        }

        /*
         * 损坏不再换候选重试（规则 1）：换密码对损坏的归档没有任何帮助。
         *
         * <para>⚠ **2026-10-18 补：落「损坏」之前先问一次"这一份是不是某一组的一片"**
         * （困难批 `30303030` 运行期定案）：判据与处置逐字同前一处（唯一出口
         * <see cref="RecursiveExtractor.IsPieceOfIncompleteGroup"/>）：
         * 内容自证是分卷组的一员且入口不在旁边 ⇒ 落「缺卷」。⛔ 判不出 ⇒ 照旧落「损坏」。</para>
         */
        if (result.IsCorrupted)
        {
            if (RecursiveExtractor.IsPieceOfIncompleteGroup(context.ArchivePath))
            {
                host.Log(
                    "WARN",
                    $"{context.LayerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                    + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                    + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                host.RememberUnresolvedVolumePiece(context.ArchivePath);

                step.Outcome = LayerOutcome.Stop(
                    host.BuildLayerReport(
                        context.Item,
                        result,
                        succeededPassword: null,
                        manifestEntries: context.LastListedEntries),
                    RecursionStopReason.MissingVolume);

                return step;
            }

            host.Log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CandidateStoppedByCorruptedLogFormat,
                    context.LayerLabel));

            step.Outcome = LayerOutcome.Stop(
                host.BuildLayerReport(
                    context.Item,
                    result,
                    succeededPassword: null,
                    manifestEntries: context.LastListedEntries),
                RecursionStopReason.Corrupted);

            return step;
        }

        /*
         * ===== 「密码其实已经对了，坏的是数据」这一档（用户 2026-10-01 真机 `giu910`）=====
         *
         * 判据与单层路径**同一个出口**（<see cref="ProducedContentGate"/>）：这一趟在这一层的
         * 产物目录里真解出了多少东西。两义那一档（`IsPasswordOrCorrupted`）一并覆盖：
         * 只要这一趟**真解出了内容**，两义就当场收敛成"数据坏了"。
         */
        if ((result.IsWrongPassword || result.IsNeedPassword || result.IsPasswordOrCorrupted)
            && ProducedContentGate.TryMeasure(
                context.Item.Layer.OutputPath,
                result.ReportedSubItemErrors,
                result.FailedEntryNames.Count,
                out int provenFiles,
                out long provenBytes))
        {
            /*
             * ⚠ 2026-10-13（困难批 `run-4444`，运行期定案）：落「损坏」之前先问一次
             * **它是不是"某个还没凑齐的分卷组的一员"**（唯一出口 <see cref="RecursiveExtractor.IsPieceOfIncompleteGroup"/>）。
             */
            if (RecursiveExtractor.IsPieceOfIncompleteGroup(context.ArchivePath))
            {
                host.Log(
                    "WARN",
                    $"{context.LayerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                    + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                    + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

                host.RememberUnresolvedVolumePiece(context.ArchivePath);

                step.Outcome = LayerOutcome.Stop(
                    host.BuildLayerReport(
                        context.Item,
                        result,
                        succeededPassword: null,
                        manifestEntries: context.LastListedEntries),
                    RecursionStopReason.MissingVolume);

                return step;
            }

            string provenMessage = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordProvenDataCorruptedFormat,
                    provenFiles,
                    provenBytes)
                + "（引擎原话：" + PasswordMasker.Sanitize(result.Message) + "）";

            host.Log("WARN", $"{context.LayerLabel}：{provenMessage}");

            step.Outcome = LayerOutcome.Stop(
                host.BuildLayerReport(
                    context.Item,
                    result,
                    succeededPassword: null,
                    overrideMessage: provenMessage,
                    manifestEntries: context.LastListedEntries),
                RecursionStopReason.Corrupted);

            return step;
        }

        // 需要密码 / 密码错误 → 这个候选没用了，试下一个。
        if (result.IsWrongPassword || result.IsNeedPassword)
        {
            /*
             * ⛔ **错密码这一支不做"结果相同 ⇒ 提前停"**（2026-10-21 全量当场逮到）：
             * 加密包的错密码也会在产物目录里留下**一模一样的半截数据**，而正确的密码
             * 排在后面时它产出的东西完全不同 —— 实测
             * `Item45LogAndPasswordTests.真7z_先试错两个候选再成功_成功档照样只留一行`。
             * "结果相同 ⇒ 换密码没用"这条只对**引擎已经接受密码**那一支成立。
             */
            host.Log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordCandidateRejectedLogFormat,
                    context.LayerLabel));

            return step;
        }

        /*
         * ===== 两义那一档（用户 2026-09-30 真机）=====
         *
         * 引擎自己说"密码可能不对、也可能数据坏了"，**谁都不许单独定原因**：
         * 与"密码错误"同一处置（继续试下一个候选）；到收尾时结论落"两义"那一档，
         * 日志里也保留两种可能。
         */
        if (result.IsPasswordOrCorrupted)
        {
            step.AmbiguousPasswordFailure = result;

            host.Log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CandidatePasswordOrCorruptedLogFormat,
                    context.LayerLabel));

            return step;
        }

        /*
         * ===== 引擎说"这根本不是归档"时**不写 ERROR**（用户 2026-10-05：三条假 ERROR）=====
         *
         * 判据只认"尾部确实有一个自洽的内嵌归档"，看不出就是看不懂，一个字都不许放宽。
         */
        bool looksDoubleFaced = context.CarvedSource == null
            && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal)
            && RecursiveExtractor.IsDoubleFacedEmbeddedArchive(context.ArchivePath);

        if (!looksDoubleFaced)
        {
            /*
             * ⚠ 2026-10-09（用户点名："第一个红字…为啥显示解压失败"）：**引擎报缺卷时不写 ERROR、
             * 也不叫"解压失败"** —— 缺卷不是失败，这一行改 WARN + 先说"先跳过"。
             * 判据读**结构化错误码**（`DetectedErrorType`），⛔ 不比中文文案。
             */
            bool missingVolume = string.Equals(
                result.DetectedErrorType,
                EngineErrorTypes.VolumeMissing,
                StringComparison.Ordinal);

            // 其余（引擎不可用、路径问题、输出冲突…）换密码也解决不了，直接停。
            host.Log(
                missingVolume ? "WARN" : "ERROR",
                missingVolume
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.CandidateMissingVolumeLogFormat,
                        context.LayerLabel,
                        result.Message)
                    : string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.CandidateStoppedByEngineErrorLogFormat,
                        context.LayerLabel,
                        result.Message));
        }

        /*
         * 例外：引擎说"这根本不是归档"时，先看一眼它是不是**双面文件**
         * （真视频 / 图片 + 尾部一整个 ZIP）—— 是的话抠出副本再解一次。
         * 只在第一次（还没抠过）时试，⛔ 不会套娃重试。
         */
        if (context.CarvedSource == null
            && string.Equals(result.DetectedErrorType, EngineErrorTypes.UnsupportedFormat, StringComparison.Ordinal))
        {
            LayerOutcome? retried = await host.RetryWithCarvedEmbeddedArchiveAsync(
                    context.Item,
                    cancellationToken,
                    context.Progress,
                    context.Stalled)
                .ConfigureAwait(false);

            if (retried != null)
            {
                step.Outcome = retried;

                return step;
            }
        }

        RecursionStopReason engineStop = RecursiveExtractor.MapEngineErrorToStopReason(result);

        /*
         * ===== 「单独一片后续卷」开失败 ⇒ 结论是**缺卷**，不是「损坏」（2026-10-13 困难批 `run-2222`）=====
         *
         * 判据与处置**逐字同前两处**（唯一出口 <see cref="RecursiveExtractor.IsPieceOfIncompleteGroup"/>）：
         * 可证"这一份是某个还没凑齐的分卷组的一员" ⇒ 结论是**缺卷**，⛔ 不是失败；
         * 片记下来，交给跨链/批末那一套去凑。⛔ 判据没有放宽。
         */
        if (!result.Success && RecursiveExtractor.IsPieceOfIncompleteGroup(context.ArchivePath))
        {
            host.Log(
                "WARN",
                $"{context.LayerLabel}：这一片属于某个分卷组、而同组的入口包不在这一层 ⇒ "
                + "按「缺卷」处理（⛔ 不是「压缩包损坏」）—— 片原样留着，交给批末/跨链那一套去凑。"
                + $"（引擎原话：{PasswordMasker.Sanitize(result.Message)}）");

            host.RememberUnresolvedVolumePiece(context.ArchivePath);

            step.Outcome = LayerOutcome.Stop(
                host.BuildLayerReport(
                    context.Item,
                    result,
                    succeededPassword: null,
                    manifestEntries: context.LastListedEntries),
                RecursionStopReason.MissingVolume);

            return step;
        }

        /*
         * 引擎说"分卷缺失"时，把**这一层打不开的那一片**记下来（跨链收卷，2026-10-05 真机第九批 CCCC）：
         * 这一片可能正是**同批另一组**缺的那一片 —— 这条链一收尾工作区就整份删掉了，
         * 不记下来的话，批末那一站到盘上一看：缺的东西刚刚被自己扔掉。
         */
        if (engineStop == RecursionStopReason.MissingVolume)
        {
            host.RememberUnresolvedVolumePiece(context.ArchivePath);
        }

        step.Outcome = LayerOutcome.Stop(
            host.BuildLayerReport(
                context.Item,
                result,
                succeededPassword: null,
                manifestEntries: context.LastListedEntries),
            engineStop);

        return step;
    }
}

