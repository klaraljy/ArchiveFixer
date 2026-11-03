using System;
using System.Collections.Generic;
using ArchiveFixer.Extraction;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 批末汇总框的**三档严重度**（用户 2026-09-29 要求："最后的弹窗，他没有颜色的区分……
    /// 现在最后的总结也要分颜色，一般情况下是蓝白字黑，加上两种就是橙白字黑和红白字黑"）。
    ///
    /// <para>它只描述"这一批的结果有多糟"，配色由显示层（<c>Views\AppDialogWindow</c>）按它取。
    /// ⛔ 判定**只读机器终态**（<see cref="ArchiveTask.Outcome"/> / <see cref="ArchiveTask.OutputVerification"/>），
    /// 绝不比对 <c>StatusText</c> 里的中文文案（AGENTS.md §7：统计与裁决不得依赖中文比较 ——
    /// 文案改一个字就会让整批的颜色悄悄变错，而且没有任何编译错误）。</para>
    /// </summary>
    public enum BatchSummarySeverity
    {
        /// <summary>全成功：没有任何失败、也没有"部分完成 / 跳过 / 取消 / 没轮到"。蓝底白字。</summary>
        Success = 0,

        /// <summary>没失败，但有"部分完成 / 跳过 / 取消 / 没轮到"这类非致命问题。橙底白字。</summary>
        Partial = 1,

        /// <summary>有失败（含校验判否 / 空间不足 / 密码试完）。红底白字。</summary>
        Failed = 2
    }

    /// <summary>
    /// 批末汇总严重度的**唯一判据出口**（用户 2026-09-29："同一件事只允许一个判据出口"）。
    ///
    /// <para><b>三档的边界（刻意定死在这里，别处不许再判断一次）</b>：</para>
    /// <list type="number">
    /// <item><description><b>红（<see cref="BatchSummarySeverity.Failed"/>）</b>：有任务落成
    /// <see cref="TaskOutcome.Failed"/>（或校验枚举已经是 <see cref="OutputVerificationOutcome.Failed"/>），
    /// **而且整批一个内容物都没解出来** —— 部分成功不许显示成成功（不变量 6）。</description></item>
    /// <item><description><b>橙（<see cref="BatchSummarySeverity.Partial"/>）</b>：有失败，但整批已经有
    /// <see cref="TaskOutcome.Succeeded"/> / <see cref="TaskOutcome.PartiallyCompleted"/>
    /// （有内容物被解出）；或没有失败但存在部分完成 / 跳过 / 取消 / 没轮到。
    /// 用户 2026-10-28 口径：「既然解压出了这么多，汇总应该是部分完成，因为有内容物被解出」。</description></item>
    /// <item><description><b>蓝（<see cref="BatchSummarySeverity.Success"/>）</b>：其余情况 ——
    /// 包括"一个任务都没有"（没有失败、也没有没做成的事，用蓝的"什么都没发生"最诚实）。</description></item>
    /// </list>
    ///
    /// <para><b>「只有跳过」算橙</b>（用户原话把"跳过"归进"非致命问题"那一档，与"部分完成"并列）：
    /// 跳过虽然通常是他自己在同名冲突框里选的，但**这一批确实有东西没做成** ——
    /// 报成蓝色会让他以为"全好了"，而去输出目录里找一个根本不存在的产物。</para>
    /// </summary>
    public static class BatchSummarySeverityRules
    {
        /// <summary>按统一批报告（tally）算严重度；⛔ 不再另起一份状态名单。</summary>
        public static BatchSummarySeverity FromTally(BatchOutcomeTally? tally)
        {
            if (tally == null)
            {
                return BatchSummarySeverity.Success;
            }

            if (tally.HasVerificationFailure)
            {
                return BatchSummarySeverity.Failed;
            }

            bool hasContent = tally.Succeeded > 0 || tally.PartiallyCompleted > 0;

            if (tally.Failed > 0)
            {
                return hasContent ? BatchSummarySeverity.Partial : BatchSummarySeverity.Failed;
            }

            bool hasPartial = tally.PartiallyCompleted > 0
                || tally.Skipped > 0
                || tally.Cancelled > 0
                || tally.Untouched > 0;

            return hasPartial ? BatchSummarySeverity.Partial : BatchSummarySeverity.Success;
        }

        /// <summary>按任务清单算这一批的汇总严重度（唯一出口；内部走同一份 tally）。</summary>
        public static BatchSummarySeverity FromTasks(IEnumerable<ArchiveTask>? tasks) =>
            FromTally(BatchOutcomeTally.Count(tasks));

        /// <summary>按机器终态算汇总严重度（纯函数，单独可测）。</summary>
        public static BatchSummarySeverity FromOutcomes(IEnumerable<TaskOutcome>? outcomes) =>
            Evaluate(outcomes, verificationFailed: false);

        private static BatchSummarySeverity Evaluate(
            IEnumerable<TaskOutcome>? outcomes,
            bool verificationFailed)
        {
            bool hasFailure = verificationFailed;
            bool hasContent = false;
            bool hasPartial = false;

            foreach (TaskOutcome outcome in outcomes ?? Array.Empty<TaskOutcome>())
            {
                switch (outcome)
                {
                    case TaskOutcome.Failed:
                        hasFailure = true;
                        break;

                    case TaskOutcome.Succeeded:
                        // 成功 = 至少一个任务真的产出了内容物。
                        hasContent = true;
                        break;

                    case TaskOutcome.PartiallyCompleted:
                        // 部分完成一定以内容物为前提，它本身就是"这一批有内容物"的证据。
                        hasContent = true;
                        hasPartial = true;
                        break;

                    case TaskOutcome.Skipped:
                    case TaskOutcome.Cancelled:
                    case TaskOutcome.Pending:
                        hasPartial = true;
                        break;
                }
            }

            /*
             * 校验判否是更硬的一档：产物没有被证明可用，不能因为别的任务成功就降成橙。
             */
            if (verificationFailed)
            {
                return BatchSummarySeverity.Failed;
            }

            /*
             * 用户 2026-10-28 新口径（覆盖旧的「只要一个 Failed 就红」）：
             * 失败 + 整批已有内容物 ⇒ 橙（部分完成），因为用户已经拿到了东西；
             * 失败 + 一个内容物都没有 ⇒ 红（整批确实什么也没做成）。
             */
            if (hasFailure)
            {
                return hasContent
                    ? BatchSummarySeverity.Partial
                    : BatchSummarySeverity.Failed;
            }

            return hasPartial
                ? BatchSummarySeverity.Partial
                : BatchSummarySeverity.Success;
        }
    }
}
