using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-10-02 真机那一批（`H:\…\111\` 11 个任务，日志逐字见他导出的
    /// `ArchiveFixer-本次操作_20261002_170923.txt`）里查实的五处缺陷，一处一组用例。
    ///
    /// <list type="number">
    /// <item><description><b>空间曲线的"收"打在其余物删除之前</b>：日志写「起 31.85 GiB → 最低 20.55 GiB →
    /// 收 26.3 GiB」，随后 10 份其余物被彻底删除（合计 6.06 GB）⇒ 真实收尾约 32.4 GiB
    /// （那批其实净省 0.5 GB，按日志读却像净吃掉 5.5 GB）。</description></item>
    /// <item><description><b>空间门那行的"可用"是账面值</b>：同一分钟里
    /// 「空间门通过（精确）…… 可用 31.85 GiB」与「空间变化：目标盘 可用 26.91 GiB」差 5 GiB
    /// （账本只在"有任务等空间"时才重探）。</description></item>
    /// <item><description><b>整组改名只点名第一卷</b>：真机写「风景01.7z → 风景01.7z.001 等 2 卷」，
    /// 而另一卷原本叫 <c>风景02.mp4</c>（被改成 <c>风景01.7z.002</c>）—— 日志里一个字都查不到，
    /// 它随后被彻底删除了。</description></item>
    /// <item><description><b>同一批两个"跳过"口径</b>：「本批汇总：… 跳过 1」与
    /// 「一键处理完成：… 跳过 0 + 另有 1 个是同一分卷组的后续卷」并存。</description></item>
    /// <item><description><b>列表显示不统一</b>：名字上明确是同一组的分卷要**只留一行**，
    /// 并在那一行注明「共 N 卷」（用户 2026-10-02 拍板）。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RealMachine20261002FixesTests : IDisposable
    {
        private const long Gib = 1024L * 1024 * 1024;

        private readonly string _root;

        public RealMachine20261002FixesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixer1002", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ② 空间曲线补记那一针

        /// <summary>
        /// **补记那一针走的是本批同一个侦察器**：起 / 最低 与批末那条曲线同源（同一份采样表），
        /// "可用"是**补记时现采的那个数**（不是批末那个数）—— 真机上这两个数差 6 GB。
        /// </summary>
        [Fact]
        public void 空间曲线补记_用的是同一个侦察器现采的那一针()
        {
            var free = new Queue<long>(new[] { 31_85L * Gib / 100, 20_55L * Gib / 100, 26_30L * Gib / 100, 32_40L * Gib / 100 });

            var monitor = new SpaceTrendMonitor(() => free.Count > 0 ? free.Dequeue() : 0L);

            monitor.Record("批首");
            monitor.Record("每 5 秒");
            monitor.Record("批末");

            string report = Assert.Single(monitor.DescribeReport());
            Assert.Contains(TaskSpaceEstimate.FormatSize(26_30L * Gib / 100), report, StringComparison.Ordinal);

            // 其余物删除之后：现采一针（32.40 GiB），再把这一针说成一行。
            monitor.Record("其余物处理之后");

            string? postscript = monitor.DescribePostscript("其余物处理之后");

            Assert.NotNull(postscript);
            Assert.StartsWith("空间曲线补记：", postscript, StringComparison.Ordinal);
            Assert.Contains("其余物处理之后", postscript!, StringComparison.Ordinal);

            // 「可用」是补记这一针的数（真机上它比"收"多出那 6 GB）。
            Assert.Contains(TaskSpaceEstimate.FormatSize(32_40L * Gib / 100), postscript!, StringComparison.Ordinal);

            // 起 / 最低 与批末那条曲线同源（⛔ 不是第二套取数）。
            Assert.Contains(TaskSpaceEstimate.FormatSize(31_85L * Gib / 100), postscript!, StringComparison.Ordinal);
            Assert.Contains(TaskSpaceEstimate.FormatSize(20_55L * Gib / 100), postscript!, StringComparison.Ordinal);
        }

        /// <summary>
        /// **一针都取不到时如实不写**（⛔ 绝不拿别的数字充数）。
        /// </summary>
        [Fact]
        public void 空间曲线补记_取不到可用空间时一个字都不写()
        {
            var monitor = new SpaceTrendMonitor(() => null);

            monitor.Record("批首");
            monitor.Record("其余物处理之后");

            Assert.Null(monitor.DescribePostscript("其余物处理之后"));
        }

        /// <summary>
        /// **接线（真跑一次一键处理）**：其余物处理完之后那一行必须出现在日志里，而且排在
        /// 「一键处理完成」之后；它报的数字是**那一刻现采的**（探针每次返回的值都不同 ⇒
        /// 这一行里的数只能是最后那一次探到的，不可能是批末那个数）。
        /// </summary>
        [Fact]
        public async Task 一键处理_其余物处理完之后补记一针真实可用空间()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            string source = harness.CreateSource("single.7z");
            harness.AddTask(source);

            await harness.OneClick.RunAsync();

            List<string> lines = harness.LogTexts.ToList();

            int summaryIndex = lines.FindIndex(line => line.Contains("一键处理完成：", StringComparison.Ordinal));
            int postscriptIndex = lines.FindIndex(line => line.Contains("空间曲线补记：", StringComparison.Ordinal));

            Assert.True(summaryIndex >= 0, "一键处理汇总行不见了：\n" + string.Join("\n", lines));
            Assert.True(postscriptIndex >= 0, "其余物处理之后那一针没写：\n" + string.Join("\n", lines));

            // 「其余物处理完之后」= 排在汇总行之后（真机上它就是最后说的一句话）。
            Assert.True(postscriptIndex > summaryIndex, "补记那一针必须排在「一键处理完成」之后");

            Assert.Contains("其余物处理之后", lines[postscriptIndex], StringComparison.Ordinal);

            // 它报的数是**现采的**：探针每次返回的值都不同，最后那一次就是它。
            long lastProbe = harness.LastProbeValue;
            Assert.Contains(TaskSpaceEstimate.FormatSize(lastProbe), lines[postscriptIndex], StringComparison.Ordinal);

            // 一条曲线 + 一条补记：⛔ 不多不少（一批只补一针）。
            Assert.Single(lines, line => line.Contains("空间曲线补记：", StringComparison.Ordinal));
        }

        // ================================================================ ③ 空间门那行是账面值

        /// <summary>
        /// **账本那条路必须说"账面可用"，打包那条路（刚探到）照旧说"可用"** ——
        /// 数字来源不同，措辞就必须不同，否则用户会拿一个批首的数去比同一分钟的空间趋势。
        /// </summary>
        [Fact]
        public void 空间门_账面值与实时值两个口径读起来不一样()
        {
            var ledger = new SpaceReservationLedger(30L * Gib, reserveBytes: 0);

            SpaceGateDecision byLedger = ledger.TryReserve(1L * Gib);

            Assert.True(byLedger.Allowed);
            Assert.Contains("账面可用", byLedger.Reason, StringComparison.Ordinal);
            Assert.Contains(TaskSpaceEstimate.FormatSize(30L * Gib), byLedger.Reason, StringComparison.Ordinal);

            // ⛔ 判据与数字一个都没动：可用还是那个 30 GiB，放行还是放行。
            Assert.Equal(30L * Gib, byLedger.AvailableBytes);
            Assert.Equal(1L * Gib, byLedger.RequiredBytes);
            Assert.Equal(0L, byLedger.ShortfallBytes);

            // 打包那条路：可用空间是**刚刚探到的** ⇒ 不许说"账面"。
            SpaceGateDecision fresh = SpaceGate.Check(1L * Gib, 30L * Gib, 0);

            Assert.True(fresh.Allowed);
            Assert.DoesNotContain("账面", fresh.Reason, StringComparison.Ordinal);
            Assert.Contains("可用", fresh.Reason, StringComparison.Ordinal);

            // 两个口径的数字部分必须逐字相同（只是"这个数哪来的"那句话不同）。
            Assert.Contains(TaskSpaceEstimate.FormatSize(30L * Gib), fresh.Reason, StringComparison.Ordinal);
            Assert.Contains(TaskSpaceEstimate.FormatSize(29L * Gib), fresh.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// **接线**：真跑一次解压，那一行「空间门通过（精确）」里必须写着"账面可用"。
        /// </summary>
        [Fact]
        public async Task 真跑一批_空间门那一行写的是账面可用()
        {
            Harness harness = CreateHarness();

            string source = harness.CreateSource("gate.7z");
            harness.AddTask(source);

            await harness.Coordinator.StartExtractAsync();

            string? gate = harness.LogTexts.FirstOrDefault(
                line => line.Contains("空间门通过（精确）", StringComparison.Ordinal));

            Assert.NotNull(gate);
            Assert.Contains("账面可用", gate!, StringComparison.Ordinal);

            // 同一批里那条**实时**曲线照旧存在（两个口径并存、各自说清自己是什么）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("空间变化：", StringComparison.Ordinal));
        }

        // ================================================================ ④ 整组改名逐条点名

        /// <summary>
        /// **两卷（真机形状）：两卷的旧名都要出现在那一行里**。
        ///
        /// <para>真机现场：`风景01.7z` + `风景02.mp4`（后者是这一组的第 2 卷，只是后缀被改坏了）——
        /// 老写法只写「风景01.7z → 风景01.7z.001 等 2 卷」，`风景02.mp4` 一个字都查不到，
        /// 而它随后被彻底删除了。</para>
        /// </summary>
        [Fact]
        public void 整组改名_两卷的两个旧名都点名()
        {
            string directory = NewDirectory("describe-two");

            CreateFile(directory, "giu910.7z.001删除");
            CreateFile(directory, "giu910.7z.002删除");

            VolumeNameRepairPlan plan = Plan(directory, "giu910.7z.001删除");

            Assert.True(plan.CanRepair);
            Assert.Equal(2, plan.Items.Count);

            string described = plan.Describe();

            Assert.Contains("giu910.7z.001删除 → giu910.7z.001", described, StringComparison.Ordinal);
            Assert.Contains("giu910.7z.002删除 → giu910.7z.002", described, StringComparison.Ordinal);
            Assert.Contains("共 2 卷", described, StringComparison.Ordinal);
        }

        /// <summary>**≤3 项全列**（三卷时三卷都点名，末尾写共几卷）。</summary>
        [Fact]
        public void 整组改名_三项时全列()
        {
            string directory = NewDirectory("describe-three");

            CreateFile(directory, "giu910.7z.001删除");
            CreateFile(directory, "giu910.7z.002删除");
            CreateFile(directory, "giu910.7z.003删除");

            VolumeNameRepairPlan plan = Plan(directory, "giu910.7z.001删除");

            Assert.True(plan.CanRepair);
            Assert.Equal(3, plan.Items.Count);

            string described = plan.Describe();

            foreach (string name in new[] { "001", "002", "003" })
            {
                Assert.Contains($"giu910.7z.{name}删除 → giu910.7z.{name}", described, StringComparison.Ordinal);
            }

            Assert.Contains("共 3 卷", described, StringComparison.Ordinal);
        }

        /// <summary>
        /// **>3 项折成「等 N 卷」**：列前 3 条 + 写出总卷数（⛔ 不是"只列了 3 卷"、
        /// 也不许静默截断成"就这么多"）。
        /// </summary>
        [Fact]
        public void 整组改名_超过三项时前三条加等N卷()
        {
            string directory = NewDirectory("describe-five");

            for (int index = 1; index <= 5; index++)
            {
                CreateFile(directory, $"giu910.7z.{index:D3}删除");
            }

            VolumeNameRepairPlan plan = Plan(directory, "giu910.7z.001删除");

            Assert.True(plan.CanRepair);
            Assert.Equal(5, plan.Items.Count);

            string described = plan.Describe();

            // 前三条点名。
            foreach (string name in new[] { "001", "002", "003" })
            {
                Assert.Contains($"giu910.7z.{name}删除 → giu910.7z.{name}", described, StringComparison.Ordinal);
            }

            // 没列的那几卷不出现，但总数必须写出来。
            Assert.DoesNotContain("giu910.7z.004删除 →", described, StringComparison.Ordinal);
            Assert.Contains("等 5 卷", described, StringComparison.Ordinal);
        }

        /// <summary>
        /// **改名结果那句话也逐条点名**（同一件事的两处说法：日志里那一行与"改完给你的那句话"）。
        /// </summary>
        [Fact]
        public void 整组改名_结果那句话也逐条点名()
        {
            string directory = NewDirectory("describe-result");

            CreateFile(directory, "giu910.7z.001删除", 128);
            CreateFile(directory, "giu910.7z.002删除", 256);

            VolumeNameRepairPlan plan = Plan(directory, "giu910.7z.001删除");

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.Contains("整组 2 卷", result.Message, StringComparison.Ordinal);
            Assert.Contains("giu910.7z.001删除 → giu910.7z.001", result.Message, StringComparison.Ordinal);
            Assert.Contains("giu910.7z.002删除 → giu910.7z.002", result.Message, StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 两个"跳过"口径统一

        /// <summary>
        /// **同一批里那两行现在说同一件事**（用户 2026-10-02 真机：一处「跳过 1」、一处「跳过 0」）：
        /// 跟班卷不算"跳过"、单列一句，而**恒等式「各分项之和 + 未处理 = 本次任务数」不许破**。
        ///
        /// <para>夹具：同一分卷组的两卷各自成一单（手工入列，模拟真机那批的形状）——
        /// `.001` 是首卷（真跑）、`.002` 是跟班卷（按设计落 Skipped）。</para>
        /// </summary>
        [Fact]
        public async Task 本批汇总与一键汇总_跟班卷都不算跳过且恒等式不破()
        {
            Harness harness = CreateHarness();

            string first = harness.CreateSource("set.7z.001");
            string second = harness.CreateSource("set.7z.002");

            ArchiveTask owner = harness.AddTask(first);
            ArchiveTask follower = harness.AddTask(second);

            await harness.OneClick.RunAsync();

            /*
             * 「一键处理完成」那一行会出现在两处（`OneClickCoordinator` 写完汇总之后，
             * `MainViewModel` 收尾时还会把它当成"一键处理汇总"再记一条）—— 与真机日志一致，
             * 所以这里两处都收进来逐条查。
             */
            List<string> batches = harness.LogTexts
                .Where(line => line.Contains("本批汇总：", StringComparison.Ordinal))
                .ToList();

            List<string> dones = harness.LogTexts
                .Where(line => line.Contains("一键处理完成：", StringComparison.Ordinal))
                .ToList();

            string batch = Assert.Single(batches);

            Assert.NotEmpty(dones);

            // 跟班卷真的按设计跳过了（判据是事实位，不是这行文案）。
            Assert.True(follower.IsVolumeGroupFollower);
            Assert.Equal(TaskOutcome.Skipped, follower.Outcome);

            foreach (string line in batches.Concat(dones))
            {
                Assert.Contains("跳过 0", line, StringComparison.Ordinal);
                Assert.Contains("另有 1 个是同一分卷组的后续卷", line, StringComparison.Ordinal);
                Assert.Contains("不是没做成", line, StringComparison.Ordinal);

                // 恒等式：成功 1 + 跟班 1 = 本次 2 个任务 ⇒ 一个字都不许冒出"未处理"。
                Assert.Contains("成功 1", line, StringComparison.Ordinal);
                Assert.DoesNotContain("未处理", line, StringComparison.Ordinal);
            }

            Assert.Equal(TaskOutcome.Succeeded, owner.Outcome);

            // 两行说的必须是**同一句**（口径只有一个）。
            Assert.Contains(
                StatusText.VolumeGroupFollowerSummaryFormat.Replace("{0}", "1", StringComparison.Ordinal),
                batch,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// **对照**：不是跟班卷的"跳过"照旧算在"跳过"那一档里（⛔ 不许被这一改顺手吞掉）——
        /// 判据落在唯一出口 <c>OneClickCoordinator.BuildSummaryLine</c> 上，
        /// 批末那一行读同一个事实位。
        /// </summary>
        [Fact]
        public void 对照_用户自己选的跳过照旧算跳过()
        {
            Harness harness = CreateHarness();

            var succeeded = new ArchiveTask(@"C:\t\111.part1.rar")
            {
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded
            };

            var userSkip = new ArchiveTask(@"C:\t\junk.txt")
            {
                Status = StatusText.Skipped,
                Outcome = TaskOutcome.Skipped
            };

            string line = harness.OneClick.BuildSummaryLine(new[] { succeeded, userSkip });

            Assert.Contains("跳过 1", line, StringComparison.Ordinal);
            Assert.DoesNotContain("同一分卷组的后续卷", line, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", line, StringComparison.Ordinal);
        }

        // ================================================================ ⑦ 「一批任务怎么数」只剩一个出口

        /// <summary>
        /// **数法只有一份**（<see cref="BatchOutcomeTally"/>）：跟班卷单列一档、
        /// 「未处理」按"总数 − 各分项"倒推 ⇒ 恒等式「各分项之和 + 未处理 = 任务数」永远成立。
        /// </summary>
        [Fact]
        public void 数法_跟班卷单列一档_且各分项加未处理等于任务数()
        {
            var owner = new ArchiveTask(@"C:\t\set.7z.001", 1)
            {
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded
            };

            // 跟班卷：同一分卷组的后续卷，按设计落 Skipped，但**不是"没做成"**。
            var follower = new ArchiveTask(@"C:\t\set.7z.002", 2)
            {
                Status = StatusText.Skipped,
                Outcome = TaskOutcome.Skipped,
                IsVolumeGroupFollower = true
            };

            // 用户在同名冲突框里自己选的"跳过"：照旧算"跳过"那一档。
            var userSkip = new ArchiveTask(@"C:\t\junk.txt", 3)
            {
                Status = StatusText.Skipped,
                Outcome = TaskOutcome.Skipped
            };

            // 还没轮到的那一单（真机导入完就先导日志，整批都是这一档）。
            var pending = new ArchiveTask(@"C:\t\later.zip", 4) { Outcome = TaskOutcome.Pending };

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new[] { owner, follower, userSkip, pending });

            Assert.Equal(4, tally.Total);
            Assert.Equal(1, tally.Succeeded);
            Assert.Equal(0, tally.Failed);
            Assert.Equal(1, tally.Skipped);
            Assert.Equal(1, tally.FollowerSkipped);
            Assert.Equal(0, tally.PartiallyCompleted);
            Assert.Equal(0, tally.Cancelled);
            Assert.Equal(1, tally.Untouched);

            // 恒等式（用户 2026-10-02 定：两个汇总行都不许破）。
            Assert.Equal(
                tally.Total,
                tally.Succeeded + tally.Failed + tally.Skipped + tally.FollowerSkipped
                    + tally.PartiallyCompleted + tally.Cancelled + tally.Untouched);
        }

        /// <summary>
        /// **"终态说成功、校验却判否"那一帧落失败侧**（不变量 6 的真机违反）——
        /// 旧写法下批末「本批汇总」把它算成功、一键汇总那一行算失败，同一份日志两个"失败 N"。
        /// </summary>
        [Fact]
        public void 数法_终态说成功但校验判否_落失败侧()
        {
            var bad = new ArchiveTask(@"C:\t\half.7z", 1)
            {
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded,
                OutputVerification = OutputVerificationOutcome.Failed
            };

            Assert.True(BatchOutcomeTally.IsCountedAsFailure(bad));
            Assert.False(BatchOutcomeTally.IsCountedAsSuccess(bad));

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new[] { bad });

            Assert.Equal(0, tally.Succeeded);
            Assert.Equal(1, tally.Failed);

            // 逐条列名字那份清单读的是**同一个**判据（否则数字与名字会差一个）。
            Assert.Contains(bad, new[] { bad }.Where(BatchOutcomeTally.IsCountedAsFailure));
        }

        /// <summary>
        /// **导出头部照同一份数法**：跟班卷不算"跳过"、单列一句，
        /// 而"既没成功也没失败也没跳过"的那些任务必须落「未处理」——
        /// 否则头部的三个分项加起来对不上任务数（真机那份导入完的日志就是
        /// 「任务数：10（成功 0 / 失败 0 / 跳过 0）」，读的人当场对不上账）。
        /// </summary>
        [Fact]
        public void 导出头部_导入完还没跑时_未处理那一档必须写出来()
        {
            Harness harness = CreateHarness();

            harness.AddTask(harness.CreateSource("a.7z"));
            harness.AddTask(harness.CreateSource("b.7z"));

            string text = string.Join("\n", harness.Vm.BuildLogExportHeader());

            Assert.Contains("任务数：2（成功 0 / 失败 0 / 跳过 0 / 未处理 2）", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// **三处口径逐个相同**：同一批任务，「本批汇总」那一行、一键汇总那一行、
        /// 以及导出的日志头部，分项数字与跟班卷那一句必须**一个字都不差**。
        /// </summary>
        [Fact]
        public async Task 三处口径一致_本批汇总与一键汇总与导出头部()
        {
            Harness harness = CreateHarness();

            string first = harness.CreateSource("set.7z.001");
            string second = harness.CreateSource("set.7z.002");

            harness.AddTask(first);
            ArchiveTask follower = harness.AddTask(second);

            await harness.OneClick.RunAsync();

            List<string> lines = harness.LogTexts.ToList();

            string batch = Assert.Single(lines
                .Where(line => line.Contains("本批汇总：", StringComparison.Ordinal))
                .ToList());
            string done = lines.First(line => line.Contains("一键处理完成：", StringComparison.Ordinal));
            string header = string.Join("\n", harness.Vm.BuildLogExportHeader());

            Assert.True(follower.IsVolumeGroupFollower);

            foreach (string line in new[] { batch, done })
            {
                Assert.Contains("成功 1 / 失败 0 / 跳过 0", line, StringComparison.Ordinal);
                Assert.Contains("另有 1 个是同一分卷组的后续卷", line, StringComparison.Ordinal);
                Assert.DoesNotContain("未处理", line, StringComparison.Ordinal);
            }

            Assert.Contains("任务数：2（成功 1 / 失败 0 / 跳过 0）", header, StringComparison.Ordinal);
            Assert.Contains("另有 1 个是同一分卷组的后续卷", header, StringComparison.Ordinal);
        }

        // ================================================================ ⑥ 列表显示统一：一组只留一行

        /// <summary>
        /// **RAR 每一卷都带签名也仍然只留一行**（用户 2026-10-02 真机那把尺子）。
        ///
        /// <para>"每一卷开头都有 8 字节 RAR 签名"是**物理事实**（RAR5 = <c>52 61 72 21 1A 07 01 00</c>、
        /// RAR 1.5–4.x = <c>52 61 72 21 1A 07 00</c>），绝不是"它们各自是完整包"的证据 ——
        /// 归组只看名字（基名 + 卷标记 + 卷号连续），签名一个字节都不参与。</para>
        /// </summary>
        [Theory]
        [InlineData("RAR5", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 })]
        [InlineData("RAR4", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 })]
        public async Task 列表_RAR每卷都带签名_仍然只有一行且注明共4卷(string family, byte[] signature)
        {
            string directory = NewDirectory("list-rar-" + family);

            for (int index = 1; index <= 4; index++)
            {
                WriteVolume(Path.Combine(directory, $"111.part{index}.rar"), signature, 4096);
            }

            List<ArchiveTask> tasks = await ScanAsync(directory);

            ArchiveTask row = Assert.Single(tasks);

            Assert.True(row.IsVolumeGroup, family + " 这一组应当归成一组");
            Assert.Equal(4, row.VolumeCount);
            Assert.Equal("111.part1.rar", row.FileName);

            // 那一行上要注明"这一组共 N 卷"（用户 2026-10-02：「并在那一行注明这一组共 N 卷」）。
            Assert.Contains("共 4 卷", row.VolumeInfoText, StringComparison.Ordinal);
        }

        /// <summary>
        /// **跨盘 zip 四片也只有一行**（只有 <c>.z01</c> 有跨盘魔数，末片 <c>.zip</c> 是引擎入口）。
        /// </summary>
        [Fact]
        public async Task 列表_跨盘zip四片只有一行且注明共4卷()
        {
            string directory = NewDirectory("list-zip");

            // 第 1 片：跨盘标记 + 本地头（真机上只有它有魔数）。
            WriteVolume(
                Path.Combine(directory, "111.z01"),
                new byte[] { 0x50, 0x4B, 0x07, 0x08, 0x50, 0x4B, 0x03, 0x04 },
                4096);

            // 其余两片是纯数据（没有任何魔数）。
            WriteVolume(Path.Combine(directory, "111.z02"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.z03"), Array.Empty<byte>(), 4096);

            // 末片：尾部有 EOCD（这里只写它该有的入口身份，归组只看名字）。
            WriteVolume(Path.Combine(directory, "111.zip"), new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 2048);

            List<ArchiveTask> tasks = await ScanAsync(directory);

            ArchiveTask row = Assert.Single(tasks);

            Assert.True(row.IsVolumeGroup);
            Assert.Equal(4, row.VolumeCount);
            Assert.Contains("共 4 卷", row.VolumeInfoText, StringComparison.Ordinal);

            // 代表 = 第 1 卷本体（<c>111.zip</c>），不是在它前面编号的 <c>.z01</c>。
            Assert.Equal("111.zip", row.FileName);
        }

        /// <summary>**7z 数字分卷（只有 <c>.001</c> 带签名）同样只留一行**。</summary>
        [Fact]
        public async Task 列表_7z数字分卷只有一行且注明共4卷()
        {
            string directory = NewDirectory("list-7z");

            WriteVolume(
                Path.Combine(directory, "111.7z.001"),
                new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
                4096);

            WriteVolume(Path.Combine(directory, "111.7z.002"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.7z.003"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.7z.004"), Array.Empty<byte>(), 2048);

            List<ArchiveTask> tasks = await ScanAsync(directory);

            ArchiveTask row = Assert.Single(tasks);

            Assert.True(row.IsVolumeGroup);
            Assert.Equal(4, row.VolumeCount);
            Assert.Contains("共 4 卷", row.VolumeInfoText, StringComparison.Ordinal);
        }

        /// <summary>
        /// **对照（红线不许松）：判不出 ⇒ 什么都不做** —— 一堆同名但**名字上没有卷标记**的文件
        /// 绝不许被硬凑成一组（认错组比不认糟得多：一键处理会去改一批不相干文件的名字）。
        /// </summary>
        [Fact]
        public async Task 对照_一堆同名文件不许被硬凑成一组()
        {
            string directory = NewDirectory("list-control");

            WriteVolume(Path.Combine(directory, "111.mp4"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.txt"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.jpg"), Array.Empty<byte>(), 4096);
            WriteVolume(Path.Combine(directory, "111.doc"), Array.Empty<byte>(), 4096);

            List<ArchiveTask> tasks = await ScanAsync(directory);

            Assert.Equal(4, tasks.Count);
            Assert.All(tasks, task => Assert.False(task.IsVolumeGroup));
            Assert.All(tasks, task => Assert.Equal(string.Empty, task.VolumeInfoText));
        }

        /// <summary>
        /// **对照**：一份光杆 <c>.zip</c> 不算分卷组（"后面还有没有卷"名字给不出答案）——
        /// 这一条与上一条合起来就是"判不出 ⇒ 什么都不做"。
        /// </summary>
        [Fact]
        public async Task 对照_光杆zip本体不算分卷组()
        {
            string directory = NewDirectory("list-control-body");

            WriteVolume(Path.Combine(directory, "111.zip"), new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 4096);

            ArchiveTask row = Assert.Single(await ScanAsync(directory));

            Assert.False(row.IsVolumeGroup);
            Assert.Equal(string.Empty, row.VolumeInfoText);
        }

        // ================================================================ 装配

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string CreateFile(string directory, string name, int size = 16)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        /// <summary>写一个"开头就是这套格式的签名"的分卷（签名之后补到指定大小）。</summary>
        private static void WriteVolume(string path, byte[] signature, int size)
        {
            var bytes = new byte[Math.Max(size, signature.Length)];
            Array.Copy(signature, bytes, signature.Length);
            File.WriteAllBytes(path, bytes);
        }

        private static VolumeNameRepairPlan Plan(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            return VolumeNameRepair.Plan(path, VolumeNameRepair.EnumerateFileNamesInDirectory(path));
        }

        private static async Task<List<ArchiveTask>> ScanAsync(string directory)
        {
            var options = new ScanOptions
            {
                RecursiveScan = true,
                ScanMode = "ScanAllFiles"
            };

            return await new FileScanService().ScanPathsAsync(new[] { directory }, options, CancellationToken.None);
        }

        /// <summary>
        /// 假引擎 + 记账对话框 + **可注入的可用空间探针**（探针每次返回的值都不同 ⇒
        /// "这一行报的是不是现采的"可以被钉死）。
        /// </summary>
        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string sourceRoot = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.RemindBeforeExtract = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.MaxParallelExtractCount = 1;

            configure?.Invoke(settings);

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var dialog = new RecordingDialogService();

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                dialog);

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var probeValues = new List<long>();

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, dialog)
            {
                SpaceReserveOverride = 0,
                SpaceProbeOverride = _ =>
                {
                    // 每次探测都返回一个**不一样**的数（10 GiB、10 GiB+1 MiB、…）：
                    // "这一行报的是不是现采的"因此可以被钉死。
                    lock (probeValues)
                    {
                        long value = (10L * Gib) + (probeValues.Count + 1) * (1024L * 1024);
                        probeValues.Add(value);
                        return value;
                    }
                },
                KeepTaskDetailInLog = true
            };

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), dialog);
            var rename = new RenameCoordinator(vm, scan, new RenameService(), dialog);
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, dialog)
            {
                OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown()
            };

            return new Harness(vm, engine, coordinator, oneClick, logService, probeValues, sourceRoot);
        }

        private sealed class Harness
        {
            private readonly List<long> _probeValues;

            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                List<long> probeValues,
                string sourceRoot)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                _probeValues = probeValues;
                SourceRoot = sourceRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public string SourceRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            /// <summary>最近一次探测返回的数（补记那一针报的必须是它）。</summary>
            public long LastProbeValue
            {
                get
                {
                    lock (_probeValues)
                    {
                        return _probeValues.Count == 0 ? 0L : _probeValues[^1];
                    }
                }
            }

            public string CreateSource(string fileName)
            {
                string path = Path.Combine(SourceRoot, fileName);
                File.WriteAllBytes(path, new byte[512]);
                return path;
            }

            /// <summary>手工入列（⛔ 不走扫描：这一组要的是"两卷各自成一单"那个形状）。</summary>
            public ArchiveTask AddTask(string path)
            {
                var task = new ArchiveTask(path, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                Vm.Tasks.Add(task);
                return task;
            }
        }

        private sealed class RecordingDialogService : DialogService
        {
            public override void ShowBatchSummary(string message, BatchSummarySeverity severity)
            {
                // 无界面宿主：什么都不弹（结论已经在日志里）。
            }
        }

        /// <summary>可控的假引擎：解压成功并写一个产物文件（与既有夹具同一套做法）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 512,
                    Entries = new List<ArchiveEntry> { new() { Path = "payload.bin", Size = 512 } },
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "payload.bin"), new byte[512]);
                }

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded() => new()
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }
    }
}
