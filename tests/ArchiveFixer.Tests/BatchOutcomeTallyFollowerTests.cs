using System;
using System.Collections.Generic;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **批末"怎么数"这一格的守门**：`BatchOutcomeTally` 是"一批任务怎么数"的**唯一出口**。
    ///
    /// <para><b>真机现场（2026-10-10 EEEE）</b>：三单是**跟班卷**（`IsVolumeGroupFollower = true`，
    /// 界面上写着「这一片随整组解开（由「111.z03」那单解的）」），又都走过"缺卷待批末判"那一档
    /// ⇒ 老写法在那一支上**无条件算进「跳过」**（绕过了下面的跟班卷分档）⇒ 批末汇总印出
    /// 「成功 1 / 跳过 3」，用户读成"还有 3 个没弄完"（2026-10-01 投诉过的那一句在另一条支上重现）。</para>
    ///
    /// <para><b>运行期证据</b>（debug-mcp 停在 `ExtractionCoordinator.AppendBatchSummary`）：
    /// `tally.Succeeded = 1`、`tally.Skipped = 3`、`tally.FollowerSkipped = 0`，
    /// 而在同一方法的上一行读到 `tasks[0..2].IsVolumeGroupFollower = true`。</para>
    /// </summary>
    public class BatchOutcomeTallyFollowerTests
    {
        /// <summary>跟班卷 + 缺卷待判 ⇒ **必须进跟班卷那一档**（不然用户以为还有活没干）。</summary>
        [Fact]
        public void 跟班卷又走了缺卷待判那一档_仍算跟班卷那一档()
        {
            var follower = new ArchiveTask(@"C:\t\in\111\111.rar", 1)
            {
                Outcome = TaskOutcome.Pending,
                IsVolumeGroupFollower = true
            };

            follower.IsVolumeDeficitDeferred = true;   // 链尾点亮的显示/统计事实位

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new List<ArchiveTask> { follower });

            Assert.Equal(0, tally.Skipped);
            Assert.Equal(1, tally.FollowerSkipped);
            Assert.Equal(1, tally.Total);
        }

        /// <summary>**哨兵**：不是跟班卷的单走了那一档 ⇒ 照旧算「跳过」（一个都不许被悄悄吞掉）。</summary>
        [Fact]
        public void 不是跟班卷的缺卷待判单_照旧算跳过()
        {
            var plain = new ArchiveTask(@"C:\t\in\222.7z.001", 1)
            {
                Outcome = TaskOutcome.Skipped,
                IsVolumeGroupFollower = false
            };

            plain.IsVolumeDeficitDeferred = true;

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new List<ArchiveTask> { plain });

            Assert.Equal(1, tally.Skipped);
            Assert.Equal(0, tally.FollowerSkipped);
        }

        /// <summary>
        /// **"还在名单里就按跳过数"这条口径一个字不放宽**（与 `DeferredVolumeStatusTests.递归中途撞上缺卷_…`
        /// 第 121 行同一口径）：批中间哪怕机器终态是 Failed，也按「跳过」数 ——
        /// 所以这里**不看 Outcome**（"成功却被算成跳过"那一格修在协调器：离开名单时清事实位）。
        /// </summary>
        [Fact]
        public void 还在名单里就算终态是失败_也按跳过数()
        {
            var failedButDeferred = new ArchiveTask(@"C:\t\in\111\111.zip", 1)
            {
                Outcome = TaskOutcome.Failed,
                IsVolumeGroupFollower = false
            };

            failedButDeferred.IsVolumeDeficitDeferred = true;

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new List<ArchiveTask> { failedButDeferred });

            Assert.Equal(1, tally.Skipped);
            Assert.Equal(0, tally.Failed);
        }

        /// <summary>恒等式那条不许破：各分项之和 + 未处理 = 任务数。</summary>
        [Fact]
        public void 分项之和加未处理等于任务数()
        {
            var a = new ArchiveTask(@"C:\t\in\111\111.rar", 1) { Outcome = TaskOutcome.Skipped, IsVolumeGroupFollower = true };
            a.IsVolumeDeficitDeferred = true;

            var b = new ArchiveTask(@"C:\t\in\111\111.z03", 2) { Outcome = TaskOutcome.Succeeded, IsVolumeGroupFollower = false };
            var c = new ArchiveTask(@"C:\t\in\222.7z", 3) { Outcome = TaskOutcome.Pending };

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new List<ArchiveTask> { a, b, c });

            Assert.Equal(
                tally.Total,
                tally.Succeeded + tally.Failed + tally.Skipped + tally.FollowerSkipped
                + tally.PartiallyCompleted + tally.Cancelled + tally.Untouched);
        }
    }
}
