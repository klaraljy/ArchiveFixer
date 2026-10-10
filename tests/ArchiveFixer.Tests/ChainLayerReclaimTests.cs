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

        /// <summary>③页「删除操作 = 彻底删除」那一档（唯一决定"逐层回收开不开"的事实位就是它）。</summary>
        private const string PermanentDeleteRestMode = RestHandlingModes.Delete;

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
        /// ② 第三层那一层**自己**没有被回收（那一层的回收判据一条都没过）；
        /// ③ 最外层源包原地不动（本档「留在原地」⇒ 一个字节都没搬）；④ 失败那一层的内容物没出来。
        ///
        /// <para><b>⚠ 2026-10-04 口径变更（用户推翻"整链成功"那道闸门）</b>：老版本这一档写着
        /// "失败层的过程物一个字节都不动、其余物一个都不生成"。现在链尾**只问根源包那一层** ——
        /// 根源包（<c>outer.7z</c>）那一层是成功的 ⇒ 按③页「彻底删除」把它那一份其余物整份删掉，
        /// 而**失败那一层的包 <c>level3.7z</c> 正躺在其余物里**（它是上一层定稿时收进去的待续解过程物）
        /// ⇒ 它**跟着一起被删了**（它的内容从未被解出来）。
        /// ⚠ 代价如实记在这里：这一层的唯一副本随其余物永久消失，能重建它的只有最外层源包
        /// （本档「留在原地」⇒ 还在盘上，见 ⑤）。要改这个取舍得先让用户拍板。</para>
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

            /*
             * ② 失败那一层**自己没被回收**（逐层回收那一支的判据一条都没过）：
             * 判据取那一段唯一的日志（"这一层的过程物没有回收"）—— 它不在，就说明那一层被误当成"跑成了"。
             */
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("这一层的过程物没有回收", StringComparison.Ordinal));

            /*
             * ③ 链尾那一档**按设置做了**（2026-10-04 新口径）：根源包那一层成功 ⇒ 其余物整份彻底删除。
             * ⚠ 失败那一层的包也在这份其余物里 ⇒ 一起没了（见上面 doc 的代价说明）。
             */
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("彻底删除：", StringComparison.Ordinal));

            Assert.Empty(FindFiles("level3.7z"));

            // ④ 链尾⛔ 不再走"把内层包收进其余物"那一段（那一档下逐层回收已经各收各的）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("内层包已移入其余物", StringComparison.Ordinal));

            // ⑤ 失败那一层的内容物当然也没出来（这一层真的失败了，不是"其实成功了"）。
            Assert.Empty(FindFiles("layer3.txt"));
            Assert.Empty(FindFiles("final.txt"));

            // ⑥ 已成功回收的上一层**不回滚**（用户 2026-09-29 明确接受："原包是已经没有了但是留下的不会破"）。
            Assert.Empty(FindFiles("level2.7z"));

            // ⑦ 最外层源包原地不动（⛔ 红线 1：本档「留在原地」⇒ 它从没进过其余物）。
            Assert.True(File.Exists(outer), "失败 / 部分完成 ⇒ 源包必须原地不动");

            // ⑧ 其余物那一份已经按设置删掉了（根源包那一层成功），盘上不再有其余物目录。
            Assert.Empty(FindDirectories(ProcessArtifactLayout.ArtifactDirectoryName));

            // ⑨ 失败那一层要落成失败（不变量 6），而不是"成功"或"部分完成"。
            Assert.Contains(harness.Vm.Tasks, task => task.Outcome == TaskOutcome.Failed);
        }

        /// <summary>
        /// <b>红线守门（「移入回收站」档 + 中间那一层失败）</b>：那一档不逐层回收，内层包要等链尾 ——
        ///
        /// <para><b>⚠ 2026-10-04 口径变更（用户推翻"整链成功"那道闸门）</b>：老版本在这里断言
        /// "链没跑完 ⇒ 链尾那一档整段不执行、一个字节都不删、源包还在盘上"。现在链尾**只问根源包那一层**：
        /// 根源包成功 ⇒ 按档把整份其余物（含<b>已经搬进其余物的最外层源包</b>）**一次**移入回收站。</para>
        ///
        /// <para><b>这一档仍然是"可还原"的那一档</b>（用户 2026-10-03 的原话："原包还是比较重要的"）：
        /// 因此这里钉的是 —— 回收站**只调一次**（整份一次搬走）、**永久删除零调用**、
        /// 而且⛔ 失败那一层自己**没有**被逐层回收（零个源包删除记录）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例B2_移入回收站档_中间那一层失败_整份一次进回收站_绝不永久删()
        {
            const int layers = 4;
            const int failingLevel = 3;

            Harness harness = CreateHarness(RestHandlingModes.RecycleBin, SourceHandlingMode.MoveToRest);
            string outer = BuildChain(layers, passwordLevel: failingLevel);

            var recycleExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = recycleExecutor;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            // ① 源包那一份**进了回收站**：它先按"源包操作 = 放入其余物"进其余物，链尾整份一次搬走。
            Assert.Single(recycleExecutor.RecycleCalls);

            Assert.True(
                IsNamed(recycleExecutor.RecycleCalls[0], ProcessArtifactLayout.ArtifactDirectoryName),
                "整份其余物一次搬走（不是逐个条目），实际：" + recycleExecutor.RecycleCalls[0]);

            Assert.Empty(FindFiles("outer.7z"));

            // ② ⛔ 永久删除**一次都不许有**（这一档是可还原的那一档）。
            Assert.Empty(recycleExecutor.PermanentCalls);

            // ③ 逐层回收那一支一次都没被调用（「移入回收站」档不逐层回收 —— B 档位分叉不变）。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

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
        /// 而同组的另一片名字被改坏（`mid.7z.0删除02`）、被当**内容物**留在成品目录树里 ⇒ 这一组"被拆在两边"。
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
            Assert.NotEmpty(FindFiles("mid.7z.0删除02"));
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
        /// （`&lt;成品根&gt;\CCC\mid.7z.0删除02`）⇒ 只取"候选的上一级"那种扫法四个根一个都盖不到 ⇒ 放行。
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
            Assert.NotEmpty(FindFiles("mid.7z.0删除02"));
            Assert.NotEmpty(FindFiles("outer.7z"));

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Level == "WARN"
                         && entry.Message.Contains("是一组分卷的一片", StringComparison.Ordinal)
                         && entry.Message.Contains("mid", StringComparison.Ordinal));
        }

        /// <summary>
        /// **用例 H2（深嵌套 + 同级另一条分支，2026-10-03 第三轮：取消 3 层上限）**：候选比成品根**深三层**
        /// （`&lt;成品根&gt;\X\AAA\BBB\mid.7z.001`），而同组的另一片在**另一条分支**
        /// （`&lt;成品根&gt;\Y\CCC\mid.7z.0删除02`）⇒ 只上溯 3 层时第 3 层那个祖先（`X`）虽然也是递归扫的，
        /// 但 `Y` 不在它里面 ⇒ 四个根一个都盖不到 ⇒ **放行 = 不可逆删除**。
        ///
        /// <para>断言：**零删除** + 内层包整组与源包都还在（被改坏的那一片也在）+ 有那一行 WARN 点名两边。</para>
        ///
        /// <para><b>红检</b>：把层数上限加回去（`EnumerateSplitGateArtifactRoots` 里那个
        /// <c>level &lt; SplitGateAncestorLevels</c> / "不在目标根里就最多上溯 3 层"那一脚）⇒ 本用例变红
        /// （`Assert.Empty() Failure: Collection was not empty` —— 整组内层包被当场删掉，§44.2 的灾难重演）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例H2_半套的另一片深三层且在同级另一条分支_也要拦下()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildSplitVolumeChain(nestedDepth: 3);

            var fakeRestExecutor = new FakeDeleteExecutor();
            harness.Coordinator.RestDeleteExecutor = fakeRestExecutor;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("mid", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① 这一层真的解开了（不然"没删"可能只是整条链没跑）。
            Assert.NotEmpty(FindFiles("final.txt"));

            // ② ⛔ 零删除：这一层与链尾那两条路都要被闸门拦下。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);
            Assert.Empty(fakeRestExecutor.PermanentCalls);
            Assert.Empty(fakeRestExecutor.RecycleCalls);

            // ③ 内层包整组（含被改坏的那一片）与源包都还在。
            Assert.NotEmpty(FindFiles("mid.7z.001"));
            Assert.NotEmpty(FindFiles("mid.7z.002"));
            Assert.NotEmpty(FindFiles("mid.7z.0删除02"));
            Assert.NotEmpty(FindFiles("outer.7z"));

            // ④ 有一行 WARN，而且**两边都点到了名**。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Level == "WARN"
                         && entry.Message.Contains("是一组分卷的一片", StringComparison.Ordinal)
                         && entry.Message.Contains("mid", StringComparison.Ordinal)
                         && entry.Message.Contains("成品目录", StringComparison.Ordinal));
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

        // ================================================================ 用例 J（定稿搬运失败：原因必须让用户看得见）

        /// <summary>
        /// <b>用例 J1（用户 2026-10-04 真机 · 缺陷 1）</b>：续解层「定稿搬运失败」时，
        /// **前 5 条明细（文件名 + 原因原文）必须落到①页「错误信息」列与日志里** ——
        /// 老写法只有一句「10 项没能搬运」，用户手里没有任何可行动的线索。
        ///
        /// <para><b>造法</b>（⛔ 绝不碰用户目录：全在本次临时根里）：续解层的 6 个内容物落点
        /// （`out\outer\level2\c0N.txt`）**预先用独占句柄占住**，③档同名冲突设成「覆盖」
        /// ⇒ 定稿走的正是"挪开旧的再落位"，而旧的挪不动 ⇒ 6 条搬运全失败、一条内容物都没落位。
        /// 这正是真机那一单的形状（10 项没能搬运 / 定稿搬运失败）。</para>
        ///
        /// <para><b>红检</b>：把 <c>ExtractionCoordinator</c> 里 summary 的明细那一段撤掉
        /// （回到"只写个数"）⇒ 本用例当场红（`Assert.Contains() Failure: c01.txt（`）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例J1_定稿搬运失败_前5条明细进错误信息列与日志()
        {
            (Harness harness, ArchiveTask failed, _) = await RunFinalizeMoveFailureAsync();

            _output.WriteLine($"[①页] {failed.ErrorMessage}");

            Assert.Equal(TaskOutcome.Failed, failed.Outcome);

            // 这一类失败的事实位（批末诊断靠它把"没有引擎原话"的那一档单独点出来）。
            Assert.True(failed.CommitMoveFailed, "定稿搬运失败必须落成事实位（批末诊断读它）");

            // ---- ①页「错误信息」列：结论 + 计数 + **前 5 条明细**
            Assert.StartsWith(StatusText.FinalizeMoveFailedPrefix, failed.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("6 项没能搬运", failed.ErrorMessage, StringComparison.Ordinal);

            foreach (string name in new[] { "c01.txt", "c02.txt", "c03.txt", "c04.txt", "c05.txt" })
            {
                // 明细的形状 = `文件名（原因原文）`。
                Assert.Contains(name + "（", failed.ErrorMessage, StringComparison.Ordinal);
            }

            // 原因原文（两阶段覆盖挪不开旧文件时那句包装 + 它带的系统原话）。
            Assert.Contains("挪开旧文件失败", failed.ErrorMessage, StringComparison.Ordinal);

            // ⛔ 只列前 5 条，多出来的折成"还有 K 项"（不许把几十条全塞进那一格）。
            Assert.DoesNotContain("c06.txt", failed.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("还有 1 项没列出来", failed.ErrorMessage, StringComparison.Ordinal);

            // ---- 日志里必须有**同样内容**（①页 / 失败清单 / 日志读的是同一份文字）。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains(failed.ErrorMessage, StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 J2（同一次真机 · 缺陷 2）</b>：定稿搬运失败之后，那句
        /// 「这一层的过程物已按『删除操作 = 彻底删除』当场回收」**一个字都不许出现** ——
        /// 那一刻回收判据一条都没过（定稿失败），过程物**原封不动躺在盘上**；
        /// 必须如实写「这一层的过程物没有回收（原因）」。
        ///
        /// <para><b>造法</b>：与用例 J1 同一个夹具（同一份 7z 造包 + 同一批占位）。</para>
        ///
        /// <para><b>红检</b>：把 <c>RunRestHandlingAsync</c> 那一句改回**无条件**的"已当场回收"
        /// ⇒ 本用例当场红（`Assert.DoesNotContain() Failure`）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例J2_定稿搬运失败_不许说已回收_源包仍在原地()
        {
            (Harness harness, ArchiveTask failed, _) = await RunFinalizeMoveFailureAsync();

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("过程物", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① ⛔ 假话不许出现：这一层**没有**回收（判据没过、一个字节都没删）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("这一层的过程物已按", StringComparison.Ordinal));

            // ② 如实那一句必须在，而且**说清了原因**（这一层自己没跑成 ⇒ 指向①页）。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("这一层的过程物没有回收", StringComparison.Ordinal)
                         && entry.Message.Contains(failed.Status, StringComparison.Ordinal));

            // ③ 不可逆动作零调用（逐层回收那个执行体一次都没被叫）。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            /*
             * ④ 这一层**自己**没跑成 ⇒ 它的源（level2.7z）**没有**被逐层回收那一步删掉。
             *
             * ⚠ 2026-10-04 口径变更：老版本在这里断言"源包仍在原地"。现在链尾只问**根源包那一层** ——
             * 根源包（outer.7z）成功 ⇒ 按③页「彻底删除」把它那一份其余物整份删掉，而 level2.7z
             * 正是上层定稿时收进其余物的待续解过程物 ⇒ 它跟着一起没了（它的内容从未被定稿出来）。
             * 判据用**日志**而不是"文件在不在"：逐层回收那一支确实没删过它（③ 已经钉了零调用）。
             */
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("这一层的过程物没有回收", StringComparison.Ordinal));

            Assert.DoesNotContain(harness.SourceDeletes.DeletedPaths, path => IsNamed(path, "level2.7z"));
        }

        /// <summary>
        /// 用例 J 的夹具：`outer.7z → level2.7z → c01..c06.txt`，续解层那 6 个落点被独占句柄占住
        /// ⇒ 定稿搬运 6 条全失败。返回（夹具、失败的那一单、最外层源包路径）。
        /// </summary>
        private async Task<(Harness Harness, ArchiveTask Failed, string Outer)> RunFinalizeMoveFailureAsync()
        {
            Harness harness = CreateHarness(
                RestHandlingModes.Delete,
                SourceHandlingMode.KeepInPlace,
                settings => settings.ConflictAction = ConflictActions.Overwrite);

            string outer = BuildFinalizeFailureChain();

            string layerDirectory = Path.Combine(_root, "out", "outer", "level2");
            Directory.CreateDirectory(layerDirectory);

            var holds = new List<FileStream>();

            try
            {
                for (int i = 1; i <= 6; i++)
                {
                    string placeholder = Path.Combine(layerDirectory, $"c{i:00}.txt");
                    File.WriteAllText(placeholder, "占位（不是解压产物）\n", new UTF8Encoding(false));
                    holds.Add(new FileStream(placeholder, FileMode.Open, FileAccess.Read, FileShare.None));
                }

                await harness.AddPathsAsync(outer);

                await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));
            }
            finally
            {
                foreach (FileStream hold in holds)
                {
                    hold.Dispose();
                }
            }

            ArchiveTask failed = Assert.Single(harness.Vm.Tasks, task => task.IsContinuationTask);

            return (harness, failed, outer);
        }

        /// <summary>
        /// 夹具：<c>outer.7z → level2.7z → c01..c06.txt</c>，而且**最外层自己也有内容物**
        /// （`layer1.txt`）⇒ 续解那一层按既有判据**不另建层**（父层已产出内容物），
        /// 它那 6 个文件的落点 = 父层内容目录下面那一层（`out\outer\level2\`）。
        /// </summary>
        private string BuildFinalizeFailureChain()
        {
            string build = Path.Combine(_root, "ff-build");
            Directory.CreateDirectory(build);

            for (int i = 1; i <= 6; i++)
            {
                File.WriteAllText(
                    Path.Combine(build, $"c{i:00}.txt"),
                    $"第 2 层的内容 {i}\n",
                    new UTF8Encoding(false));
            }

            Run7z(build, "a", "-t7z", "level2.7z", "c01.txt", "c02.txt", "c03.txt", "c04.txt", "c05.txt", "c06.txt");

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");

            Directory.Delete(build, recursive: true);

            return outer;
        }

        // ================================================================ 用例 K（措辞与事实对齐：用户 2026-10-04 真机）

        /// <summary>
        /// <b>用例 K1（A3，2026-10-04 改结论）</b>：这一层里**只有 1 个**内层归档、可它旁边还有个视频 ⇒
        /// **继续解**（不再是"按保守档停在那一层"）。
        ///
        /// <para><b>⚠ 用户当场推翻旧口径</b>：老用例断言的是"走到了不展开那一支"、日志里写
        /// "除了那个内层归档还有别的文件（不是一条单链）" —— 用户原话：
        /// 「那层内层包只有 1 个，但旁边还有不属于"说明类"的文件 ⇒ 被算成"多分支" ——
        /// **这是压缩包吗，不是那你停什么**」。现在单链档的判据只看**内层归档的数量**：
        /// 1 个 ⇒ 一律继续解（旁边是什么文件都不影响）。</para>
        ///
        /// <para><b>新判据</b>：① 内层包的内容真的解出来了；② 日志里那句"只有 1 个内层归档…继续解"
        /// 出现；③ ⛔ 不再有任何"未展开 / 停在半路"的痕迹。</para>
        ///
        /// <para><b>红检</b>：把判据改回"同层除该包以外全是说明类文件"
        /// （`toProcess.Count == 1 &amp;&amp; HasOnlyInformationalSiblings(…)`）⇒ 本用例当场红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例K1_只1个内层包但旁边有别的文件_照旧继续解()
        {
            Harness harness = CreateHarness(
                RestHandlingModes.Delete,
                SourceHandlingMode.KeepInPlace,
                settings => settings.RecursionMode = "SingleChain");

            string outer = BuildSingleInnerWithSiblingChain();

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("内层归档", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① 这一层只有 1 个内层归档 ⇒ 继续解，日志如实说明判据。
            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains("只看内层归档的数量", StringComparison.Ordinal));

            // ② 内层包的内容真的解出来了（`.mp4` 不是理由，别停）。
            Assert.NotEmpty(FindFiles("payload.txt"));

            // ③ ⛔ 不许再出现"旁边有别的文件所以停下"那套旧判据，也不许说"多分支"。
            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("不是一条单链", StringComparison.Ordinal));

            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("个内层归档（多分支）", StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 K2（A4）</b>：多分支出现在**更深的层**（一键档不弹框 ⇒ 停因是
        /// <c>BranchNotExpanded</c>）时，「两条出路」那一句**也必须给** ——
        /// 真机上用户两层已成功、停半路、产物被清，却一条出路都看不到。
        ///
        /// <para><b>红检</b>：把那一支的判据改回只认 <c>NeedsDecision</c>
        /// ⇒ 本用例当场红（`Assert.Contains() Failure: 两条出路`）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例K2_更深层多分支停半路_必须给两条出路()
        {
            Harness harness = CreateHarness(
                RestHandlingModes.Delete,
                SourceHandlingMode.KeepInPlace,
                settings => settings.RecursionMode = "SingleChain");

            string outer = BuildDeepBranchChain();

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("出路", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            Assert.Contains(
                harness.Log.Logs,
                entry => entry.Message.Contains(StatusText.RecursionBranchAdviceTail, StringComparison.Ordinal));
        }

        /// <summary>
        /// <b>用例 K3（A5）</b>：**其余物没处理时，那句"为什么"必须同时进①页「错误信息」列与批末诊断**，
        /// ⛔ 不许只躺在日志里（真机：界面上只看到"解压成功、其余物还在"）。
        ///
        /// <para><b>⚠ 2026-10-04 口径变更</b>：老版本用"链上有一层没成功"当那个原因 ——
        /// 用户当天的原话（「你不会读设置吗，我勾选了保留吗，没勾选你留着干什么」）把那条整链闸门推翻了
        /// ⇒ 现在**只有根源包自己那一层不成立**时才会写这句话。所以这一条改用
        /// **根源包自己失败**（外层包设了密码、密码本里没有它）来触发同一套 A5 机制。</para>
        ///
        /// <para><b>红检</b>：把链尾那两处的 <c>RecordRestKept</c> 撤掉 ⇒ 本用例当场红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例K3_其余物没处理_原因要进错误信息列与批末诊断()
        {
            Harness harness = CreateHarness(RestHandlingModes.Delete, SourceHandlingMode.KeepInPlace);
            string outer = BuildChain(2, passwordOuter: true);

            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            ArchiveTask root = harness.Vm.Tasks.First(task => !task.IsContinuationTask);

            foreach (var entry in harness.Log.Logs.Where(e => e.Message.Contains("其余物", StringComparison.Ordinal)))
            {
                _output.WriteLine($"[{entry.Level}] {entry.Message}");
            }

            // ① ①页「错误信息」列里必须有那一句（含原因）。
            Assert.Contains("其余物没有处理", root.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains(StatusText.ChainRestBlockedTaskOutcomeFormat, root.ErrorMessage, StringComparison.Ordinal);

            // ② 批末诊断（弹窗正文与日志逐行同一份文字）里也要有。
            Assert.Contains(
                outcome.DiagnosticLines,
                line => line.Contains("其余物没有处理", StringComparison.Ordinal));

            // ③ 红线：根源包那一单失败 ⇒ **一个字节都不删**（源包原地不动）。
            Assert.DoesNotContain(harness.SourceDeletes.DeletedPaths, path => IsNamed(path, "outer.7z"));
            Assert.NotEmpty(FindFiles("outer.7z"));
        }

        /// <summary>
        /// 夹具（用例 K1）：<c>outer.7z → level2.7z + movie.mp4</c> —— 这一层里**只有 1 个**内层归档，
        /// 旁边有个**非说明类**文件（`.mp4`）。真机那批（`第6集.7z`）就是这个形状：
        /// ⚠ 2026-10-04 起它**不再**是停下来的理由（判据只看内层归档的数量，1 个一律继续解）。
        /// </summary>
        private string BuildSingleInnerWithSiblingChain()
        {
            string build = Path.Combine(_root, "k1-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "payload.txt"), "内层的内容\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level2.7z", "payload.txt");
            File.Delete(Path.Combine(build, "payload.txt"));

            File.WriteAllBytes(Path.Combine(build, "movie.mp4"), new byte[4096]);

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z", "movie.mp4");

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 夹具（用例 K2）：<c>outer.7z → level2.7z →（level3.7z + level4.7z + movie.mp4）</c> ——
        /// 多分支出现在**第 1 层**（更深的层）⇒ 一键档不弹框、停因是 <c>BranchNotExpanded</c>。
        ///
        /// <para>⚠ 2026-10-04：原来那一层的"多分支"是 **1 个内层包 + 一个 `.mp4`**（旧判据）；
        /// 用户当场推翻之后判据只看**内层归档的数量**（1 个一律继续解）⇒ 那个夹具不再会停，
        /// 本用例也就测不到"停半路要给两条出路"。现在这一层放**两个**内层包，才真的构成多分支。</para>
        /// </summary>
        private string BuildDeepBranchChain()
        {
            string build = Path.Combine(_root, "k2-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "leaf.txt"), "最深一层\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level3.7z", "leaf.txt");
            File.Delete(Path.Combine(build, "leaf.txt"));

            File.WriteAllText(Path.Combine(build, "leaf2.txt"), "另一个分支\n", new UTF8Encoding(false));
            Run7z(build, "a", "-t7z", "level4.7z", "leaf2.txt");
            File.Delete(Path.Combine(build, "leaf2.txt"));

            File.WriteAllBytes(Path.Combine(build, "movie.mp4"), new byte[4096]);

            Run7z(build, "a", "-t7z", "level2.7z", "level3.7z", "level4.7z", "movie.mp4");
            File.Delete(Path.Combine(build, "level3.7z"));
            File.Delete(Path.Combine(build, "level4.7z"));
            File.Delete(Path.Combine(build, "movie.mp4"));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "level2.7z");

            Directory.Delete(build, recursive: true);

            return outer;
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
        /// **用例 G / H / H2 共用的夹具**（复刻 §44.2）：`outer.7z` 里装着
        /// 「一组真 7z 分卷 `mid.7z.001/.002/...`」＋「同组一片、名字被改坏的 `mid.7z.0删除02`」＋`layer1.txt`。
        ///
        /// <para>那一组里是第 2 层的内容（`final.txt` + `layer2.txt` + 5 KiB 不可压数据，保证真的切成多片）；
        /// 名字被改坏的那一片取"**卷标记里夹垃圾**"那种坏法（用户 2026-09-28 真机 `amb909.7sz.00c1` 那一类）
        /// —— 它与 `mid.7z.001` **同族同基名**，闸门认得出它跟分卷是一组
        /// （⚠ 2026-10-10 起判据是"基名 + 族"：原来那个 `mid.z删除ip` 是 zip 族、与 7z 分卷**可证跨族**，
        /// 用它会把这套夹具的前提写坏 ⇒ 三条用例全红，见下面写入点那段注释）。</para>
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
             * 三档形状（**只看"候选离那两条分支的共同祖先有多远"**）：
             *
             * · `nestedDepth = 1`（用例 G）：分卷组与另一片各在一层里（`pieces\` vs `stray\`）。
             * · `nestedDepth = 2`（用例 H）：组在 `AAA\BBB\`、另一片在**同级另一条分支** `CCC\` ——
             *   第一版套了 `pieces\AAA\BBB\`（候选比共同祖先深三层）时，祖先链上溯三层刚好差一层、
             *   扫不到 `CCC\` ⇒ 用例红（实测踩过这一脚），所以这一档刻意只套两层。
             * · `nestedDepth = 3`（用例 H2，2026-10-03 第三轮）：组在 `X\AAA\BBB\`、另一片在
             *   **另一条分支** `Y\CCC\` —— 候选离共同祖先（那个同时含 `X` 与 `Y` 的目录）**四层**，
             *   3 层上限必然漏扫（这正是用户批准「取消 3 层上限」要关掉的那个口子）。
             */
            string piecesDirectory = nestedDepth switch
            {
                <= 1 => Path.Combine(build, "pieces"),
                2 => Path.Combine(build, "AAA", "BBB"),
                _ => Path.Combine(build, "X", "AAA", "BBB")
            };

            string strayDirectory = nestedDepth switch
            {
                <= 1 => Path.Combine(build, "stray"),
                2 => Path.Combine(build, "CCC"),
                _ => Path.Combine(build, "Y", "CCC")
            };

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

            /*
             * 名字被改坏的同组一片（§44.2：`一只顶美.z删除ip` 那种）—— **必须与分卷同一族**：
             * 判据 2026-10-10 起是"基名 + 族"（用户拍板，跨族同基名不再算同组），
             * 而这里的组是 **7z 数字分卷族**（`mid.7z.001`）⇒ 这一片只能用**卷标记里夹垃圾**那种坏法
             * （`mid.7z.0删除02`）。
             * ⚠ 原来写的是 `mid.z删除ip`（zip 族）—— 探针实测 `VolumeGroupDetector` 对它与 `mid.7z.001`
             * 判「可证跨族=True」，也就是**它压根不在这一组里**，夹具的前提本身就不成立（于是新判据一落地
             * 这三条用例就红）。⛔ 别改回 zip 族那个名字。
             */
            File.WriteAllText(
                Path.Combine(strayDirectory, "mid.7z.0删除02"),
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

            outerArgs.Add(Path.GetRelativePath(build, Path.Combine(strayDirectory, "mid.7z.0删除02")));
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
        /// 密码刻意不进密码本 ⇒ 轮到那一层必然失败（用例 B 用）。
        /// <paramref name="passwordOuter"/> = true ⇒ **最外层源包自己**用密码包造
        /// ⇒ 根源包那一层就失败（用例 K3 用：只有这一档才会触发"其余物没处理"那句点名）。</para>
        /// </summary>
        /// <summary>
        /// 造一条嵌套链（旧签名，等价于"在当前临时根里造"）：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>，
        /// **每一层都另外带一个自己的内容文件**（`layerN.txt`）—— 真实机器上的包大多如此，
        /// 而且这样最外层那一单第 1 轮就有内容物可定稿（源包搬运因此走"本轮直接搬"）。
        ///
        /// <para><paramref name="passwordLevel"/> 指定的那一层用密码包（<c>-mhe=on</c>）造，
        /// 密码刻意不进密码本 ⇒ 轮到那一层必然失败（用例 B 用）。
        /// <paramref name="passwordOuter"/> = true ⇒ **最外层源包自己**用密码包造
        /// ⇒ 根源包那一层就失败（用例 K3 用：只有这一档才会触发"其余物没处理"那句点名）。</para>
        /// </summary>
        private string BuildChain(int levelCount, int? passwordLevel = null, bool passwordOuter = false) =>
            BuildChainInto(_root, levelCount, passwordLevel, passwordOuter);

        /// <summary>
        /// 「造一条嵌套链」的**唯一实现**（<paramref name="rootDirectory"/> 决定样本落在哪个临时根里）。
        ///
        /// <para>为什么要带根：递归路那一组用例要**跑两遍对照**（逐层回收开 / 关），
        /// 两遍必须各有各的临时根 —— 共用一份会让第二遍看到第一遍留下的其余物与工作区，
        /// 而"两遍产物逐字节相同"那条判据正是靠"两遍互不干扰"才成立的。</para>
        /// </summary>
        private string BuildChainInto(
            string rootDirectory,
            int levelCount,
            int? passwordLevel = null,
            bool passwordOuter = false)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(rootDirectory, "chain-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));

            for (int level = levelCount; level >= 2; level--)
            {
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

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            string sourceDirectory = Path.Combine(rootDirectory, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            if (passwordOuter)
            {
                Run7z(build, "a", "-t7z", outer, "-p" + UnknownLayerPassword, "-mhe=on", "level2.7z", "layer1.txt");
            }
            else
            {
                Run7z(build, "a", "-t7z", outer, "level2.7z", "layer1.txt");
            }

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

        private Harness CreateHarness(
            string restMode,
            SourceHandlingMode sourceMode,
            Action<AppSettings>? tweak = null,
            IArchiveEngine? engineOverride = null)
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

            tweak?.Invoke(settings);

            settingsService.Save(settings);

            var engine = engineOverride ?? new SevenZipEngine();
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

        // ==================================================================================
        // 递归路（一条链是**一个任务**、层是工作区目录）—— 用户 2026-10-05：「两条路要同步」
        //
        // 上面那一整组钉的是**续解链**（层 = 任务）。而「展开所有分支」那条 4 层链是**一个任务**内
        // 由 `RecursiveExtractor` 展开的（层 = 工作区目录），过去**没有**逐层回收 —— 过程物一直攒到
        // 定稿那一刻才释放（真机第六批：空间曲线 13:39:32 最低 62.48 GiB → 13:40:57 回到 82.63 GiB，
        // 峰值 26.94 GiB 而结果只有 7.28 GB）。
        //
        // ⛔ 这一组**不是新功能**：AGENTS.md §11.3 早就写定「续解链每一层『定稿 + 输出校验通过 +
        // 未取消 + 可证完整』之后当场按『删除操作』处理这一层的过程物」，只是**只在一条路上落了地**。
        // ==================================================================================

        /// <summary>
        /// <b>用例 R1（递归路逐层回收）</b>：四层真链 + ②页「展开所有分支」（**出厂默认档**）+
        /// ③页「彻底删除」⇒
        ///
        /// <list type="number">
        /// <item><description><b>链还没跑完的那一刻，已经被解开的那一层的内层包在盘上已经没了</b> ——
        /// 判据不在"最后删干净了"（那可能只是链尾清得好），而在**中途那一帧**：
        /// 注入的假引擎在**每一次开始解压之前**拍一帧盘上事实，于是"层 3 开工时
        /// <c>level2.7z</c> 已经不在了"这件事只能由"它是在层 2 那一步被删的"来解释。</description></item>
        /// <item><description>同一个任务、同一条链里，**三个内层包各删各的**（层 1/2/3 各一次），
        /// 走的是"删这一层的源"那一个既有执行体。</description></item>
        /// <item><description><b>最外层源包留到链尾</b>（红线：它不在递归工作区里，这一档永远不碰它）。</description></item>
        /// <item><description><b>最终产物逐字节不变</b>：同一份样本跑两遍（回收开 / 关），
        /// 两遍的 ① 文件数 ② 相对路径清单 ③ 每个文件的 SHA256 必须逐项相同。</description></item>
        /// </list>
        ///
        /// <para>⚠ 这里刻意**不用**计时 / 内存 / 峰值字节断言（那是脆的）：判据全是"盘上有没有这个文件"。
        /// 峰值那件事由峰值的**机制**回答 —— 过程物在下一层开工前就没了，峰值自然从"四层全攒着"
        /// 降到"源包 + 当前层 + 下一层"。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例R1_递归路每一层当场回收内层包_链没跑完就已经没了_产物逐字节不变()
        {
            const int layers = 4;

            /*
             * 两遍跑同一份**代码路径**、同一份样本，唯一的差别是那个事实位：
             * 一遍「彻底删除」（逐层回收开），一遍「不动其余物」（逐层回收关）。
             * 两遍的最终产物必须逐字节相同 —— 这就是"逐层回收只是把过程物早点还回去，
             * 不是换一套产物"的机器判据。⚠ 两遍都用「源包留在原地」：源包要留在原处做第二次跑。
             */
            RecursionReclaimRun deleted = await RunRecursionChainAsync(PermanentDeleteRestMode);

            RecursionReclaimRun untouched = await RunRecursionChainAsync(RestHandlingModes.Keep);

            _output.WriteLine(
                $"【用例R1】彻底删除档：解压调用 {deleted.Probe.ExtractStarts.Count} 次 ｜ 逐层回收删了 "
                + $"{deleted.Harness.SourceDeletes.DeletedPaths.Count} 个："
                + string.Join("、", deleted.Harness.SourceDeletes.DeletedPaths.Select(Path.GetFileName)));

            foreach ((string archive, IReadOnlyList<string> snapshot) in deleted.Probe.ExtractStarts)
            {
                _output.WriteLine($"　　开始解压 {archive} 时盘上的 level*.7z：{string.Join("、", snapshot)}");
            }

            foreach (OperationLogItem entry in deleted.Harness.Log.Logs)
            {
                _output.WriteLine($"　　[{entry.Level}] {entry.Message}");
            }

            // ① 内容物出来了（不然"内层包都没了"可能只是整条链没跑）。
            Assert.NotEmpty(deleted.ProducedFiles);

            /*
             * ② **核心断言（红检点，刻意放在最前面）**：链还没跑完的那一刻，
             * 已经被解开的那一层的内层包在盘上**已经没了**。
             *
             * 取"开始解压 level3.7z"那一帧：那一刻 `level2.7z` 必须在**上一层跑完时**就被删掉了，
             * 而 `level3.7z`（这一层要解的）当然还在。老口径（攒到链尾）这一帧里三个都还在
             * —— 红检实测的失败原文就是这一条：
             * `Assert.DoesNotContain() Failure: Filter matched in collection`，帧快照 =
             * `level2.7z、level3.7z`。
             */
            (string Archive, IReadOnlyList<string> Snapshot) frame =
                deleted.Probe.ExtractStarts.FirstOrDefault(f => IsNamed(f.Archive, "level3.7z"));

            Assert.False(
                string.IsNullOrEmpty(frame.Archive),
                "假引擎没拍到「开始解压 level3.7z」那一帧 —— 这条链根本没走到第 3 层，"
                + "拍到的是：" + string.Join("、", deleted.Probe.ExtractStarts.Select(f => Path.GetFileName(f.Archive))));

            Assert.DoesNotContain("level2.7z", frame.Snapshot);

            Assert.Contains("level3.7z", frame.Snapshot);

            // ③ **对照（判据不许恒真）**：同一帧在"不逐层回收"那一遍里，level2.7z 必须还在盘上。
            (string Archive, IReadOnlyList<string> Snapshot) comparisonFrame =
                untouched.Probe.ExtractStarts.FirstOrDefault(f => IsNamed(f.Archive, "level3.7z"));

            Assert.False(string.IsNullOrEmpty(comparisonFrame.Archive), "对照那一遍也没走到第 3 层");

            Assert.Contains("level2.7z", comparisonFrame.Snapshot);

            /*
             * ④ 三个内层包各删各的（层 1/2/3 各一次）：判据是"删这一层的源"那个执行体被调了三次，
             * 而且点的正是那三个内层包；⛔ 最外层源包**一次都不在里面**。
             */
            Assert.Equal(layers - 1, deleted.Harness.SourceDeletes.DeletedPaths.Count);

            Assert.Contains(deleted.Harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level2.7z"));
            Assert.Contains(deleted.Harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level3.7z"));
            Assert.Contains(deleted.Harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level4.7z"));
            Assert.DoesNotContain(deleted.Harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "outer.7z"));

            // ⑤ 那一句话每一层都要说（"我的内层包哪去了"必须能事后回答）。
            int reclaimLines = deleted.Harness.Log.Logs.Count(
                entry => entry.Message.Contains("这一层的过程物（上一层的输入包）已按", StringComparison.Ordinal));

            Assert.True(
                reclaimLines == layers - 1,
                $"该有 {layers - 1} 条递归路逐层回收日志，实际 {reclaimLines} 条。");

            // ⑥ **最终产物逐字节不变**（文件数 + 相对路径清单 + 每个文件的 SHA256）。
            Assert.Equal(untouched.ProducedFiles.Count, deleted.ProducedFiles.Count);

            Assert.Equal(
                untouched.ProducedFiles.Select(p => Path.GetRelativePath(untouched.Root, p)).OrderBy(p => p, StringComparer.Ordinal),
                deleted.ProducedFiles.Select(p => Path.GetRelativePath(deleted.Root, p)).OrderBy(p => p, StringComparer.Ordinal));

            foreach (string relative in deleted.Digest.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                Assert.True(
                    untouched.Digest.TryGetValue(relative, out string? expected),
                    $"对照那一遍的产物里没有 {relative}");

                Assert.Equal(expected, deleted.Digest[relative]);
            }

            // ⑦ 最终产物里**一个内层包都不该留**（内容是解到底的东西）。
            Assert.All(deleted.ProducedFiles, file => Assert.EndsWith(".txt", file, StringComparison.OrdinalIgnoreCase));

            _output.WriteLine($"【用例R1】两遍产物逐字节相同：{deleted.ProducedFiles.Count} 个文件");
        }

        /// <summary>
        /// <b>用例 R2（红线：这一层没跑成 ⇒ 一个字节都不动）</b>：同一条四层真链，但**第三层**解不开
        /// （那一层的包设了密码、密码本里没有它）⇒
        ///
        /// <list type="number">
        /// <item><description>**只有第二层那一层回收成功过**（它自己那一层的输入 = <c>level2.7z</c>）：
        /// 第三层的回收要等第三层跑成，而链在第二层就停了。</description></item>
        /// <item><description>第三层那一层的输入包**一个字节都没被这一档碰过**
        /// （判据 = 删除执行体没被点到它，而且"已回收"那句话只出现一次）。</description></item>
        /// <item><description>最外层源包原地不动；失败层没有内容物落地。</description></item>
        /// </list>
        /// </summary>
        [SevenZipFact]
        public async Task 用例R2_递归路中间那一层失败_一个字节都不动()
        {
            Harness harness = CreateHarness(
                PermanentDeleteRestMode,
                SourceHandlingMode.KeepInPlace,
                settings => settings.RecursionMode = "AllBranches");

            string outer = BuildChain(4, passwordLevel: 3);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

            _output.WriteLine(
                $"【用例R2】删除执行体被调用 {harness.SourceDeletes.DeletedPaths.Count} 次："
                + string.Join("、", harness.SourceDeletes.DeletedPaths.Select(Path.GetFileName)));

            /*
             * ① 只有第二层那一层回收成功过（它自己那一层的输入 = level2.7z）。
             * 第三层的回收要等**第三层跑成**才发生，而链在第二层就停了 ⇒ 它一次都没发生。
             */
            Assert.Single(harness.SourceDeletes.DeletedPaths);

            Assert.Contains(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level2.7z"));

            // ② 第三层那一层的输入包**一个字节都没被这一档碰过**（判据 = 删除执行体没被点到它）。
            Assert.DoesNotContain(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level3.7z"));
            Assert.DoesNotContain(harness.SourceDeletes.DeletedPaths, p => IsNamed(p, "level4.7z"));

            /*
             * ③ 而且**回收回调根本没资格被调**：它排在"这一层跑成了"之后，本层没跑成 ⇒ 一次都不调。
             * 证据是那一行"已回收"只出现**一次**（第二层那一次），第三层那一层一个字都没说。
             */
            int reclaimLines = harness.Log.Logs.Count(
                entry => entry.Message.Contains("这一层的过程物（上一层的输入包）已按", StringComparison.Ordinal));

            Assert.Equal(1, reclaimLines);

            // ④ 最外层源包原地不动（本档「留在原地」）。
            Assert.True(File.Exists(outer), "失败 / 部分完成 ⇒ 源包必须原地不动");

            // ⑤ 失败层没有内容物落地（这一层真的失败了）。
            Assert.Empty(FindFiles("layer3.txt"));
        }

        /// <summary>
        /// <b>用例 R3（B 档位分叉在递归路上同样成立）</b>：③页「移入回收站」档**一层都不当场回收** ——
        /// 递归路的回收只认那**一个**事实位（<c>_layerReclaimThisBatch</c>），
        /// ⛔ 不许在递归里另算一遍（§9.5：同一件事只有一个出口）。
        /// </summary>
        [SevenZipFact]
        public async Task 用例R3_递归路移入回收站档_一层都不当场回收()
        {
            Harness harness = CreateHarness(
                RestHandlingModes.RecycleBin,
                SourceHandlingMode.MoveToRest,
                settings => settings.RecursionMode = "AllBranches");

            string outer = BuildChain(4);

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(240));

            _output.WriteLine($"【用例R3】回收站档下逐层回收删了 {harness.SourceDeletes.DeletedPaths.Count} 个");

            // ⛔ 零个当场回收（回收站不释放盘上空间 ⇒ 逐层做零收益、只多造条目）。
            Assert.Empty(harness.SourceDeletes.DeletedPaths);

            Assert.DoesNotContain(
                harness.Log.Logs,
                entry => entry.Message.Contains("这一层的过程物（上一层的输入包）已按", StringComparison.Ordinal));

            // 内容物照旧出来了（不是"整条链没跑"）。
            Assert.NotEmpty(FindFiles("final.txt"));
        }

        /// <summary>
        /// <b>用例 R4（分卷组整组一起还）</b>：第一层的内层包是**一组真 7z 分卷**
        /// （`level2.7z.001` / `.002` / `.003`，`-v16k` 切出来的）⇒
        /// ① 递归层把这一组折叠成**一个**内层归档（既有那把折叠尺）、
        /// ② 这一层跑成时**整组三片一起**被回收（只删首卷会留下再也拼不起来的碎片）、
        /// ③ 最外层源包一次都不在那份名单里、④ 产物照旧解到底。
        ///
        /// <para>⚠ 真机第六批那条 4 层链里内层包正是"3 片分卷 + 3 个 mp4 + zip"的形状
        /// （6.8 GB 的分卷一直留到定稿那一刻），所以这条用例钉的是"分卷组也一起还"。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 用例R4_内层包是一组分卷时_整组三片一起当场回收()
        {
            Harness harness = CreateHarness(
                PermanentDeleteRestMode,
                SourceHandlingMode.KeepInPlace,
                settings => settings.RecursionMode = "AllBranches");

            string outer = BuildVolumeGroupChain();

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(240));

            List<string> deletedNames = harness.SourceDeletes.DeletedPaths
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _output.WriteLine($"【用例R4】逐层回收删了 {deletedNames.Count} 个：{string.Join("、", deletedNames)}");

            // ① 内容物出来了（说明这一组真被解开过 —— 不是"没解所以没删"）。
            Assert.NotEmpty(FindFiles("final.txt"));
            Assert.NotEmpty(FindFiles("layer2.txt"));

            /*
             * ② 整组三片**一起**被回收：判据是删除清单里那三片一个不少。
             * 只删首卷 = 留下一组再也拼不起来的碎片（`SourceCleanupService` 在这里看的是分卷组清单）。
             */
            Assert.Equal(3, deletedNames.Count(name => name.StartsWith("level2.7z.00", StringComparison.OrdinalIgnoreCase)));

            Assert.Contains("level2.7z.001", deletedNames);
            Assert.Contains("level2.7z.002", deletedNames);
            Assert.Contains("level2.7z.003", deletedNames);

            // ③ ⛔ 最外层源包一次都不在那份名单里。
            Assert.DoesNotContain("outer.7z", deletedNames);

            // ④ 最外层源包原地不动（本档「留在原地」）。
            Assert.True(File.Exists(outer), "源包必须原地不动");

            /*
             * ⑤ 这一组的三片与内层包 <c>mid.7z</c> 都**不在了**（内容物留下、过程物走光）。
             * ⚠ 判据按**名字**点这四个（⛔ 不写"盘上一个 *.7z 都没有"：本类几十条用例共用同一个临时根，
             * 别的用例留下的 <c>其余物</c> 里本来就有包 —— 那样的断言量的是别人的残渣，不是本用例的结论）。
             */
            Assert.Empty(FindFiles("level2.7z.00*"));
            Assert.Empty(FindFiles("mid.7z"));
        }

        /// <summary>
        /// 造一条"第一层的内层包是**一组三片分卷**"的链：
        /// <c>outer.7z → level2.7z.001/.002/.003 → mid.7z → final.txt</c>（外加各层自己的 txt）。
        ///
        /// <para><c>-v16k</c> 切出来的三片在同一层里，入口是 <c>.001</c>（自报第 1 卷）——
        /// 与真机那条链（3 片 7z 分卷）同形。⚠ 分卷本体只有第 1 片带魔数，
        /// 所以"这一组算一个内层归档"靠的是既有折叠尺（同目录 + 同基名 + 是后续卷）。</para>
        /// </summary>
        private string BuildVolumeGroupChain()
        {
            string build = Path.Combine(_root, "vol-build");
            Directory.CreateDirectory(build);

            File.WriteAllText(Path.Combine(build, "final.txt"), "最深一层的内容\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(build, "layer2.txt"), "第 2 层自己的内容\n", new UTF8Encoding(false));

            // 32 KiB 不可压数据：`-v16k` 下必然切出 ≥3 片（不然就不是"一组分卷"了）。
            byte[] blob = new byte[32 * 1024];
            new Random(20261005).NextBytes(blob);
            File.WriteAllBytes(Path.Combine(build, "blob.bin"), blob);

            // 第 2 层（最内层）自己的包：装 final.txt + layer2.txt + blob（分卷那一组要装它）。
            Run7z(build, "a", "-t7z", "-mx0", "mid.7z", "final.txt", "layer2.txt", "blob.bin");

            File.WriteAllText(Path.Combine(build, "layer1.txt"), "第 1 层自己的内容\n", new UTF8Encoding(false));

            // 第 1 层的内层包 = 一组三片分卷（装 mid.7z + layer1.txt…… 用 layer2.txt 做陪衬）。
            Run7z(build, "a", "-t7z", "-mx0", "-v16k", "level2.7z", "mid.7z", "layer2.txt");

            string[] parts = Directory.GetFiles(build, "level2.7z.*");

            Assert.True(
                parts.Length >= 3,
                "`-v16k` 下应当切出至少三片，实际 " + parts.Length + " 片：" + string.Join("、", parts.Select(Path.GetFileName)));

            string sourceDirectory = Path.Combine(_root, "src");
            Directory.CreateDirectory(sourceDirectory);

            string outer = Path.Combine(sourceDirectory, "outer.7z");

            var outerArgs = new List<object> { "a", "-t7z", outer };

            foreach (string part in parts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                outerArgs.Add(Path.GetRelativePath(build, part));
            }

            outerArgs.Add("layer1.txt");

            Run7z(build, outerArgs.ToArray());

            Directory.Delete(build, recursive: true);

            return outer;
        }

        /// <summary>
        /// 跑一遍"递归展开一条四层链"（②页「展开所有分支」），把判据需要的事实全部带回来：
        /// 那一次跑的临时根、产物文件清单、逐字节指纹、假引擎拍到的每一帧、以及记账式删除执行体。
        ///
        /// <para>⚠ 每一次都在**自己那一个临时根**里跑：两遍对照必须互不干扰（源包要留在原处）。</para>
        /// </summary>
        private async Task<RecursionReclaimRun> RunRecursionChainAsync(string restMode)
        {
            string runRoot = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(runRoot);

            var run = new RecursionReclaimRun(runRoot);

            string buildSource = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(buildSource);

            (Harness harness, ExtractionProbeEngine probe, string outer) = CreateProbeHarness(runRoot, restMode);

            run.Harness = harness;
            run.Probe = probe;
            run.SourcePackage = outer;

            await harness.AddPathsAsync(outer);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(240));

            run.ProducedFiles = Directory.Exists(runRoot)
                ? Directory.GetFiles(runRoot, "*", SearchOption.AllDirectories)
                    .Where(file => !IsNamed(file, "outer.7z"))

                    /*
                     * 只比**内容物**（`out\` 那棵树）：另两个是每次跑都必然不同的运行时文件 ——
                     * `data\logs\...<时间戳>.log`（日志文件名带跑的时间）与
                     * `data\appsettings.json` / `data\password-list.dat`（运行期写盘的状态）。
                     * ⛔ 把它们算进"逐字节不变"只会让判据变脆，与"过程物早删晚删不影响产物"这件事无关。
                     */
                    .Where(file => !file.Contains(
                        Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            foreach (string file in run.ProducedFiles)
            {
                run.Digest[Path.GetRelativePath(runRoot, file)] = Sha256(file);
            }

            return run;
        }

        /// <summary>
        /// 在一个**给定的临时根**里造一条四层真链 + 一套带假引擎的测试宿主
        /// （样本来源目录、数据目录、输出目录全在那一个根下面 —— 两遍对照因此互不干扰）。
        /// </summary>
        private (Harness Harness, ExtractionProbeEngine Probe, string Outer) CreateProbeHarness(
            string runRoot,
            string restMode)
        {
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;

            // ★ 出厂默认档：展开所有分支 ⇒ 整条链由 **RecursiveExtractor** 在一个任务里展开。
            settings.RecursionMode = "AllBranches";

            settings.AutoScanAfterDrop = false;
            settings.TryEmptyPasswordFirst = false;
            settings.SourceHandling = SourceHandlingMode.KeepInPlace.ToString();
            settings.RestHandlingAfterVerify = restMode;
            settings.CustomSevenZipExePath = string.Empty;

            settingsService.Save(settings);

            var probe = new ExtractionProbeEngine(new SevenZipEngine(), runRoot);
            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                probe,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, probe, new PasswordService(), pathService, new DialogService());

            var sourceDeletes = new RecordingDeleteFileSystem(runRoot);

            extraction.SourcePackageDeleteFileSystem = sourceDeletes;
            extraction.KeepTaskDetailInLog = true;

            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            string outer = BuildChainInto(runRoot, 4);

            return (new Harness(vm, oneClick, extraction, logService, sourceDeletes), probe, outer);
        }

        /// <summary>一次"递归跑一条四层链"的全部事实（见 <see cref="RunRecursionChainAsync"/>）。</summary>
        private sealed class RecursionReclaimRun
        {
            public RecursionReclaimRun(string root) => Root = root;

            public string Root { get; }

            public Harness Harness { get; set; } = null!;

            public ExtractionProbeEngine Probe { get; set; } = null!;

            public string SourcePackage { get; set; } = string.Empty;

            public IReadOnlyList<string> ProducedFiles { get; set; } = Array.Empty<string>();

            public Dictionary<string, string> Digest { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 假的引擎包装（**只观测、不改变任何结论**）：每一次开始解压之前，把"这一次跑的临时根下面
        /// 还剩哪几个 <c>level*.7z</c>"记一帧 —— 递归路逐层回收的判据就是这一帧。
        /// ⛔ 它原样转发给真 7-Zip，不拼参数、不改结论（AGENTS.md §3 四条禁止项）。
        /// </summary>
        private sealed class ExtractionProbeEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;
            private readonly string _root;

            public ExtractionProbeEngine(IArchiveEngine inner, string root)
            {
                _inner = inner;
                _root = root;
            }

            /// <summary>每一次开始解压：要解的归档 + 那一刻盘上还剩哪几个 <c>level*.7z</c>（按名字）。</summary>
            public List<(string Archive, IReadOnlyList<string> Snapshot)> ExtractStarts { get; } = new();

            public string Name => _inner.Id;

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.ProbeAsync(request, cancellationToken);

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.ListAsync(request, cancellationToken);

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                _inner.TestAsync(request, cancellationToken);

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractStarts.Add((request.ArchivePath, SnapshotLevelPackages()));

                return _inner.ExtractAsync(request, options, cancellationToken);
            }

            private IReadOnlyList<string> SnapshotLevelPackages()
            {
                try
                {
                    return Directory.Exists(_root)
                        ? Directory.GetFiles(_root, "level*.7z", SearchOption.AllDirectories)
                            .Select(path => Path.GetFileName(path) ?? string.Empty)
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                        : new List<string>();
                }
                catch
                {
                    // 观测失败就当这一帧什么都没看见（判据会因此变红时宁可红，也不许猜）。
                    return new List<string>();
                }
            }
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }
}
