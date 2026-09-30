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
    /// 「空间规划 + 其余物处理（删除操作）」的**纯模型**测试（用户 2026-09-22 需求第 1 / 2 条；2026-09-25 第 32 条改口径）。
    ///
    /// <para>这一组不碰 WPF、不跑管线，只钉三件事：</para>
    /// <list type="number">
    /// <item><description><b>空间核算口径</b>：峰值 = 源包（分卷整组去重）+ 内容物 + 过程物额外增量，
    /// 够不够判在 <see cref="SpaceGate"/>；</description></item>
    /// <item><description><b>调度</b>：按需求从小到大排 + "装不下就跳过、绝不静默" + "5G 与 6G 不许一起上"
    /// （用户点名的反例）；</description></item>
    /// <item><description><b>其余物处理的五条门槛</b>：只有「完成 + 校验通过 + 未取消 + 路径是记下来的那一条 + 在自己输出根之内」才动手（彻底删除与移入回收站共用）。</description></item>
    /// </list>
    ///
    /// <para>端到端（真跑一遍解压管线、真的删掉其余物）在 <c>SourceDeleteAfterVerifyTests</c> 里。</para>
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

            // 「彻底删除」能立刻收回的正好是"任务完成前一直占着"的那一份（源包 + 过程物）。
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
        public void 粗估_分卷整组求和且按路径去重_内嵌归档过程物单独计()
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
        public void 粗估_内嵌归档抠出来的过程物计入过程物()
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

            // 已经量出来的内嵌过程物照记（那是真量出来的字节）。
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

            // 建议动作必须包含三种正路（清其余物 / 换盘 / 开①页「空间不足」模式）。
            string suggestions = string.Join("|", decision.Suggestions);

            Assert.Contains("删除其余物", suggestions, StringComparison.Ordinal);
            Assert.Contains("指定位置", suggestions, StringComparison.Ordinal);
            Assert.Contains("空间不足", suggestions, StringComparison.Ordinal);
            Assert.Contains("并发", suggestions, StringComparison.Ordinal);

            /*
             * ⛔ 退役文案不许回潮（2026-09-27）：那条建议以前写的是"改用激进模式 + 必须先跑自测"，
             * 而"危险模式 / 自测凭证"整套机理早已被用户删掉（第 32 条）——
             * 界面还在教用户去开一个不存在的东西，就是误导。
             */
            Assert.DoesNotContain("激进模式", suggestions, StringComparison.Ordinal);
            Assert.DoesNotContain("自测", suggestions, StringComparison.Ordinal);
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
        public void 账本_其余物彻底删除回收的空间会出现在下一次判断里()
        {
            var ledger = new SpaceReservationLedger(10 * Gib, reserveBytes: 0);

            Assert.True(ledger.TryReserve(9 * Gib).Allowed);
            Assert.False(ledger.TryReserve(2 * Gib).Allowed);

            // 彻底删除：任务成功后其余物被真的删掉，可用空间涨回来。
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

            /*
             * 建议并行数按"这些任务同时达到各自的需求也不撑爆盘"来算：
             * 需求 = 本次要从可用空间里新写多少（清单没读时 = 内容物 = 源包 1 倍估）→
             * 0.08 + 0.3 + 1 + 2 + 4 = 7.38G ≤ 10G；再加 5G 那个就超了 → **5 个**。
             *
             * ⚠ 2026-09-29 改口径前这里是 4 个：老口径把**源包**也加进需求（峰值 = 2× 源包），
             * 等于把已经在盘上、不在"可用"里的那 17.7 GiB 又算了一遍 —— 真机上就是这么被误拦的。
             */
            Assert.Equal(5, plan.RecommendedParallelCount);
            Assert.Contains("建议并行 5 个", plan.ParallelAdviceText(), StringComparison.Ordinal);

            /*
             * 这一组里**没有一个**包"单独跑也放不下"（需求最大的 6G ≤ 可用 10G）——
             * 它们只是排队等前面的跑完，**不许**把任何一个报成跳过。
             */
            Assert.Empty(plan.BlockedAtPlanTime);
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

            // 需求 = 内容物（源包 16G 按 1 倍估 = 16G；源包本身**不算**进需求），可用 10G → 差 6G。
            Assert.Equal(16 * Gib, huge.RequiredBytes);
            Assert.Equal(6 * Gib, huge.ShortfallBytes);

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

        // ================================================================ ⑤ 其余物处理：五条门槛（彻底删除 / 移入回收站共用）

        [Fact]
        public void 彻底删除_成功加校验通过_其余物被彻底删除并且不进回收站()
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
                        line.Contains(RestItemPurger.AutoPurgeReason, StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(StatusText.ExtractFailed)]
        [InlineData(StatusText.PartiallyCompleted)]
        [InlineData(StatusText.Cancelled)]
        [InlineData(StatusText.Skipped)]
        [InlineData(StatusText.Extracting)]
        public void 彻底删除_不是完整成功就一个字节都不删(string status)
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
        public void 彻底删除_判不出完整性就不删_哪怕状态是解压成功()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            task.Status = StatusText.ExtractSuccess;
            task.IsOutputVerified = true;

            // L4：校验写着通过，但**没有核对过清单**（拿不到可信清单，只做了非空底线校验）⇒ 判不出。
            task.OutputManifestCrossChecked = false;
            task.IsOutputVerified = true;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.False(outcome.Attempted);
            Assert.Contains("无法确认", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 彻底删除_被取消就不删()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreatePurgeScenario();

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: true);

            Assert.False(outcome.Attempted);
            Assert.Contains("已取消", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory));
            Assert.True(File.Exists(sourcePath));
        }

        [Fact]
        public void 彻底删除_没记下本次的其余物路径时不敢猜_一个字节都不删()
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
        public void 彻底删除_其余物落在自己输出根之外时拒绝删除()
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
        public void 彻底删除_形状不像其余物的目录一律拒绝()
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
        public void 彻底删除_共用输出根的分层布局_其余物包名_同样认()
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

                // L4：只有"拿清单核对过"才算可证完整，也才允许删源包（见 ResultCompletenessTests）。
                OutputManifestCrossChecked = true,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = restDirectory
            };

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false);

            Assert.True(outcome.Succeeded);
            Assert.False(Directory.Exists(restDirectory));

            // 共享根下"其余物"那一层还在（别的包的目录还在里面）—— 只删属于本任务的那一份。
            Assert.True(Directory.Exists(Path.Combine(outputRoot, "其余物")));
        }

        [Fact]
        public void 彻底删除_共用输出根下只删自己那一份_别的包的目录不动()
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

                // L4：只有"拿清单核对过"才算可证完整，也才允许删源包（见 ResultCompletenessTests）。
                OutputManifestCrossChecked = true,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = mine
            };

            Assert.True(new RestItemPurger().Purge(task, cancelled: false).Succeeded);

            Assert.False(Directory.Exists(mine));
            Assert.True(File.Exists(Path.Combine(other, "333.7z")), "别的包的其余物一个字节都不许动");
        }

        // ================================================================ ⑥ 删除操作三档（用户 2026-09-25 第 32 条）

        /// <summary>
        /// 新设置项 <c>RestHandlingAfterVerify</c>：默认**不动其余物**，三档都能落盘读回，
        /// 认不出的值一律回落 "Keep"（保守方向：宁可不动，绝不误删）。
        /// </summary>
        [Fact]
        public void 删除操作_默认不动其余物_三档都能落盘读回()
        {
            Assert.Equal(RestHandlingModes.Keep, AppSettings.CreateDefault().RestHandlingAfterVerify);

            Assert.Equal(RestHandlingModes.Keep, RestHandlingModes.Normalize(null));
            Assert.Equal(RestHandlingModes.Keep, RestHandlingModes.Normalize("  "));
            Assert.Equal(RestHandlingModes.Keep, RestHandlingModes.Normalize("什么鬼"));
            Assert.Equal(RestHandlingModes.RecycleBin, RestHandlingModes.Normalize("recyclebin"));
            Assert.Equal(RestHandlingModes.Delete, RestHandlingModes.Normalize("DELETE"));

            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var service = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            service.Save(settings);

            AppSettings reloaded = new SettingsService(new PathService { DataRootDirectory = dataRoot }).Load();

            Assert.Equal(RestHandlingModes.Delete, reloaded.RestHandlingAfterVerify);
        }

        /// <summary>
        /// 退役迁移（第 32 条）：旧配置里开过危险模式 → 一次性迁移成「彻底删除」，并把旧字段清干净；
        /// 旧的手动删源开关 / 旧的「校验通过后删除」档 → "源包移入其余物 + 移入回收站"（可还原的那一档，不替用户重做不可逆选择）。
        /// </summary>
        [Fact]
        public void 旧危险模式与旧删源档_一次性迁移成新的两档()
        {
            AppSettings danger = AppSettings.CreateDefault();
            danger.DangerousSpaceModeEnabled = true;
            danger.DangerModeSelfTestStamp = "自测通过|2026-09-22 21:30:00|并发=2|样本=4";
            danger.SourceHandling = nameof(SourceHandlingMode.MoveToRest);

            danger.Normalize();

            Assert.Equal(RestHandlingModes.Delete, danger.RestHandlingAfterVerify);
            Assert.False(danger.DangerousSpaceModeEnabled);
            Assert.Equal(string.Empty, danger.DangerModeSelfTestStamp);

            AppSettings manualDelete = AppSettings.CreateDefault();
            manualDelete.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            manualDelete.DeleteSourceAfterExtract = true;

            manualDelete.Normalize();

            Assert.Equal(nameof(SourceHandlingMode.MoveToRest), manualDelete.SourceHandling);
            Assert.Equal(RestHandlingModes.RecycleBin, manualDelete.RestHandlingAfterVerify);
            Assert.False(manualDelete.DeleteSourceAfterExtract);

            AppSettings legacySource = AppSettings.CreateDefault();
            legacySource.SourceHandling = "DeleteAfterVerify";

            legacySource.Normalize();

            Assert.Equal(nameof(SourceHandlingMode.MoveToRest), legacySource.SourceHandling);
            Assert.Equal(RestHandlingModes.RecycleBin, legacySource.RestHandlingAfterVerify);
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
        // ================================================================ ⑩ 界面：三档在③页，②页不再有红区

        /// <summary>
        /// 用户 2026-09-25 第 32 条定的界面形态：
        /// ③页 = 「1 源包操作」（两个单选）+「2 删除操作」（三个单选，第三档整条红字 +
        /// 选中时下面**常驻**一条不可关闭的提示）；②页那个「高风险区（危险模式）」整块没有了。
        /// </summary>
        [Fact]
        public void 界面_三档删除操作在清理页_解压方式页不再有危险模式红区()
        {
            string cleanup = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "Tabs", "CleanupTab.xaml"));

            Assert.Contains("1 源包操作", cleanup, StringComparison.Ordinal);
            Assert.Contains("2 删除操作", cleanup, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.SourceHandling", cleanup, StringComparison.Ordinal);
            Assert.Contains("ConverterParameter=KeepInPlace", cleanup, StringComparison.Ordinal);
            Assert.Contains("ConverterParameter=MoveToRest", cleanup, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.RestHandling", cleanup, StringComparison.Ordinal);
            Assert.Contains("ConverterParameter=Keep", cleanup, StringComparison.Ordinal);
            Assert.Contains("ConverterParameter=RecycleBin", cleanup, StringComparison.Ordinal);
            Assert.Contains("ConverterParameter=Delete", cleanup, StringComparison.Ordinal);

            // 第三档那条红字提示：绑 IsRestDeleteSelected（选中才出现，没有关闭按钮 = 不能消掉）。
            Assert.Contains("IsRestDeleteSelected", cleanup, StringComparison.Ordinal);

            // 旧的手动删源复选框与"校验通过后删除"那一档都不许再出现。
            Assert.DoesNotContain("DeleteAfterVerify", cleanup, StringComparison.Ordinal);
            Assert.DoesNotContain("DeleteSourceAfterExtract", cleanup, StringComparison.Ordinal);

            string extraction = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "Views", "Tabs", "ExtractionTab.xaml"));

            Assert.DoesNotContain("Text=\u0022高风险区\u0022", extraction, StringComparison.Ordinal);
            Assert.DoesNotContain("ToggleDangerModeCommand", extraction, StringComparison.Ordinal);
            Assert.DoesNotContain("DangerModeRiskLines", extraction, StringComparison.Ordinal);
            Assert.DoesNotContain("DangerousSpaceModeEnabled", extraction, StringComparison.Ordinal);
        }

        // ================================================================ ⑪ 空间不足模式的排序与并发档（用户 2026-09-27）

        /// <summary>
        /// 空间不足模式**按净占用排序**（净占用 = 解完真正留在盘上的字节数 = 内容物），
        /// 而普通档按"本次要从可用空间里新写多少"（内容物 + 过程物）排 —— 同两份估算、两种顺序，
        /// 而且 `Basis` 那句也要跟着换。
        ///
        /// <para>为什么这条重要：那个模式的全部意义是"边解边把空间还回来"，所以必须先解
        /// "解完占地最少"的包。而放行判断**仍然按需求**（见下一条测试）——
        /// 排序换口径、放行不换口径，这两件事必须同时钉住。</para>
        ///
        /// <para>⚠ 2026-09-29 改口径之后**源包不再进需求**，所以"两种顺序不同"这件事只能由
        /// **过程物**造出来：A 的内容物小、过程物大（需求 11G 远大于净占用 1G），B 的内容物 5G、没有过程物。</para>
        /// </summary>
        [Fact]
        public void 空间不足模式_按净占用排序_普通档仍按需求()
        {
            ArchiveTask bigProcess = CreateLazyTask("a-big-process.7z", 10 * Gib);
            ArchiveTask bigContent = CreateLazyTask("b-big-content.7z", 1 * Gib);

            var estimates = new Dictionary<string, TaskSpaceEstimate>(StringComparer.OrdinalIgnoreCase)
            {
                // A：内容物小、过程物大（净占用小，但需求大）
                [bigProcess.FileName] = EstimateOf(10 * Gib, 1 * Gib, 10 * Gib),

                // B：内容物大、没有过程物（净占用与需求都是 5G）
                [bigContent.FileName] = EstimateOf(1 * Gib, 5 * Gib)
            };

            List<ArchiveTask> tasks = new() { bigProcess, bigContent };

            TaskSpaceEstimate Estimate(ArchiveTask task) => estimates[task.FileName];

            ExtractionSchedulePlan normal = ExtractionScheduler.Build(
                tasks, Estimate, availableBytes: 100 * Gib, reserveBytes: 0, requestedParallelCount: 1);

            ExtractionSchedulePlan tight = ExtractionScheduler.Build(
                tasks,
                Estimate,
                availableBytes: 100 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 1,
                sortKey: item => item.Estimate.NetOccupancyBytes,
                orderBasis: ExtractionSchedulePlan.NetOccupancyOrderBasis);

            // 普通档：需求 A=11G、B=5G → B 先。
            Assert.Equal("b-big-content.7z", normal.Ordered[0].Task.FileName);

            // 空间不足档：净占用 A=1G、B=5G → A 先。
            Assert.Equal("a-big-process.7z", tight.Ordered[0].Task.FileName);

            Assert.Equal(ExtractionSchedulePlan.DefaultOrderBasis, normal.OrderBasis);
            Assert.Contains("净占用", tight.OrderBasis, StringComparison.Ordinal);

            // 顺序换了，但"放行"这个判断一个字节都没变（还是按需求 = 内容物 + 过程物算的）。
            Assert.Equal(normal.Ordered[0].RequiredBytes, normal.Ordered[0].Estimate.FreeSpaceDemandBytes);
            Assert.All(tight.Ordered, item => Assert.True(item.FitsAlone));
        }

        /// <summary>
        /// 空间不足模式**不会**因为"净占用小"就放行一个需求装不下的包：
        /// 排序键只改顺序，<see cref="ScheduledExtractionItem.FitsAlone"/> 与差值仍按需求算。
        ///
        /// <para>形状：内容物 1G（净占用就是 1G）+ 过程物 1G → 需求 2G，可用只有 1G → 单独跑也放不下。
        /// ⚠ 2026-09-29 改口径之后源包不进需求，所以"净占用小于需求"只能靠**过程物**造出来。</para>
        /// </summary>
        [Fact]
        public void 空间不足模式_净占用小但需求装不下_仍然算放不下()
        {
            List<ArchiveTask> tasks = new() { CreateLazyTask("cannot-fit.7z", 1 * Gib) };

            TaskSpaceEstimate Estimate(ArchiveTask _) => EstimateOf(1 * Gib, 1 * Gib, 1 * Gib);

            ExtractionSchedulePlan tight = ExtractionScheduler.Build(
                tasks,
                Estimate,
                availableBytes: 1 * Gib,
                reserveBytes: 0,
                requestedParallelCount: 1,
                sortKey: item => item.Estimate.NetOccupancyBytes,
                orderBasis: ExtractionSchedulePlan.NetOccupancyOrderBasis);

            Assert.Single(tight.BlockedAtPlanTime);
            Assert.False(tight.Ordered[0].FitsAlone);
            Assert.Equal(1 * Gib, tight.Ordered[0].ShortfallBytes);
        }

        /// <summary>
        /// 并发档（用户 2026-09-27 拍板）：**很多个体积相近的小包 → 5**，
        /// 多个偏大 / 体积差得远的包 → **3**；取不到可用空间时按最保守的 3。
        /// </summary>
        [Fact]
        public void 空间不足模式_并发档_相近小包给5个_其余给3个_取不到空间也给3个()
        {
            long budget = 10 * Gib;

            // ① 六个体积相近的小包（每个峰值 200M ≤ 预算/4 = 2.5G）→ 5
            var manySmall = new List<ScheduledExtractionItem>();

            for (int index = 0; index < 6; index++)
            {
                manySmall.Add(CreateLazyItem($"small-{index}.7z", 100L * 1024 * 1024));
            }

            int count = ExtractionScheduler.ResolveSpaceTightParallelCount(
                manySmall, budget, recommendedBySpace: 8, out string basis);

            Assert.Equal(ExtractionScheduler.SpaceTightParallelForManySmall, count);
            Assert.Contains("小包", basis, StringComparison.Ordinal);

            // ② 同样六个包，但体积差得远（100M 与 4G）→ 3（不是"很多个一样的包"）
            var mixed = new List<ScheduledExtractionItem>
            {
                CreateLazyItem("m-100m.7z", 100L * 1024 * 1024),
                CreateLazyItem("m-4g.7z", 4L * Gib),
                CreateLazyItem("m-200m.7z", 200L * 1024 * 1024),
                CreateLazyItem("m-300m.7z", 300L * 1024 * 1024),
                CreateLazyItem("m-1g.7z", 1 * Gib),
                CreateLazyItem("m-2g.7z", 2 * Gib)
            };

            Assert.Equal(
                ExtractionScheduler.SpaceTightParallelForMixedOrLarge,
                ExtractionScheduler.ResolveSpaceTightParallelCount(mixed, budget, 8, out _));

            // ③ 个数不够（只有 3 个）→ 3
            Assert.Equal(
                ExtractionScheduler.SpaceTightParallelForMixedOrLarge,
                ExtractionScheduler.ResolveSpaceTightParallelCount(manySmall.Take(3).ToList(), budget, 8, out _));

            // ④ 取不到可用空间 → 空间建议就是 1（不知道够不够时不并行），模式照它办
            Assert.Equal(
                1,
                ExtractionScheduler.ResolveSpaceTightParallelCount(manySmall, -1, 1, out string unknownBasis));

            Assert.Contains("没能取到", unknownBasis, StringComparison.Ordinal);
            Assert.Contains("1 个", unknownBasis, StringComparison.Ordinal);

            /*
             * ⑤ 空间建议更小的时候**取小**、而且说法跟着那个数走（"上限 5、实际 2"必须说出来，
             * 否则用户拿日志对并发数就会得出"日志是假的"）；它给 0（连最小的都放不下）时**取 1 而不是 0** ——
             * 0 会让"等并发位"那个循环永远等不到空位（`runningTasks.Count >= maxParallel` 恒真）。
             */
            Assert.Equal(2, ExtractionScheduler.ResolveSpaceTightParallelCount(manySmall, budget, 2, out string clampedBasis));
            Assert.Contains("压到 2 个", clampedBasis, StringComparison.Ordinal);

            Assert.Equal(1, ExtractionScheduler.ResolveSpaceTightParallelCount(manySmall, budget, 0, out _));
        }

        /// <summary>造一个带净占用的调度项（只用于并发档那几条，不碰真实文件）。</summary>
        private ScheduledExtractionItem CreateLazyItem(string name, long sourceBytes)
        {
            return new ScheduledExtractionItem(
                CreateLazyTask(name, sourceBytes),
                LazyEstimate(CreateLazyTask(name, sourceBytes)),
                0);
        }

        /// <summary>合成估算：源包 / 内容物 / 过程物各自指定（排序那两条要"净占用顺序 ≠ 峰值顺序"）。</summary>
        private static TaskSpaceEstimate EstimateOf(long sourceBytes, long contentBytes, long processArtifactBytes = 0)
        {
            return new TaskSpaceEstimate
            {
                SourceBytes = sourceBytes,
                ContentBytes = contentBytes,
                ProcessArtifactBytes = processArtifactBytes,
                ContentEstimated = true,
                Basis = "测试用的合成估算"
            };
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

        /// <summary>
        /// 造一个"其余物处理该动手"的现场：成功 + **可证完整** + 其余物里有源包。
        ///
        /// <para>⚠ 2026-09-30（检验等级 L4）：光"校验通过"已经不够了 —— 只有
        /// <c>ManifestCrossChecked = true</c>（拿清单逐条核对过）才算**可证完整**，
        /// 也才允许删源包。这个现场要能代表"该动手"那一档，所以两样都给上；
        /// "判不出 ⇒ 不删"另有一条专门的用例（<c>ResultCompletenessTests</c>）。</para>
        /// </summary>
        private (ArchiveTask Task, string SourcePath, string RestDirectory) CreatePurgeScenario()
        {
            string outputPath = Path.Combine(_root, "out", "222");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(restDirectory);

            string sourcePath = Path.Combine(restDirectory, "222.7z");
            File.WriteAllText(sourcePath, "源包（会被永久删除）");
            File.WriteAllText(Path.Combine(restDirectory, "inner-222.7z.001"), "内层分卷（过程物）");

            // 内容物也要在位：删除的判据是"内容物已定稿"，这个现场得有内容物。
            File.WriteAllText(Path.Combine(outputPath, "payload.mp4"), "内容物");

            var task = new ArchiveTask(Path.Combine(_root, "src", "222.7z"), 1)
            {
                FileName = "222.7z",
                OutputPath = outputPath,
                Status = StatusText.ExtractSuccess,
                IsOutputVerified = true,
                OutputManifestCrossChecked = true,

                /*
                 * 机器终态也要一起给（2026-09-24 起删除裁决读它，不再读 Status 那个中文文案）：
                 * 光有"状态写着解压成功 + 校验通过"还不够 —— 那两样都可能被别处改掉，
                 * 而彻底删除是**永久删除**，判据必须是管线在结论成立那一刻写下的枚举。
                 */
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "222"
            };

            return (task, sourcePath, restDirectory);
        }

    }
}
