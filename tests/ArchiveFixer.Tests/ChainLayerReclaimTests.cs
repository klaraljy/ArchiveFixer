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
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **普通档「逐层回收」**（用户 2026-10-03 拍板的大改）在真链路端到端的行为。
    ///
    /// <para>规格书：`_tmp\方案-普通档逐层回收其余物.md`（§1 目标 / §3 七条红线 / §5 用例 A~D /
    /// §7 B 档位分叉 + §7.1 用例 E~F）。这里钉四件事：</para>
    /// <list type="number">
    /// <item><description><b>用例 A</b>：普通档 + 「彻底删除」⇒ **每层当场回收**这一层的内层包（走"删这一层的源"
    /// 那个执行体），最外层源包留到链尾才被处理；峰值 = 源包 + 当前层 + 下一层（≤ 3）。</description></item>
    /// <item><description><b>用例 B</b>：中间某一层失败 ⇒ **那一层的过程物一个字节都不动**、源包原地不动、
    /// 其余物不生成；已成功回收的上层不回滚（用户明确接受）。</description></item>
    /// <item><description><b>用例 C</b>：「空间不足」模式**一个字不改** —— 仍每层各删各的（含最外层源包），
    /// 峰值 ≤ 2 倍源包（用户给的新判据）。</description></item>
    /// <item><description><b>用例 E（B 档位分叉）**：「移入回收站」档**一层都不当场回收**，
    /// 内层包到链尾才处理（回收站只调一次）；「彻底删除」档才逐层。</description></item>
    /// </list>
    ///
    /// <para><b>为什么必须用真 7z</b>（AGENTS.md §11 验收规则）：合成假引擎证明得了"我按自己以为的形状处理得对"，
    /// 证明不了"真的 7z 一层层套出来的东西我处理得对"。所以整条链都当真：真 7z.exe 造包、
    /// 真 <see cref="SevenZipEngine"/>、真 <see cref="MainViewModel"/> 与真的解压管线。</para>
    ///
    /// <para><b>⛔ 两条自我约束</b>：① 绝不往**用户的系统回收站**里塞东西 —— 回收站那一档全部走注入的假执行器
    /// （<c>ExtractionCoordinator.RestDeleteExecutor</c> 这个测试缝）；② 只在临时目录里动真实文件系统，
    /// <see cref="Dispose"/> 里清干净。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ChainLayerReclaimTests : IDisposable
    {
        /// <summary>拿它给某一层加密码、密码本里没有 ⇒ 那一层必然失败（用例 B）。</summary>
        private const string UnknownLayerPassword = "测试用未知密码";

        private readonly string _root;
        private readonly string _sevenZip;
        private readonly ITestOutputHelper _output;

        public ChainLayerReclaimTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLayerReclaim", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 用例 A（逐层回收）

        /// <summary>
        /// <b>用例 A</b>：四层真链 + 普通档 + ③页「彻底删除」⇒
        /// ① 三个内层包**各自那一层**就被就地删掉了（走"删这一层的源"那个执行体）；
        /// ② **最外层源包不在那份名单里**（它由链尾那一档处理，不是逐层回收删的）；
        /// ③ 全程盘上的包不超过 3 个（源包 + 当前层 + 下一层）；老口径攒到链尾是 4 个；
        /// ④ 跑完之后内层包与源包都没了（源包由链尾"放入其余物 + 彻底删除"处理掉），内容物在。
        /// </summary>
        [SevenZipFact]
        public async Task 用例A_普通档彻底删除_每层当场回收内层包_源包留到链尾()
        {
            const int layers = 4;

            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers);

            long demand = await MeasureDemandAsync(outer);

            using var sampler = new DiskSampler(_root);

            await harness.AddPathsAsync(outer);

            sampler.Start();

            OneClickOutcome outcome = await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            sampler.Stop();

            _output.WriteLine(
                $"【用例A】峰值包数 {sampler.MaxPackageCount} / 峰值字节 {sampler.MaxPackageBytes}"
                + $"（精估放行需求 {demand} ⇒ 比值 {(double)sampler.MaxPackageBytes / demand:0.00}）"
                + $" ｜ 逐层回收删了 {harness.SourceDeletes.DeletedPaths.Count} 个"
                + $"：{string.Join("、", harness.SourceDeletes.DeletedPaths.Select(Path.GetFileName))}");

            // ① 内容物出来了（不然"内层包都没了"可能只是整条链没跑）。
            Assert.NotEmpty(FindFiles("final.txt"));

            // ② 三个内层包各自那一层就被回收了（level2/3/4，由"删这一层的源"那个执行体删的）。
            Assert.Equal(layers - 1, harness.SourceDeletes.DeletedPaths.Count);

            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level2.7z"));
            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level3.7z"));
            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level4.7z"));

            // ③ ⛔ 最外层源包**不**由这一档删（它留到链尾按「源包操作 + 删除操作」处理）。
            Assert.DoesNotContain(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "outer.7z"));

            // ④ 峰值 = 源包 + 当前层 + 下一层（≤ 3）；老口径（攒到链尾）会是 4。
            Assert.True(
                sampler.MaxPackageCount <= 3,
                $"逐层回收下峰值不该超过 3 个包（源包 + 当前层 + 下一层），实测 {sampler.MaxPackageCount}："
                + string.Join("、", sampler.PeakSnapshot));

            // ⑤ 跑完：源包与内层包都不在盘上了（源包走的是链尾那一档）。
            Assert.Empty(FindFiles("*.7z"));

            // ⑥ 每一层都要**说出来**（"我的内层包哪去了"必须能事后回答）。
            int purgeLines = harness.Log.Logs.Count(
                entry => entry.Message.Contains("已立刻永久删除", StringComparison.Ordinal));

            Assert.True(purgeLines == layers - 1, $"该有 {layers - 1} 条逐层回收日志，实际 {purgeLines} 条。");

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("逐层回收", StringComparison.Ordinal));
        }

        // ================================================================ 用例 B（失败那一层不动）

        /// <summary>
        /// <b>用例 B</b>：四层真链 + 普通档 + 「彻底删除」，但**第三层解不开**（那一层的包设了密码、
        /// 密码本里没有它）⇒
        /// ① 第二层（成功的那一层）当场回收了它自己的源（level2.7z）—— 已回收的不回滚（用户明确接受）；
        /// ② **第三层**的过程物（level3.7z）与它产出的 level4.7z **一个字节都不动**；
        /// ③ 最外层源包原地不动；④ 其余物一个都不生成。
        /// </summary>
        [SevenZipFact]
        public async Task 用例B_中间那一层失败_那一层的过程物一个字节不动_源包原地不动()
        {
            const int layers = 4;
            const int failingLevel = 3;

            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildChain(layers, passwordLevel: failingLevel);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            _output.WriteLine(
                $"【用例B】逐层回收删了 {harness.SourceDeletes.DeletedPaths.Count} 个："
                + string.Join("、", harness.SourceDeletes.DeletedPaths.Select(Path.GetFileName)));

            // ① 只有第二层那一层回收成功过（它自己那一层的源 = level2.7z）。
            Assert.True(
                harness.SourceDeletes.DeletedPaths.Count == 1,
                "只有「第二层」这一层该回收，实际删了 " + harness.SourceDeletes.DeletedPaths.Count + " 个："
                + string.Join("、", harness.SourceDeletes.DeletedPaths));

            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level2.7z"));

            // ② 失败那一层的过程物一个字节都不动（level3.7z 就是它的源；第四层的包根本没被解出来）。
            string[] failedLayerSources = FindFiles("level3.7z");

            Assert.NotEmpty(failedLayerSources);

            /*
             * ⚠ 2026-10-03（第 ② 条）：链尾那一档**只处理最外层源包** —— 内层包已经在各层回收过了，
             * 链尾⛔ 不许再走"把内层包收进其余物"那一段（老口径是攒到链尾统一收）。
             * 判据取那一段唯一的日志（"内层包已移入其余物"）：它在，就说明链尾又去搬内层包了。
             */
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("内层包已移入其余物", StringComparison.Ordinal));

            // ③ 失败那一层的内容物当然也没出来（这一层真的失败了，不是"其实成功了"）。
            Assert.Empty(FindFiles("layer3.txt"));
            Assert.Empty(FindFiles("final.txt"));

            // ④ 已成功回收的上一层**不回滚**（用户 2026-09-29 明确接受："原包是已经没有了但是留下的不会破"）。
            Assert.Empty(FindFiles("level2.7z"));

            // ⑤ 源包原地不动（⛔ 红线 1）。
            Assert.True(File.Exists(outer), "失败 / 部分完成 ⇒ 源包必须原地不动");

            // ⑥ 链没跑完 ⇒ 链尾那一档**什么都没做**：没有任何"整份删除 / 搬进回收站"的动作。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("彻底删除：", StringComparison.Ordinal)
                         || entry.Message.Contains("移入回收站：", StringComparison.Ordinal));

            /*
             * ⑦ 其余物里只有过程物：源包是「留在原地」档，**一个字节都没进其余物**
             * （其余物本身会为过程物而存在 —— 那是既有机制，与"失败不删东西"不冲突）。
             */
            foreach (string restDirectory in FindDirectories(ProcessArtifactLayout.ArtifactDirectoryName))
            {
                Assert.DoesNotContain(
                    Directory.GetFiles(restDirectory, "*", SearchOption.AllDirectories),
                    path => IsNamed(path, "outer.7z"));
            }

            // ⑧ 失败那一层要落成失败（不变量 6），而不是"成功"或"部分完成"。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);
        }

        /// <summary>
        /// <b>红线守门（「移入回收站」档 + 中间那一层失败）</b>：那一档不逐层回收，内层包要等链尾 ——
        /// 而链没跑完 ⇒ 链尾那一档整段不执行 ⇒ **一个字节都不许被删 / 不许被回收**，
        /// 源包那一份也**必须还在盘上**（用户 2026-10-03 的原话："原包还是比较重要的"）。
        /// </summary>
        [SevenZipFact]
        public async Task 用例B2_移入回收站档_中间那一层失败_源包还在_回收站零调用()
        {
            const int layers = 4;
            const int failingLevel = 3;

            Harness harness = CreateHarness(RestHandlingModes.RecycleBin, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers, passwordLevel: failingLevel);

            var recycleExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = recycleExecutor;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            // ① 源包那一份还在盘上（第 1 层自己成功过 ⇒ 它按用户选的档进了其余物；谁都没删它）。
            Assert.NotEmpty(FindFiles("outer.7z"));

            // ② 一个字节都没被回收 / 永久删除（链没跑完 ⇒ 链尾那一档整段不执行）。
            Assert.Empty(recycleExecutor.RecycleCalls);
            Assert.Empty(recycleExecutor.PermanentCalls);
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ③ 失败那一层的过程物还在（红线 3：那一层的过程物一个字节都不动）。
            Assert.NotEmpty(FindFiles("level3.7z"));

            // ④ 失败那一层要落成失败（不变量 6）。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);
        }

        // ================================================================ 用例 C（空间不足模式不变）
        /// <summary>
        /// <b>用例 C</b>：「空间不足」模式**一个字不改** —— 仍每层各删各的（**连最外层源包**都当场删），
        /// 而且峰值 ≤ 2 倍源包（用户 2026-10-03 给的新判据）。
        /// </summary>
        [SevenZipFact]
        public async Task 用例C_空间不足模式不变_每层各删各的_峰值不超过两倍源包()
        {
            const int layers = 4;

            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers);

            long outerBytes = new FileInfo(outer).Length;

            using var sampler = new DiskSampler(_root);

            await harness.AddPathsAsync(outer);

            harness.Vm.SpaceTightMode = true;

            sampler.Start();

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            sampler.Stop();

            _output.WriteLine(
                $"【用例C】峰值包数 {sampler.MaxPackageCount} / 峰值字节 {sampler.MaxPackageBytes}"
                + $"（源包 {outerBytes}，两倍 = {2 * outerBytes}）");

            Assert.NotEmpty(FindFiles("final.txt"));

            // ① 每一层各删各的，**连最外层源包**（四个包全走"删这一层的源"那个执行体）。
            Assert.Equal(layers, harness.SourceDeletes.DeletedPaths.Count);
            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "outer.7z"));

            // ② 峰值 ≤ 2 倍源包（用户给的新判据）。
            Assert.True(
                sampler.MaxPackageBytes <= 2 * outerBytes,
                $"空间不足模式的峰值必须 ≤ 2 倍源包：实测 {sampler.MaxPackageBytes} > {2 * outerBytes}");

            // ③ 同时不超过两个包（当前层 + 下一层）。
            Assert.True(sampler.MaxPackageCount <= 2, $"实测最多同时存在 {sampler.MaxPackageCount} 个包");

            Assert.Empty(FindFiles("*.7z"));
        }

        // ================================================================ 用例 E（B 档位分叉）

        /// <summary>
        /// <b>用例 E（B 档位分叉）</b>：同一个夹具换成③页「移入回收站」⇒
        /// **一层都不当场回收**（"删这一层的源"那个执行体一次都没被调用），内层包一直攒到链尾才处理，
        /// 回收站**只调一次**（整份其余物一次搬走）；峰值因此回到"层数 + 1"。
        /// </summary>
        [SevenZipFact]
        public async Task 用例E_移入回收站档不逐层_内层包到链尾才处理_回收站只调一次()
        {
            const int layers = 4;

            Harness harness = CreateHarness(RestHandlingModes.RecycleBin, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers);

            // 同一个夹具 ⇒ 放行需求与用例 A 相同（两档的差别只在**实际峰值**）。
            long demand = await MeasureDemandAsync(outer);

            var recycleExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = recycleExecutor;

            using var sampler = new DiskSampler(_root);

            await harness.AddPathsAsync(outer);

            sampler.Start();

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            sampler.Stop();

            _output.WriteLine(
                $"【用例E】峰值包数 {sampler.MaxPackageCount} / 峰值字节 {sampler.MaxPackageBytes}"
                + $"（精估放行需求 {demand} ⇒ 比值 {(double)sampler.MaxPackageBytes / demand:0.00}）"
                + $" ｜ 逐层回收调用 {harness.SourceDeletes.DeletedPaths.Count} 次"
                + $" ｜ 回收站调用 {recycleExecutor.RecycleCalls.Count} 次");

            Assert.NotEmpty(FindFiles("final.txt"));

            // ① ⛔ 这一档**一层都不当场回收**（B 档位分叉）。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ② 内层包攒到链尾（峰值 ≥ 4 = 源包 + 三层内层包），这正是"回收站档不逐层"的可见后果。
            Assert.True(
                sampler.MaxPackageCount >= layers,
                $"「移入回收站」档不逐层回收 ⇒ 峰值该攒到 {layers} 个包，实测 {sampler.MaxPackageCount}："
                + string.Join("、", sampler.PeakSnapshot));

            // ③ 回收站**只调一次**（链尾整份其余物一次性搬走）。
            Assert.Single(recycleExecutor.RecycleCalls);
            Assert.Empty(recycleExecutor.PermanentCalls);

            // ④ 跑完盘上一个包都不剩（其余物整份进了回收站）。
            Assert.Empty(FindFiles("*.7z"));
        }

        // ================================================================ 用例 G（半套分卷 ⇒ 一个字节都不删）

        /// <summary>
        /// <b>用例 G（复刻 §44.2 那次 25 GB 永久消失的形状）</b>：这一层的内层包是**一组分卷**，
        /// 而同组的另一片名字被改坏（`mid.z删除ip`）、被当**内容物**留在成品目录树里 ⇒ 这一组"被拆在两边"。
        ///
        /// <para>⇒ 逐层回收那一支**必须被「半套分卷」闸门拦下**：<b>零删除</b>
        /// （`SourceDeletes` 空、其余物那条路的执行器也零调用）、**内层包与源包都还在**、
        /// 并且写一行 WARN **点名两边**。</para>
        ///
        /// <para><b>红检</b>：把 `ExtractionCoordinator` 里那道闸门（`DescribeLayerSplitBlocker` 那一段）
        /// 临时撤掉 ⇒ 本用例立刻变红（`Assert.Empty() Failure: Collection was not empty` ——
        /// 整组内层包被当场删掉，§44.2 的灾难重演）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例G_逐层回收遇到半套分卷_一个字节都不删_并点名两边()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildSplitVolumeChain();

            // ⛔ 绝不碰用户系统回收站：两条删除路径的执行体都换成假的（只为记账）。
            var fakeRestExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = fakeRestExecutor;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("mid", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① 这一层真的解开了（不然"没删"可能只是整条链没跑）—— 内容物在。
            Assert.NotEmpty(FindFiles("final.txt"));

            // ② ⛔ 零删除：逐层回收那一支被闸门拦下。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ③ 链尾那一条路也被同一道闸门拦下（其余物那一档的执行器零调用）。
            Assert.Empty(fakeRestExecutor.PermanentCalls);
            Assert.Empty(fakeRestExecutor.RecycleCalls);

            // ④ 内层包（整组）与源包都还在，被改坏的那一片也还在。
            Assert.NotEmpty(FindFiles("mid.7z.001"));
            Assert.NotEmpty(FindFiles("mid.7z.002"));
            Assert.NotEmpty(FindFiles("mid.z删除ip"));
            Assert.NotEmpty(FindFiles("outer.7z"));

            // ⑤ 有一行 WARN，而且**两边都点到了名**。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Level == "WARN"
                         && entry.Message.Contains("是一组分卷的一片", StringComparison.Ordinal)
                         && entry.Message.Contains("mid", StringComparison.Ordinal)
                         && entry.Message.Contains("成品目录", StringComparison.Ordinal));
        }

        // ================================================================ 用例 H / I（第二轮复核逮出的两条放行路径）

        /// <summary>
        /// <b>用例 H（深嵌套分支漏扫，2026-10-03 第二轮复核）</b>：候选比成品根**深两层**
        /// （`&lt;成品根&gt;\AAA\BBB\mid.7z.001`），而同组的另一片在同一个成品根下的**另一条分支**
        /// （`&lt;成品根&gt;\CCC\mid.z删除ip`）⇒ 只取"候选的上一级"那种扫法四个根一个都盖不到 ⇒ 放行。
        ///
        /// <para>断言：**零删除** + 内层包整组与源包都还在 + 有那一行 WARN。</para>
        ///
        /// <para><b>红检</b>：把扫描根改回"只取第一个候选的上一级"（去掉沿祖先链上溯那一圈）⇒ 本用例变红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例H_半套的另一片在同级另一条分支_也要拦下()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildSplitVolumeChain(nestedDepth: 2);

            var fakeRestExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = fakeRestExecutor;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("mid", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            Assert.NotEmpty(FindFiles("final.txt"));
            Assert.Empty(harness.SourceDeletes.DeletedPaths);
            Assert.Empty(fakeRestExecutor.PermanentCalls);
            Assert.Empty(fakeRestExecutor.RecycleCalls);

            Assert.NotEmpty(FindFiles("mid.7z.001"));
            Assert.NotEmpty(FindFiles("mid.z删除ip"));
            Assert.NotEmpty(FindFiles("outer.7z"));

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Level == "WARN"
                         && entry.Message.Contains("是一组分卷的一片", StringComparison.Ordinal)
                         && entry.Message.Contains("mid", StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 I（成品目录树读不动，2026-10-03 第二轮复核）</b>：闸门扫不到那棵树时**必须拦下**
        /// （原来 `catch ⇒ 当没有外面` = 放行，与类注释自称的"读不动就拦"相反）。
        ///
        /// <para>造法：用闸门上的**测试缝** `RestVolumeCompletenessGate.EnumerateFilesForTest` 让成品目录树那一层
        /// 抛异常（⛔ 绝不去改用户目录的权限 / ACL；假实现只对带标记的那一层抛，别的路径原样转调真实实现，
        /// 免得串到并发跑的别的用例上去）。</para>
        ///
        /// <para>断言：**零删除** + 有那一行 WARN（写清哪一棵树读不动）。</para>
        ///
        /// <para><b>红检</b>：把 <c>TryEnumerateFilesSafe</c> 的失败分支改回"当没有外面"⇒ 本用例变红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例I_成品目录树读不动_也要拦下()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildChain(2);

            int calls = 0;

            /*
             * 缝只对"候选所在那一层的上一级"（= 成品根）抛，别的路径一律转调真实实现。
             * 用"这一次临时根"当标记，保证只影响本用例自己那棵树。
             */
            Func<string, IReadOnlyList<string>> fake = directory =>
            {
                if (directory.Contains("ArchiveFixerLayerReclaim", StringComparison.OrdinalIgnoreCase) &&
                    !directory.Contains(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase))
                {
                    calls++;
                    throw new UnauthorizedAccessException("测试造出来的『读不动』");
                }

                return Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            };

            RestVolumeCompletenessGate.EnumerateFilesForTest = fake;

            try
            {
                await harness.AddPathsAsync(outer);

                await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));
            }
            finally
            {
                RestVolumeCompletenessGate.EnumerateFilesForTest = null;
            }

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("读不动", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① 内容物出来了（链真的跑了）+ 那一层真的被扫过（不是"没走到闸门"）。
            Assert.NotEmpty(FindFiles("final.txt"));
            Assert.True(calls > 0, "测试缝一次都没被调用 —— 那说明闸门根本没扫（用例白测）");

            // ② ⛔ 零删除：扫不到那棵树 = 判不出 ⇒ 一个字节都不删。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ③ 有那一行 WARN，写清了是哪一棵树读不动。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Level == "WARN" && entry.Message.Contains("读不动", StringComparison.Ordinal));
        }

        // ================================================================ 用例 D（三处消费点同一数字）

        /// <summary>
        /// <b>用例 D（三处消费点同一数字）</b>：调度 <c>RequiredBytes</c> / 空间门 / 导入后的空间体检
        /// （<c>BuildSpaceAdvice</c>）读到的必须是**同一个数字** —— 唯一出口
        /// <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/> 没有被绕过。
        /// </summary>
        [SevenZipFact]
        public async Task 用例D_三处消费点读同一个数字()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(3);

            await harness.AddPathsAsync(outer);

            ArchiveTask task = harness.Vm.Tasks.Single(t => !t.IsContinuationTask);
            TaskSpaceEstimate estimate = SpaceEstimator.FromSourceFiles(task);

            long available = estimate.FreeSpaceDemandBytes + SpaceGate.DefaultReserveBytes + (16L * 1024 * 1024);
            long reserve = SpaceGate.DefaultReserveBytes;

            // ① 调度（真正开跑时用的那段实现）。
            ExtractionSchedulePlan plan = ExtractionScheduler.Build(
                new[] { task },
                _ => estimate,
                available,
                reserve,
                requestedParallelCount: 1);

            // ② 导入后的空间体检（与调度共用同一个 BuildSchedulePlan）。
            ExtractionSchedulePlan advice = harness.Coordinator.BuildSpaceAdvice(new[] { task });

            // ③ 空间门收到的是同一个数字。
            SpaceGateDecision gate = SpaceGate.Check(plan.Ordered[0].RequiredBytes, available, reserve);

            _output.WriteLine(
                $"【用例D】放行需求 {estimate.FreeSpaceDemandBytes} ｜ 调度 {plan.Ordered[0].RequiredBytes}"
                + $" ｜ 体检 {advice.Ordered[0].RequiredBytes} ｜ 空间门需求 {gate.RequiredBytes}");

            Assert.Equal(estimate.FreeSpaceDemandBytes, plan.Ordered[0].RequiredBytes);
            Assert.Equal(estimate.FreeSpaceDemandBytes, advice.Ordered[0].RequiredBytes);
            Assert.Equal(estimate.FreeSpaceDemandBytes, gate.RequiredBytes);

            // 铁律：永远不得小于内容物。
            Assert.True(estimate.FreeSpaceDemandBytes >= estimate.ContentBytes);
        }

        // ================================================================ 用例 F（Keep 档：残渣没有、东西都在）

        /// <summary>
        /// <b>用例 F（② Keep 档随层清中间件）</b>：③页 = 「不动其余物」⇒ 工作区里**没有**第 2、3 类残渣，
        /// 而**内层包与源包都还在**（⛔ 这是"什么都没删用户东西"的守门断言）。
        ///
        /// <para><b>⚠ 这条用例是"实测口径"的守门断言，不是新加的删除动作</b>：用户 2026-10-03 第 ② 条要求
        /// "动手前先只读量一遍，按实测决定清哪几处，⛔ 不许凭想象加删除点"。实测（同文件那条只读实测用例）
        /// 的结论是：**工作区 `.ArchiveFixer.work` 整棵在跑完之前就已经被既有收尾删掉了**，
        /// 里面除了 `<工作区>\<任务>\stage\<产物>` 再也没有别的东西 ⇒ **② 是空操作**，
        /// 于是这里不新增任何清点，只把实测事实钉成回归断言（将来谁在工作区里留了残渣，这条会红）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例F_Keep档_工作区里没有残渣_内层包与源包都还在()
        {
            const int layers = 3;

            Harness harness = CreateHarness(RestHandlingModes.Keep, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers);

            using var sampler = new DiskSampler(_root);

            await harness.AddPathsAsync(outer);

            sampler.Start();

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            sampler.Stop();

            // ① 跑完之后工作区整棵不在了（既有收尾的功劳，⛔ 不是本次新加的删除点）。
            Assert.Empty(Directory.GetDirectories(_root, ".ArchiveFixer.work", SearchOption.AllDirectories));

            // ② 全程工作区里只出现过 `<工作区>\<任务>\stage\…` 这一种形状 —— 没有第 2、3 类残渣。
            string[] workspacePaths = sampler.AllPathsSeen
                .Where(path => path.Contains(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.NotEmpty(workspacePaths);

            foreach (string path in workspacePaths)
            {
                string relative = path[(path.IndexOf(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase)
                                        + ".ArchiveFixer.work".Length)..].TrimStart('\\', '/');

                string[] segments = relative.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);

                Assert.True(
                    segments.Length == 0
                    || (segments.Length >= 1 && segments.Length <= 3 && (segments.Length < 2 || segments[1] == "stage")),
                    $"工作区里出现了意料之外的残渣：{path}");
            }

            // ③ ⛔ 什么都没删：逐层回收那个执行体一次都没被调用。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            // ④ 内层包与源包**都还在**（Keep 档那是用户要留的其余物）。
            Assert.NotEmpty(FindFiles("level2.7z"));
            Assert.NotEmpty(FindFiles("outer.7z"));

            // ⑤ 内容物也在（不然"东西都在"可能只是整条链没跑）。
            Assert.NotEmpty(FindFiles("final.txt"));
        }

        // ================================================================ 只读实测（② 的判据来源）

        /// <summary>
        /// **只读实测**：「不动其余物」（Keep）档跑完一条普通档链路之后，`<目标目录>\.ArchiveFixer.work`
        /// 与成品目录里到底还剩什么（用户 2026-10-03 第 ② 条要求"先量一遍，按实测决定清哪几处，⛔ 不许凭想象加删除点"）。
        ///
        /// <para>这条用例**不断言产品行为**，只把实测清单打出来（+ 钉住"内层包与源包都还在"这条守门事实）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 只读实测_Keep档跑完之后工作区与成品目录里还剩什么()
        {
            const int layers = 3;

            Harness harness = CreateHarness(RestHandlingModes.Keep, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers);

            using var sampler = new DiskSampler(_root);

            await harness.AddPathsAsync(outer);

            sampler.Start();

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            sampler.Stop();

            string[] finalPaths = EnumerateAll(_root);

            _output.WriteLine("【只读实测】跑完之后盘上还剩（相对本次临时根，已排序）：");

            foreach (string path in finalPaths.Select(p => Path.GetRelativePath(_root, p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                _output.WriteLine("  · " + path);
            }

            _output.WriteLine("【只读实测】全程出现过的路径（采样并集）：");

            foreach (string path in sampler.AllPathsSeen.Select(p => Path.GetRelativePath(_root, p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                _output.WriteLine("  ~ " + path);
            }

            // 守门事实（用例 F 的那两条）：Keep 档下内层包与源包**都还在**。
            Assert.NotEmpty(FindFiles("level2.7z"));
            Assert.NotEmpty(FindFiles("outer.7z"));
            Assert.Empty(harness.SourceDeletes.DeletedPaths);
        }

        // ================================================================ 造样本 / 断言辅助

        /// <summary>
        /// 只读：这一单的**精估放行需求**（真清单）—— 用来算「真实峰值 / 放行需求」这个比值
        /// （用户 2026-10-03 要求先报事实：需求是按逐层回收的净增量算的，而有的档不逐层回收）。
        /// </summary>
        private async Task<long> MeasureDemandAsync(string outer)
        {
            var task = new ArchiveTask(outer, 1) { FileName = "outer.7z" };

            TaskSpaceEstimate cheap = SpaceEstimator.FromSourceFiles(task);

            ArchiveListResult list = await new SevenZipEngine().ListAsync(
                ArchiveRequest.For(outer),
                CancellationToken.None);

            Assert.True(list.Success, list.Message);

            return SpaceEstimator.RefineWithListing(cheap, list).FreeSpaceDemandBytes;
        }

        /// <summary>
        /// **用例 G 的夹具**（复刻 §44.2）：`outer.7z` 里装着
        /// 「一组真 7z 分卷 `mid.7z.001/.002/...`」＋「同组一片、名字被改坏的 `mid.z删除ip`」＋`layer1.txt`。
        ///
        /// <para>那一组里是第 2 层的内容（`final.txt` + `layer2.txt` + 5 KiB 不可压数据，保证真的切成多片）；
        /// 名字被改坏的那一片按 §44.2 的真实形态取名（实测 `TryRecoverDisguisedArchiveBody("z删除ip") = true`、
        /// `GetArchiveBaseName("mid.z删除ip") = "mid"` ⇒ 闸门认得出它跟分卷同基名）。</para>
        /// </summary>
        private string BuildSplitVolumeChain(int nestedDepth = 1)
        {
            string build = Path.Combine(_root, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(build, "layer2.txt"), "第 2 层自己的内容\n", new UTF8Encoding(false));

            // 5 KiB 不可压数据：`-v2k` 下必然切成多片（不然只会出一片，就不是"一组分卷"了）。
            byte[] blob = new byte[5 * 1024];
            new Random(20261003).NextBytes(blob);
            File.WriteAllBytes(Path.Combine(build, "blob.bin"), blob);

            Run7z(build, "a", "-t7z", "-mx0", "-v2k", "mid.7z", "final.txt", "layer2.txt", "blob.bin");

            /*
             * `nestedDepth = 2` 时用**复核给的那个形状**（用例 H）：
             * 分卷组在 `AAA\BBB\` 里（候选比成品根深两层），另一片在**同级另一条分支** `CCC\` 里。
             * ⚠ 刻意**不再多套一层**（第一版套了 `pieces\AAA\BBB\`，候选比成品根深三层 ⇒
             * 祖先链上溯三层刚好差一层，闸门扫不到 `CCC\` ⇒ 用例红 —— 实测踩过这一脚；
             * 详见 §9「已知上界」）。
             */
            string piecesDirectory = nestedDepth <= 1
                ? Path.Combine(build, "pieces")
                : Path.Combine(build, "AAA", "BBB");

            string strayDirectory = nestedDepth <= 1
                ? Path.Combine(build, "stray")
                : Path.Combine(build, "CCC");

            Directory.CreateDirectory(piecesDirectory);
            Directory.CreateDirectory(strayDirectory);

            var pieces = new List<string>();

            foreach (string piece in Directory.GetFiles(build, "mid.7z.*"))
            {
                string target = Path.Combine(piecesDirectory, Path.GetFileName(piece));
                File.Move(piece, target);
                pieces.Add(target);
            }

            Assert.True(pieces.Count >= 2, "`-v2k` 下应当切出至少两片，实际 " + pieces.Count + " 片");

            // 名字被改坏的同组一片（§44.2：`一只顶美.z删除ip` 那种）—— 基名同样是 mid。
            File.WriteAllText(
                Path.Combine(strayDirectory, "mid.z删除ip"),
                "名字被改坏的那一片（占位，不是真归档）\n",
                new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            var outerArgs = new List<object> { "a", "-t7z", outer };

            foreach (string piece in pieces)
            {
                outerArgs.Add(Path.GetRelativePath(build, piece));
            }

            outerArgs.Add(Path.GetRelativePath(build, Path.Combine(strayDirectory, "mid.z删除ip")));
            outerArgs.Add("layer1.txt");

            Run7z(build, outerArgs.ToArray());

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 造一条嵌套链：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>，
        /// **每一层都另外带一个自己的内容文件**（`layerN.txt`）—— 真实机器上的包大多如此，
        /// 而且这样最外层那一单第 1 轮就有内容物可定稿（源包搬运因此走"本轮直接搬"）。
        ///
        /// <para><paramref name="passwordLevel"/> 指定的那一层用密码包（<c>-mhe=on</c>）造，
        /// 密码刻意不进密码本 ⇒ 轮到那一层必然失败（用例 B 用）。</para>
        /// </summary>
        private string BuildChain(int levelCount, int? passwordLevel = null)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));

            for (int level = levelCount; level >= 2; level--)
            {
                // 每一层自己的内容文件（✔ 让"第一层只出过程物"这个形状不至于遮住别的路径）。
                File.WriteAllText(
                    Path.Combine(build, $"layer{level}.txt"),
                    $"第 {level} 层自己的内容\n",
                    new UTF8Encoding(false));

                string inner = level == levelCount ? "final.txt" : $"level{level + 1}.7z";

                if (passwordLevel == level)
                {
                    Run7z(build, "a", "-t7z", $"level{level}.7z", "-p" + UnknownLayerPassword, "-mhe=on", inner, $"layer{level}.txt");
                }
                else
                {
                    Run7z(build, "a", "-t7z", $"level{level}.7z", inner, $"layer{level}.txt");
                }
            }

            // 最外层自己也带一个内容文件（第 1 轮就有内容物定稿）。
            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");

            /*
             * 造包用的中间层（level2..levelN）是"造样本的脚手架"，不是管线产物 ——
             * 留着的话"盘上还剩几个包"这类断言会自欺欺人（实测：四层链会多数出好几个）。
             */
            Directory.Delete(build, recursive: true);

            return outer;
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

        private static bool IsNamed(string path, string fileName) =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

        private static string[] EnumerateAll(string root) =>
            Directory.Exists(root)
                ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

        /// <summary>在本次测试的临时根下面按文件名找文件（包会被搬到别的目录，所以按名字找）。</summary>
        private string[] FindFiles(string pattern) =>
            Directory.Exists(_root)
                ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private string[] FindDirectories(string name) =>
            Directory.Exists(_root)
                ? Directory.GetDirectories(_root, name, SearchOption.AllDirectories)
                : Array.Empty<string>();

        private Harness CreateHarness(string restMode, SourceHandlingMode sourceMode)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = sourceMode.ToString();
            settings.RestHandlingAfterVerify = restMode;
            settings.CustomSevenZipExePath = string.Empty;

            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            var sourceDeletes = new RecordingDeleteFileSystem(_root);
            extraction.SourcePackageDeleteFileSystem = sourceDeletes;

            // 默认档下成功的任务在日志里只留一行，而本类要断言的"每层都说过它回收了"正是那些中间行。
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, extraction, logService, sourceDeletes);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                ExtractionCoordinator coordinator,
                LogService log,
                RecordingDeleteFileSystem sourceDeletes)
            {
                Vm = vm;
                _oneClick = oneClick;
                Coordinator = coordinator;
                Log = log;
                SourceDeletes = sourceDeletes;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public RecordingDeleteFileSystem SourceDeletes { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 记账式的"删源包"执行体（产品代码默认走真实文件系统，这里只替换执行体、判据一个字不改）：
        /// 逐层回收走的正是它（<c>SourcePackageDeleteFileSystem</c>）。
        /// </summary>
        private sealed class RecordingDeleteFileSystem : ISourceDeleteFileSystem
        {
            private readonly string _root;

            public RecordingDeleteFileSystem(string root) => _root = root;

            public List<string> DeletedPaths { get; } = new();

            public bool FileExists(string path) => File.Exists(path);

            public void DeleteFile(string path)
            {
                DeletedPaths.Add(path);
                File.Delete(path);
            }
        }

        /// <summary>
        /// 假的其余物删除执行器（**绝不碰用户的系统回收站**）：记调用、按档真删临时目录。
        /// </summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                Delete(path, isDirectory);

                return RecycleAttemptResult.Recycled;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
                Delete(path, isDirectory);
            }

            private static void Delete(string path, bool isDirectory)
            {
                if (isDirectory && Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// 每 10 毫秒采一帧：盘上同时有几个 <c>*.7z</c>、它们一共多少字节、以及本次临时根下面
        /// 出现过的**所有**路径（用来回答"工作区里还剩什么"）。
        ///
        /// <para>采样只会**少看**、绝不会多看，所以"最多看到 N 个"这类上界断言是安全的
        /// （与 <c>ChainSpaceReclaimTests.PackagePeakSampler</c> 同一套做法）。</para>
        /// </summary>
        private sealed class DiskSampler : IDisposable
        {
            private readonly string _root;
            private readonly List<string> _peakSnapshot = new();
            private readonly HashSet<string> _allPathsSeen = new(StringComparer.OrdinalIgnoreCase);
            private volatile bool _running;
            private Task? _loop;

            public DiskSampler(string root) => _root = root;

            public int MaxPackageCount { get; private set; }

            public long MaxPackageBytes { get; private set; }

            public IReadOnlyList<string> PeakSnapshot => _peakSnapshot;

            public IReadOnlyCollection<string> AllPathsSeen => _allPathsSeen;

            public void Start()
            {
                _running = true;

                _loop = Task.Run(
                    async () =>
                    {
                        while (_running)
                        {
                            Observe();
                            await Task.Delay(10).ConfigureAwait(false);
                        }
                    });
            }

            public void Stop()
            {
                _running = false;

                try
                {
                    _loop?.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 采样线程收不干净不影响结论。
                }
            }

            public void Dispose() => Stop();

            private void Observe()
            {
                try
                {
                    if (!Directory.Exists(_root))
                    {
                        return;
                    }

                    string[] packages = Directory.GetFiles(_root, "*.7z", SearchOption.AllDirectories);
                    long bytes = packages.Sum(p => new FileInfo(p).Length);

                    if (packages.Length > MaxPackageCount)
                    {
                        MaxPackageCount = packages.Length;

                        _peakSnapshot.Clear();
                        _peakSnapshot.AddRange(packages.Select(Path.GetFileName).OfType<string>());
                    }

                    MaxPackageBytes = Math.Max(MaxPackageBytes, bytes);

                    foreach (string path in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                    {
                        _allPathsSeen.Add(path);
                    }
                }
                catch
                {
                    // 采样是观测行为：目录正在被搬动时读不到就当这一帧没看见。
                }
            }
        }
    }
}
