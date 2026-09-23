using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「空间规划 + 危险模式」的**纯模型**测试（用户 2026-09-22 需求第 1 / 2 / 3 条）。
    ///
    /// <para>这一组不碰 WPF、不跑管线，只钉三件事：</para>
    /// <list type="number">
    /// <item><description><b>空间核算口径</b>：峰值 = 源包（分卷整组去重）+ 内容物 + 过程物额外增量，
    /// 够不够判在 <see cref="SpaceGate"/>；</description></item>
    /// <item><description><b>调度</b>：按需求从小到大排 + "装不下就跳过、绝不静默" + "5G 与 6G 不许一起上"
    /// （用户点名的反例）；</description></item>
    /// <item><description><b>危险模式的五条门槛</b>与<b>自测协议的四条判据</b>。</description></item>
    /// </list>
    ///
    /// <para>端到端（真跑一遍解压管线、真的删掉其余物）在 <c>DangerModePipelineTests</c> 里。</para>
    /// </summary>
    public class SpaceModeTests : IDisposable
    {
        private const long Gib = 1024L * 1024 * 1024;

        private readonly string _root;

        public SpaceModeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpaceMode", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ ① 空间核算

        [Fact]
        public void 峰值等于_源包加内容物加过程物_三份去重后相加()
        {
            long source = 3 * Gib;

            var estimate = new TaskSpaceEstimate
            {
                SourceBytes = source,
                ContentBytes = 2 * Gib,
                ProcessArtifactBytes = 512L * 1024 * 1024
            };

            Assert.Equal(source + 512L * 1024 * 1024, estimate.RetainedBytes);
            Assert.Equal(source + 2 * Gib + 512L * 1024 * 1024, estimate.PeakBytes);

            // 危险模式能立刻收回的正好是"任务完成前一直占着"的那一份（源包 + 过程物）。
            Assert.Equal(estimate.RetainedBytes, estimate.ReclaimableBytes);
        }

        [Fact]
        public void 饱和加法_不因为溢出把_空间不足_算成_空间充足()
        {
            Assert.Equal(long.MaxValue, TaskSpaceEstimate.SaturatingSum(long.MaxValue, 1));
            Assert.Equal(long.MaxValue, TaskSpaceEstimate.SaturatingSum(long.MaxValue - 1, 100));

            // 负数（解析异常）按 0 计：绝不允许它把已累计的量冲减掉。
            Assert.Equal(5, TaskSpaceEstimate.SaturatingSum(5, -1));
            Assert.Equal(0, TaskSpaceEstimate.SaturatingSum(-1, -1));
        }

        [Fact]
        public void 粗估_分卷整组求和且按路径去重_内嵌归档中间件单独计()
        {
            string part1 = CreateSizedFile("vol.7z.001", 4096);
            string part2 = CreateSizedFile("vol.7z.002", 2048);

            var task = new ArchiveTask(part1, 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                FileName = "vol.7z.001"
            };

            task.VolumePaths.Add(part1);
            task.VolumePaths.Add(part2);

            // 故意再塞一遍同一个路径：去重规则必须让它只算一次。
            task.VolumePaths.Add(part1);

            TaskSpaceEstimate estimate = SpaceEstimator.FromSourceFiles(task);

            Assert.Equal(6144, estimate.SourceBytes);

            // 清单还没读 → 内容物是估算值，必须如实标注（不许假装精确）。
            Assert.True(estimate.ContentEstimated);
            Assert.False(estimate.HasListing);
            Assert.Equal(6144, estimate.ContentBytes);

            // 不是内嵌归档 → 过程物为 0（绝不凭空加一份）。
            Assert.Equal(0, estimate.ProcessArtifactBytes);
            Assert.Equal(6144 + 6144, estimate.PeakBytes);
        }

        [Fact]
        public void 粗估_内嵌归档抠出来的中间件计入过程物()
        {
            string file = CreateSizedFile("double.mp4", 10000);

            var task = new ArchiveTask(file, 1)
            {
                IsArchive = true,
                DetectedFormat = "ZIP",
                EmbeddedArchiveOffset = 4000
            };

            TaskSpaceEstimate estimate = SpaceEstimator.FromSourceFiles(task);

            Assert.Equal(10000, estimate.SourceBytes);

            // 抠出来的是 [4000, EOF) 那一段 = 6000 字节的副本（这是真实会多出来的一份）。
            Assert.Equal(6000, estimate.ProcessArtifactBytes);
            Assert.Equal(10000 + 10000 + 6000, estimate.PeakBytes);
        }

        [Fact]
        public void 精估_用清单里的真实解压后大小替换估算值()
        {
            string file = CreateSizedFile("data.7z", 1000);

            var task = new ArchiveTask(file, 1) { IsArchive = true, DetectedFormat = "7Z" };

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);

            var list = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                TotalUncompressedSize = 0,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "a.bin", Size = 3000 },
                    new() { Path = "b.bin", Size = 2000 }
                }
            };

            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(cheap, list);

            Assert.False(refined.ContentEstimated);
            Assert.True(refined.HasListing);
            Assert.Equal(5000, refined.ContentBytes);
            Assert.Equal(1000, refined.SourceBytes);

            // 没有内层包 → 过程物为 0。
            Assert.Equal(0, refined.ProcessArtifactBytes);
            Assert.Equal(6000, refined.PeakBytes);
        }

        [Fact]
        public void 精估_内层包只计再展开的增量_不重复计它自身()
        {
            string file = CreateSizedFile("outer.7z", 1000);

            var task = new ArchiveTask(file, 1) { IsArchive = true, DetectedFormat = "7Z" };

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);

            var list = new ArchiveListResult
            {
                Success = true,
                FileCount = 2,
                Entries = new List<ArchiveEntry>
                {
                    // 内层分卷（要再解一层）+ 一个普通文件
                    new() { Path = "inner.7z.001", Size = 2000 },
                    new() { Path = "readme.txt", Size = 500 }
                }
            };

            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(cheap, list);

            // 内容物 = 2500（内层包自身**在**里面）
            Assert.Equal(2500, refined.ContentBytes);

            // 过程物 = 内层包的**再展开增量**（2000 × 1.0），不是 2000 的重复计自身。
            Assert.Equal(2000, refined.ProcessArtifactBytes);

            // 峰值 = 源包 1000 + 过程物 2000 + 内容物 2500
            Assert.Equal(5500, refined.PeakBytes);
        }

        [Fact]
        public void 精估_清单拿不到时保留估算值并如实标注()
        {
            string file = CreateSizedFile("locked.7z", 800);

            var task = new ArchiveTask(file, 1) { IsArchive = true, DetectedFormat = "7Z" };

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);
            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(cheap, null, carvedBytes: 256);

            Assert.True(refined.ContentEstimated);
            Assert.False(refined.HasListing);
            Assert.Equal(800, refined.ContentBytes);

            // 已经量出来的内嵌中间件照记（那是真量出来的字节）。
            Assert.Equal(256, refined.ProcessArtifactBytes);
            Assert.Contains("没拿到条目清单", refined.Basis, StringComparison.Ordinal);
        }

        // ================================================================ ② 空间门

        [Fact]
        public void 空间门_不够就拦下_文案里必须同时有需要可用差三个数字()
        {
            SpaceGateDecision decision = SpaceGate.Check(
                requiredBytes: 32 * Gib,
                availableBytes: 10 * Gib,
                reserveBytes: 512L * 1024 * 1024);

            Assert.False(decision.Allowed);
            Assert.True(decision.ShortOfSpace);
            Assert.Equal(22 * Gib, decision.ShortfallBytes);

            Assert.Contains("磁盘空间不足", decision.Reason, StringComparison.Ordinal);
            Assert.Contains("这个任务需要", decision.Reason, StringComparison.Ordinal);
            Assert.Contains("可用", decision.Reason, StringComparison.Ordinal);
            Assert.Contains("差", decision.Reason, StringComparison.Ordinal);

            // 建议动作必须包含三种正路（清其余物 / 换盘 / 危险模式）。
            string suggestions = string.Join("|", decision.Suggestions);

            Assert.Contains("删除其余物", suggestions, StringComparison.Ordinal);
            Assert.Contains("指定位置", suggestions, StringComparison.Ordinal);
            Assert.Contains("激进模式", suggestions, StringComparison.Ordinal);
            Assert.Contains("并发", suggestions, StringComparison.Ordinal);
        }

        [Fact]
        public void 空间门_装得下但打完会低于保留余量_同样拦下()
        {
            SpaceGateDecision decision = SpaceGate.Check(
                requiredBytes: 9 * Gib,
                availableBytes: 10 * Gib,
                reserveBytes: 2 * Gib);

            Assert.False(decision.Allowed);
            Assert.Contains("低于要保留的", decision.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 空间门_够就放行_并且不把_够_写成任务结论()
        {
            SpaceGateDecision decision = SpaceGate.Check(2 * Gib, 10 * Gib, 512L * 1024 * 1024);

            Assert.True(decision.Allowed);
            Assert.Equal(0, decision.ShortfallBytes);
            Assert.Contains("空间门通过", decision.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 空间门_取不到可用空间时放行但明说_这不代表空间充足()
        {
            SpaceGateDecision decision = SpaceGate.Check(2 * Gib, null, 0);

            Assert.True(decision.Allowed);
            Assert.True(decision.ProbeFailed);
            Assert.Contains("这不代表空间充足", decision.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 空间门_已经在跑的任务占下的份额要算进去()
        {
            // 盘上还有 10G，但另一个任务已经许出去 8G：再来一个 4G 的必须被拦下。
            SpaceGateDecision decision = SpaceGate.Check(
                requiredBytes: 4 * Gib,
                availableBytes: 10 * Gib,
                reserveBytes: 0,
                alreadyReservedBytes: 8 * Gib);

            Assert.False(decision.Allowed);
            Assert.Equal(2 * Gib, decision.ShortfallBytes);
            Assert.Contains("已经在跑的任务占的", decision.Reason, StringComparison.Ordinal);
        }

        // ================================================================ ③ 预留账本（用户点名的反例）

        [Fact]
        public void 账本_10G可用时_5G与6G两个大包不许同时排上()
        {
            var ledger = new SpaceReservationLedger(10 * Gib, reserveBytes: 0);

            // 第一个（峰值 5G）放行并记账。
            SpaceGateDecision first = ledger.TryReserve(5 * Gib);
            Assert.True(first.Allowed);
            Assert.Equal(5 * Gib, ledger.ReservedBytes);

            // 第二个（峰值 6G）：5 + 6 = 11 > 10 → **必须拦下**，而且要算出差多少。
            SpaceGateDecision second = ledger.TryReserve(6 * Gib);
            Assert.False(second.Allowed);
            Assert.Equal(1 * Gib, second.ShortfallBytes);

            // 拦下时**不记账**（否则账面会凭空多出 6G，后面的判断全错）。
            Assert.Equal(5 * Gib, ledger.ReservedBytes);
        }

        [Fact]
        public void 账本_前一个任务跑完之后_后一个才排得上()
        {
            var ledger = new SpaceReservationLedger(10 * Gib, reserveBytes: 0);

            Assert.True(ledger.TryReserve(5 * Gib).Allowed);
            Assert.False(ledger.TryReserve(6 * Gib).Allowed);

            ledger.Release(5 * Gib);

            Assert.True(ledger.TryReserve(6 * Gib).Allowed);
            Assert.Equal(6 * Gib, ledger.ReservedBytes);
        }

        [Fact]
        public void 账本_危险模式回收的空间会出现在下一次判断里()
        {
            var ledger = new SpaceReservationLedger(10 * Gib, reserveBytes: 0);

            Assert.True(ledger.TryReserve(9 * Gib).Allowed);
            Assert.False(ledger.TryReserve(2 * Gib).Allowed);

            // 危险模式：任务成功后其余物被真的删掉，可用空间涨回来。
            ledger.RefreshAvailable(12 * Gib);

            Assert.True(ledger.TryReserve(2 * Gib).Allowed);
            Assert.Equal(11 * Gib, ledger.ReservedBytes);
            Assert.Equal(1 * Gib, ledger.RemainingBytes);
        }

        [Fact]
        public void 账本_取不到可用空间时不记账也不谎报够()
        {
            var ledger = new SpaceReservationLedger(null, reserveBytes: 0);

            SpaceGateDecision decision = ledger.TryReserve(5 * Gib);

            Assert.True(decision.Allowed);
            Assert.True(decision.ProbeFailed);
            Assert.Equal(0, ledger.ReservedBytes);
            Assert.Equal(SpaceReservationLedger.UnknownAvailable, ledger.RemainingBytes);
        }

        [Fact]
        public void 账本_开工后按精确清单调预留_不够就拦下()
        {
            var ledger = new SpaceReservationLedger(10 * Gib, reserveBytes: 0);

            // 开工时粗估 2G。
            Assert.True(ledger.TryReserve(2 * Gib).Allowed);
            Assert.Equal(2 * Gib, ledger.ReservedBytes);

            // 拿到清单发现要 6G：差额 4G 补得上 → 放行，预留涨到 6G。
            Assert.True(ledger.Adjust(2 * Gib, 6 * Gib).Allowed);
            Assert.Equal(6 * Gib, ledger.ReservedBytes);

            // 再涨到 12G：超出可用 → 拦下，**预留保持 6G**（不是被冲成 12G）。
            Assert.False(ledger.Adjust(6 * Gib, 12 * Gib).Allowed);
            Assert.Equal(6 * Gib, ledger.ReservedBytes);

            // 估大了要还回去。
            Assert.True(ledger.Adjust(6 * Gib, 1 * Gib).Allowed);
            Assert.Equal(1 * Gib, ledger.ReservedBytes);
        }

        // ================================================================ ④ 调度

        [Fact]
        public void 调度_按空间需求从小到大排_同需求按原顺序稳定()
        {
            List<ArchiveTask> tasks = new()
            {
                CreateLazyTask("big.7z", 6 * Gib),
                CreateLazyTask("small.7z", 1 * Gib),
                CreateLazyTask("middle.7z", 4 * Gib)
            };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                tasks,
                LazyEstimate,
                availableBytes: 100 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 2);

            Assert.Equal(
                new[] { "small.7z", "middle.7z", "big.7z" },
                plan.Ordered.Select(item => item.Task.FileName).ToArray());
        }

        [Fact]
        public void 调度_用户点名的反例_一开始绝不能排上5G加6G()
        {
            /*
             * 用户原话："而且一键解压要对内容进行排序解压，比如你一开始不能去解 5G+6G 的文件，
             * 这样并行两个都弄不了"。
             *
             * 场景照抄他给的那一组：总 20G 有 7 个（4/5/6/2/1/0.3/0.08 G），还剩 10G。
             */
            List<ArchiveTask> seven = new()
            {
                CreateLazyTask("a-4g.7z", 4 * Gib),
                CreateLazyTask("b-5g.7z", 5 * Gib),
                CreateLazyTask("c-6g.7z", 6 * Gib),
                CreateLazyTask("d-2g.7z", 2 * Gib),
                CreateLazyTask("e-1g.7z", 1 * Gib),
                CreateLazyTask("f-300m.7z", 300L * 1024 * 1024),
                CreateLazyTask("g-80m.7z", 80L * 1024 * 1024)
            };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                seven,
                LazyEstimate,
                availableBytes: 10 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 2);

            // 前两个必须是**最小的那两个**（80M / 300M），绝不能是 5G 与 6G。
            Assert.Equal("g-80m.7z", plan.Ordered[0].Task.FileName);
            Assert.Equal("f-300m.7z", plan.Ordered[1].Task.FileName);

            // 建议并行数按"这些任务同时达到峰值也不撑爆盘"来算：
            // 峰值 = 2× 源包 → 0.16 + 0.6 + 2 + 4 = 6.76G ≤ 10G；再加 8G 就超了 → 4 个。
            Assert.Equal(4, plan.RecommendedParallelCount);
            Assert.Contains("建议并行 4 个", plan.ParallelAdviceText(), StringComparison.Ordinal);

            // 这一组里真正"因为空间不足被跳过"的只有 6G 那个（峰值 12G > 可用 10G）——
            // 其余几个只是排队等前面的跑完，**不许**把它们报成跳过。
            Assert.Equal(
                new[] { "c-6g.7z" },
                plan.BlockedAtPlanTime.Select(item => item.Estimate.DisplayName).ToArray());

            Assert.Equal(2 * Gib, plan.BlockedAtPlanTime[0].ShortfallBytes);
        }

        [Fact]
        public void 调度_只有10G时16G那个包被拦下_并给出差多少()
        {
            /*
             * 用户原话的场景一："有多个文件总共 20G，有 4 个：16G、2G、300MB、100MB，而空余只有 10G…
             * 首先检测完要提示用户空间不足，这种情况就弄不了了（16G 那个是单独一个）"。
             */
            List<ArchiveTask> tasks = new()
            {
                CreateLazyTask("huge-16g.7z", 16 * Gib),
                CreateLazyTask("b-2g.7z", 2 * Gib),
                CreateLazyTask("c-300m.7z", 300L * 1024 * 1024),
                CreateLazyTask("d-100m.7z", 100L * 1024 * 1024)
            };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                tasks,
                LazyEstimate,
                availableBytes: 10 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 1);

            ScheduledExtractionItem huge = plan.Ordered.Single(item => item.Task.FileName == "huge-16g.7z");

            Assert.False(huge.FitsAtPlanTime);
            Assert.False(huge.FitsAlone);

            // 峰值 32G（源包 16G + 内容物按 1 倍估 16G），可用 10G → 差 22G。
            Assert.Equal(32 * Gib, huge.RequiredBytes);
            Assert.Equal(22 * Gib, huge.ShortfallBytes);

            ScheduledExtractionItem small = plan.Ordered.Single(item => item.Task.FileName == "d-100m.7z");
            Assert.True(small.FitsAtPlanTime);
            Assert.True(small.FitsAlone);

            // 只有 16G 那一个被跳过，其余照跑。
            Assert.Single(plan.BlockedAtPlanTime);
            Assert.Equal("huge-16g.7z", plan.BlockedAtPlanTime[0].Estimate.DisplayName);
        }

        [Fact]
        public void 调度_全部放不下时_建议并行数是0并且明说连最小的都放不下()
        {
            List<ArchiveTask> tasks = new()
            {
                CreateLazyTask("a.7z", 16 * Gib),
                CreateLazyTask("b.7z", 20 * Gib)
            };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                tasks,
                LazyEstimate,
                availableBytes: 1 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 8);

            Assert.Equal(0, plan.RecommendedParallelCount);
            Assert.Equal(2, plan.BlockedAtPlanTime.Count);
            Assert.Contains("连需求最小的那个包都放不下", plan.ParallelAdviceText(), StringComparison.Ordinal);
        }

        [Fact]
        public void 调度_取不到可用空间时不做判断_建议并行取最保守的1()
        {
            List<ArchiveTask> tasks = new() { CreateLazyTask("a.7z", 1 * Gib) };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                tasks,
                LazyEstimate,
                availableBytes: null,
                reserveBytes: 0,
                requestedParallelCount: 4);

            Assert.Empty(plan.BlockedAtPlanTime);
            Assert.Equal(1, plan.RecommendedParallelCount);
            Assert.Contains("未能取到目标盘可用空间", plan.ParallelAdviceText(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 4)]
        [InlineData(5, 8)]
        [InlineData(8, 8)]
        [InlineData(99, 8)]
        public void 并发档_只有1_2_3_4_8四个合法档_其余夹到最接近的更大档(int input, int expected)
        {
            Assert.Equal(expected, ExtractionScheduler.NormalizeParallelCount(input));
        }

        [Fact]
        public void 并发档_候选就是用户要求的那五个()
        {
            Assert.Equal(new[] { 1, 2, 3, 4, 8 }, ExtractionScheduler.AllowedParallelCounts.ToArray());
        }

        // ================================================================ ⑤ 危险模式：五条门槛

        [Fact]
        public void 危险模式_成功加校验通过_其余物被彻底删除并且不进回收站()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            var purger = new RestItemPurger();
            RestPurgeOutcome outcome = purger.Purge(task, cancelled: false);

            Assert.True(outcome.Attempted);
            Assert.True(outcome.Succeeded);
            Assert.False(Directory.Exists(restDirectory), "其余物目录必须被删掉");

            // 源包也一起没了（它在其余物里面）—— 这正是"净占用基本不变"的来源。
            Assert.False(File.Exists(sourcePath));
            Assert.True(outcome.FreedBytes > 0);

            // 删除日志必须带上路径与理由。
            Assert.NotEmpty(outcome.LogLines);
            Assert.Contains(
                outcome.LogLines,
                line => line.Contains("其余物", StringComparison.Ordinal) ||
                        line.Contains(RestItemPurger.DangerModeReason, StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(StatusText.ExtractFailed)]
        [InlineData(StatusText.PartiallyCompleted)]
        [InlineData(StatusText.Cancelled)]
        [InlineData(StatusText.Skipped)]
        [InlineData(StatusText.Extracting)]
        public void 危险模式_不是完整成功就一个字节都不删(string status)
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            task.Status = status;
            task.IsOutputVerified = false;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.False(outcome.Succeeded);
            Assert.True(Directory.Exists(restDirectory), $"终态是「{status}」时其余物必须原封不动");
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 危险模式_校验没通过就不删_哪怕状态是解压成功()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            task.Status = StatusText.ExtractSuccess;
            task.IsOutputVerified = false;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.Contains("输出校验", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 危险模式_被取消就不删()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: true);

            Assert.False(outcome.Attempted);
            Assert.Contains("已取消", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 危险模式_没记下本次的其余物路径时不敢猜_一个字节都不删()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            task.RestDirectoryPath = string.Empty;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.Contains("不敢猜路径", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 危险模式_其余物落在自己输出根之外时拒绝删除()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            // 把记录里的其余物改成"别人的目录"：必须在允许根校验处被拦下。
            string outsider = Path.Combine(_root, "别人的包", "其余物");
            Directory.CreateDirectory(outsider);
            File.WriteAllText(Path.Combine(outsider, "other.bin"), "别人的东西");

            task.RestDirectoryPath = outsider;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.Contains("已拒绝删除", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(outsider), "别人的其余物一个字节都不许动");
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 危险模式_形状不像其余物的目录一律拒绝()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            /**
             * 造一个"在自己输出根之内、但根本不是其余物"的目录：
             * 这正是最危险的错删形态（用户的内容物目录被当成其余物删掉）。
             */
            string contentDirectory = Path.Combine(task.OutputPath, "内容物");
            Directory.CreateDirectory(contentDirectory);
            File.WriteAllText(Path.Combine(contentDirectory, "keep.mp4"), "内容物");

            task.RestDirectoryPath = contentDirectory;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.Contains("形状不像", outcome.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(contentDirectory, "keep.mp4")));
            Assert.True(Directory.Exists(restDirectory));
        }

        [Fact]
        public void 危险模式_共用输出根的分层布局_其余物包名_同样认()
        {
            string outputRoot = Path.Combine(_root, "shared-out");
            string restDirectory = Path.Combine(outputRoot, "其余物", "222");
            Directory.CreateDirectory(restDirectory);

            string source = Path.Combine(restDirectory, "222.7z");
            File.WriteAllText(source, "源包");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputRoot,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,
                RestDirectoryPath = restDirectory
            };

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.True(outcome.Succeeded);
            Assert.False(Directory.Exists(restDirectory));

            // 共享根下"其余物"那一层还在（别的包的目录还在里面）—— 只删属于本任务的那一份。
            Assert.True(Directory.Exists(Path.Combine(outputRoot, "其余物")));
        }

        [Fact]
        public void 危险模式_共用输出根下只删自己那一份_别的包的目录不动()
        {
            string outputRoot = Path.Combine(_root, "shared-out2");
            string mine = Path.Combine(outputRoot, "其余物", "222");
            string other = Path.Combine(outputRoot, "其余物", "333");

            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(mine, "222.7z"), "我的源包");
            File.WriteAllText(Path.Combine(other, "333.7z"), "别人的源包");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputRoot,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,
                RestDirectoryPath = mine
            };

            Assert.True(new RestItemPurger().Purge(task, cancelled: false).Succeeded);

            Assert.False(Directory.Exists(mine));
            Assert.True(File.Exists(Path.Combine(other, "333.7z")), "别的包的其余物一个字节都不许动");
        }

        // ================================================================ ⑥ 自测协议

        [Fact]
        public void 自测_样本量就是并发数的两倍()
        {
            Assert.Equal(2, DangerModeSelfTestProtocol.RequiredSampleSize(1));
            Assert.Equal(4, DangerModeSelfTestProtocol.RequiredSampleSize(2));
            Assert.Equal(8, DangerModeSelfTestProtocol.RequiredSampleSize(4));
            Assert.Equal(10, DangerModeSelfTestProtocol.RequiredSampleSize(5));
            Assert.Equal(16, DangerModeSelfTestProtocol.RequiredSampleSize(8));
        }

        [Fact]
        public void 自测_挑样本从最小的开始_并排除解不了的包()
        {
            var usableSmall = CreateTask("small.7z", 1 * 1024 * 1024);
            var usableBig = CreateTask("big.7z", 8 * 1024 * 1024);

            var unknown = CreateTask("unknown.xyz", 512);
            unknown.IsArchive = false;
            unknown.DetectedFormat = "Unknown";

            var missingVolume = CreateTask("broken.7z.001", 256);
            missingVolume.IsVolumeGroup = true;
            missingVolume.IsVolumeComplete = false;

            List<ArchiveTask> samples = DangerModeSelfTestProtocol
                .SelectSamples(new[] { usableBig, unknown, usableSmall, missingVolume }, requiredSampleSize: 2)
                .ToList();

            Assert.Equal(2, samples.Count);
            Assert.Equal("small.7z", samples[0].FileName);
            Assert.Equal("big.7z", samples[1].FileName);
        }

        [Fact]
        public void 自测_四条判据全过才通过_并且说出用户可以一试但风险还在()
        {
            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(GoodEvidence());

            Assert.True(verdict.Passed);
            Assert.Empty(verdict.FailureReasons);
            Assert.Contains("可以一试，但风险还是有的", verdict.Summary, StringComparison.Ordinal);
            Assert.Contains("源包已被永久删除", verdict.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public void 自测_样本不够就不通过_并且说清要几个()
        {
            DangerModeSelfTestEvidence evidence = GoodEvidence();
            evidence = new DangerModeSelfTestEvidence
            {
                ParallelCount = evidence.ParallelCount,
                RequiredSampleSize = evidence.RequiredSampleSize,
                Steps = evidence.Steps.Take(1).ToList()
            };

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(evidence);

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains("实际只跑了 1 个", StringComparison.Ordinal));
        }

        [Fact]
        public void 自测_有一个文件校验没通过_就拒绝开启并点名那个文件()
        {
            DangerModeSelfTestEvidence evidence = GoodEvidence();

            List<DangerModeSelfTestStep> steps = evidence.Steps.ToList();
            steps[1] = new DangerModeSelfTestStep
            {
                DisplayName = steps[1].DisplayName,
                ExtractSucceeded = true,
                Verified = false,
                RestPurged = true,
                PurgedBytes = steps[1].PurgedBytes,
                AvailableBeforeStart = steps[1].AvailableBeforeStart,
                AvailableAfterFinish = steps[1].AvailableAfterFinish,
                ContentBytes = steps[1].ContentBytes
            };

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
            {
                ParallelCount = evidence.ParallelCount,
                RequiredSampleSize = evidence.RequiredSampleSize,
                Steps = steps
            });

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains(steps[1].DisplayName, StringComparison.Ordinal) &&
                          reason.Contains("输出校验", StringComparison.Ordinal));
        }

        [Fact]
        public void 自测_其余物没被删就拒绝开启()
        {
            DangerModeSelfTestEvidence evidence = GoodEvidence();

            List<DangerModeSelfTestStep> steps = evidence.Steps
                .Select((step, index) => index == 0
                    ? new DangerModeSelfTestStep
                    {
                        DisplayName = step.DisplayName,
                        ExtractSucceeded = true,
                        Verified = true,
                        RestPurged = false,
                        AvailableBeforeStart = step.AvailableBeforeStart,
                        AvailableAfterFinish = step.AvailableAfterFinish,
                        ContentBytes = step.ContentBytes
                    }
                    : step)
                .ToList();

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
            {
                ParallelCount = evidence.ParallelCount,
                RequiredSampleSize = evidence.RequiredSampleSize,
                Steps = steps
            });

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains("其余物没有被彻底删除", StringComparison.Ordinal));
        }

        [Fact]
        public void 自测_空间曲线不符合预期就拒绝开启()
        {
            /*
             * 这一条检的是"净占用基本不变"这句承诺：源包与中间件没被真正回收时，
             * 可用空间会掉下大约一整个峰值需求的量 —— 必须判不通过。
             */
            long content = 100L * 1024 * 1024;
            long purged = 100L * 1024 * 1024;

            var step = new DangerModeSelfTestStep
            {
                DisplayName = "a.7z",
                ExtractSucceeded = true,
                Verified = true,
                RestPurged = true,
                PurgedBytes = purged,

                // 开始 10G、收尾只剩 9G：净消耗 1G 远超"内容物 − 回收 + 容差"。
                AvailableBeforeStart = 10 * Gib,
                AvailableAfterFinish = 9 * Gib,
                ContentBytes = content
            };

            var steps = new List<DangerModeSelfTestStep>();
            steps.AddRange(Enumerable.Repeat(step, 2));

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
            {
                ParallelCount = 1,
                RequiredSampleSize = 2,
                Steps = steps
            });

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains("空间曲线不符合预期", StringComparison.Ordinal));
        }

        [Fact]
        public void 自测_取不到可用空间时不算通过_验不了就是验不了()
        {
            DangerModeSelfTestEvidence evidence = GoodEvidence();

            List<DangerModeSelfTestStep> steps = evidence.Steps
                .Select(step => new DangerModeSelfTestStep
                {
                    DisplayName = step.DisplayName,
                    ExtractSucceeded = true,
                    Verified = true,
                    RestPurged = true,
                    PurgedBytes = step.PurgedBytes,
                    AvailableBeforeStart = -1,
                    AvailableAfterFinish = -1,
                    ContentBytes = step.ContentBytes
                })
                .ToList();

            DangerModeSelfTestVerdict verdict = DangerModeSelfTestProtocol.Evaluate(new DangerModeSelfTestEvidence
            {
                ParallelCount = evidence.ParallelCount,
                RequiredSampleSize = evidence.RequiredSampleSize,
                Steps = steps
            });

            Assert.False(verdict.Passed);
            Assert.Contains(
                verdict.FailureReasons,
                reason => reason.Contains("空间曲线无法验证", StringComparison.Ordinal));
        }

        [Fact]
        public void 自测_源包留在原地时直接拒绝_因为它省不出源包那一份空间()
        {
            AppSettings settings = AppSettings.CreateDefault();
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            Assert.False(DangerModeSelfTestProtocol.CheckPreconditions(settings, out string reason));
            Assert.Contains("留在原地", reason, StringComparison.Ordinal);

            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);

            Assert.True(DangerModeSelfTestProtocol.CheckPreconditions(settings, out _));
        }

        // ================================================================ ⑦ 自测凭证

        [Fact]
        public void 凭证_没自测过的空凭证一律不算数()
        {
            Assert.False(DangerModeSelfTestStamp.IsValid(null));
            Assert.False(DangerModeSelfTestStamp.IsValid(string.Empty));
            Assert.False(DangerModeSelfTestStamp.IsValid("   "));
            Assert.False(DangerModeSelfTestStamp.IsValid("通过"));
            Assert.False(DangerModeSelfTestStamp.IsValid("自测没通过"));
        }

        [Fact]
        public void 凭证_由结论造出来之后能读出并发与样本数()
        {
            var verdict = new DangerModeSelfTestVerdict
            {
                Passed = true,
                ParallelCount = 5,
                SampleSize = 10
            };

            string stamp = DangerModeSelfTestStamp.Create(verdict, new DateTime(2026, 9, 22, 21, 30, 0));

            Assert.True(DangerModeSelfTestStamp.IsValid(stamp));
            Assert.Equal(5, DangerModeSelfTestStamp.ParseParallelCount(stamp));
            Assert.Equal(10, DangerModeSelfTestStamp.ParseSampleSize(stamp));
            Assert.Contains("2026-09-22 21:30:00", stamp, StringComparison.Ordinal);
        }

        // ================================================================ ⑧ 设置项

        [Fact]
        public void 设置_危险模式默认关_而且没有凭证时开不起来()
        {
            Assert.False(new AppSettings().DangerousSpaceModeEnabled);
            Assert.False(AppSettings.CreateDefault().DangerousSpaceModeEnabled);

            // 手改配置文件把开关写成 true：Normalize 必须把它关回去（这就是自测协议的自动执行点）。
            var hacked = new AppSettings
            {
                DangerousSpaceModeEnabled = true,
                DangerModeSelfTestStamp = string.Empty
            };

            hacked.Normalize();

            Assert.False(hacked.DangerousSpaceModeEnabled);

            // 写坏了的凭证同样不算数。
            hacked.DangerousSpaceModeEnabled = true;
            hacked.DangerModeSelfTestStamp = "我手动写的通过";
            hacked.Normalize();

            Assert.False(hacked.DangerousSpaceModeEnabled);
        }

        [Fact]
        public void 设置_有凭证时开关保持打开_关掉时凭证不清()
        {
            var settings = new AppSettings
            {
                DangerousSpaceModeEnabled = true,
                DangerModeSelfTestStamp = DangerModeSelfTestStamp.Create(
                    new DangerModeSelfTestVerdict { Passed = true, ParallelCount = 2, SampleSize = 4 },
                    DateTime.Now)
            };

            settings.Normalize();
            Assert.True(settings.DangerousSpaceModeEnabled);

            settings.DangerousSpaceModeEnabled = false;
            settings.Normalize();

            // 凭证保留：它记录的是"这台机器上做过自测"这个事实，清掉只会逼用户再删一批源包。
            Assert.True(DangerModeSelfTestStamp.IsValid(settings.DangerModeSelfTestStamp));
        }

        [Fact]
        public void 设置_改并发档不会顺手清掉自测凭证()
        {
            var settings = new AppSettings
            {
                MaxParallelExtractCount = 2,
                DangerousSpaceModeEnabled = true,
                DangerModeSelfTestStamp = DangerModeSelfTestStamp.Create(
                    new DangerModeSelfTestVerdict { Passed = true, ParallelCount = 2, SampleSize = 4 },
                    DateTime.Now)
            };

            settings.MaxParallelExtractCount = 8;
            settings.Normalize();

            Assert.Equal(8, settings.MaxParallelExtractCount);
            Assert.True(settings.DangerousSpaceModeEnabled);
            Assert.Equal(2, DangerModeSelfTestStamp.ParseParallelCount(settings.DangerModeSelfTestStamp));
        }

        [Fact]
        public void 凭证覆盖判定_盖得住才生效_而且调小算盖得住()
        {
            /*
             * "自测凭证只覆盖它跑过的那一档"：协议是"拿并发数 × 2 个文件真跑一遍"，
             * 所以凭证不能替更高的档位背书（那等于没测过就用，代价是源包永久删除）。
             * 单调方向是刻意的：调小并发只会更安全，不该逼用户再删一批样本源包。
             */
            string stamp4 = DangerModeSelfTestStamp.Create(
                new DangerModeSelfTestVerdict { Passed = true, ParallelCount = 4, SampleSize = 8 },
                DateTime.Now);

            Assert.True(DangerModeSelfTestStamp.Covers(stamp4, 1));
            Assert.True(DangerModeSelfTestStamp.Covers(stamp4, 4));
            Assert.False(DangerModeSelfTestStamp.Covers(stamp4, 8));

            // 没有凭证 / 写坏的凭证：一律盖不住。
            Assert.False(DangerModeSelfTestStamp.Covers(string.Empty, 1));
            Assert.False(DangerModeSelfTestStamp.Covers("我手动写的通过", 1));

            // 样本数不够 2× 并发也不算数（"并发=8|样本=2"是手改得出来的）。
            string thin = DangerModeSelfTestStamp.Create(
                new DangerModeSelfTestVerdict { Passed = true, ParallelCount = 8, SampleSize = 2 },
                DateTime.Now);

            Assert.False(DangerModeSelfTestStamp.Covers(thin, 4));

            // 盖不住时那句话必须带"怎么办"，不然用户只能干看着。
            string why = DangerModeSelfTestStamp.DescribeCoverage(stamp4, 8);

            Assert.Contains("并发 4", why, StringComparison.Ordinal);
            Assert.Contains("重新自测", why, StringComparison.Ordinal);
            Assert.Equal(string.Empty, DangerModeSelfTestStamp.DescribeCoverage(stamp4, 4));
        }

        [Fact]
        public void 设置_两个新键能落盘并在重启后读回来()
        {
            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var service = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.DangerousSpaceModeEnabled = true;
            settings.DangerModeSelfTestStamp = DangerModeSelfTestStamp.Create(
                new DangerModeSelfTestVerdict { Passed = true, ParallelCount = 4, SampleSize = 8 },
                DateTime.Now);

            service.Save(settings);

            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = dataRoot }).Load();

            Assert.True(reloaded.DangerousSpaceModeEnabled);
            Assert.Equal(4, DangerModeSelfTestStamp.ParseParallelCount(reloaded.DangerModeSelfTestStamp));
            Assert.Equal(8, DangerModeSelfTestStamp.ParseSampleSize(reloaded.DangerModeSelfTestStamp));
        }

        // ================================================================ ⑨ 状态三处同改

        [Fact]
        public void 新状态_磁盘空间不足_在配色与统计两处都按失败口径处理()
        {
            // ① 文案常量存在且是中文（引用的地方不许再手写）。
            Assert.Equal("磁盘空间不足", StatusText.DiskSpaceInsufficient);

            // ② 配色：错误色（与"解压失败"同一档）。
            var converter = new ArchiveFixer.Converters.StatusToBrushConverter();
            object brush = converter.Convert(
                StatusText.DiskSpaceInsufficient,
                typeof(object),
                null!,
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.Same(converter.ErrorBrush, brush);

            // ③ 统计：进"解压失败"桶，并且出现在失败清单里（不许静默消失）。
            var task = new ArchiveTask("x.7z", 1)
            {
                Status = StatusText.DiskSpaceInsufficient,
                ErrorMessage = "磁盘空间不足：需要 32 GiB，目标盘可用 10 GiB，差 22 GiB"
            };

            Assert.Equal(SummaryBucket.ExtractFailed, TaskSummaryService.ClassifyOutcome(task));

            var summary = new TaskSummaryService();

            Assert.True(summary.IsFailedStatus(StatusText.DiskSpaceInsufficient));
            Assert.True(summary.IsFailedOrUnknownTask(task));

            string failedList = summary.BuildFailedListText(new[] { task });

            Assert.Contains("磁盘空间不足", failedList, StringComparison.Ordinal);
            Assert.Contains("差 22 GiB", failedList, StringComparison.Ordinal);
        }

        // ================================================================ ⑩ 文案不许分叉

        [Fact]
        public void 风险四条_确认框设置界面与文档用的是同一份措辞()
        {
            /*
             * 用户 2026-09-22 的原话是"我们要对用户**详细描述**一下"。
             * 这里的判据不是"某处写了就算"：同一份风险必须同时出现在**三个地方**，
             * 而且措辞要对得上 —— 改一处漏两处，用户拿不可逆操作换来的知情权就缺一块。
             */
            Assert.Equal(4, StatusText.DangerModeRiskLines.Length);

            string settingsXaml = ReadRepositoryFile("ArchiveFixer", "Views", "SettingsWindow.xaml");
            string mainXaml = ReadRepositoryFile("ArchiveFixer", "MainWindow.xaml");
            string usage = ReadRepositoryFile("docs", "使用说明.md");
            string mainViewModel = ReadRepositoryFile("ArchiveFixer", "ViewModels", "MainViewModel.cs");

            foreach (string risk in StatusText.DangerModeRiskLines)
            {
                // ① 设置界面逐条列出（XAML 里拿不到数组，所以那里是同一份文本的副本）。
                Assert.Contains(risk, settingsXaml, StringComparison.Ordinal);

                // ② 主界面**引用**同一份（不重抄一遍，所以这里查的是绑定名，不是文本）。
                Assert.Contains("DangerModeRiskLines", mainXaml, StringComparison.Ordinal);
            }

            // ③ 使用说明逐条列出（文档里有 markdown 加粗与序号，所以比"关键句"而不是整句）。
            string[] keyPhrases =
            {
                "源包会被永久删除",
                "校验不等于你确认过内容",
                "源包已删、内容物未完成",
                "只在你确实没有空间时才用它"
            };

            foreach (string phrase in keyPhrases)
            {
                Assert.Contains(phrase, usage, StringComparison.Ordinal);
            }

            // 自测协议那两句也必须在设置界面与文档里都有落点。
            // 设置界面这里是**引用**（x:Static），不是抄一遍 —— 所以查的是引用名。
            Assert.Contains("StatusText.DangerModeSelfTestWarning", settingsXaml, StringComparison.Ordinal);
            Assert.Contains("并发数 × 2", usage, StringComparison.Ordinal);
            Assert.Contains("可以一试，但风险还是有的", usage, StringComparison.Ordinal);

            // 确认框正文必须把风险四条拼进去（只看调用点：MainViewModel 是唯一弹它的地方）。
            Assert.Contains("StatusText.DangerModeRisks", mainViewModel, StringComparison.Ordinal);
            Assert.Contains("StatusText.DangerModeSelfTestProtocolText", mainViewModel, StringComparison.Ordinal);
            Assert.Contains("StatusText.DangerModeSelfTestWarning", mainViewModel, StringComparison.Ordinal);
        }

        [Fact]
        public void 风险四条_顺序与内容就是用户要求的那四条()
        {
            // 顺序不要重排：它就是用户给的顺序，读起来是一条从"最不可逆"到"什么时候才该用"的链。
            Assert.Contains("源包会被永久删除", StatusText.DangerModeRiskLines[0], StringComparison.Ordinal);
            Assert.Contains("校验不等于你确认过内容", StatusText.DangerModeRiskLines[1], StringComparison.Ordinal);
            Assert.Contains("源包已删、内容物未完成", StatusText.DangerModeRiskLines[2], StringComparison.Ordinal);
            Assert.Contains("只在你确实没有空间时才用它", StatusText.DangerModeRiskLines[3], StringComparison.Ordinal);
        }

        [Fact]
        public void 危险模式的界面入口_是个红色按钮并且写着风险()
        {
            string mainXaml = ReadRepositoryFile("ArchiveFixer", "MainWindow.xaml");

            // 红色（DangerButtonStyle 是实心红那一档）。
            Assert.Contains("DangerButtonStyle", mainXaml, StringComparison.Ordinal);
            Assert.Contains("ToggleDangerModeCommand", mainXaml, StringComparison.Ordinal);

            // 并发档与"按空间算建议"也在界面上（用户要求"算出并显示最多能并行几个"）。
            Assert.Contains("MaxParallelChoices", mainXaml, StringComparison.Ordinal);
            Assert.Contains("ParallelAdviceText", mainXaml, StringComparison.Ordinal);
            Assert.Contains("RefreshParallelAdviceCommand", mainXaml, StringComparison.Ordinal);

            // 开启时那条常驻红横幅（风险一直摆在界面上，而不是只在弹窗里出现一次）。
            Assert.Contains("DangerModeEnabled", mainXaml, StringComparison.Ordinal);

            // 横幅里的四条是**绑定**过来的（主 ViewModel 引用 StatusText 那一份，不重抄）。
            string mainViewModel = ReadRepositoryFile("ArchiveFixer", "ViewModels", "MainViewModel.cs");

            Assert.Contains(
                "IReadOnlyList<string> DangerModeRiskLines => StatusText.DangerModeRiskLines;",
                mainViewModel.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("  ", " "),
                StringComparison.Ordinal);
        }

        private static string ReadRepositoryFile(params string[] parts)
        {
            string path = XamlBindingScan.RepositoryRoot;

            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }

            Assert.True(File.Exists(path), $"读不到文件（清单过时了？）：{path}");

            return File.ReadAllText(path);
        }

        // ================================================================ 装配

        /// <summary>
        /// 调度测试用的任务：**不造真实的大文件**。
        ///
        /// <para>为什么不造：跑测试的机器上未必有 16 GiB 空闲（NTFS 的 <c>SetLength</c> 会真的分配），
        /// 造一个 16G 的样本既慢又会因为盘满而失败 —— 那样测的是这台机器的磁盘，不是调度逻辑。
        /// 所以调度测试统一用 <see cref="LazyEstimate"/> 喂合成需求；
        /// 真正需要真文件的只有 <see cref="SpaceEstimator"/> 自己的那几条（用几 KiB 的样本就够）。</para>
        /// </summary>
        private readonly Dictionary<string, long> _lazySizes = new(StringComparer.OrdinalIgnoreCase);

        private ArchiveTask CreateLazyTask(string fileName, long size)
        {
            _lazySizes[fileName] = size;

            return new ArchiveTask(Path.Combine(_root, "src", fileName), 1)
            {
                FileName = fileName,
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };
        }

        /// <summary>合成估算：源包 = 声明的体积，内容物按 1 倍估（与粗估同一套口径）。</summary>
        private TaskSpaceEstimate LazyEstimate(ArchiveTask task)
        {
            long size = _lazySizes.TryGetValue(task.FileName, out long declared) ? declared : 0L;

            return new TaskSpaceEstimate
            {
                TaskPath = task.CurrentPath,
                DisplayName = task.FileName,
                SourceBytes = size,
                ContentBytes = size,
                ContentEstimated = true,
                Basis = "测试用的合成估算"
            };
        }

        private ArchiveTask CreateTask(string fileName, long size)
        {
            string path = CreateSizedFile(fileName, size);

            return new ArchiveTask(path, 1)
            {
                FileName = fileName,
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };
        }

        private string CreateSizedFile(string fileName, long size)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);

            using FileStream stream = File.Create(path);

            // 用 SetLength 造稀疏文件：几 GiB 的样本也不会真的占盘。
            stream.SetLength(size);

            return path;
        }

        /// <summary>造一个"危险模式该删"的现场：成功 + 校验通过 + 其余物里有源包。</summary>
        private (ArchiveTask Task, string SourcePath, string RestDirectory) CreatePurgeScenario()
        {
            string outputPath = Path.Combine(_root, "out", "222");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(restDirectory);

            string sourcePath = Path.Combine(restDirectory, "222.7z");
            File.WriteAllText(sourcePath, "源包（会被永久删除）");
            File.WriteAllText(Path.Combine(restDirectory, "inner-222.7z.001"), "内层分卷（中间件）");

            // 内容物也要在位：删除的判据是"内容物已定稿"，这个现场得有内容物。
            File.WriteAllText(Path.Combine(outputPath, "payload.mp4"), "内容物");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputPath,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "222"
            };

            return (task, sourcePath, restDirectory);
        }

        /// <summary>一份"全过"的自测证据（并发 2 → 4 个样本）。</summary>
        private static DangerModeSelfTestEvidence GoodEvidence()
        {
            long content = 100L * 1024 * 1024;
            long purged = 120L * 1024 * 1024;

            var steps = new List<DangerModeSelfTestStep>();

            for (int i = 0; i < 4; i++)
            {
                steps.Add(new DangerModeSelfTestStep
                {
                    DisplayName = $"sample-{i}.7z",
                    TaskPath = Path.Combine("src", $"sample-{i}.7z"),
                    ExtractSucceeded = true,
                    Verified = true,
                    RestPurged = true,
                    PurgedBytes = purged,

                    // 危险模式的预期曲线：净消耗 ≈ 内容物 − 回收 ≈ 0。
                    AvailableBeforeStart = 10 * Gib,
                    AvailableAfterFinish = 10 * Gib,
                    RequiredBytes = content + purged,
                    ContentBytes = content
                });
            }

            return new DangerModeSelfTestEvidence
            {
                ParallelCount = 2,
                RequiredSampleSize = 4,
                Steps = steps
            };
        }
    }
}
