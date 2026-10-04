using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **需求口径「按净增量」（用户 2026-10-03 拍板的第 ③ 条）**的守门用例。
    ///
    /// <para>老口径（<c>NestedExpansionAllowance = 1.0</c>）：内层包**自身**已经算在内容物里，
    /// 又要为它"再展开"按**整份**再扣一遍 ⇒ 需求 = 内容物 + 内层包 = 两层都被算了两遍
    /// （这正是朋友那台"60 G 报不够"的根子）。</para>
    ///
    /// <para>新口径：内层包再展开按**净增量**估（展开后 − 包本体 ≈ 0，因为资源包本来就是压过一遍的东西）
    /// ⇒ 需求 = 内容物 + 抠取副本，且**永远 ≥ 内容物**（不得小于内容物）。</para>
    ///
    /// <para>用**真 7z 造的多层链**测（AGENTS.md §11：合成假引擎证明不了真引擎一层层套出来的形状）。</para>
    /// </summary>
    public class SpaceNetIncrementTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sevenZip;
        private readonly ITestOutputHelper _output;

        public SpaceNetIncrementTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpaceNet", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        /// <summary>每一层的内容物都取 2 MiB 不可压数据（保证内层包与展开后的量级可比）。</summary>
        private const int PayloadBytes = 2 * 1024 * 1024;

        /// <summary>
        /// **用例 D（需求口径）**：同一单在改口径前后，<c>FreeSpaceDemandBytes</c> 必须
        /// **≤ 老口径那个值**（内层包不再按整份扣一遍）且 **≥ 内容物**（不得小于内容物）；
        /// 而且空间门 / 调度 <c>RequiredBytes</c> 读到的都是**同一个数字**（唯一出口没被绕过）。
        ///
        /// <para><b>红检</b>：把 <c>SpaceEstimator.NestedExpansionAllowance</c> 改回 <c>1.0</c> ⇒
        /// 下面第一条「过程物 = 抠取副本」立刻变红（那正是"内层包被算了两遍"）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例D_净增量口径_需求不大于老口径且不小于内容物()
        {
            (ArchiveTask task, ArchiveListResult list) = await BuildScenarioAsync();

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);
            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(cheap, list);

            long innerBytes = SumInnerArchiveBytes(list);
            long carvedBytes = SpaceEstimator.EstimateCarvedBytes(task, refined.SourceBytes);
            long oldDemand = refined.ContentBytes + carvedBytes + innerBytes;

            _output.WriteLine(
                $"【实测】老口径需求 {oldDemand} → 新口径需求 {refined.FreeSpaceDemandBytes}"
                + $"（内容物 {refined.ContentBytes} / 内层包 {innerBytes} / 抠取副本 {carvedBytes}）"
                + $" ｜ 盘上总占地 {refined.PeakBytes}");

            // 夹具必须真有内层包，否则这一条测不出"净增量"。
            Assert.True(innerBytes > 0, "夹具里没有内层包 —— 这条用例就白测了");

            // ① 过程物只剩"真量出来的抠取副本"：内层包**不再**按整份另扣一遍。
            Assert.Equal(carvedBytes, refined.ProcessArtifactBytes);

            // ② 铁律：放行需求永远不得小于内容物。
            Assert.True(
                refined.FreeSpaceDemandBytes >= refined.ContentBytes,
                $"放行需求 {refined.FreeSpaceDemandBytes} 不得小于内容物 {refined.ContentBytes}");

            // ③ 新口径必须真的比老口径小（老口径 = 内容物 + 抠取副本 + 内层包整份）。
            Assert.True(
                refined.FreeSpaceDemandBytes < oldDemand,
                $"新口径 {refined.FreeSpaceDemandBytes} 应当小于老口径 {oldDemand}（内层包不再算两遍）");

            Assert.Equal(refined.ContentBytes + carvedBytes, refined.FreeSpaceDemandBytes);

            // ④ 三处消费点读到同一个数字：调度 RequiredBytes 就是它，空间门收到的也是它。
            long available = refined.FreeSpaceDemandBytes + SpaceGate.DefaultReserveBytes + (64L * 1024 * 1024);
            long reserve = SpaceGate.DefaultReserveBytes;

            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { task },
                _ => refined,
                available,
                reserve,
                requestedParallelCount: 1);

            Assert.Equal(refined.FreeSpaceDemandBytes, plan.Ordered[0].RequiredBytes);

            SpaceGateDecision gate = SpaceGate.Check(plan.Ordered[0].RequiredBytes, available, reserve);

            Assert.True(gate.Allowed, gate.ToLogLine());
        }

        /// <summary>
        /// 只读量一遍：真三层链上把 <see cref="TaskSpaceEstimate"/> 的四个数原样打出来
        /// （这就是报告里"改动前 → 改动后"那组数字的来源）。
        /// </summary>
        [SevenZipFact]
        public async Task 净增量口径_真三层链_四个数实测()
        {
            (ArchiveTask task, ArchiveListResult list) = await BuildScenarioAsync();

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);
            TaskSpaceEstimate refined = SpaceEstimator.RefineWithListing(cheap, list);

            long innerBytes = SumInnerArchiveBytes(list);
            long carvedBytes = SpaceEstimator.EstimateCarvedBytes(task, refined.SourceBytes);

            // 老口径那个数（内层包再按 1.0 倍扣一遍）—— 只用于报告与"新 ≤ 旧"这条断言。
            long oldDemand = refined.ContentBytes + carvedBytes + innerBytes;

            _output.WriteLine(
                $"【实测】源包 {refined.SourceBytes} / 内容物 {refined.ContentBytes} / 过程物 {refined.ProcessArtifactBytes}"
                + $" / 放行需求 {refined.FreeSpaceDemandBytes} / 盘上总占地 {refined.PeakBytes}"
                + $" ｜ 内层包 {innerBytes} / 抠取副本 {carvedBytes} / 老口径需求 {oldDemand}");
            _output.WriteLine($"依据：{refined.Basis}");

            // 铁律：放行需求永远不得小于内容物（⛔ 这一条与口径无关，改前改后都必须成立）。
            Assert.True(
                refined.FreeSpaceDemandBytes >= refined.ContentBytes,
                $"放行需求 {refined.FreeSpaceDemandBytes} 不得小于内容物 {refined.ContentBytes}");
        }

        // ================================================================ 造样本

        /// <summary>
        /// 真三层链：<c>src\outer.7z → level2.7z → level3.7z → payload.bin</c>，
        /// 每一层的清单都真的问一遍引擎（返回**最外层**那一份清单）。
        /// </summary>
        private async Task<(ArchiveTask Task, ArchiveListResult List)> BuildScenarioAsync()
        {
            string build = Path.Combine(_root, "build");
            Directory.CreateDirectory(build);

            // 不可压数据：复制同一个块比 Random 快得多，而 7z 压不动它（量级不会被压缩比搅乱）。
            byte[] block = new byte[1024 * 1024];
            new Random(20261003).NextBytes(block);

            using (FileStream stream = File.Create(Path.Combine(build, "payload.bin")))
            {
                stream.Write(block, 0, block.Length);
                stream.Write(block, 0, block.Length);
            }

            Run7z(build, "a", "-t7z", "-mx1", "level3.7z", "payload.bin");
            Run7z(build, "a", "-t7z", "-mx1", "level2.7z", "level3.7z");

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", "-mx1", outer, "level2.7z");

            var task = new ArchiveTask(outer, 1)
            {
                FileName = "outer.7z",
                VolumeGroupKey = "outer"
            };

            var engine = new SevenZipEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(outer), CancellationToken.None);

            Assert.True(list.Success, list.Message);

            return (task, list);
        }

        /// <summary>清单里"解出来还要再解一层"的那些条目占多少字节（内层包本体）。</summary>
        private static long SumInnerArchiveBytes(ArchiveListResult list)
        {
            long total = 0;

            foreach (ArchiveEntry? entry in list.Entries ?? Array.Empty<ArchiveEntry>())
            {
                if (entry == null || entry.IsDirectory || !SpaceEstimator.LooksLikeNestedArchive(entry.Path))
                {
                    continue;
                }

                total += entry.Size > 0 ? entry.Size : 0L;
            }

            return total;
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }
    }
}
