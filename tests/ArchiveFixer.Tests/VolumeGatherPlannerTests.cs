using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **分卷收拢决策器**的守门用例（用户 2026-10-17 拍板的四步算法里的 ②分组 + ③硬链接试开证实）。
    ///
    /// <para>要钉住的三条：</para>
    /// <list type="number">
    /// <item><description>**不完整** ⇒ 一个字节都不搬（原地不动）；</description></item>
    /// <item><description>**完整但试开不过** ⇒ 一个字节都不搬；</description></item>
    /// <item><description>**完整 + 体积规律对 + 试开通过** ⇒ 才决定搬，且按卷序给出那几份。</description></item>
    /// </list>
    /// </summary>
    public sealed class VolumeGatherPlannerTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-gatherplanner-" + Guid.NewGuid().ToString("N"));

        public VolumeGatherPlannerTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响用例结论。
            }
        }

        private string Make(string name, long size)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        [Fact]
        public void 完整的一组_体积规律对且试开通过_才决定搬()
        {
            string first = Make("x.7z.001", 4096);
            string second = Make("x.7z.002", 4096);
            string last = Make("x.7z.003", 1024);   // 末卷可以更小

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, second, last },
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Gather, plan.Action);
            Assert.Equal(3, plan.Pieces.Count);
            Assert.Equal(first, plan.Pieces[0]);
        }

        [Fact]
        public void 不完整_一个字节都不搬()
        {
            string first = Make("y.7z.001", 4096);
            string third = Make("y.7z.003", 1024);   // 缺第 2 卷

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, third },
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
            Assert.Contains("不完整", plan.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 完整但试开不过_整组不动()
        {
            string first = Make("z.7z.001", 4096);
            string second = Make("z.7z.002", 4096);

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, second },
                _root,
                probeOpens: _ => false);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
            Assert.Contains("试开", plan.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 体积不规则_不搬()
        {
            string first = Make("w.7z.001", 4096);
            string second = Make("w.7z.002", 2048);   // 中间片比满片小 ⇒ 不合"除末卷外等大"
            string last = Make("w.7z.003", 1024);

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, second, last },
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
            Assert.Contains("体积规律", plan.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 末卷比满片还大_不搬()
        {
            string first = Make("v.7z.001", 4096);
            string last = Make("v.7z.002", 8192);     // 末卷比满片大 ⇒ 判不出

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, last },
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
            Assert.Contains("体积规律", plan.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void 只有一片_判不出是哪一组_不搬()
        {
            string single = Make("u.7z.001", 4096);

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { single },
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
        }

        [Fact]
        public void 空候选_不搬()
        {
            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                Array.Empty<string>(),
                _root,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
        }

        [Fact]
        public void 没有入口层_不搬()
        {
            string first = Make("t.7z.001", 4096);
            string second = Make("t.7z.002", 4096);

            VolumeGatherPlan plan = VolumeGatherPlanner.Decide(
                new[] { first, second },
                entryLayer: null,
                probeOpens: _ => true);

            Assert.Equal(VolumeGatherAction.Leave, plan.Action);
        }
    }
}
