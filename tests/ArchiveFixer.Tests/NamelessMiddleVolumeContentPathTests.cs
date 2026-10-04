using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 真机形状的实测：一组三卷 7z，**中间那一卷的名字全丢了**。
    ///
    /// <para>形状（全部用占位名）：<c>111.7z.001</c>（满片）/ <c>111</c>（满片，本该叫
    /// <c>111.7z.002</c>，名字整个丢了）/ <c>111.7z.003</c>（末卷，更短）。</para>
    ///
    /// <para>名字那条路对这个形状给的是「它的名字本来就是标准的，问题不在名字上」（第一节是标准名
    /// <c>.001</c>，要改的却是兄弟卷），所以只剩**内容那条路**能回答：它会不会把 <c>111</c> 认成
    /// 缺的那一卷 <c>111.7z.002</c>。</para>
    ///
    /// <para>这一组用例把内容路的「现状」钉住：真 7z 造样本、真 <c>SevenZipEngine</c>、
    /// 真工作区根（与样本同卷，硬链接做得了），把 <c>CanRepair</c> / <c>SuggestedFileName</c> /
    /// <c>Items</c> / <c>Reason</c> / <c>TrialAttempted</c> 全部打印出来并逐条断言。
    /// ⛔ 一个字节都不许动（源文件不改名、内容不变）—— 这一条无论结论是哪一档都成立。</para>
    ///
    /// <para>「实测结论（真 7z 造样本跑出来）：内容路认不出这个形状」，两道闸门各自把它挡在门外，
    /// 而且都「连试开都没放行」（<c>TrialAttempted=False</c>）：</para>
    /// <list type="number">
    /// <item><description>「按管线入口问（<c>111.7z.001</c>）」：内容路在
    /// <c>VolumeNameRepair.PlanByContentCoreAsync</c> 里就判「名字里已经有卷号 ⇒ 那是名字路的活，这里不抢」
    /// ⇒ 结论与名字路「逐字相同」（「它的名字本来就是标准的，问题不在名字上」）。管线手上那一卷
    /// 正是这个标准名，所以这条路在真机上永远走不到内容定序那一步。</description></item>
    /// <item><description>「直接拿名字全丢的那一卷（<c>111</c>）问」：7z 的中间卷内容里「没有任何身份信息」
    /// （只有第 1 卷有魔数，之后是裸字节流），<c>VolumeNumberFromContent.Read</c> 读出来是
    /// <c>Unknown</c> ⇒ 如实回答「它的内容没说自己是分卷组的一员」；放宽到邻近目录问一遍，结论一字不差。
    /// </description></item>
    /// </list>
    ///
    /// <para>⇒ 这个形状（标准名第 1 卷 + 名字全丢的中间卷 + 标准名末卷）落在现有两条路的缝里：
    /// 名字路要兄弟卷的名字里还留着卷标记，内容路（7z）又只认「手上这一卷的名字被改坏了」的那个入口。
    /// 这两条断言就是「现在做不到」的钉子 —— 哪天产品能认了，它们会「变红」，那时按新行为改写。
    /// ⛔ 不许为了让它们变绿而放宽断言。</para>
    /// </summary>
    public class NamelessMiddleVolumeContentPathTests : IDisposable
    {
        /// <summary>2.5 MiB：<c>-v1m</c> 正好三卷（1 MiB + 1 MiB + 0.5 MiB）。</summary>
        private const int PayloadBytes = 2621440;

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly ITestOutputHelper _output;

        public NamelessMiddleVolumeContentPathTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerNamelessMiddle", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点。
            }
        }

        /// <summary>
        /// 管线的实际入口：它手上那一卷是 <c>111.7z.001</c>（名字是标准的），
        /// 名字路判不由它改 ⇒ 落到内容路时传进去的也是这一卷。
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_中间卷名字全丢_内容路按管线入口问_现状是不认()
        {
            Sample sample = NewSample();

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                sample.First,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(sample.First),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111.7z.001｜同目录候选", plan);

            // 现状：内容路在这一档上给出的结论与名字路逐字相同，连一次引擎都没调过。
            Assert.False(plan.CanRepair);
            Assert.Empty(plan.SuggestedFileName);
            Assert.Empty(plan.Items);
            Assert.Equal(StatusText.VolumeRepairAlreadyStandard, plan.Reason);
            Assert.False(plan.TrialAttempted, "这一档一次引擎都没调过");

            sample.AssertUntouched();
        }

        /// <summary>
        /// 直接拿那个「名字全丢的中间卷」（<c>111</c>）去问内容路 —— 这是这一组里唯一「名字需要修」
        /// 的那一份，也是内容路理论上最该回答的那一问。
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_中间卷名字全丢_直接拿那一卷问内容路_现状也是不认()
        {
            Sample sample = NewSample();

            VolumeNameRepairPlan byContent = await VolumeNameRepair.PlanByContentAsync(
                sample.Nameless,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(sample.Nameless),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111｜同目录候选", byContent);

            // 现状：7z 中间卷的内容里没有身份信息 ⇒ 内容路如实说不认，也不猜顺序。
            Assert.False(byContent.CanRepair);
            Assert.Empty(byContent.SuggestedFileName);
            Assert.Empty(byContent.Items);
            Assert.Equal(StatusText.VolumeRepairContentNotAVolumeMember, byContent.Reason);
            Assert.False(byContent.TrialAttempted, "内容里没有身份信息 ⇒ 连试开都没放行");

            // 放宽到邻近目录的那一次（管线在同一目录判不出来时会再问一遍）结论一样。
            VolumeNameRepairPlan widened = await VolumeNameRepair.PlanByContentWithNearbyCandidatesAsync(
                sample.Nameless,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(sample.Nameless),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111｜邻近目录候选", widened);

            Assert.False(widened.CanRepair);
            Assert.Empty(widened.SuggestedFileName);
            Assert.Empty(widened.Items);
            Assert.Equal(byContent.Reason, widened.Reason);
            Assert.False(widened.TrialAttempted);

            sample.AssertUntouched();
        }

        // ── 样本 ──

        /// <summary>造样本的公共部分：真 7z 三卷 + 把中间那一卷改名成 <c>111</c>。</summary>
        private Sample NewSample()
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var payload = new byte[PayloadBytes];
            new Random(20261003).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "111.7z", "data.bin");

            string first = Path.Combine(directory, "111.7z.001");
            string nameless = Path.Combine(directory, "111");
            string third = Path.Combine(directory, "111.7z.003");

            Assert.True(File.Exists(Path.Combine(directory, "111.7z.002")), "样本不是三卷");
            Assert.True(File.Exists(third), "样本不是三卷");

            // 真机那种丢法：中间那一卷的名字整个丢了（没有后缀、也没有卷号）。
            File.Move(Path.Combine(directory, "111.7z.002"), nameless);

            long firstLength = new FileInfo(first).Length;

            // 形状必须与真机一致，否则这一组用例验的不是那个形状。
            Assert.Equal(1024 * 1024, firstLength);                                    // 第 1 卷 = 切分上限（满片）
            Assert.Equal(firstLength, new FileInfo(nameless).Length);                  // 中间那一卷也是满片
            Assert.True(new FileInfo(third).Length < firstLength, "末卷应当更短");

            // 名字这条路对这个形状的结论（用户已经在真机上量过的那一条）：第一节是标准名 ⇒ 判不由它改。
            VolumeNameRepairPlan byName = VolumeNameRepair.Plan(
                first,
                VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            _output.WriteLine($"[入口=111.7z.001｜名字路] CanRepair={byName.CanRepair} Reason=[{byName.Reason}]");

            Assert.False(byName.CanRepair);

            return new Sample(directory, first, nameless, third);
        }

        private void Dump(string label, VolumeNameRepairPlan plan)
        {
            _output.WriteLine(string.Empty);
            _output.WriteLine($"[{label}] CanRepair={plan.CanRepair}");
            _output.WriteLine($"[{label}] SuggestedFileName=[{plan.SuggestedFileName}]");
            _output.WriteLine($"[{label}] Reason=[{plan.Reason}]");
            _output.WriteLine($"[{label}] TrialAttempted={plan.TrialAttempted}");
            _output.WriteLine($"[{label}] Items（{plan.Items.Count} 条）=[{string.Join(" | ", plan.Items.Select(i => i.CurrentFileName + " → " + i.SuggestedFileName))}]");
        }

        private Engines.IArchiveEngine NewEngine()
        {
            var engine = new Engines.SevenZip.SevenZipEngine();

            Assert.True(engine.IsAvailable, "内置 7z.exe 不可用，这一组用例跑不起来");

            return engine;
        }

        /// <summary>一份造好的样本（三卷、中间那一卷叫 <c>111</c>）以及"一个字节都没动"的判据。</summary>
        private sealed class Sample
        {
            private readonly Dictionary<string, byte[]> _before;

            public Sample(string directory, string first, string nameless, string third)
            {
                Directory = directory;
                First = first;
                Nameless = nameless;
                Third = third;

                /*
                 * 工作区根 = 样本目录里的工作区（与样本同卷 ⇒ 硬链接做得了）。
                 * 真实调用方传的是本批目标目录里的 `<目标目录>\.ArchiveFixer.work`（不变量 12）——
                 * 用例里样本就在临时目录里，用同一个常量拼，⛔ 不写死名字。
                 */
                WorkRoot = Path.Combine(directory, VolumeContentInference.WorkDirectoryName);

                _before = new[] { first, nameless, third }
                    .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            }

            public string Directory { get; }

            public string First { get; }

            public string Nameless { get; }

            public string Third { get; }

            public string WorkRoot { get; }

            /// <summary>结论无论是哪一档，都不许改名字、不许改内容、不许凭空造出 <c>111.7z.002</c>。</summary>
            public void AssertUntouched()
            {
                Assert.False(
                    File.Exists(Path.Combine(Directory, "111.7z.002")),
                    "内容路把 111 改名成了 111.7z.002（这一组用例本该钉住现状，说明行为已经变了）");

                foreach ((string path, byte[] expected) in _before)
                {
                    Assert.True(File.Exists(path), $"源文件被动过：{path}");
                    Assert.Equal(expected, File.ReadAllBytes(path));
                }
            }
        }

        // ── 真 7z ──

        private void Run7z(string workingDirectory, params string[] args)
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe");
            }

            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }
    }
}
