using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 这一组测试要构造真的 <see cref="MainViewModel"/>，而它的构造过程会写两个**进程级静态**：
    /// 7z.exe 路径（<c>ToolLocator.Default</c>）与递归工作区根目录（<c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>）。
    /// 和别的测试集并行跑时，别的测试会拿着"我这边马上要被删掉的临时目录"当工作区去解压
    /// （实测：RecursiveExtractorTests 因此在解压到一半时目录被删，报"引擎操作失败"）。
    ///
    /// 所以这一组声明成"不与其他集合并行"，并且每次装配后**立刻把两个静态还原**（见 CreateHarness）。
    /// </summary>
    [CollectionDefinition("ArchiveFixerGlobalState", DisableParallelization = true)]
    public class ArchiveFixerGlobalStateCollection
    {
    }

    /// <summary>
    /// 一键处理**自动续解内层包**（第 2 层起）的行为测试。
    ///
    /// 真实现场：用户的文件是"双面文件"（<c>xxx.mp4</c> = 真 MP4 头 + 尾部一个完整 ZIP），
    /// 解出来的第一层不是最终数据，而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c>（加密分卷），
    /// 要再解一层才是结果。以前一键处理只做第一层就停手，用户看到的现象就是"只有第一层能解出来"。
    ///
    /// 这里跑的是**真链路**：真 7z.exe、真 MainViewModel + 各 Coordinator、真的单层解压管线
    /// （刻意不碰 <c>RecursiveExtractor</c> —— 它在用户的真实文件上会卡死，续解不走它）。
    /// 引擎被包一层只是为了**数调用次数**：要证明"第 2 轮没有再解一遍已经处理过的包"，
    /// 只能看引擎实际被调了几次、拿的是哪个文件。
    ///
    /// 样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class InnerLayerContinuationTests : IDisposable
    {
        private const string OuterPassword = "OuterPass1";
        private const string InnerPassword = "InnerPass2";

        /// <summary>续解链测试用的统一密码（列表式密码本，一行一个）。</summary>
        private const string ChainPassword = "ChainPass9";

        private const string InnerPayloadText = "第二层才有的最终数据\n";

        /// <summary>假视频头长度：与 EmbeddedArchiveTests 一致（34 KB 的文件头读不到尾部的 ZIP）。</summary>
        private const int FakeVideoPrefixLength = 32768;

        private readonly string _root;
        private readonly string _sevenZip;

        public InnerLayerContinuationTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerInnerLayer", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ---------------------------------------------------------------- 第一步：产物里有 .7z.001 → 续解

        [Fact]
        public async Task 第一层产物里有7z分卷时_自动续解到第二层()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionNormal, outerTask.ExtensionStatus);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.False(outcome.HitRoundLimit);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            // 第 2 层真的解出来了。
            // 输出目录名沿用既有的 BuildOutputPath 规则（分卷名 inner.7z.001 → 目录名 inner.7z），
            // 这里不去猜目录名，直接在产物根下找那份只有第 2 层才有的文件。
            Assert.Equal(2, harness.Vm.Tasks.Count);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"第 2 层应该产出一份 payload.txt，实际 {payloads.Length} 份（目录：{harness.OutputRoot}）");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 分项之和必须等于本次处理的任务数（含续解加进来的任务）
            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);

            // 临时关掉的"导入后自动扫描"必须还原（不能把用户的设置改坏了）
            Assert.True(harness.Vm.Settings.AutoScanAfterDrop);

            /*
             * 源包（一键处理默认档 MoveToRest）：**链结束之后**被补搬进其余物。
             *
             * 这是 2026-09-22 修掉的那个缺陷的判据：本样本第一层只有内层分卷（0 个内容物），
             * 修复前 `commit.MovedContentCount == 0` 会让源包搬运彻底不发生；
             * 现在最外层那一轮把它记账成"留到链结束后补搬"，链尾补做。
             */
            string rest = Path.Combine(harness.OutputRoot, "outer", "其余物");

            Assert.False(File.Exists(outer), "源包应该已经被搬进其余物（用户 2026-09-22 的新规则）");
            Assert.True(File.Exists(Path.Combine(rest, "outer.7z")), $"源包没有落到 {rest}");
            Assert.True(File.Exists(Path.Combine(rest, "inner.7z.001")), "内层分卷也应该在同一个其余物里");

            // 日志必须能分清"链结束后的补搬"（而不是本轮直接搬），并说清它凭什么搬。
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("链结束后的补搬", StringComparison.Ordinal) &&
                        item.Message.Contains("内容物", StringComparison.Ordinal));

            // 任务对象跟着改到新位置（续解扫描靠它把刚搬走的源包排除掉）。
            Assert.Equal(Path.Combine(rest, "outer.7z"), outerTask.CurrentPath);
        }

        // ---------------------------------------------------------------- 形状 A：第一层直接出内容物

        /// <summary>
        /// <b>形状 A（正向对照）</b>：真正的 7z 分卷组，第一层直接解出内容物
        /// （对应 flowe2e 里那个 <c>444.7z.001/.002/.003</c> 的现场）。
        ///
        /// <para>
        /// 这条形状在修复前就是好的，必须继续成立：源包**整组**（3 卷一个都不许落下）
        /// 当场搬进其余物，内容物不受影响。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 形状A_第一层直接出内容物_真7z分卷组整组移入其余物()
        {
            string packageDirectory = BuildVolumeGroupWithContent("444");
            string first = Path.Combine(packageDirectory, "444.7z.001");

            Assert.True(File.Exists(first), "样本没造出来");

            List<string> volumes = Directory.GetFiles(packageDirectory, "444.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Harness harness = CreateHarness($"444:{OuterPassword}\n");

            // 整组一起导入：VolumeGroupingService 只用**已导入的任务**喂分组器。
            await harness.AddPathsAsync(volumes.ToArray());

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.True(task.IsVolumeGroup, "3 卷应该被认成一组分卷");
            Assert.Equal(volumes.Count, task.VolumePaths.Count);
            Assert.True(task.IsVolumeComplete, $"分卷应该完整：{task.VolumeInfoText}");

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);

            string rest = Path.Combine(harness.OutputRoot, "444", "其余物");

            // 整组源包一个都不许落下（决策 D-12）。
            foreach (string volume in volumes)
            {
                Assert.False(File.Exists(volume), $"源包分卷没有被搬走：{volume}");
                Assert.True(
                    File.Exists(Path.Combine(rest, Path.GetFileName(volume))),
                    $"源包分卷没有落进其余物：{Path.GetFileName(volume)}");
            }

            // 分卷清单跟着改到新位置（后续清理 / 续解都读它）。
            Assert.All(
                task.VolumePaths,
                path => Assert.StartsWith(rest, path, StringComparison.OrdinalIgnoreCase));

            // 内容物照常，而且是**当场**搬的（日志里不该出现"链结束后的补搬"）。
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "444", "content.txt")));
            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("链结束后的补搬", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- 第二步：不重复解压已处理的任务

        [Fact]
        public async Task 第二轮不会把已经处理过的任务再解一遍()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);
            string outerPath = outerTask.CurrentPath;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 先确认真的续解了，否则"没重复解压"是句废话
            Assert.Equal(2, outcome.Rounds);

            /*
             * 一个包被"完整解一轮"最多只会提交几次（每个密码候选一次：空密码 + 密码本命中）。
             * 如果第 2 轮把第 1 轮的任务又解了一遍，这里会变成 4 次以上。
             */
            int calls = harness.Engine.ExtractCalls.Count(p => string.Equals(p, outerPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(calls <= 2, $"第 1 轮的任务被重复解压了：ExtractAsync 一共被调了 {calls} 次");

            Assert.False(outerTask.IsSelected, "处理过的任务必须取消勾选，否则下一轮会重新解压、产出 (1) 垃圾副本");

            /*
             * 内层分卷落进**其余物**（契约 §3.2）：用户看到的是"一个源包 = 一个目录"，
             * 过程物集中在 其余物 里，不再和内容物混在同一层。
             */
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "outer", "其余物", "inner.7z.001")));

            // 没有多出"(1)"这种自动改名副本目录
            Assert.False(Directory.Exists(Path.Combine(harness.OutputRoot, "outer (1)")), "不该出现重复解压留下的副本目录");
        }

        // ---------------------------------------------------------------- 第三步：轮数硬上限

        [Fact]
        public async Task 一直产出新包时_最多只续解到设置里的最大层数_到顶提示还剩几个()
        {
            Harness harness = CreateHarness(ChainPassword + "\n");

            /*
             * 上限**跟着设置走**（②页「最大嵌套层数」，默认 5，天花板 10）：
             * 取生效值而不是那个常量，否则"界面说 5、程序跑 10"这种不同步在测试里也看不出来。
             */
            int roundLimit = harness.OneClick.RoundLimit;

            // 链比上限多一层：跑满 roundLimit 轮之后，正好还剩最后那一层没解。
            int lastLevel = roundLimit + 1;
            string outer = BuildChain(lastLevel);

            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(roundLimit, outcome.Rounds);
            Assert.Equal(roundLimit - 1, outcome.ContinuationLayers);
            Assert.True(outcome.HitRoundLimit, $"第 {lastLevel} 层还有包，应该报到上限");
            Assert.Contains("轮上限", outcome.Summary, StringComparison.Ordinal);
            Assert.False(outcome.Stopped);

            // 最后那一层的包**没有被解压**：既没有引擎调用，也没有产物目录
            string lastPackage = $"level{lastLevel}.7z";
            Assert.DoesNotContain(harness.Engine.ExtractCalls, p => p.EndsWith(lastPackage, StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(harness.OutputRoot, $"level{lastLevel}")), "到上限就不该再往下解");

            /*
             * 用户 2026-09-24 第 16 条追加：到顶**不许静默停下**。
             * 剩下那一层要① 报个数、② 已经加进列表并勾好（等「继续解」接着解）。
             */
            Assert.Equal(1, outcome.PendingContinuationCount);
            Assert.Contains("还剩 1 个内层包", outcome.Summary, StringComparison.Ordinal);

            ArchiveTask pending = Assert.Single(
                harness.Vm.Tasks,
                t => t.CurrentPath.EndsWith(lastPackage, StringComparison.OrdinalIgnoreCase));

            Assert.True(pending.IsSelected, "剩下的内层包要勾好，否则「继续解」点下去什么都不做");

            // 已经处理过的那些 + 剩下这一个（= 上限轮数 + 1）
            Assert.Equal(roundLimit + 1, harness.Vm.Tasks.Count);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(roundLimit, scope);
        }

        /// <summary>
        /// 「继续解」：到顶之后把剩下那些内层包接着解下去，直到解完（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>它跑的就是一键处理本身（界面上的「继续解」按钮绑的是同一个入口），
        /// 所以这一条同时证明了：剩下的包确实**能**被接着解，而不是只报了个数。</para>
        /// </summary>
        [Fact]
        public async Task 到上限之后继续解_把剩下的解完()
        {
            Harness harness = CreateHarness(ChainPassword + "\n");

            // 上限取生效值（②页「最大嵌套层数」）：链比它多一层，第一轮必然撞上限。
            int lastLevel = harness.OneClick.RoundLimit + 1;
            string outer = BuildChain(lastLevel);

            await harness.AddPathsAsync(outer);

            OneClickOutcome first = await harness.RunOneClickAsync();

            Assert.True(first.HitRoundLimit);
            Assert.Equal(1, first.PendingContinuationCount);

            // 第二次：接着解剩下那一层（界面上就是点「继续解」）
            OneClickOutcome second = await harness.RunOneClickAsync();

            Assert.False(second.HitRoundLimit, "剩下那一层解完就到底了，不该再报上限");
            Assert.Equal(0, second.PendingContinuationCount);

            // 最深处的内容物出来了
            Assert.Contains(
                Directory.GetFiles(harness.OutputRoot, "final.txt", SearchOption.AllDirectories),
                path => File.ReadAllText(path).Contains("最深一层的内容", StringComparison.Ordinal));

            // 而且最后那一层真的被引擎解过（不是"报了个数就算完"）
            Assert.Contains(
                harness.Engine.ExtractCalls,
                p => p.EndsWith($"level{lastLevel}.7z", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 轮数上限**只有一个真值来源**：②页「最大嵌套层数」（<c>AppSettings.MaxRecursionDepth</c>）。
        ///
        /// <para>用户 2026-09-26 原话：<i>"现在将默认的最大的解压层数改成 5，用户有需要自己会改的"</i>——
        /// 默认 5、天花板 10，而且界面写几，一键处理这一批就只跑几轮
        /// （⛔ 不许再出现"界面说 5、程序按 10 跑"的不同步）。</para>
        /// </summary>
        [Fact]
        public void 轮数上限_默认5_跟着设置走_天花板仍然是10()
        {
            Harness harness = CreateHarness(ChainPassword + "\n");

            Assert.Equal(5, harness.Vm.Settings.MaxRecursionDepth);
            Assert.Equal(5, harness.OneClick.RoundLimit);

            // 用户自己改大 → 当场跟着走（不需要重启、不需要重开一批）
            harness.Vm.Settings.MaxRecursionDepth = 7;
            Assert.Equal(7, harness.OneClick.RoundLimit);

            // 超范围两头都要夹回：上面是硬上限（不变量 8），下面最小 1 轮
            harness.Vm.Settings.MaxRecursionDepth = 99;
            Assert.Equal(OneClickCoordinator.MaxRoundsCeiling, harness.OneClick.RoundLimit);
            Assert.Equal(10, harness.OneClick.RoundLimit);

            harness.Vm.Settings.MaxRecursionDepth = 0;
            Assert.Equal(1, harness.OneClick.RoundLimit);
        }

        // ---------------------------------------------------------------- 用户的验收判据：一个源包 = 一个目录

        /// <summary>
        /// 用户的原始抱怨（必须被这条测试钉死）：
        /// <i>"这次的一键处理给我弄的相当糟糕，你给我多弄了四个文件夹，分卷文件你居然又解压到外面来了，
        /// 文件一多根本就分不清"</i>。
        ///
        /// 期望：处理完一个源包之后，**最终只有 <c>&lt;输出根&gt;\&lt;包名&gt;\</c> 这一个目录**，
        /// 里面是内容物 + 一个集中的 <c>其余物</c>；过程物（内层分卷、抠出的 ZIP）一个都不许漏到外面。
        ///
        /// 这里用的是最像真实现场的那条链：`outer.7z` →（第一层就是内层加密分卷）→ 分卷 → 内容物。
        /// 旧实现会额外产出 <c>inner.7z\</c> 这类平级目录，而且分卷本身还和内容物躺在同一层。
        /// </summary>
        [Fact]
        public async Task 一个源包处理完_输出根下只有一个目录()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            // ① 输出根下只有那**一个**目录（源包名）
            string[] directories = Directory.GetDirectories(harness.OutputRoot);
            Assert.True(
                directories.Length == 1,
                $"输出根下应该只有一个目录，实际 {directories.Length} 个：{string.Join("、", directories.Select(Path.GetFileName))}");
            Assert.Equal(Path.Combine(harness.OutputRoot, "outer"), directories[0]);

            string outputDirectory = directories[0];

            // ② 内容物在这个目录里，而且只有一份
            string[] payloads = Directory.GetFiles(outputDirectory, "payload.txt", SearchOption.AllDirectories);
            Assert.Single(payloads);
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // ③ 过程物全部集中在 其余物 里，一个都不许漏在外面
            string processDirectory = Path.Combine(outputDirectory, "其余物");
            Assert.True(Directory.Exists(processDirectory), $"其余物目录不存在：{processDirectory}");
            Assert.True(File.Exists(Path.Combine(processDirectory, "inner.7z.001")));

            // 除 其余物 之外的顶层条目只能有内容物（这里就是 payload.txt / big.bin）
            string[] topLevel = Directory.GetFileSystemEntries(outputDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.Equals(name, "其余物", StringComparison.Ordinal))
                .Select(name => name ?? string.Empty)
                .ToArray();

            Assert.All(
                topLevel,
                name => Assert.False(
                    OneClickCoordinator.IsArchiveStartPoint(name) || name.EndsWith(".002", StringComparison.OrdinalIgnoreCase),
                    $"过程物漏到内容物一层了：{name}"));

            /*
             * ④ 源包（含它的过程物）都进了那**一个**目录的 其余物 里 —— 用户 2026-09-22 的规则：
             * 成功 + 校验通过就把源包放进其余物；本样本第一层没有内容物，所以发生在链结束之后。
             */
            Assert.False(File.Exists(outer), "源包应该已经被搬进其余物");
            Assert.True(File.Exists(Path.Combine(processDirectory, "outer.7z")));
        }

        // ---------------------------------------------------------------- 第 33 条：伪装过的下一层

        /// <summary>
        /// <b>2026-09-25 真机现场（这一条就是为它写的）</b>：第一层解出来的下一层，
        /// 名字被打包方**塞了字**（真机上是 <c>222.ra删除r</c> —— 把「删除」插进 <c>.rar</c> 中间）。
        ///
        /// <para>修复前：续解扫描的第一道门是后缀白名单（<c>IsArchiveStartPoint</c> →
        /// <c>IsKnownArchiveExtension(".ra删除r")</c> = false），于是**在魔数体检之前**就把这个包扔了 ——
        /// 日志只写"第 1 层没有发现可继续解压的内层包"，用户看到的就是"只解了一层"。
        /// 这一条钉的是"只认内容、后缀不参与判决"：样本里那个内层包叫 <c>inner.7删除z</c>
        /// （同一种伪装手法），它必须被认出来、自动改名成 <c>.7z</c>、解到第二层。</para>
        ///
        /// <para>⛔ 撤掉修复（把后缀白名单那道门加回去）这一条立刻变红。</para>
        /// </summary>
        [Fact]
        public async Task 续解_下一层的名字被塞了字_按内容认出并解到第二层()
        {
            const string mangledName = "inner.7删除z";

            string innerStage = Path.Combine(_root, "mangled-inner");
            Directory.CreateDirectory(innerStage);
            WriteText(Path.Combine(innerStage, "payload.txt"), InnerPayloadText);
            Run7z(innerStage, "a", "-t7z", mangledName, "-p" + InnerPassword, "-mhe=on", "payload.txt");

            // 外层里装的正是那个名字被改坏的内层包。
            string outerStage = Path.Combine(_root, "mangled-outer");
            Directory.CreateDirectory(outerStage);
            File.Copy(Path.Combine(innerStage, mangledName), Path.Combine(outerStage, mangledName), overwrite: true);

            string outer = BuildPackage("mangled.7z", outerStage, mangledName);

            // 密码本用**列表式**：内层包的名字被改过，"按名字命中"的映射式本来就不该指望它。
            Harness harness = CreateHarness($"{OuterPassword}\n{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);

            // ① 扫描日志必须自证：看了几个、认出几个、认的是谁、按什么判据
            //    （用户 2026-09-25 的要求："出问题我看日志就能知道"，不用再去翻目录）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("产物扫描", StringComparison.Ordinal) &&
                        line.Contains("按内容认出归档 1 个", StringComparison.Ordinal));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(mangledName, StringComparison.Ordinal) &&
                        line.Contains("文件头魔数", StringComparison.Ordinal));

            // ② 最深处的内容物真的出来了。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.Single(payloads);
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // ③ 而且那个包被**自动改名**成了正常后缀（第 32 条那条"一键档自动改名"照旧生效）。
            Assert.Single(Directory.GetFiles(harness.OutputRoot, "inner.7z", SearchOption.AllDirectories));
        }

        /// <summary>
        /// <b>"伪装两层"</b>（用户 2026-09-25 原话："我明显说过，用户可能会伪装两层的"）：
        /// 第一层的产物**名字**是假的（<c>user.jp删除g</c>）**而且**文件头也是假的
        /// （前 128 KB 是假 MP4 头，完整 ZIP 挂在尾部）—— 两层面具都得看穿。
        ///
        /// <para>修复前：光是名字那一层就被后缀白名单挡掉了（<c>.jp删除g</c> 不是已知归档后缀）；
        /// 而只把后缀改对也还不够 —— 文件头是 <c>ftyp</c>，只看魔数照样认不出。
        /// 现在两条判据一起上：文件头认不出 → 按"双面文件"读尾部
        /// （与识别阶段同一个 <c>EmbeddedArchiveDetector</c>，不另造一套）。</para>
        /// </summary>
        [Fact]
        public async Task 续解_下一层名字和文件头都是假的_两层面具都要看穿()
        {
            // 现成的真实现场样本：假 MP4 头 + 尾部完整 ZIP（ZIP 里是加密的 7z 分卷）。
            string polyglot = BuildPolyglotUserFile();
            const string disguisedName = "user.jp删除g";

            string outerStage = Path.Combine(_root, "polyglot-outer");
            Directory.CreateDirectory(outerStage);
            File.Copy(polyglot, Path.Combine(outerStage, disguisedName), overwrite: true);

            string outer = BuildPackage("polyglot.7z", outerStage, disguisedName);

            Harness harness = CreateHarness($"{OuterPassword}\n{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // ① 第一层之后**必须继续**：名字与文件头都没能拦住它，而且日志写明是按尾部认出来的。
            Assert.True(
                outcome.ContinuationLayers >= 1,
                $"第一层之后没有续解（轮数 {outcome.Rounds}）—— 名字或文件头把下一层挡掉了");
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains(disguisedName, StringComparison.Ordinal) &&
                        line.Contains("尾部内嵌归档", StringComparison.Ordinal));

            // ② 最深处的内容物出来了（尾部 ZIP 里的加密分卷被解开）。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(
                payloads.Length > 0,
                $"没找到 payload.txt。实际目录树：{string.Join(" | ", Directory.GetFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories))}"
                + $"\n轮数 {outcome.Rounds} / 续解层 {outcome.ContinuationLayers}"
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            Assert.Contains(payloads, path => File.ReadAllText(path) == InnerPayloadText);
        }

        // ---------------------------------------------------------------- 第 35 条：落点与内层包归属

        /// <summary>
        /// <b>2026-09-25 第 35 条真机现场（共用输出根那一档）</b>：选整个文件夹 → 解到指定位置，
        /// 4 个包共用同一个输出根（<c>BBB\111</c>）。
        ///
        /// <para>修复前：续解产物按**父任务的 OutputPath**（＝共用根）落，于是内容物与包名目录**平级** ——
        /// 用户看到 <c>BBB\111\222</c> 和 <c>BBB\111\2222</c> 并排，原话是
        /// "你应该是要将 `BBB\111\222` 文件夹放在 `BBB\111\2222` 这个里面"。
        /// 现在续解落点取"父任务**内容物实际所在的那一层**"，产物落进包名目录里。</para>
        /// </summary>
        [Fact]
        public async Task 共用输出根时_续解产物落进包名目录里而不是与它平级()
        {
            /*
             * 样本必须与真机**同形**（这一点很关键，第一版样本造错了就测不出这个缺陷）：
             * 外层包 → 里面一个**同名文件夹** `2222\` → 文件夹里是**名字被塞了字的下一层包**
             * （真机是 `222.ra删除r`）。名字被改坏 → 定稿按"内容物"收下那一层文件夹
             * （认得出是归档的话会整层进其余物，那是另一种形状），内容物层因此是 `…\111\2222`。
             */
            const string mangledName = "222.ra删除r";
            const string mangledName2 = "333.ra删除r";

            string innerStage = Path.Combine(_root, "wrap-inner");
            Directory.CreateDirectory(innerStage);
            WriteText(Path.Combine(innerStage, "payload.txt"), InnerPayloadText);
            Run7z(innerStage, "a", "-t7z", mangledName, "-p" + InnerPassword, "-mhe=on", "payload.txt");
            Run7z(innerStage, "a", "-t7z", mangledName2, "-p" + InnerPassword, "-mhe=on", "payload.txt");

            string outerStage = Path.Combine(_root, "wrap-outer");
            Directory.CreateDirectory(Path.Combine(outerStage, "2222"));
            Directory.CreateDirectory(Path.Combine(outerStage, "3333"));
            File.Copy(
                Path.Combine(innerStage, mangledName),
                Path.Combine(outerStage, "2222", mangledName),
                overwrite: true);
            File.Copy(
                Path.Combine(innerStage, mangledName2),
                Path.Combine(outerStage, "3333", mangledName2),
                overwrite: true);

            string outer = BuildPackage("2222.7z", outerStage, "2222");
            string outer2 = BuildPackage("3333.7z", outerStage, "3333");

            /*
             * ⚠ 必须走"**添加文件夹** + 指定位置"那一档，而且文件夹里**不止一个包**
             * （两个以上才会真的走"共用输出根"那条路）：这时父任务的 OutputPath 是共用根
             * （`…\111`），而内容物实际落在包名那一层（`…\111\2222`）—— 两者分家才是真机的现场
             * （`BBB\111\222` 与 `BBB\111\2222` 平级）。
             */
            string sourceFolder = Path.Combine(_root, "AAA", "111");
            Directory.CreateDirectory(sourceFolder);
            File.Copy(outer, Path.Combine(sourceFolder, Path.GetFileName(outer)), overwrite: true);
            File.Copy(outer2, Path.Combine(sourceFolder, Path.GetFileName(outer2)), overwrite: true);

            Harness harness = CreateHarness($"{OuterPassword}\n{InnerPassword}\n");
            await harness.AddPathsAsync(sourceFolder);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            string sharedRoot = Path.Combine(harness.OutputRoot, "111");

            // 每个包的内容物都必须落在**它自己的包名目录**里。
            foreach (string packageName in new[] { "2222", "3333" })
            {
                Assert.True(
                    File.Exists(Path.Combine(sharedRoot, packageName, "payload.txt")),
                    $"{packageName} 的内容物没落进包名目录。实际目录树："
                    + string.Join(" | ", Directory.GetFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories))
                    + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            }

            // 反向断言：内容物**不许**直接躺在共用根上（那就是"与包名目录平级"的旧缺陷）。
            Assert.False(
                File.Exists(Path.Combine(sharedRoot, "payload.bin")),
                "内容物被放到了共用根上（与包名目录平级）—— 续解落点又退回 OutputPath 了");
        }

        /// <summary>
        /// **外层包里只有内层包（没有内容文件夹）时，内容物必须落进"外层包名目录"**（用户 2026-09-25 第 43 条，
        /// 真机：68 套图全平铺进共用根、4339 个文件互相改名）。
        ///
        /// <para>真机形状：`添加文件夹 + 指定位置` 导入一整批包 → 落点是**共用输出根**
        /// （最后一段是导入文件夹名）；每个外层包 `NNN.7z` 的清单里**只有内层分卷**
        /// （`00NN_auto.7z.001/.002/.003`）、没有内容文件夹 → 外层这一轮什么都不定稿，
        /// 链根那一层包名目录从来没建出来 → 续解产物落到共用根上（再被"只留一层内容"塌一层）
        /// → 所有包的内容混在同一层。</para>
        ///
        /// <para>期望（用户 2026-09-24 拍板的语义 `222\1111\内容物`）：每个包**自己一层包名目录** ——
        /// `<共用根>\<包名>\内容物`。</para>
        /// </summary>
        [Fact]
        public async Task 外层包只有内层包时_内容物落进外层包名目录而不是平铺进共用根()
        {
            // ① 内层包：真的内容（payload.bin）打成 level.7z，再切成两卷以上 —— 与真机"内层是分卷"同形。
            string innerStage = Path.Combine(_root, "flat-inner");
            Directory.CreateDirectory(innerStage);

            var payloadBytes = new byte[128 * 1024];
            new Random(20260925).NextBytes(payloadBytes);
            File.WriteAllBytes(Path.Combine(innerStage, "payload.bin"), payloadBytes);

            Run7z(innerStage, "a", "-t7z", "-mx0", "-v16k", "level.7z", "-p" + InnerPassword, "-mhe=on", "payload.bin");

            string[] innerVolumes = Directory.GetFiles(innerStage, "level.7z.*")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.True(innerVolumes.Length >= 2, "内层包应该是分卷（两卷以上）");

            // ② 两个外层包：**只有内层分卷、没有内容文件夹**（这就是缺陷的触发条件）。
            string outerStage = Path.Combine(_root, "flat-outer");
            Directory.CreateDirectory(outerStage);

            foreach (string volume in innerVolumes)
            {
                File.Copy(volume, Path.Combine(outerStage, Path.GetFileName(volume)), overwrite: true);
            }

            string outer1 = BuildPackage("055.7z", outerStage, innerVolumes.Select(Path.GetFileName).Cast<object>().ToArray());
            string outer2 = BuildPackage("056.7z", outerStage, innerVolumes.Select(Path.GetFileName).Cast<object>().ToArray());

            string sourceFolder = Path.Combine(_root, "AAA", "111");
            Directory.CreateDirectory(sourceFolder);
            File.Copy(outer1, Path.Combine(sourceFolder, Path.GetFileName(outer1)), overwrite: true);
            File.Copy(outer2, Path.Combine(sourceFolder, Path.GetFileName(outer2)), overwrite: true);

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(sourceFolder);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            string sharedRoot = Path.Combine(harness.OutputRoot, "111");

            // ③ 每个包的内容物都在**它自己的包名目录**里。
            foreach (string packageName in new[] { "055", "056" })
            {
                string expected = Path.Combine(sharedRoot, packageName, "payload.bin");

                Assert.True(
                    File.Exists(expected),
                    $"{packageName} 的内容物没落进包名目录（期望 {expected}）。实际目录树："
                    + string.Join(" | ", Directory.GetFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories))
                    + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            }

            // ④ 反向断言：共用根上**不许**直接躺着内容物（那就是真机"68 套混一层"的现场）。
            Assert.False(
                File.Exists(Path.Combine(sharedRoot, "payload.bin")),
                "内容物被平铺到了共用根上 —— 链根包名那一层又没建出来");

            // ⑤ 两个包的内容物各在一层，不互相撞名。
            Assert.NotEqual(
                Path.GetDirectoryName(Path.Combine(sharedRoot, "055", "payload.bin")),
                Path.GetDirectoryName(Path.Combine(sharedRoot, "056", "payload.bin")));
        }

        /// <summary>
        /// <b>被续解链真正解过的内层包必须进「其余物」</b>（第 35 条）：
        /// 修复前第一层把内层包当**内容物**放在成品目录里，`RestDirectoryPath` 一直是空的 ——
        /// 于是「彻底删除」在链尾**没有对象可删**，内层包原样留着（真机：<c>CCC\2222\222.rar</c>）。
        /// </summary>
        [Fact]
        public async Task 内层包在链尾被收进其余物_保留档留在其余物里()
        {
            const string mangledName = "inner.7删除z";
            string outer = BuildMangledInnerPackage(mangledName);

            Harness harness = CreateHarness($"{OuterPassword}\n{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            // 内容物照常在。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.Single(payloads);
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 内层包（已自动改名成 .7z）不在成品目录里裸放着了，而是进了「其余物」。
            Assert.DoesNotContain(
                Directory.GetFiles(harness.OutputRoot, "inner.7z", SearchOption.AllDirectories),
                path => !path.Contains("其余物", StringComparison.Ordinal));

            if (harness.Vm.Settings.RestHandlingAfterVerify == RestHandlingModes.Keep)
            {
                Assert.Single(
                    Directory.GetFiles(harness.OutputRoot, "inner.7z", SearchOption.AllDirectories),
                    path => path.Contains("其余物", StringComparison.Ordinal));
            }

            // 日志里那一条：第 45 条起是"内层包已移入其余物 —— <文件名>（<包名> 那一层）"
            // （原来写的是两条完整绝对路径，真机上 38 个包 × 200 字符纯属噪声）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("内层包已移入其余物", StringComparison.Ordinal)
                        && line.Contains("inner.7z", StringComparison.Ordinal));
        }

        /// <summary>
        /// 同一形状 + 「删除操作 = 彻底删除」：链尾收进其余物之后**连内层包一起被删掉**
        /// （源包留在原地 —— 这份样本的源包档就是默认的「原来的位置不动」）。
        /// </summary>
        [Fact]
        public async Task 彻底删除档_链尾把内层包连其余物一起删掉()
        {
            const string mangledName = "inner.7删除z";
            string outer = BuildMangledInnerPackage(mangledName);

            Harness harness = CreateHarness(
                $"{OuterPassword}\n{InnerPassword}\n",
                settings =>
                {
                    settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                    settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
                });

            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            // 内容物还在（成功路径一个字节都没动它）。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.Single(payloads);

            // 内层包与其余物目录都不见了（源包按「源包操作」搬进了其余物 → 也一起被删）。
            Assert.Empty(Directory.GetFiles(harness.OutputRoot, "inner.7z", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(harness.OutputRoot, "其余物", SearchOption.AllDirectories));
            Assert.False(File.Exists(outer), "源包操作=放入其余物 + 删除操作=彻底删除 → 源包也该没了");
        }

        /// <summary>造"外层包 → 名字被塞字的内层包 → 内容物"这条链（第 33/35 条共用样本）。</summary>
        private string BuildMangledInnerPackage(string mangledName)
        {
            string innerStage = Path.Combine(_root, "mangle-inner-" + mangledName);
            Directory.CreateDirectory(innerStage);
            WriteText(Path.Combine(innerStage, "payload.txt"), InnerPayloadText);
            Run7z(innerStage, "a", "-t7z", mangledName, "-p" + InnerPassword, "-mhe=on", "payload.txt");

            string outerStage = Path.Combine(_root, "mangle-outer-" + mangledName);
            Directory.CreateDirectory(outerStage);
            File.Copy(Path.Combine(innerStage, mangledName), Path.Combine(outerStage, mangledName), overwrite: true);

            return BuildPackage("mangle-" + Guid.NewGuid().ToString("N") + ".7z", outerStage, mangledName);
        }

        // ---------------------------------------------------------------- 第四步：没有内层包 = 回归

        [Fact]
        public async Task 第一层就是最后一层时_只跑一轮()
        {
            string stage = Path.Combine(_root, "plain-stage");
            WriteText(Path.Combine(stage, "readme.txt"), "就一层\n");

            string outer = BuildPackage("plain.7z", stage, "readme.txt");

            Harness harness = CreateHarness($"plain:{OuterPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.False(outcome.HitRoundLimit);
            Assert.DoesNotContain("自动续解", outcome.Summary);

            Assert.Single(harness.Vm.Tasks);
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "plain", "readme.txt")));

            /*
             * "一个内层包都没找到"**不许静默**：以前这里一声不吭地 break，
             * 用户分不清"本来就没有内层包"和"续解坏了 / 内层包落在了没被看到的目录里"。
             */
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("没有发现可继续解压的内层包", StringComparison.Ordinal));

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(1, scope);
            Assert.Equal(scope, sum);
        }

        // ---------------------------------------------------------------- 第四步之二：输出目录被自动改名

        [Fact]
        public async Task 输出目录已存在被改名成括号1时_第二层仍然会被续解()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");

            /*
             * 预置一个"已存在且非空"的目录，占住原定的输出目录名 <out>\outer。
             * 解压管线遇到这种目录会为避免混入旧文件而自动改名成 <out>\outer(1)，
             * 并把**实际落点**回写进 task.OutputPath（不是"打算输出到哪"）。
             *
             * 旧实现的续解只认"解压前快照过的目录集合"，而快照里只有原定的 <out>\outer，
             * 于是内层包所在的 outer(1) 被整段跳过 → 第二层静默不跑，
             * 汇总却照样打印"一键处理完成：成功 N"（用户看到的就是"只有第一层能解出来"）。
             */
            string plannedOutput = Path.Combine(harness.OutputRoot, "outer");
            Directory.CreateDirectory(plannedOutput);
            string staleFile = Path.Combine(plannedOutput, "用户原有文件.txt");
            WriteText(staleFile, "这一份不能被覆盖，也不该被当成产物\n");

            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 前提：解压真的落到改名后的目录（否则这条测试什么都没验证到）
            Assert.Equal(Path.Combine(harness.OutputRoot, "outer(1)"), outerTask.OutputPath);
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("自动改用新目录", StringComparison.Ordinal));

            // 核心判据：第二层照样续解
            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"第 2 层应该产出一份 payload.txt，实际 {payloads.Length} 份（目录：{harness.OutputRoot}）");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 内层包确实是落在"实际落点"里的那一批（其余物 是它的集中处，契约 §3.2）
            Assert.True(
                File.Exists(Path.Combine(outerTask.OutputPath, "其余物", "inner.7z.001")),
                $"内层分卷应该落在实际输出目录的其余物里：{outerTask.OutputPath}");

            // 用户原来放在同名目录里的文件一个字节都不许动（不变量 1）
            Assert.True(File.Exists(staleFile));
            Assert.Equal("这一份不能被覆盖，也不该被当成产物\n", File.ReadAllText(staleFile));

            // 汇总仍然自洽
            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);
        }

        // ---------------------------------------------------------------- 第四步之三：达到密码尝试上限

        [Fact]
        public async Task 达到密码尝试上限的任务_算失败而不是未处理或已停止()
        {
            string stage = Path.Combine(_root, "locked-stage");
            WriteText(Path.Combine(stage, "secret.txt"), "密码不在密码本里\n");
            string locked = BuildPackage("locked.7z", stage, "secret.txt");

            // 40 条候选（都不是真密码）：超过单层上限（ExtractionCoordinator 里是 10），
            // 于是任务终态是"达到密码尝试上限"，而不是"密码错误"。
            var book = new StringBuilder();

            for (int i = 1; i <= 40; i++)
            {
                book.AppendLine("候选密码" + i);
            }

            Harness harness = CreateHarness(book.ToString());
            await harness.AddPathsAsync(locked);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);

            /*
             * 先看**用户能看到的症状**（汇总），再看底层判定。
             * 顺序是有意的：这条测试要防的正是"跑完了却被说成被停止 / 未处理"，
             * 先断言症状，失败信息里就是那句错话，一眼能看懂坏在哪。
             */
            Assert.False(outcome.Stopped, outcome.Summary);
            Assert.StartsWith("一键处理完成", outcome.Summary, StringComparison.Ordinal);
            Assert.Contains("失败 1", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", outcome.Summary);
            Assert.DoesNotContain("已停止", outcome.Summary);

            /*
             * 核对移交清单里的第 2 条：这个新状态必须被 IsFailureStatus / IsHandled 认账。
             * 漏掉它的后果不是编译失败，而是上面那句汇总直接说反。
             */
            Assert.True(OneClickCoordinator.IsFailureStatus(task));
            Assert.True(OneClickCoordinator.IsHandled(task));

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(1, scope);
            Assert.Equal(scope, sum);
        }

        // ---------------------------------------------------------------- 第五步：停止后续

        [Fact]
        public async Task 点了停止后续_不再续解_且汇总不显示成成功()
        {
            BuildInnerVolumeGroup();
            string first = BuildPackageFromInnerStage("pack-a.7z");
            string second = BuildPackageFromInnerStage("pack-b.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(first, second);

            Assert.Equal(2, harness.Vm.Tasks.Count);

            // 模拟用户在第 1 个包解压期间点了「停止后续」
            harness.Engine.OnExtract = _ => harness.Vm.IsStopping = true;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.True(outcome.Stopped, "按过停止后续就必须记成已停止");
            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.StartsWith("一键处理已停止", outcome.Summary, StringComparison.Ordinal);
            Assert.Contains("停止后续", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("自动续解", outcome.Summary);

            // 内层包一个都没加进任务列表 —— 停止之后不许开新活
            Assert.Equal(2, harness.Vm.Tasks.Count);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);
        }

        // ---------------------------------------------------------------- 第五步之二：收尾守卫不许误判"没轮到"

        /// <summary>
        /// <b>第 1 轮只有 1 个包，解压途中产物里冒出一个内层包 → 必须接着解第 2 层</b>
        /// （2026-09-28 真机："只解一层就停，汇总还写『已按「停止后续」中断』"，而用户根本没按过）。
        ///
        /// <para>收尾守卫（<c>roundStartTargets.Any(t =&gt; !IsHandled(t))</c>）判的是"**这一轮开跑时**
        /// 的目标里还有没有没轮到的"。本轮解压途中通过 <c>AddInnerTasksAsync</c> 加进来的内层包是
        /// <b>下一轮</b>才该解的，绝不能被读成"有任务没轮到" —— 一旦读错，一次正常的续解会当场
        /// 变成"用户叫停"：只解一层就收工，汇总里还写着用户按了「停止后续」。</para>
        ///
        /// <para>这条用例钉的是**用户看得见的那三件事**：续解层数 ≥ 1、内层包真的被引擎解过
        /// （第 2 层的内容物出来了）、汇总里既没有"已按「停止后续」中断"也没有"没轮到"。</para>
        /// </summary>
        [Fact]
        public async Task 单包一轮_产物里冒出内层包_必须续解第二层且汇总不谎报用户叫停()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            ArchiveTask only = Assert.Single(harness.Vm.Tasks);

            Assert.True(only.IsSelected, "唯一那个包必须是勾选的，否则这条用例什么都没跑");

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // ① 续解真的发生了：第 1 轮目标只有 1 个包，第 2 层的包是解压途中才出现的。
            Assert.True(
                outcome.ContinuationLayers >= 1,
                $"第 1 轮只有 1 个包、产物里有内层包时必须继续解第 2 层。"
                + $"实际轮数 {outcome.Rounds} / 续解层 {outcome.ContinuationLayers}"
                + $"\n汇总：{outcome.Summary}"
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            Assert.Equal(2, outcome.Rounds);

            // ② 没有被读成"用户叫停"（守卫误判的信号就是这两条）。
            Assert.False(
                outcome.Stopped,
                "正常的第 2 层续解被守卫读成了『有任务没轮到』：" + outcome.Summary);
            Assert.DoesNotContain("已按「停止后续」中断", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("没轮到", outcome.Summary, StringComparison.Ordinal);

            // ③ 内层包真的被解了：引擎为它被调用过，而且第 2 层的内容物确实出来了。
            Assert.Contains(
                harness.Engine.ExtractCalls,
                path => path.EndsWith("inner.7z.001", StringComparison.OrdinalIgnoreCase));

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(
                payloads.Length == 1,
                $"第 2 层应该产出一份 payload.txt，实际 {payloads.Length} 份（目录：{harness.OutputRoot}）");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // ④ 汇总正面写着续解——不是"停在一层"。
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);
        }

        /// <summary>
        /// <b>开工前被移出列表的任务，不许被读成"这一轮有任务没轮到"</b>——这是"只解一层就停"的成因。
        ///
        /// <para>真机形状（2026-09-28）：用户选的文件夹里既有真包、也有打包者附带的说明；
        /// 一键处理开工前那一步会先把无用物从**任务列表**里移掉
        /// （<c>ScanCoordinator.RemoveJunkTasksFromListAsync</c>，只动列表、不动磁盘），
        /// 而"这一批要处理谁"的清单是在那之前拍的 —— 拿它去比，刚被移出列表的无用物就成了
        /// "有任务没轮到"：第一批只解一层就收工，汇总还写"已按「停止后续」中断"。</para>
        ///
        /// <para>⚠ 这里**故意按真实顺序**复刻那条链路（先拍清单 → 把无用物移出列表 → 再跑管线）：
        /// 只有"清单里有一个已经不在列表里的任务"这种状态才测得到这条判据。
        /// 撤掉 <c>RunPipelineAsync</c> 里那道"只认还在列表里的目标"的过滤，这一条立刻变红
        /// （红在"只解了一层 + 汇总报成用户叫停"这个点上）。</para>
        /// </summary>
        [Fact]
        public async Task 开工前被移出列表的无用物_不算这一轮没轮到_照常续解第二层()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            // 真包旁边放一个打包者常带的说明（一键处理开工前那一步就是把它从列表里移掉的）。
            string sourceDirectory = Path.GetDirectoryName(outer)!;
            string junk = Path.Combine(sourceDirectory, "说明.txt");
            WriteText(junk, "打包者附带的说明，不是压缩包\n");
            DateTime junkWriteTime = File.GetLastWriteTimeUtc(junk);

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            /*
             * 再往列表里放一行"说明.txt"（用户点一键处理那一刻，列表里就是这两行）。
             *
             * ⚠ 这一行**只能手工放**：2026-09-28 起"导入时的无用物清理"（见 ImportJunkReminderTests）
             * 会把它直接移出列表，所以走一次完整导入已经造不出"列表里还留着无用物"这个状态；
             * 而那个状态在一键处理开工前**真实存在过**（用户先导入、列表之后又变过，
             * 或者无用物扫描撞到上限留下残渣）—— 要钉的判据就是它。
             */
            var junkTask = new ArchiveTask(junk, harness.Vm.Tasks.Count + 1)
            {
                IsSelected = true,
                Status = StatusText.Recognized,
                ExtensionStatus = StatusText.ExtensionNormal
            };

            harness.Vm.Tasks.Add(junkTask);

            // 用户点「一键处理」那一刻，勾选的就是这两行（真包 + 说明）。
            List<ArchiveTask> targets = harness.Vm.Tasks.Where(task => task.IsSelected).ToList();

            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, task => task.FileName.Equals("说明.txt", StringComparison.OrdinalIgnoreCase));

            // 开工前那一步：把无用物从列表里移掉（磁盘一个字节都不动）。
            int removed = await harness.Scan.RemoveJunkTasksFromListAsync(targets);

            Assert.Equal(1, removed);
            Assert.Single(harness.Vm.Tasks);

            OneClickOutcome outcome = await harness.OneClick.RunPipelineAsync(targets);

            // ① 照常续解，没有被读成"用户叫停"（红检时这一条正是"只解了一层"的现场）。
            Assert.True(
                outcome.Rounds == 2 && outcome.ContinuationLayers == 1,
                $"只解了一层就停（轮数 {outcome.Rounds} / 续解层 {outcome.ContinuationLayers}）。"
                + $"\n汇总：{outcome.Summary}"
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");
            Assert.False(
                outcome.Stopped,
                "已经不在列表里的无用物被守卫读成了『有任务没轮到』：" + outcome.Summary);
            Assert.DoesNotContain("已按「停止后续」中断", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("没轮到", outcome.Summary, StringComparison.Ordinal);

            // ② 移出列表的那一个**不许**再算进"本次处理"（否则汇总里会凭空多一个"未处理 1"）。
            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);

            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);

            // ③ 第 2 层的内容物真的出来了。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);

            Assert.Single(payloads);
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // ④ 红线：只动列表，磁盘上的那个说明文件还在原位、内容与写入时间都没变。
            Assert.True(File.Exists(junk), "移出列表不许删磁盘上的文件");
            Assert.Equal("打包者附带的说明，不是压缩包\n", File.ReadAllText(junk));
            Assert.Equal(junkWriteTime, File.GetLastWriteTimeUtc(junk));
        }

        // ---------------------------------------------------------------- 第六步：用户的真实现场

        [Fact]
        public async Task 双面文件真实现场_抠出尾部ZIP后继续解内层加密分卷()
        {
            string userFile = BuildPolyglotUserFile();
            long sourceLength = new FileInfo(userFile).Length;

            Harness harness = CreateHarness($"inner:{InnerPassword}\n");
            await harness.AddPathsAsync(userFile);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0, "应该识别出尾部有内嵌归档");
            Assert.False(OneClickCoordinator.IsArchiveStartPoint(userFile), "用户给的 mp4 不是归档起点，不该被自己当成内层包");

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"应该只有第 2 层产出一份 payload.txt，实际 {payloads.Length} 份");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            /*
             * 源文件（双面文件）**内容一个字节都没变**，但按用户 2026-09-22 的规则被搬进了其余物：
             * 它第一层只出内层分卷，所以搬运发生在整条续解链结束之后。
             */
            string rest = Path.Combine(harness.OutputRoot, "user", "其余物");
            string movedUserFile = Path.Combine(rest, "user.mp4");

            Assert.False(File.Exists(userFile), "源包应该已经被搬进其余物");
            Assert.True(File.Exists(movedUserFile), $"源包没有落到 {movedUserFile}");

            // "搬"不是"重写"：字节数与内容必须与原件逐位一致。
            Assert.Equal(sourceLength, new FileInfo(movedUserFile).Length);
            Assert.True(File.Exists(Path.Combine(rest, "inner.7z.001")), "内层分卷也应该在同一个其余物里");
        }

        // ---------------------------------------------------------------- 落点模型 v2：手动档「解压到当前文件夹」

        /// <summary>
        /// **手动档「解压到当前文件夹」的真 7z 端到端**（用户 2026-09-27 新增的那颗按钮）：
        /// 内容物**不建包名那一层**、直接落在源包所在的那一层，而**归档自带的那层文件夹照旧保留**
        /// （"按包内原样解开"，不是把归档结构也拆掉）。
        ///
        /// <para>形状：<c>&lt;root&gt;\packages\flat.7z</c> 里是 <c>内容物\payload.bin</c>。</para>
        /// </summary>
        [Fact]
        public async Task 手动档解压到当前文件夹_真7z端到端_内容物落源包那一层()
        {
            string build = Path.Combine(_root, "flat-build");
            Directory.CreateDirectory(Path.Combine(build, "内容物"));

            WriteText(Path.Combine(build, "内容物", "payload.bin"), InnerPayloadText);

            string package = BuildPackage("flat.7z", build, @"内容物\payload.bin");
            string sourceDirectory = Path.GetDirectoryName(package)!;

            Harness harness = CreateHarness($"{OuterPassword}\n");

            await harness.AddPathsAsync(package);

            await harness.Extraction
                .StartExtractAsync(extractIntoSourceFolder: true)
                .WaitAsync(TimeSpan.FromSeconds(120));

            // ① 内容物落在源包那一层里（`packages\内容物\payload.bin`），**没有** `flat\` 这一层。
            Assert.True(
                File.Exists(Path.Combine(sourceDirectory, "内容物", "payload.bin")),
                "手动摊平之后内容物应该落在源包所在目录里。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories))
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");

            Assert.False(
                Directory.Exists(Path.Combine(sourceDirectory, "flat")),
                "手动摊平不该建包名那一层");

            // ② 日志里留了那句提醒（事后能回答"这批为什么没有包名那一层"）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("本次按「解压到当前文件夹」执行", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- 落点模型 v2：续解层的两种档位

        /// <summary>
        /// **续解层"首尾必留、中间看开关"的真 7z 端到端**（用户 2026-09-27 拍板）：
        /// 同一条**单链**样本，开关关着 = 忠实档（每个内层包各占一层），打开 = 简洁档
        /// （中间那些"只出过程物"的过路层省掉，第一层与最后一层永远保留）。
        ///
        /// <para>形状：<c>outer.7z → level2.7z → level3.7z → level4.7z → 内容物\payload.bin</c>，
        /// 每一层里**只有**下一个包（干净单链），所以每一层都会走"该不该建那一层"的判据。
        /// 最深一层放的是**一个文件夹**（不是单个文件）：判定表 1 对"终端只有单个文件"本来就不套层，
        /// 拿单个文件当样本会把"末层那一层"这件事测糊。</para>
        ///
        /// <para>红在哪：把 <c>ShouldAddContinuationLevelLayer</c> 里"简洁档"那一支改成恒 <c>true</c>
        /// → 简洁档那两段立刻红；判据若退回用 <c>ContentDirectoryPath</c> 非空（而不是真的内容物文件数）
        /// → 也红（这正是端到端测试当场逮到的一次）。</para>
        /// </summary>
        [Fact]
        public async Task 续解中间层_忠实档每层各占一层_简洁档只留首尾()
        {
            // ── 忠实档（默认，开关关）：每层各占一层 ─────────────────────────────
            Harness faithful = CreateHarness($"{ChainPassword}\n");

            await faithful.AddPathsAsync(BuildFolderChain(4));

            OneClickOutcome faithfulOutcome = await faithful.RunOneClickAsync();

            Assert.True(faithfulOutcome.Rounds >= 2, "这条链至少要解两轮才能看出层数差别");

            string faithfulPayload = Assert.Single(
                Directory.GetFiles(faithful.OutputRoot, "payload.bin", SearchOption.AllDirectories));

            Assert.Equal(
                Path.Combine(faithful.OutputRoot, "outer", "level2", "level3", "level4", "内容物", "payload.bin"),
                faithfulPayload);

            // 忠实档：每一层自己的其余物在**它自己那一层**里（里面的包分得清是谁的）。
            Assert.True(
                File.Exists(Path.Combine(faithful.OutputRoot, "outer", "level2", "其余物", "level3.7z")),
                "忠实档下 level3.7z 应该躺在 level2 那一层的其余物里");

            // ── 简洁档（开关开）：中间层省掉，只留第一层与最后一层 ────────────────
            /*
             * ⚠ 换一个输出根：两套 harness 默认共用 `<root>\out`，第一段已经在那里留下了一份产物
             * （`Assert.Single` 会数到两份，测的就不是这一档了）。
             */
            string compactOutputRoot = Path.Combine(_root, "out-compact");

            Harness compact = CreateHarness(
                $"{ChainPassword}\n",
                settings =>
                {
                    settings.OmitMiddleContinuationLayers = true;
                    settings.CustomOutputDirectory = compactOutputRoot;
                });

            await compact.AddPathsAsync(BuildFolderChain(4));

            // 前提必须成立：开关确实读进了设置（否则下面测的就不是"简洁档"而是默认档）。
            Assert.True(
                compact.Vm.Settings.OmitMiddleContinuationLayers,
                "「续解时省略中间层」没有生效到设置上 —— 后面的层数断言没有意义");

            await compact.RunOneClickAsync();

            string compactPayload = Assert.Single(
                Directory.GetFiles(compactOutputRoot, "payload.bin", SearchOption.AllDirectories));

            /*
             * 简洁档：`…\outer\内容物\payload.bin` ——
             * `level2`/`level3`/`level4` 三层都是"只出过程物"的过路层（省掉），
             * `outer` 是第一层（永远保留）、`内容物` 是最后一层（装着内容物的那个包自己那一层）。
             */
            Assert.True(
                string.Equals(
                    Path.Combine(compactOutputRoot, "outer", "内容物", "payload.bin"),
                    compactPayload,
                    StringComparison.OrdinalIgnoreCase),
                $"简洁档应当只留首尾两层。实际：{compactPayload}"
                + $"\n目录树：{string.Join(" | ", Directory.GetFileSystemEntries(compactOutputRoot, "*", SearchOption.AllDirectories))}"
                + $"\n日志（层相关）：\n{string.Join("\n", compact.LogTexts.Where(line => line.Contains("层", StringComparison.Ordinal)))}");

            // 中间层真的省了：三个内层包 + 源包**全都在 `outer\其余物\` 这一处**（没有各自的层）。
            Assert.True(
                File.Exists(Path.Combine(compactOutputRoot, "outer", "其余物", "level2.7z")),
                "简洁档下内层包应该集中在外层那一层的其余物里");
            Assert.True(
                File.Exists(Path.Combine(compactOutputRoot, "outer", "其余物", "level4.7z")),
                "简洁档下最后一层的包也应该在外层那一层的其余物里");

            Assert.False(
                Directory.Exists(Path.Combine(compactOutputRoot, "outer", "level2")),
                "简洁档不该给过路层建目录");
        }

        /// <summary>
        /// 简洁档下**末层那一层用的是包基名**（用户 2026-09-27 写的就是这个形状：`111\222\666\内容物`）：
        /// 最深一层的内容物**直接摊在归档根上**（没有自带文件夹）时，最后一层由"包基名"担任；
        /// 中间那些只出过程物的过路层仍然省掉。
        ///
        /// <para>形状：<c>outer.7z → level2.7z → level3.7z → flat1.bin + flat2.bin</c>
        /// （最深一层是**两个散文件**，不是文件夹 —— 单个文件会走判定表 1，根本不套层，测不出这件事）。</para>
        /// </summary>
        [Fact]
        public async Task 简洁档_末层内容物摊在归档根上时_最后一层用包基名()
        {
            string build = Path.Combine(_root, "flat-chain-build");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "flat1.bin"), InnerPayloadText);
            WriteText(Path.Combine(build, "flat2.bin"), InnerPayloadText);

            Run7z(build, "a", "-t7z", "level3.7z", "-p" + ChainPassword, "-mhe=on", "flat1.bin", "flat2.bin");
            Run7z(build, "a", "-t7z", "level2.7z", "-p" + ChainPassword, "-mhe=on", "level3.7z");

            string outer = BuildPackage("outer.7z", build, "level2.7z");

            // ── 忠实档：每层各占一层，末层就是它自己那一层 ─────────────────────
            Harness faithful = CreateHarness($"{OuterPassword}\n{ChainPassword}\n");
            await faithful.AddPathsAsync(outer);
            await faithful.RunOneClickAsync();

            Assert.True(
                File.Exists(Path.Combine(faithful.OutputRoot, "outer", "level2", "level3", "flat1.bin")),
                "忠实档应当是 outer\\level2\\level3\\flat1.bin。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(faithful.OutputRoot, "*", SearchOption.AllDirectories)));

            // ── 简洁档：中间层省掉，最后一层用包基名 ───────────────────────────
            string compactOutputRoot = Path.Combine(_root, "out-flat");

            Harness compact = CreateHarness(
                $"{OuterPassword}\n{ChainPassword}\n",
                settings =>
                {
                    settings.OmitMiddleContinuationLayers = true;
                    settings.CustomOutputDirectory = compactOutputRoot;
                });

            await compact.AddPathsAsync(BuildPackage("outer.7z", build, "level2.7z"));
            await compact.RunOneClickAsync();

            Assert.True(
                File.Exists(Path.Combine(compactOutputRoot, "outer", "level3", "flat1.bin")),
                "简洁档应当是 outer\\level3\\flat1.bin（第一层 outer + 最后一层 level3）。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(compactOutputRoot, "*", SearchOption.AllDirectories))
                + $"\n日志：\n{string.Join("\n", compact.LogTexts)}");

            Assert.True(
                File.Exists(Path.Combine(compactOutputRoot, "outer", "level3", "flat2.bin")),
                "两份内容物都要在最后一层里");

            Assert.False(
                Directory.Exists(Path.Combine(compactOutputRoot, "outer", "level2")),
                "简洁档不该给过路层建目录");
        }

        /// <summary>
        /// **父层自己产出了内容物 → 内层包的东西并进父层**（用户 2026-09-27 真机改的口径）：
        /// 现场是 `（T250）经济2\国考资料.txt` 旁边本该就是 `老王宣传\…`，
        /// 而我们给内层包 `Sociology.7z` 又造了一层 `Sociology\`，把真内容埋深了一层 ——
        /// 他原话："最后的结果应该和上一层文件在同一个目录里面的……这个就非常的危险了"。
        ///
        /// <para>形状：<c>outer.7z</c> 里既有内容物 <c>payload.txt</c> 又有内层包 <c>level2.7z</c>
        /// （而 <c>level2.7z</c> 里是 <c>内容物\payload.bin</c>）。</para>
        /// </summary>
        [Fact]
        public async Task 父层已有内容物时_内层包的东西并进父层_不另建目录()
        {
            string build = Path.Combine(_root, "branch-build");
            Directory.CreateDirectory(Path.Combine(build, "内容物"));

            WriteText(Path.Combine(build, "payload.txt"), InnerPayloadText);
            WriteText(Path.Combine(build, "内容物", "payload.bin"), InnerPayloadText);

            // level2.7z：里面只有一个文件夹（单链内容）。
            Run7z(build, "a", "-t7z", "level2.7z", "-p" + ChainPassword, "-mhe=on", @"内容物\payload.bin");

            // outer.7z：**既有内容物又有内层包** —— 这就是"父层已出内容物"的形状。
            string outer = BuildPackage("outer.7z", build, "payload.txt", "level2.7z");

            string compactOutputRoot = Path.Combine(_root, "out-branch");

            Harness harness = CreateHarness(
                $"{OuterPassword}\n{ChainPassword}\n",
                settings =>
                {
                    settings.OmitMiddleContinuationLayers = true;
                    settings.CustomOutputDirectory = compactOutputRoot;
                });

            await harness.AddPathsAsync(outer);
            await harness.RunOneClickAsync();

            // ① 外层自己的内容物照常落进第一层。
            Assert.True(
                File.Exists(Path.Combine(compactOutputRoot, "outer", "payload.txt")),
                "外层内容物没落进第一层。实际目录树："
                + (Directory.Exists(compactOutputRoot)
                    ? string.Join(" | ", Directory.GetFileSystemEntries(compactOutputRoot, "*", SearchOption.AllDirectories))
                    : "（输出根都没建出来）")
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");

            // ② 内层包那一层**不另建**：它的内容并进 `outer\`（与 payload.txt 同一目录）。
            string payload = Assert.Single(
                Directory.GetFiles(compactOutputRoot, "payload.bin", SearchOption.AllDirectories));

            Assert.True(
                string.Equals(
                    Path.Combine(compactOutputRoot, "outer", "内容物", "payload.bin"),
                    payload,
                    StringComparison.OrdinalIgnoreCase),
                $"内层包的东西应该并进父层（outer\\内容物\\payload.bin）。实际：{payload}");

            Assert.False(
                Directory.Exists(Path.Combine(compactOutputRoot, "outer", "level2")),
                "父层已经出了内容物 → 不该再给内层包另建一层目录");

            // ③ 理由写在日志里（⛔ 绝不静默）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("并进这一层", StringComparison.Ordinal));
        }

        /// <summary>
        /// **递归展开多层时，结果校验不许拿第 0 层的清单去核对最终产物**（用户 2026-09-27 真机 CCC：
        /// 13 个包**全部显示「解压失败」**，而内容其实完好；日志里同一段写着"已完成 2 层递归解压…已发布 7 个文件"，
        /// 校验却报"预期 2 个文件 / …，实际 7 个 / …"）。
        ///
        /// <para>形状：<c>outer.7z</c> = <c>level2.7z</c> + <c>decoy.txt</c>（**2 个条目**）；
        /// <c>level2.7z</c> = <c>final.txt</c>（1 个条目）。递归模式 SingleChain 会展开两层：
        /// 第 0 层声明 2 个、最终产物 1 个 —— 旧口径必然判"校验未通过"。</para>
        /// </summary>
        [Fact]
        public async Task 递归展开多层_不再拿第一层清单判校验失败()
        {
            string build = Path.Combine(_root, "recursion-verify-build");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "final.txt"), InnerPayloadText);
            Run7z(build, "a", "-t7z", "level2.7z", "-p" + ChainPassword, "-mhe=on", "final.txt");

            WriteText(Path.Combine(build, "decoy.txt"), "打包者附带的说明\n");
            string outer = BuildPackage("outer.7z", build, "level2.7z", "decoy.txt");

            string outputRoot = Path.Combine(_root, "out-recursion-verify");

            Harness harness = CreateHarness(
                $"{OuterPassword}\n{ChainPassword}\n",
                settings =>
                {
                    // 这一条的关键前提：走**递归核心**（不是一键处理的续解轮）。
                    settings.RecursionMode = "SingleChain";
                    settings.CustomOutputDirectory = outputRoot;

                    // 校验那一段属于"成功任务的细节"，默认档只留一行摘要 —— 这条用例要看细节。
                    settings.VerboseLog = true;
                });

            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            /*
             * ⛔ 落点最少两层（用户 2026-09-30 真机红线）：`outer\` = 最外层（源包包名目录）、
             * `level2\` = **最里层**（递归链里最后一个被展开的内层包）—— 两层都不许省。
             *
             * 这一次展开过内层包（第 0 层 outer.7z → 第 1 层 level2.7z），终端内容物又恰好是
             * **单个文件**：旧口径走判定表 ①"单个文件直接放 destDir"，落成 `outer\final.txt`
             * （只剩一层）。红线把这一支也划进去了（"任何分支都不许省"），
             * 那一层的名字取内层包的包基名 ⇒ `outer\level2\final.txt`。
             */
            Assert.True(
                File.Exists(Path.Combine(outputRoot, "outer", "level2", "final.txt")),
                "最终产物应当是 level2.7z 里的 final.txt，且待在「最后一个压缩包」那一层里面。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(outputRoot, "*", SearchOption.AllDirectories))
                + $"\n日志：\n{string.Join("\n", harness.LogTexts)}");

            // 校验那一段必须如实写明"这次不拿第 0 层清单核对"，不许假装做过清单校验。
            Assert.True(
                harness.LogTexts.Any(
                    line => line.Contains("结果校验", StringComparison.Ordinal)
                            && line.Contains("未取得预期条目数", StringComparison.Ordinal)),
                "结果校验那一行没有如实写明\"未取得预期条目数\"。日志：\n" + string.Join("\n", harness.LogTexts));

            Assert.Equal(1, outcome.Rounds);
        }

        /// <summary>
        /// **一键处理期间不弹"要不要展开多分支"的确认框**（用户 2026-09-27："一键解压 = 用户走开，
        /// 以后不要出现弹窗"）：多分支时按保守档只保留当前这一层，并把这件事写进日志。
        /// </summary>
        [Fact]
        public async Task 一键处理遇到多分支_不弹确认框_按保守档只留当前层()
        {
            string build = Path.Combine(_root, "multibranch-build");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "a.txt"), "分支 A\n");
            WriteText(Path.Combine(build, "b.txt"), "分支 B\n");
            Run7z(build, "a", "-t7z", "innerA.7z", "-p" + ChainPassword, "-mhe=on", "a.txt");
            Run7z(build, "a", "-t7z", "innerB.7z", "-p" + ChainPassword, "-mhe=on", "b.txt");

            // 外层里就是两个内层包 —— 递归核心会停成 NeedsDecision（以前这里弹框）。
            string outer = BuildPackage("outer.7z", build, "innerA.7z", "innerB.7z");

            string outputRoot = Path.Combine(_root, "out-multibranch");

            Harness harness = CreateHarness(
                $"{OuterPassword}\n{ChainPassword}\n",
                settings =>
                {
                    settings.RecursionMode = "SingleChain";
                    settings.CustomOutputDirectory = outputRoot;
                });

            await harness.AddPathsAsync(outer);
            await harness.RunOneClickAsync();

            // 保守档的痕迹必须在日志里（⛔ 不许静默）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("一键处理不弹确认框", StringComparison.Ordinal));

            /*
             * 而且**不能**因此判失败：当前这一层的结果照样定稿（两个内层包本身）；
             * 接着续解链会把这两个内层包各自当成一个任务继续解（各占一层），
             * 所以任务数会变成 3 —— 这里断言的是"没有一个是失败/部分完成"。
             */
            Assert.NotEmpty(harness.Vm.Tasks);

            Assert.DoesNotContain(
                harness.Vm.Tasks,
                task => task.Status == StatusText.ExtractFailed || task.Status == StatusText.PartiallyCompleted);
        }

        /// <summary>
        /// 造一条**单链**、最深一层是"一个文件夹 + 里面一个文件"的样本：
        /// <c>outer.7z → level2.7z → … → levelN.7z → 内容物\payload.bin</c>。
        ///
        /// <para>与 <see cref="BuildChain"/> 的差别只有最后一层的内容形状（那个是单文件，
        /// 判定表 1 不套层）—— 落点模型 v2 的"末层那一层"必须用一个文件夹才测得出来。</para>
        /// </summary>
        private string BuildFolderChain(int levelCount)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "folder-chain-" + levelCount);
            Directory.CreateDirectory(Path.Combine(build, "内容物"));

            WriteText(Path.Combine(build, "内容物", "payload.bin"), InnerPayloadText);

            Run7z(build, "a", "-t7z", $"level{levelCount}.7z", "-p" + ChainPassword, "-mhe=on", @"内容物\payload.bin");

            for (int level = levelCount - 1; level >= 2; level--)
            {
                Run7z(
                    build,
                    "a",
                    "-t7z",
                    $"level{level}.7z",
                    "-p" + ChainPassword,
                    "-mhe=on",
                    $"level{level + 1}.7z");
            }

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string outer = Path.Combine(packageDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "-p" + ChainPassword, "-mhe=on", "level2.7z");

            return outer;
        }

        // ---------------------------------------------------------------- 归档起点判定（纯函数）

        [Theory]
        [InlineData("a.7z.001", true)]
        [InlineData("a.zip.001", true)]
        [InlineData("a.rar.001", true)]
        [InlineData("a.7z.002", false)]
        [InlineData("a.7z.010", false)]
        [InlineData("a.zip.z01", false)]
        [InlineData("a.zip.z02", false)]
        [InlineData("a.rar.r00", false)]
        [InlineData("a.rar.r01", false)]
        [InlineData("a.part1.rar", true)]
        [InlineData("a.part01.rar", true)]
        [InlineData("a.part2.rar", false)]
        [InlineData("a.part002.rar", false)]
        [InlineData("a.7z", true)]
        [InlineData("a.zip", true)]
        [InlineData("a.tar.gz", true)]
        [InlineData("a.mp4", false)]
        [InlineData("a.txt", false)]
        [InlineData("noextension", false)]
        public void 只有归档起点才算内层包(string fileName, bool expected)
        {
            Assert.Equal(expected, OneClickCoordinator.IsArchiveStartPoint(Path.Combine("C:\\somewhere", fileName)));
        }

        // ---------------------------------------------------------------- 样本构造

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
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
                if (a is IEnumerable<string> many)
                {
                    foreach (string s in many)
                    {
                        psi.ArgumentList.Add(s);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(a.ToString() ?? string.Empty);
                }
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

        private void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 造一组加密分卷 <c>inner.7z.001/.002…</c>（40 KB 一卷，保证切得开），落在
        /// <c>&lt;root&gt;/inner-stage</c>，并返回这些分卷的路径。
        /// </summary>
        private List<string> BuildInnerVolumeGroup()
        {
            string stage = InnerStageDirectory;
            Directory.CreateDirectory(stage);

            WriteText(Path.Combine(stage, "payload.txt"), InnerPayloadText);

            byte[] filler = new byte[150 * 1024];
            new Random(20260921).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "big.bin"), filler);

            // 文件名用相对形式传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(stage, "a", "-t7z", "inner.7z", "-v40k", "-p" + InnerPassword, "-mhe=on", "payload.txt", "big.bin");

            List<string> volumes = Directory.GetFiles(stage, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(volumes.Count >= 2, $"应该切出多个分卷，实际 {volumes.Count} 个");

            return volumes;
        }

        /// <summary>把内层分卷打成一个加密包，返回包的路径。</summary>
        private string BuildPackageFromInnerStage(string packageFileName)
        {
            List<string> volumes = Directory.GetFiles(InnerStageDirectory, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetFileName(p) ?? string.Empty)
                .ToList();

            Assert.NotEmpty(volumes);

            return BuildPackage(packageFileName, InnerStageDirectory, volumes.Cast<object>().ToArray());
        }

        /// <summary>在 <c>&lt;root&gt;/packages</c> 下造一个加密 7z（条目名相对 <paramref name="workingDirectory"/>）。</summary>
        private string BuildPackage(string packageFileName, string workingDirectory, params object[] entryNames)
        {
            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string path = Path.Combine(packageDirectory, packageFileName);

            var args = new List<object> { "a", "-t7z", path, "-p" + OuterPassword, "-mhe=on" };
            args.AddRange(entryNames);

            Run7z(workingDirectory, args.ToArray());

            return path;
        }

        /// <summary>
        /// 造一条嵌套链：<c>outer.7z → level2.7z → … → levelN.7z → final.txt</c>。
        /// 每一层的产物里都有一个**新包**，所以它会一直想往下解 ——
        /// 正好用来验证轮数硬上限（不变量 8）与「继续解」（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>层数由调用方给，而且**必须跟着生效的
        /// <see cref="OneClickCoordinator.RoundLimit"/> 走**：上限从 10 收敛成"设置里的最大嵌套层数
        /// （默认 5）"之后，写死"四层链"的那种样本会**跑到底**、根本碰不到上限，
        /// 测试就从"验证上限"悄悄变成"验证能跑完"（假绿）。</para>
        /// </summary>
        private string BuildChain(int levelCount)
        {
            Assert.True(levelCount >= 2, "至少要有 outer + 一层内层包");

            string build = Path.Combine(_root, "chain-" + levelCount);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "final.txt"), "最深一层的内容\n");

            // 从最深一层往外套：levelN 里是内容物，levelN-1 里是 levelN，……，outer 里是 level2。
            Run7z(build, "a", "-t7z", $"level{levelCount}.7z", "-p" + ChainPassword, "-mhe=on", "final.txt");

            for (int level = levelCount - 1; level >= 2; level--)
            {
                Run7z(
                    build,
                    "a",
                    "-t7z",
                    $"level{level}.7z",
                    "-p" + ChainPassword,
                    "-mhe=on",
                    $"level{level + 1}.7z");
            }

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string outer = Path.Combine(packageDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "-p" + ChainPassword, "-mhe=on", "level2.7z");

            return outer;
        }

        /// <summary>
        /// 造用户的真实现场：<c>user.mp4</c> = 假 MP4 头 + 尾部一个完整 ZIP，ZIP 里是加密的 7z 分卷。
        ///
        /// 造法必须是"先把 ZIP 整个建好、再在前面拼数据"：这样 ZIP 的内部偏移才保持相对它自己，
        /// 才是那个"7z 打不开、必须按偏移抠出来"的形态（见 EmbeddedArchiveTests 的说明）。
        /// </summary>
        private string BuildPolyglotUserFile()
        {
            List<string> volumes = BuildInnerVolumeGroup();

            string stage = Path.Combine(_root, "zip-stage");
            Directory.CreateDirectory(stage);

            foreach (string volume in volumes)
            {
                File.Copy(volume, Path.Combine(stage, Path.GetFileName(volume)), overwrite: true);
            }

            string plainZip = Path.Combine(_root, "tail.zip");
            ZipFile.CreateFromDirectory(stage, plainZip);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string userFile = Path.Combine(packageDirectory, "user.mp4");

            using (var output = new FileStream(userFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(BuildFakeVideoPrefix());
                output.Write(File.ReadAllBytes(plainZip));
            }

            return userFile;
        }

        /// <summary>
        /// 造一组**第一层直接出内容物**的加密 7z 分卷（形状 A 的样本，对应 flowe2e 里的
        /// <c>444.7z.001/.002/.003</c>）：<c>&lt;root&gt;/packages/&lt;baseName&gt;.7z.001/.002…</c>。
        ///
        /// 40 KB 一卷，保证真的切得开；内容物是一个 <c>content.txt</c> 加一个随机大文件。
        /// </summary>
        private string BuildVolumeGroupWithContent(string baseName)
        {
            string stage = Path.Combine(_root, baseName + "-stage");
            Directory.CreateDirectory(stage);

            WriteText(Path.Combine(stage, "content.txt"), InnerPayloadText);

            byte[] filler = new byte[150 * 1024];
            new Random(20260922).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "big.bin"), filler);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            // 文件名用相对形式传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(
                stage,
                "a",
                "-t7z",
                Path.Combine(packageDirectory, baseName + ".7z"),
                "-v40k",
                "-p" + OuterPassword,
                "-mhe=on",
                "content.txt",
                "big.bin");

            Assert.True(
                Directory.GetFiles(packageDirectory, baseName + ".7z.*").Length >= 2,
                "样本没有切成多个分卷");

            return packageDirectory;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 ftyp box，后面填随机字节（固定种子，样本可复现）。</summary>
        private static byte[] BuildFakeVideoPrefix()
        {
            byte[] prefix = new byte[FakeVideoPrefixLength];
            new Random(20260921).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        private string InnerStageDirectory => Path.Combine(_root, "inner-stage");

        // ---------------------------------------------------------------- 装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎（外面包一层计数）。
        /// 缓存根 / 输出目录 / 密码本全部落在临时目录里，绝不碰用户的目录（AGENTS.md §8）。
        /// </summary>
        private Harness CreateHarness(string passwordBookText, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "密码本.txt");
            WriteText(bookPath, passwordBookText);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;

            /*
             * 第 32 条之后设置里的默认档是「源包留在原地」；本类全部用例钉的都是"源包进其余物"那条链
             * （含链尾补搬），所以在这里显式选上「放入其余物」，免得用例变成在测默认档。
             */
            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);

            // 7z 路径留空 = 用 ToolLocator 解析出的内置路径。测试目录里也有一份 tools\7zip
            // （由 ArchiveFixer.csproj 的 CopyToOutputDirectory 带过来），所以真引擎在测试里是可用的。
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new CountingEngine(new SevenZipEngine());
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            /*
             * MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）。
             * 这里先存下来、构造完立刻还原：不然别的测试会拿着"我这边马上要删掉的临时目录"当工作区去解压。
             * （本测试类同时声明成不与其他集合并行，见文件顶部的 CollectionDefinition。）
             */
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
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, engine, oneClick, extraction, scan, outputRoot, logService);
        }

        /// <summary>把汇总里的分项数字抠出来求和，用来验证"分项之和 = 本次任务数"这条硬要求。</summary>
        private static (int Sum, int Scope) ParseSummaryCounts(string summary)
        {
            int sum = 0;

            foreach (Match match in Regex.Matches(summary, @"(成功|失败|跳过|部分完成|取消|未处理) (\d+)"))
            {
                sum += int.Parse(match.Groups[2].Value);
            }

            /*
             * 两种 scope 写法都要认：全部处理过时是"本次 N 个任务"，
             * 列表里还有没轮到的时候是"本次 N 个 / 列表共 M 个"（第 16 条追加之后，
             * 撞上限那一批的列表里会多出"已加进列表、等继续解"的内层包，正好走后一种）。
             */
            Match scope = Regex.Match(summary, @"本次 (\d+) 个");

            Assert.True(scope.Success, $"汇总里没有本次 N 个任务：{summary}");

            return (sum, int.Parse(scope.Groups[1].Value));
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                CountingEngine engine,
                OneClickCoordinator oneClick,
                ExtractionCoordinator extraction,
                ScanCoordinator scan,
                string outputRoot,
                LogService log)
            {
                Vm = vm;
                Engine = engine;
                _oneClick = oneClick;
                Extraction = extraction;
                Scan = scan;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public CountingEngine Engine { get; }

            /// <summary>手动那条解压入口（「只解压」/「解压到当前文件夹」走它）—— 落点模型 v2 的用例要直接调它。</summary>
            public ExtractionCoordinator Extraction { get; }

            /// <summary>
            /// 扫描协调器。用例要直接调它做的只有一件事：复刻"一键处理开工前把无用物移出列表"
            /// 那一步（<see cref="ScanCoordinator.RemoveJunkTasksFromListAsync"/>）。
            /// </summary>
            public ScanCoordinator Scan { get; }

            /// <summary>一键处理协调器：用例要读**生效的轮数上限**（<c>RoundLimit</c>），⛔ 不许写死常量。</summary>
            public OneClickCoordinator OneClick => _oneClick;

            public string OutputRoot { get; }

            /// <summary>
            /// 真 LogService：屏幕日志集合（<c>Logs</c>）在无 WPF 应用的测试进程里照样会被填充
            /// （MainViewModel.AppendLog 走的就是它），所以"有没有写这条日志"可以直接断言。
            /// </summary>
            public LogService Log { get; }

            /// <summary>屏幕日志的文本（断言"写没写这条日志"用；与既有同类测试同一写法）。</summary>
            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>
            /// 跑一键处理的**流程部分**（不弹任何对话框 —— 测试里没人在那儿点确定）。
            /// 走的是 RunAsync 内部同一个 RunPipelineAsync。
            /// </summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 真引擎外面包一层，只为**数调用**：测试要证明"第 2 轮没有再解一遍已经处理过的包"，
        /// 只能看引擎实际被调了几次、拿的是哪个文件。
        /// </summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;

            public CountingEngine(IArchiveEngine inner)
            {
                _inner = inner;
            }

            public List<string> ExtractCalls { get; } = new();

            /// <summary>每次解压前回调（用来模拟"用户在批次中途点了停止后续"）。</summary>
            public Action<ArchiveRequest>? OnExtract { get; set; }

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ProbeAsync(request, cancellationToken);
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.TestAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request.ArchivePath);
                OnExtract?.Invoke(request);

                return _inner.ExtractAsync(request, options, cancellationToken);
            }
        }
    }
}
