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
             * 中间件集中在 其余物 里，不再和内容物混在同一层。
             */
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "outer", "其余物", "inner.7z.001")));

            // 没有多出"(1)"这种自动改名副本目录
            Assert.False(Directory.Exists(Path.Combine(harness.OutputRoot, "outer (1)")), "不该出现重复解压留下的副本目录");
        }

        // ---------------------------------------------------------------- 第三步：轮数硬上限

        [Fact]
        public async Task 一直产出新包时_最多只续解到MaxRounds轮_到顶提示还剩几个()
        {
            // 链比上限多一层：跑满 MaxRounds 轮之后，正好还剩最后那一层没解。
            int lastLevel = OneClickCoordinator.MaxRounds + 1;
            string outer = BuildChain(lastLevel);

            Harness harness = CreateHarness(ChainPassword + "\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(OneClickCoordinator.MaxRounds, outcome.Rounds);
            Assert.Equal(OneClickCoordinator.MaxRounds - 1, outcome.ContinuationLayers);
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
            Assert.Equal(OneClickCoordinator.MaxRounds + 1, harness.Vm.Tasks.Count);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(OneClickCoordinator.MaxRounds, scope);
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
            int lastLevel = OneClickCoordinator.MaxRounds + 1;
            string outer = BuildChain(lastLevel);

            Harness harness = CreateHarness(ChainPassword + "\n");
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

        // ---------------------------------------------------------------- 用户的验收判据：一个源包 = 一个目录

        /// <summary>
        /// 用户的原始抱怨（必须被这条测试钉死）：
        /// <i>"这次的一键处理给我弄的相当糟糕，你给我多弄了四个文件夹，分卷文件你居然又解压到外面来了，
        /// 文件一多根本就分不清"</i>。
        ///
        /// 期望：处理完一个源包之后，**最终只有 <c>&lt;输出根&gt;\&lt;包名&gt;\</c> 这一个目录**，
        /// 里面是内容物 + 一个集中的 <c>其余物</c>；中间件（内层分卷、抠出的 ZIP）一个都不许漏到外面。
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

            // ③ 中间件全部集中在 其余物 里，一个都不许漏在外面
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
                    $"中间件漏到内容物一层了：{name}"));

            /*
             * ④ 源包（含它的中间件）都进了那**一个**目录的 其余物 里 —— 用户 2026-09-22 的规则：
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
            Assert.NotEmpty(payloads);
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
                File.Exists(Path.Combine(sharedRoot, "payload.txt")),
                "内容物被放到了共用根上（与包名目录平级）—— 续解落点又退回 OutputPath 了");
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

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("内层包移入其余物", StringComparison.Ordinal));
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
                    string candidate = Path.Combine(dir.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

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
        /// <para>层数由调用方给，而且**必须跟着 <see cref="OneClickCoordinator.MaxRounds"/> 走**：
        /// 上限从 3 提到 10 之后，写死"四层链"的那种样本会**跑到底**、根本碰不到上限，
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

            return new Harness(vm, engine, oneClick, outputRoot, logService);
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
                string outputRoot,
                LogService log)
            {
                Vm = vm;
                Engine = engine;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public CountingEngine Engine { get; }

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
