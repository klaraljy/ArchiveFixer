using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 空间变化侦察（<see cref="SpaceTrendMonitor"/>；用户 2026-09-27："你要时刻弄空间检测"）。
    ///
    /// <para>纯逻辑、注时钟，所以这里能精确摆布每一针：什么时候报、什么时候不报、曲线怎么算。
    /// 它**只观察不判断**（不参与放行与调度），这一点也由本文件钉住：报告里的数字只来自采样。</para>
    /// </summary>
    public class SpaceTrendMonitorTests
    {
        private const long Gib = 1024L * 1024 * 1024;

        [Fact]
        public void 第一针永远报_之后只有变化够大才报()
        {
            long free = 10 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            DateTime at = new(2026, 9, 27, 10, 0, 0);

            // 第一针：永远报（"现在还剩多少"是后面所有变化的基准）。
            SpaceTrendChange? first = monitor.Record("批首", at);
            Assert.NotNull(first);
            Assert.Equal(10 * Gib, first!.Current.FreeBytes);

            // 只掉 1 MiB（< 64 MiB 也 < 1%）：不报，但**照样记进采样表**。
            free = 10 * Gib - (1024 * 1024);
            Assert.Null(monitor.Record("每 5 秒", at.AddSeconds(5)));

            // 掉 2 GiB（既超绝对阈值也超 1%）：报。
            free = 8 * Gib;
            SpaceTrendChange? big = monitor.Record("「x.7z」开工", at.AddSeconds(10));

            Assert.NotNull(big);
            Assert.Equal(-2 * Gib, big!.DeltaBytes);
            Assert.Equal(TimeSpan.FromSeconds(10), big.Since);
            Assert.Contains("少了 2 GiB", SpaceTrendMonitor.DescribeChange(big, "目标盘"), StringComparison.Ordinal);
            Assert.Contains("「x.7z」开工", SpaceTrendMonitor.DescribeChange(big, "目标盘"), StringComparison.Ordinal);

            Assert.Equal(3, monitor.SampleCount);
        }

        [Fact]
        public void 变化大但比例很小_也要报_而只超比例不超绝对值则不报()
        {
            // 100 GiB 的盘掉 1 GiB = 1%…… 取 200 GiB 掉 1 GiB = 0.5% → 两个阈值都没过 → 不报
            long free = 200 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            DateTime at = new(2026, 9, 27, 11, 0, 0);
            monitor.Record("批首", at);

            free = 199 * Gib;
            Assert.Null(monitor.Record("每 5 秒", at.AddSeconds(5)));

            // 再掉 2 GiB（累计 3 GiB，超过 64 MiB 且超过 1%）：报。
            free = 197 * Gib;
            Assert.NotNull(monitor.Record("每 5 秒", at.AddSeconds(10)));
        }

        [Fact]
        public void 取不到可用空间_如实记下且不参与变化比较()
        {
            long? free = 10 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            DateTime at = new(2026, 9, 27, 12, 0, 0);
            monitor.Record("批首", at);

            // 探测失败：不报变化，也**不许**把它当成基准（否则下一针会算出一个巨大的假变化）。
            free = null;
            Assert.Null(monitor.Record("每 5 秒", at.AddSeconds(5)));

            free = 9 * Gib;
            SpaceTrendChange? change = monitor.Record("每 5 秒", at.AddSeconds(10));

            Assert.NotNull(change);
            Assert.Equal(10 * Gib, change!.Previous.FreeBytes);
            Assert.Equal(-1 * Gib, change.DeltaBytes);

            // 报告里要如实写"有几针取不到"。
            IReadOnlyList<string> report = monitor.DescribeReport();
            Assert.Contains(report, line => line.Contains("1 针取不到", StringComparison.Ordinal));
            Assert.Contains(report, line => line.Contains("起 10 GiB", StringComparison.Ordinal));
        }

        [Fact]
        public void 探测抛异常_不算批失败_只当取不到()
        {
            int calls = 0;
            var monitor = new SpaceTrendMonitor(() =>
            {
                calls++;

                if (calls == 2)
                {
                    throw new InvalidOperationException("盘没就绪");
                }

                // 第一针 5 GiB、第三针 4 GiB：中间那一针抛异常，不能把它当成基准（否则第三针会跟它比）。
                return calls == 1 ? 5 * Gib : 4 * Gib;
            });

            DateTime at = new(2026, 9, 27, 13, 0, 0);

            Assert.NotNull(monitor.Record("批首", at));
            Assert.Null(monitor.Record("每 5 秒", at.AddSeconds(5)));

            SpaceTrendChange? change = monitor.Record("每 5 秒", at.AddSeconds(10));

            Assert.NotNull(change);
            Assert.Equal(5 * Gib, change!.Previous.FreeBytes);
            Assert.Equal(-1 * Gib, change.DeltaBytes);

            Assert.All(
                monitor.DescribeReport(),
                line => Assert.False(string.IsNullOrWhiteSpace(line)));
        }

        [Fact]
        public void 批末曲线_给出起最低收与最多同时占用()
        {
            long free = 30 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            DateTime at = new(2026, 9, 27, 14, 0, 0);

            monitor.Record("批首", at);

            free = 24 * Gib;
            monitor.Record("「a.7z」开工", at.AddSeconds(30));

            free = 22 * Gib;   // 最低点
            monitor.Record("「b.7z」开工", at.AddSeconds(60));

            free = 26 * Gib;   // 源包被删 / 清理回收了一些
            monitor.Record("「a.7z」收尾", at.AddSeconds(90));

            IReadOnlyList<string> report = monitor.DescribeReport();

            string curve = Assert.Single(report);

            Assert.Contains("起 30 GiB", curve, StringComparison.Ordinal);
            Assert.Contains("最低 22 GiB", curve, StringComparison.Ordinal);
            Assert.Contains("收 26 GiB", curve, StringComparison.Ordinal);
            Assert.Contains("最多同时占用约 8 GiB", curve, StringComparison.Ordinal);
            Assert.Contains("「b.7z」开工", curve, StringComparison.Ordinal);
        }

        [Fact]
        public void 一针都没有时_报告为空_一针都没取到时_如实说明()
        {
            var empty = new SpaceTrendMonitor(() => 1 * Gib);
            Assert.Empty(empty.DescribeReport());

            var blind = new SpaceTrendMonitor(() => null);
            blind.Record("批首");

            Assert.Contains(
                blind.DescribeReport(),
                line => line.Contains("一针都没取到", StringComparison.Ordinal));
        }

        [Fact]
        public void 任务边界的采样只进曲线_不单独报一行()
        {
            /*
             * 为什么这条必须钉住：任务开工 / 收尾那一针的 note 里带**任务文件名**，
             * 而"成功的任务只留一行"是第 45 条定下的日志纪律（`Item45LogAndPasswordTests` 用
             * "含该文件名的行数 ≤ 3" 钉着）。空间侦察在这里多报一行 = 那条纪律当场失效
             * —— 全量回归实测：两条真 7z 用例正是这么变红的。
             *
             * 判据：notify: false 的那一针**不返回变化**（调用方因此不会写日志），
             * 但它**照样进采样表**（曲线的最低点仍然能写清是哪一刻、哪个任务）。
             */
            long free = 10 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            DateTime at = new(2026, 9, 27, 15, 0, 0);

            Assert.NotNull(monitor.Record("批首", at));

            free = 4 * Gib;

            Assert.Null(monitor.Record("「x.7z」收尾", at.AddSeconds(30), notify: false));

            // 下一针（周期采样）仍然对着"上次报过的那个数"算，所以它照样能报出这次下跌。
            SpaceTrendChange? change = monitor.Record("每 5 秒", at.AddSeconds(35));

            Assert.NotNull(change);
            Assert.Equal(10 * Gib, change!.Previous.FreeBytes);
            Assert.Equal(-6 * Gib, change.DeltaBytes);

            // 曲线里最低点那一刻的注脚仍然是那个任务（"哪个任务把盘吃下去了"看得见）。
            Assert.Contains(
                monitor.DescribeReport(),
                line => line.Contains("最低 4 GiB", StringComparison.Ordinal) &&
                        line.Contains("「x.7z」收尾", StringComparison.Ordinal));
        }

        [Fact]
        public async System.Threading.Tasks.Task 周期循环_按间隔采样_取消后立刻停()
        {
            long free = 8 * Gib;
            var monitor = new SpaceTrendMonitor(() => free);

            var lines = new List<string>();
            using var cts = new System.Threading.CancellationTokenSource();

            System.Threading.Tasks.Task loop = monitor.RunAsync(
                TimeSpan.FromMilliseconds(20),
                line => lines.Add(line),
                cts.Token);

            await System.Threading.Tasks.Task.Delay(120);

            cts.Cancel();

            await loop;

            Assert.True(monitor.SampleCount >= 1, "周期循环至少要采到一针");

            // 取消之后不再增长（有界：给它 100 ms 再看一次）。
            int after = monitor.SampleCount;
            await System.Threading.Tasks.Task.Delay(100);

            Assert.Equal(after, monitor.SampleCount);
        }
    }
}
