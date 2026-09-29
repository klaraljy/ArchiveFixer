using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **放行判据的守门用例**（用户 2026-09-29 真机当场报："普通人随便都能解压，你这里说空间不足"）。
    ///
    /// <para><b>真机那一单的形状</b>（H: 上只读量出来的真实字节数）：一个 7z 三卷组
    /// 7,516,192,768 + 7,516,192,768 + 3,975,934,338 = <see cref="RealSourceTotal"/> 字节（17.70 GiB），
    /// 目标盘（与源包同一块盘）可用 <see cref="RealAvailable"/> 字节（33.45 GiB）。
    /// 老口径把**源包**也加进"需要"里去比"可用空间"，于是 17.70 + 17.70 = 35.41 GiB &gt; 32.95 GiB（可用 − 512 MiB 余量）
    /// → 判成"整盘都放不下"（差 2.46 GiB），**一个字节都没解**。</para>
    ///
    /// <para><b>为什么这是错的（不是"他盘不够"）</b>：可用空间**已经**把盘上现有的东西排除在外，
    /// 源包那 17.70 GiB 从来不在"可用"里。盘上塞得下的恒等式是
    /// "本次新写 ≤ 可用 − 余量"，而"本次新写"只有**内容物 + 过程物**
    /// （源包原地不动、或搬进同盘的 `其余物`，都不产生新字节）。
    /// 同一份判断在 <c>Security/ResourceBudget.CheckFreeSpace</c> 里一直是对的（它比的是纯内容物），
    /// 错的是任务级那道门（<c>Storage/SpaceGate</c> + <c>TaskSpaceEstimate.PeakBytes</c>）。</para>
    ///
    /// <para><b>这一组钉三件事</b>：①真机那一单必须放行；②可用空间明显不够时必须**照旧拒绝并说清差多少**
    /// （⛔ 放宽的是口径，不是把闸门拆掉）；③真样本那一组（只读）在真机可用空间下必须放行。</para>
    /// </summary>
    public class SpaceDemandAccountingTests
    {
        private const long Gib = 1024L * 1024 * 1024;
        private const long Mib = 1024L * 1024;

        // 真机那一单的真实字节数（只读 stat 出来的；⛔ 样本名不进仓库，所以这里只有数字）。
        private const long RealVolume1 = 7_516_192_768L;
        private const long RealVolume2 = 7_516_192_768L;
        private const long RealVolume3 = 3_975_934_338L;

        /// <summary>三卷求和 = 19,008,319,874 字节（17.70 GiB）。</summary>
        private const long RealSourceTotal = RealVolume1 + RealVolume2 + RealVolume3;

        /// <summary>那一刻目标盘的可用空间 = 35,916,664,832 字节（33.45 GiB）。</summary>
        private const long RealAvailable = 35_916_664_832L;

        /// <summary>要保留的余量（与 <see cref="ResourceBudgetOptions.MinFreeSpaceReserveBytes"/> 同源）。</summary>
        private const long RealReserve = 512L * Mib;

        // ================================================================ ① 真机那一单：必须放行

        /// <summary>
        /// **核心守门**：单任务、整组 17.70 GiB、可用 33.45 GiB → 放行。
        ///
        /// <para>红检的落点也在这里：把判据改回 <c>PeakBytes</c>（含源包）→ 这一条立刻变红
        /// （"这条计划里不该有放不下的包，实际 1 个"）。</para>
        /// </summary>
        [Fact]
        public void 真机那一单_三卷共17点70GiB_可用33点45GiB_必须放行()
        {
            TaskSpaceEstimate estimate = RealCaseEstimate();

            // 判据 = 内容物 + 过程物。清单还没读时内容物按源包 1 倍估（下界），过程物为 0。
            Assert.Equal(RealSourceTotal, estimate.SourceBytes);
            Assert.Equal(RealSourceTotal, estimate.ContentBytes);
            Assert.Equal(0, estimate.ProcessArtifactBytes);
            Assert.Equal(RealSourceTotal, estimate.FreeSpaceDemandBytes);

            // 源包**只**出现在"盘上总占地"里 —— 这正是老口径把它算两遍的地方。
            Assert.Equal(2 * RealSourceTotal, estimate.PeakBytes);
            Assert.Equal(estimate.SourceBytes, estimate.PeakBytes - estimate.FreeSpaceDemandBytes);

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { RealCaseTask() },
                _ => estimate,
                RealAvailable,
                RealReserve,
                requestedParallelCount: 1);

            Assert.Empty(plan.BlockedAtPlanTime);
            Assert.True(plan.Ordered[0].FitsAlone);
            Assert.Equal(0, plan.Ordered[0].ShortfallBytes);
            Assert.Equal(1, plan.RecommendedParallelCount);
            Assert.Equal(RealSourceTotal, plan.Ordered[0].RequiredBytes);

            // 运行期那道门（账本）按同一个数字也必须放行 —— 它与计划时那道门共用 RequiredBytes。
            SpaceGateDecision runtime = new SpaceReservationLedger(RealAvailable, RealReserve)
                .TryReserve(estimate.FreeSpaceDemandBytes);

            Assert.True(runtime.Allowed, runtime.ToLogLine());

            /*
             * 反向钉死"改的到底是哪一处"：把**盘上总占地**当需求时，这块盘上确实是放不下的
             * （35.41 GiB > 可用 33.45 GiB）—— 那正是他看到的"整盘都放不下"。
             * 这一条不是在给老口径背书，而是让"算了两遍"这件事可执行地留档：
             * 谁要是把判据改回 PeakBytes，上面三条会红，而这一条会绿。
             *
             * ⚠ "差多少"在这一档是按**可用空间**算的（SpaceGate 的既有口径：连可用都不够时
             * 报的是"要装下还差多少"），所以是 2×源包 − 可用 = 1.96 GiB；
             * 而排计划那一档（ExtractionScheduler.FitsAlone）的差值是按"可用 − 余量"算的 = 2.46 GiB。
             * 两个数都写清了口径，这里各自按自己的口径断言。
             */
            SpaceGateDecision peakAsDemand = SpaceGate.Check(estimate.PeakBytes, RealAvailable, RealReserve);

            Assert.False(peakAsDemand.Allowed);
            Assert.Equal((2 * RealSourceTotal) - RealAvailable, peakAsDemand.ShortfallBytes);
        }

        // ================================================================ ② 明显不够时：照旧拒绝并报差多少

        /// <summary>
        /// 同一单、可用空间改成 8 GiB（明显不够）→ **必须拒绝**，而且报出"需要多少、可用多少、差多少"。
        ///
        /// <para>⛔ 这一条是给"别为了让他能跑就把闸门整体拆掉"上的锁：
        /// 需求 17.70 GiB 减掉预算（8 GiB − 512 MiB）还差 10.20 GiB，一个字都不许含糊。</para>
        /// </summary>
        [Fact]
        public void 真机那一单_可用只剩8GiB_必须拒绝并报出差多少()
        {
            TaskSpaceEstimate estimate = RealCaseEstimate();

            long available = 8 * Gib;
            long budget = available - RealReserve;
            long expectedShortfall = RealSourceTotal - budget;

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { RealCaseTask() },
                _ => estimate,
                available,
                RealReserve,
                requestedParallelCount: 1);

            ScheduledExtractionItem blocked = Assert.Single(plan.BlockedAtPlanTime);

            Assert.False(blocked.FitsAlone);
            Assert.Equal(RealSourceTotal, blocked.RequiredBytes);
            Assert.Equal(expectedShortfall, blocked.ShortfallBytes);
            Assert.Equal(0, plan.RecommendedParallelCount);
            Assert.Contains("连需求最小的那个包都放不下", plan.ParallelAdviceText(), StringComparison.Ordinal);

            // 文案三个数字齐（需要 / 可用 / 差），而且差值写成"差 9.7 GiB"这种能对账的形态。
            SpaceGateDecision gate = SpaceGate.Check(blocked.RequiredBytes, available, RealReserve);
            long gateShortfall = RealSourceTotal - available;

            Assert.False(gate.Allowed);
            Assert.Contains("这个任务需要", gate.Reason, StringComparison.Ordinal);
            Assert.Contains("目标盘可用", gate.Reason, StringComparison.Ordinal);
            Assert.Contains(TaskSpaceEstimate.FormatSize(gateShortfall), gate.Reason, StringComparison.Ordinal);
            Assert.Contains("差 ", gate.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// **"真放不下"的形状照旧拦**：源包很小、内容物很大（清单算出来 40 GiB）→ 可用 33.45 GiB 也不够。
        ///
        /// <para>这一条防的是另一种改错方向：为了绕开"源包算两遍"，把判据改成"只看源包"或"干脆不看空间"。
        /// 判据只有一个数（内容物 + 过程物），内容物真超过可用空间时必须拦。</para>
        /// </summary>
        [Fact]
        public void 内容物真的超过可用空间_照旧拒绝并说清差多少()
        {
            var estimate = new TaskSpaceEstimate
            {
                DisplayName = "内容物 40 GiB 的包",
                SourceBytes = 2 * Gib,
                ContentBytes = 40 * Gib,
                ProcessArtifactBytes = 0,
                ContentEstimated = false,
                HasListing = true,
                Basis = "测试用：清单算出来的内容物"
            };

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { RealCaseTask() },
                _ => estimate,
                RealAvailable,
                RealReserve,
                requestedParallelCount: 1);

            ScheduledExtractionItem blocked = Assert.Single(plan.BlockedAtPlanTime);

            Assert.Equal(40 * Gib, blocked.RequiredBytes);
            Assert.Equal((40 * Gib) - (RealAvailable - RealReserve), blocked.ShortfallBytes);
            Assert.True(blocked.ShortfallBytes > 0);
        }

        // ================================================================ ③ 「空间不足」模式：同一口径

        /// <summary>
        /// ①页「空间不足」模式**不需要另立一套判据**：源包在两种档下都不进需求（它已经在盘上），
        /// 所以模式开着时真机那一单照样放行；模式的差别只体现在"跑完把源包收回来、下一个包更宽"
        /// （账本每个任务收尾都真实重探可用空间）。
        ///
        /// <para>同时钉住另一半：排序键换成"净占用"**不等于**放宽放行 ——
        /// 净占用（内容物）只有 1 GiB、可过程物要 40 GiB 的那个包仍然必须被拦下。</para>
        /// </summary>
        [Fact]
        public void 空间不足模式_真机那一单照旧放行_而净占用小不等于放行()
        {
            TaskSpaceEstimate real = RealCaseEstimate();

            ExtractionSchedulePlan tight = ExtractionScheduler.Build(
                new[] { RealCaseTask() },
                _ => real,
                RealAvailable,
                RealReserve,
                requestedParallelCount: 1,
                sortKey: item => item.Estimate.NetOccupancyBytes,
                orderBasis: ExtractionSchedulePlan.NetOccupancyOrderBasis);

            Assert.Empty(tight.BlockedAtPlanTime);
            Assert.True(tight.Ordered[0].FitsAlone);

            // 放行判据还是"内容物 + 过程物"，不是净占用。
            Assert.Equal(real.FreeSpaceDemandBytes, tight.Ordered[0].RequiredBytes);
            Assert.Equal(real.NetOccupancyBytes, real.ContentBytes);

            var spiky = new TaskSpaceEstimate
            {
                DisplayName = "净占用小但过程物大的包",
                SourceBytes = Gib,
                ContentBytes = Gib,
                ProcessArtifactBytes = 40 * Gib,
                Basis = "测试用：净占用 1 GiB、过程物 40 GiB"
            };

            ExtractionSchedulePlan spikyPlan = ExtractionScheduler.Build(
                new[] { RealCaseTask() },
                _ => spiky,
                RealAvailable,
                RealReserve,
                requestedParallelCount: 1,
                sortKey: item => item.Estimate.NetOccupancyBytes,
                orderBasis: ExtractionSchedulePlan.NetOccupancyOrderBasis);

            ScheduledExtractionItem blocked = Assert.Single(spikyPlan.BlockedAtPlanTime);

            Assert.Equal(41 * Gib, blocked.RequiredBytes);
            Assert.False(blocked.FitsAlone);
        }

        // ================================================================ ④ 真样本（只读）

        /// <summary>
        /// **真机那一组真实分卷 + 真机那一刻的真实可用空间**（只读，⛔ 一个字节都不动）：
        /// 用产品自己的代码算一遍判据 —— 组识别、估算、可用空间探测、调度。
        ///
        /// <para>环境变量 <c>ARCHIVEFIXER_REAL_SPACE_CASE_DIR</c> 指向放着那一组分卷的目录
        /// （或放到仓库同级 <c>_tmp\ArchiveFixer\space-real\</c>）；不在就**跳过并说明**，
        /// 与 <c>RealVolumeSampleTests</c> 同一套做法。⛔ 真机路径绝不写进仓库。</para>
        ///
        /// <para><b>为什么必须真跑一次</b>（AGENTS §11 的验收规则）：合成样本只能证明"我以为的形状我算得对"。
        /// 这一条在真文件上断言"源包不进判据"（<c>PeakBytes − FreeSpaceDemandBytes == 源包</c>），
        /// 并把真机数字打出来 —— 谁把判据改回峰值，它当场变红。</para>
        /// </summary>
        [RealSpaceCaseFact]
        public void 真样本只读_那一组真实分卷_判据里不含源包_真机可用空间下必须放行()
        {
            string directory = RealSpaceCaseFactAttribute.CaseRoot;

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(
                Directory.GetFiles(directory).Select(
                    file => new VolumeCandidate { Path = file, Size = LengthOf(file) }));

            VolumeGroup group = groups
                .OrderByDescending(candidate => candidate.Volumes.Sum(volume => LengthOf(volume.Path)))
                .First();

            var task = new ArchiveTask(group.FirstVolumePath, 1)
            {
                FileName = Path.GetFileName(group.FirstVolumePath)
            };

            foreach (VolumeCandidate volume in group.Volumes)
            {
                task.VolumePaths.Add(volume.Path);
            }

            long groupBytes = task.VolumePaths.Sum(LengthOf);
            TaskSpaceEstimate estimate = SpaceEstimator.FromSourceFiles(task);

            long? available = SpaceChecker.GetAvailableFreeSpace(directory);

            Assert.True(available.HasValue, $"取不到 {directory} 所在盘的可用空间");
            Assert.True(groupBytes > 0, "这一组真实分卷一个字节都没量到");

            long reserve = SpaceGate.DefaultReserveBytes;

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { task },
                _ => estimate,
                available,
                reserve,
                requestedParallelCount: 1);

            _output.WriteLine(
                $"真机只读：{group.Volumes.Count} 卷 / 源包 {groupBytes} 字节（{TaskSpaceEstimate.FormatSize(groupBytes)}）/ "
                + $"内容物估 {estimate.ContentBytes} / 放行需求 {estimate.FreeSpaceDemandBytes} / "
                + $"盘上总占地 {estimate.PeakBytes} / 可用 {available.Value}（{TaskSpaceEstimate.FormatSize(available.Value)}）/ "
                + $"预算 {available.Value - reserve}");

            // ① 源包整组求和（真实字节数）。
            Assert.Equal(groupBytes, estimate.SourceBytes);

            // ② **源包只在"总占地"里，不在判据里** —— 这一条就是这次修复的全部内容。
            Assert.Equal(estimate.SourceBytes, estimate.PeakBytes - estimate.FreeSpaceDemandBytes);
            Assert.Equal(estimate.FreeSpaceDemandBytes, plan.Ordered[0].RequiredBytes);

            // ③ 真机上"够就放行、不够就如实拦下"必须与实际算式一致（⛔ 不允许静默跳过）。
            bool shouldPass = estimate.FreeSpaceDemandBytes <= available.Value - reserve;
            bool didPass = plan.BlockedAtPlanTime.Count == 0;

            if (shouldPass)
            {
                _output.WriteLine("→ 结论：放行（需求 ≤ 可用 − 余量）。");
            }
            else
            {
                ScheduledExtractionItem blocked = plan.BlockedAtPlanTime[0];

                _output.WriteLine(
                    $"→ 结论：拦下 —— 需要 {blocked.RequiredBytes}，差 {blocked.ShortfallBytes}。");
            }

            Assert.Equal(shouldPass, didPass);
        }

        // ================================================================ 装配

        private readonly ITestOutputHelper _output;

        public SpaceDemandAccountingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static long LengthOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0L;
            }
        }

        /// <summary>
        /// 真机那一单的合成估算（三个字段都是真机上的真实数字）。
        ///
        /// <para>⛔ 不造 17.7 GiB 的真样本：那既慢又会因为盘满而失败 ——
        /// 那样测的是这台机器的磁盘，不是判据（与 <c>SpaceModeTests</c> 里"调度测试不造大文件"同一条理由）。
        /// 真文件那条路由 <see cref="真样本只读_那一组真实分卷_判据里不含源包_真机可用空间下必须放行"/> 覆盖。</para>
        /// </summary>
        private static TaskSpaceEstimate RealCaseEstimate()
        {
            return new TaskSpaceEstimate
            {
                TaskPath = @"X:\下载\resource.7z.001",
                DisplayName = "resource.7z.001",
                SourceBytes = RealSourceTotal,
                ContentBytes = RealSourceTotal,
                ProcessArtifactBytes = 0,
                ContentEstimated = true,
                HasListing = false,
                Basis = "测试用：真机那一单的真实字节数（内容物按源包 1 倍估，清单还没读）"
            };
        }

        private static ArchiveTask RealCaseTask()
        {
            var task = new ArchiveTask(@"X:\下载\resource.7z.001", 1)
            {
                FileName = "resource.7z.001",
                IsArchive = true,
                DetectedFormat = "7Z",
                IsVolumeGroup = true
            };

            task.VolumePaths.Add(@"X:\下载\resource.7z.001");

            return task;
        }
    }

    /// <summary>
    /// "这台机器上有真机那一组真实分卷"用例的开关（用户 2026-09-29 当场报的那一单）。
    ///
    /// <para>目录由环境变量 <c>ARCHIVEFIXER_REAL_SPACE_CASE_DIR</c> 指定，
    /// 或放在仓库同级的 <c>_tmp\ArchiveFixer\space-real\</c>。用例**只读**：只 stat 文件、只查 DriveInfo，
    /// ⛔ 不改名、不复制、不写任何东西到那个目录。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealSpaceCaseFactAttribute : FactAttribute
    {
        /// <summary>真机那一组样本所在目录的环境变量名（⛔ 真机路径不写进仓库）。</summary>
        internal const string DirectoryEnvironmentVariable = "ARCHIVEFIXER_REAL_SPACE_CASE_DIR";

        /// <summary>放在仓库同级 <c>_tmp\ArchiveFixer\</c> 下的默认目录名（换机器/换盘符都成立）。</summary>
        private const string RelativeCaseDirectory = "space-real";

        internal static readonly string CaseRoot = ResolveCaseRoot();

        public RealSpaceCaseFactAttribute()
        {
            if (!HasCase())
            {
                Skip = $@"真样本不在（环境变量 {DirectoryEnvironmentVariable} 没设，"
                       + @"仓库同级 _tmp\ArchiveFixer\space-real 里也没有分卷文件）—— 跳过真机只读用例。";
            }
        }

        internal static bool HasCase()
        {
            try
            {
                return Directory.Exists(CaseRoot) && Directory.GetFiles(CaseRoot, "*").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveCaseRoot()
        {
            string? configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured!;
            }

            // 从测试程序集目录向上找含 ArchiveFixer.slnx 的目录，再回到它的**同级**（与真样本用例同一套）。
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string? parent = directory.Parent?.FullName;

                    return string.IsNullOrWhiteSpace(parent)
                        ? Path.Combine(directory.FullName, "_tmp", "ArchiveFixer", RelativeCaseDirectory)
                        : Path.Combine(parent!, "_tmp", "ArchiveFixer", RelativeCaseDirectory);
                }

                directory = directory.Parent;
            }

            return Path.Combine(Path.GetTempPath(), "ArchiveFixer", RelativeCaseDirectory);
        }
    }
}
