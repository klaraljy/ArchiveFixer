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
    /// <item><description><b>红（<see cref="BatchSummarySeverity.Failed"/>）</b>：只要有一个任务落成
    /// <see cref="TaskOutcome.Failed"/>（或校验枚举已经是 <see cref="OutputVerificationOutcome.Failed"/>），
    /// 整批就是红 —— 部分成功不许显示成成功（不变量 6）。</description></item>
    /// <item><description><b>橙（<see cref="BatchSummarySeverity.Partial"/>）</b>：没有失败，但存在
    /// <see cref="TaskOutcome.PartiallyCompleted"/> / <see cref="TaskOutcome.Skipped"/> /
    /// <see cref="TaskOutcome.Cancelled"/> / <see cref="TaskOutcome.Pending"/>（还没结论 = 没轮到）。</description></item>
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
        /// <summary>按任务清单算这一批的汇总严重度（判据的唯一出口）。</summary>
        public static BatchSummarySeverity FromTasks(IEnumerable<ArchiveTask>? tasks)
        {
            var outcomes = new List<TaskOutcome>();
            bool verificationFailed = false;

            foreach (ArchiveTask? task in tasks ?? Array.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                outcomes.Add(task.Outcome);

                /*
                 * 校验判否也算"失败"：真机上出现过"终态写着成功、校验却已判否"的那一帧
                 * （见 OneClickCoordinator.IsSuccessStatus 的说明），两个事实取更坏的那个。
                 * 这一步不改变正常路径的结论 —— 校验判否时管线本来就会把 Outcome 落成 Failed。
                 */
                if (task.OutputVerification == OutputVerificationOutcome.Failed)
                {
                    verificationFailed = true;
                }
            }

            return verificationFailed
                ? BatchSummarySeverity.Failed
                : FromOutcomes(outcomes);
        }

        /// <summary>按机器终态算汇总严重度（纯函数，单独可测）。</summary>
        public static BatchSummarySeverity FromOutcomes(IEnumerable<TaskOutcome>? outcomes)
        {
            var severity = BatchSummarySeverity.Success;

            foreach (TaskOutcome outcome in outcomes ?? Array.Empty<TaskOutcome>())
            {
                switch (outcome)
                {
                    case TaskOutcome.Failed:
                        // 有失败就是红，后面的不用再看了。
                        return BatchSummarySeverity.Failed;

                    case TaskOutcome.PartiallyCompleted:
                    case TaskOutcome.Skipped:
                    case TaskOutcome.Cancelled:
                    case TaskOutcome.Pending:
                        severity = BatchSummarySeverity.Partial;
                        break;

                    default:
                        // Succeeded：不动档位。
                        break;
                }
            }

            return severity;
        }
    }
}
