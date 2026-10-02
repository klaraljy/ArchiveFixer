using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ArchiveFixer.Extraction;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// **一批任务按机器终态怎么数** —— 唯一出口（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。
    ///
    /// <para><b>为什么必须有它</b>（用户 2026-10-02 真机日志）：同一批任务有两个汇总行 ——
    /// 批末的「本批汇总：N 个任务 —— 成功 x / 失败 y / 跳过 z」与一键处理收尾的
    /// 「一键处理完成：成功 x / 失败 y / 跳过 z」。这两行**各写了一遍数法**，于是：
    /// ① 同一个"跳过"一处算跟班卷、一处不算 ⇒ 同一份日志里两个口径（这一条 2026-10-02 已修）；
    /// ② "终态说成功、校验却判否"那一帧（不变量 6 的真机违反）在一键汇总里算**失败**、
    ///    在本批汇总里却算**成功** —— 同一批日志里两个"失败 N"，用户拿哪一行去对数都不对。</para>
    ///
    /// <para>现在数法只在这里一份：<c>ExtractionCoordinator.AppendBatchSummary</c>、
    /// <c>OneClickCoordinator.BuildSummaryLine</c>、<c>MainViewModel.BuildLogExportHeader</c>
    /// 三处都调 <see cref="Count"/>，⛔ 谁都不许再自己写一遍 <c>Count(task =&gt; …)</c>。</para>
    ///
    /// <para>⚠ 判据只有两个**机器字段**：<see cref="ArchiveTask.Outcome"/> 与
    /// <see cref="ArchiveTask.OutputVerification"/>，外加一个事实位
    /// <see cref="ArchiveTask.CountsTowardBatchOutcome"/>（跟班卷 = false）。
    /// ⛔ 绝不比对中文文案（§7），⛔ 也绝不按"像不像成功"去猜。</para>
    /// </summary>
    public sealed class BatchOutcomeTally
    {
        /// <summary>这一批一共几个任务（= 传进来的那一份清单的长度）。</summary>
        public int Total { get; private init; }

        /// <summary>成功：终态 <see cref="TaskOutcome.Succeeded"/> **且**输出校验没判否。</summary>
        public int Succeeded { get; private init; }

        /// <summary>失败：终态 <see cref="TaskOutcome.Failed"/>，或"终态说成功、校验却判否"。</summary>
        public int Failed { get; private init; }

        /// <summary>跳过（**只算"算数"的那些**）：不是压缩包、同名冲突按用户选择跳过。</summary>
        public int Skipped { get; private init; }

        /// <summary>跟班卷（同一分卷组的后续卷）—— 按设计跳过，**不是"没做成"**，所以单列一档。</summary>
        public int FollowerSkipped { get; private init; }

        /// <summary>部分完成（解出来一部分，源包没能收干净 / 递归停在要用户决定的层）。</summary>
        public int PartiallyCompleted { get; private init; }

        /// <summary>用户取消。</summary>
        public int Cancelled { get; private init; }

        /// <summary>
        /// 没轮到（终态还是 <see cref="TaskOutcome.Pending"/>）：例如格式未知却没被处理、
        /// 被「停止后续」截断的那几单。
        ///
        /// <para><b>它是兜底那一档</b>：这不是"数出来的"，而是
        /// <c>总数 − 上面各分项</c> —— 所以恒等式
        /// 「成功 + 失败 + 跳过 + 跟班卷 + 部分完成 + 取消 + 未处理 = 任务数」**永远成立**，
        /// 将来加了新的终态也不会凭空漏出一个数（漏出去的表现就是用户在日志里对不上账）。</para>
        /// </summary>
        public int Untouched { get; private init; }

        /// <summary>
        /// 这一单算"成功"吗 —— 终态 <see cref="TaskOutcome.Succeeded"/> **且**输出校验没判否。
        ///
        /// <para>与 ①页那三格（<c>TaskSummaryService.ClassifyOutcome</c>）读的是同一对字段，
        /// ⛔ 不是拿校验那句中文文案去比（§7）。</para>
        /// </summary>
        public static bool IsCountedAsSuccess(ArchiveTask task) =>
            task != null &&
            task.Outcome == TaskOutcome.Succeeded &&
            task.OutputVerification != OutputVerificationOutcome.Failed;

        /// <summary>
        /// 这一单算"失败"吗 —— 终态失败，**或**"终态说成功、校验却判否"那一帧（不变量 6 的真机违反）。
        ///
        /// <para>第二个条件不许省：省了它，同一个任务在①页算「解压失败」、在批末「本批汇总」里算「成功」，
        /// 用户拿日志对不上账（2026-10-02 真机两个"失败 N"的来源之一）。</para>
        /// </summary>
        public static bool IsCountedAsFailure(ArchiveTask task) =>
            task != null &&
            (task.Outcome == TaskOutcome.Failed ||
             (task.Outcome == TaskOutcome.Succeeded && task.OutputVerification == OutputVerificationOutcome.Failed));

        /// <summary>按机器终态数一遍。</summary>
        public static BatchOutcomeTally Count(IEnumerable<ArchiveTask>? tasks)
        {
            List<ArchiveTask> list = tasks?.Where(task => task != null).ToList() ?? new List<ArchiveTask>();

            int succeeded = 0;
            int failed = 0;
            int skipped = 0;
            int followerSkipped = 0;
            int partiallyCompleted = 0;
            int cancelled = 0;

            foreach (ArchiveTask task in list)
            {
                switch (task.Outcome)
                {
                    case TaskOutcome.Succeeded:
                        /*
                         * 「终态说成功、校验却判否」那一帧（不变量 6 的真机违反：7z 用错密码写出 0 字节桩）。
                         * 它必须落**失败**侧 —— 判据由 IsCountedAsFailure 一处给出，
                         * 下面"失败逐条列名字"那份清单读的也是它（否则计数与清单会差一两个）。
                         */
                        if (IsCountedAsFailure(task))
                        {
                            failed++;
                        }
                        else
                        {
                            succeeded++;
                        }

                        break;

                    case TaskOutcome.Failed:
                        failed++;
                        break;

                    case TaskOutcome.PartiallyCompleted:
                        partiallyCompleted++;
                        break;

                    case TaskOutcome.Cancelled:
                        cancelled++;
                        break;

                    case TaskOutcome.Skipped:
                        /*
                         * 「跳过」必须分成两档（用户 2026-10-01 第三报 + 2026-10-02 真机）：
                         * 真机那批 10 个任务里 4 个是各组的分卷后续卷，一行「跳过 4」被他读成
                         * "还有 4 个没弄完"（他原话：「这四个应该是要跳过的，我绝对没必要，
                         * 你这样会让用户觉得还有任务没弄完」）。
                         *
                         * 判据只读事实位 `CountsTowardBatchOutcome`（跟班卷 = false）——
                         * 与批末色带 / 批末诊断同一个出口。
                         */
                        if (task.CountsTowardBatchOutcome)
                        {
                            skipped++;
                        }
                        else
                        {
                            followerSkipped++;
                        }

                        break;

                    // TaskOutcome.Pending（以及将来可能新增的档）：什么都不算，落到下面的"未处理"。
                    default:
                        break;
                }
            }

            return new BatchOutcomeTally
            {
                Total = list.Count,
                Succeeded = succeeded,
                Failed = failed,
                Skipped = skipped,
                FollowerSkipped = followerSkipped,
                PartiallyCompleted = partiallyCompleted,
                Cancelled = cancelled,
                Untouched = list.Count - succeeded - failed - skipped - followerSkipped - partiallyCompleted - cancelled
            };
        }

        /// <summary>
        /// 「成功 N / 失败 N / 跳过 N …」那串分项 —— **两个汇总行 + 导出的日志头部共用同一份措辞与顺序**
        /// （顺序：成功 / 失败 / 跳过 / 部分完成 / 取消 / 未处理）。
        ///
        /// <para>零的那几档只有"部分完成 / 取消 / 未处理"会省掉：前三档是固定项，
        /// 因为它们就是用户对账的那三个数（老口径也是这么写的）。</para>
        ///
        /// <para>「部分完成」必须**单独一档**（用户 2026-09-27："统一成「部分完成」可以"）：
        /// 过去它被算进"未处理"，于是同一件事批末说"未处理 1"、一键汇总说"部分完成 1" ——
        /// 两个说法，用户对着日志看会以为有一单压根没跑。</para>
        /// </summary>
        public IReadOnlyList<string> BuildParts()
        {
            var parts = new List<string>
            {
                $"成功 {Succeeded}",
                $"失败 {Failed}",
                $"跳过 {Skipped}"
            };

            if (PartiallyCompleted > 0)
            {
                parts.Add($"部分完成 {PartiallyCompleted}");
            }

            if (Cancelled > 0)
            {
                parts.Add($"取消 {Cancelled}");
            }

            if (Untouched > 0)
            {
                parts.Add($"未处理 {Untouched}");
            }

            return parts;
        }

        /// <summary>
        /// 「另有 N 个是同一分卷组的后续卷 ……」那一句；没有跟班卷时返回空串。
        ///
        /// <para>文案本体是 <see cref="StatusText.VolumeGroupFollowerSummaryFormat"/> ——
        /// 批末「本批汇总」、一键汇总行、日志导出头部三处**读同一句**，
        /// ⛔ 不许哪一处自己再写一遍（这正是 2026-10-02 那次"同一份日志两个口径"的来源）。</para>
        /// </summary>
        public string DescribeFollowerNote() =>
            FollowerSkipped > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.VolumeGroupFollowerSummaryFormat,
                    FollowerSkipped)
                : string.Empty;
    }
}
