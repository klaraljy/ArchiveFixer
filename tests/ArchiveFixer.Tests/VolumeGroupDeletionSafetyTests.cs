using System;
using System.IO;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **分卷组完整性闸门的守门用例**（用户 2026-09-30 真机事故：25 GB 被当"其余物"永久删除）。
    ///
    /// <para><b>现场</b>：外层 RAR 解出一组 PKZIP 跨盘 —— <c>一只顶美.z01…z05</c> + 末卷 <c>一只顶美.zip</c>，
    /// 而末卷的名字被改坏成 <c>一只顶美.z删除ip</c> ⇒ 认不出是归档。老判据只看"扩展名像不像分卷"，
    /// 于是剩下 5 卷每一卷单看都像"待续解的过程物"，全被打包收进**可删的**其余物；
    /// 再叠加「空间不足」模式的永久删除 ⇒ 那 25 GB 当场没了，而日志写着"解压成功 ｜ 校验通过"。</para>
    ///
    /// <para><b>红线</b>：分卷要进"可删的其余物"，必须**先能证明整组完整**；证明不了 ⇒
    /// 整份定稿计划作废（<see cref="ExtractionCoordinator.FinalLayoutPlan.Failed"/>），
    /// 于是"源包删除 / 其余物删除"这两条（都挂在"定稿 + 校验通过"之后）一个都不会发生。</para>
    ///
    /// <para><b>红检</b>：把 <c>PlanFinalLayout</c> 里那道闸门去掉（或让
    /// <c>TryConfirmVolumeGroupComplete</c> 恒返回 true），本文件第 1、3 条立刻变红
    /// （计划不再是 Failed，缺末卷/缺首卷的那些卷又进了 <c>ProcessArtifactSources</c>）。</para>
    /// </summary>
    public sealed class VolumeGroupDeletionSafetyTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupDeletionSafetyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-volume-safety-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void ZIP跨盘缺末卷_整份计划作废_一卷都不许当成可删的其余物()
        {
            string stage = CreateStage("一只顶美.z01", "一只顶美.z02", "一只顶美.z03", "一只顶美.z04", "一只顶美.z05");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "一只顶美");

            Assert.True(plan.Failed, "缺末卷（同名 .zip）的跨盘组必须让整份计划作废");
            Assert.Contains("不完整", plan.FailureReason, StringComparison.Ordinal);
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        [Fact]
        public void ZIP跨盘末卷在_照常按其余物处理_闸门不许误伤完整组()
        {
            string stage = CreateStage(
                "一只顶美.z01", "一只顶美.z02", "一只顶美.z03", "一只顶美.z04", "一只顶美.z05", "一只顶美.zip");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "一只顶美");

            Assert.False(plan.Failed);
            Assert.NotEmpty(plan.ProcessArtifactSources);
        }

        [Fact]
        public void 数字分卷缺首卷_整份计划作废()
        {
            string stage = CreateStage("x.7z.002", "x.7z.003");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "x");

            Assert.True(plan.Failed, "只剩后续卷（缺 .001）必须让整份计划作废");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        [Fact]
        public void 数字分卷首卷在_照常按其余物处理()
        {
            string stage = CreateStage("x.7z.001", "x.7z.002", "x.7z.003");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "x");

            Assert.False(plan.Failed);
            Assert.NotEmpty(plan.ProcessArtifactSources);
        }

        /// <summary>
        /// **RAR 老式分卷族**（用户要求：ZIP 出过问题 ≠ 7z / RAR 就安全，三个格式都要覆盖）：
        /// 只解出 <c>.r00…</c> 而缺首卷 <c>x.rar</c> ⇒ 整组不完整 ⇒ 整份计划作废。
        /// </summary>
        [Fact]
        public void RAR老式分卷缺首卷_整份计划作废()
        {
            string stage = CreateStage("x.r00", "x.r01");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "x");

            Assert.True(plan.Failed, "缺 x.rar 的 .r00 族必须让整份计划作废");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        /// <summary>RAR 新式分卷族：只解出 <c>.part02.rar</c> 而缺 <c>.part01.rar</c> ⇒ 作废。</summary>
        [Fact]
        public void RAR新式分卷缺首卷_整份计划作废()
        {
            string stage = CreateStage("x.part02.rar", "x.part03.rar");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "x");

            Assert.True(plan.Failed, "缺 .part01.rar 的 part 族必须让整份计划作废");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        /// <summary>RAR part 族完整时不许误伤：<c>.part01/.part02/.part03</c> 齐全 ⇒ 照常按其余物处理。</summary>
        [Fact]
        public void RAR新式分卷完整_照常按其余物处理()
        {
            string stage = CreateStage("x.part01.rar", "x.part02.rar", "x.part03.rar");

            ExtractionCoordinator.FinalLayoutPlan plan = ExtractionCoordinator.PlanFinalLayout(
                stage, Path.Combine(_root, "out"), sharedOutputRoot: false, "x");

            Assert.False(plan.Failed);
            Assert.NotEmpty(plan.ProcessArtifactSources);
        }

        /// <summary>造一个只含这些文件的暂存目录（内容随便，闸门只看文件名与目录事实）。</summary>
        private string CreateStage(params string[] names)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stage);

            foreach (string name in names)
            {
                File.WriteAllText(Path.Combine(stage, name), name);
            }

            return stage;
        }
    }
}
