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
    /// <para><b>结论为什么变了（2026-10-03 阶段 B）</b>：这一组用例原来钉住的现状是「两条路都不接」——
    /// 名字路判「它的名字本来就是标准的，问题不在名字上」（第一节确实是标准名，要改的却是兄弟卷），
    /// 内容路又被 <c>VolumeNameRepair.PlanByContentCoreAsync</c> 里那句
    /// 「**手上这一卷**的名字里已经有卷号 ⇒ 那是名字路的活，这里不抢」挡在门外
    /// （<c>CanRepair=False</c> + <c>TrialAttempted=False</c>）。</para>
    ///
    /// <para>阶段 B 把**入口判据换成了对象**：不再问"手上这一卷的名字里碰巧有没有卷号"，
    /// 而是问"**这一组的名字自不自洽**"——
    /// ① 第 1 卷有 7z 起始头（魔数，第 0 层证据）；
    /// ② 同目录能配出一组形状自洽的兄弟卷（基名逐字相同 / 卷标记连续 / 除末片外等大，
    /// 唯一出口 <see cref="VolumeContentInference.ReadSiblingShape"/>）；
    /// ③ 这一组里真有一片**名字丢了**（<c>NamelessFillers</c>）⇒「缺的那一卷叫什么」是已知的。
    /// 三条同时成立才进内容路。⇒ 现在能出计划、能认出 <c>111</c> 就是缺的 <c>111.7z.002</c>。</para>
    ///
    /// <para><b>⛔ 红线一条没放宽</b>：改名仍然"只有硬链接试开成功才认"
    /// （<see cref="VolumeProbeVerifier"/>；⛔ 绝不复制大文件），试不开 ⇒ 一个名字都不改；
    /// 判不出（比如两片都丢了名字、又缺了一卷）⇒ 什么都不做，并如实报出"还差多少字节"
    /// （7z 起始头是明文，<c>32 + NextHeaderOffset + NextHeaderSize</c> = 整包应当有的字节数，与卷序无关）。</para>
    /// </summary>
    public class NamelessMiddleVolumeContentPathTests : IDisposable
    {
        /// <summary>2.5 MiB：<c>-v1m</c> 正好三卷（1 MiB + 1 MiB + 0.5 MiB）。</summary>
        private const int PayloadBytes = 2621440;

        /// <summary>4.5 MiB：<c>-v1m</c> 正好五卷（4 × 1 MiB + 0.5 MiB）—— 「判不出」那一档用它。</summary>
        private const int FiveVolumePayloadBytes = 4718592;

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
        public async Task 真七z_中间卷名字全丢_内容路按管线入口问_现在认了而且计划正确()
        {
            Sample sample = NewSample();

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                sample.First,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(sample.First),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111.7z.001｜同目录候选", plan);

            // ① 认出 `111` 就是缺的那一卷 `111.7z.002`。
            Assert.True(plan.CanRepair, $"这一档现在应当能出计划：{plan.Reason}");
            Assert.Equal("111.7z.002", plan.SuggestedFileName);

            // ② 改名计划正确：三卷都在组里，只有 `111` 真要改名，其余两卷原地不动、也没有目标名被占。
            Assert.Equal(3, plan.Items.Count);
            Assert.Equal(
                new[] { "111.7z.001→111.7z.001", "111→111.7z.002", "111.7z.003→111.7z.003" },
                plan.Items.Select(i => i.CurrentFileName + "→" + i.SuggestedFileName));
            Assert.Single(
                plan.Items,
                i => !string.Equals(i.CurrentPath, i.TargetPath, StringComparison.OrdinalIgnoreCase));
            Assert.All(plan.Items, i => Assert.False(
                File.Exists(i.TargetPath) && !string.Equals(i.CurrentPath, i.TargetPath, StringComparison.OrdinalIgnoreCase),
                "目标名被别的文件占了"));

            // 计划本身**不碰盘**：出计划这一步只读（⛔ 一个字节都不许动）。
            Assert.True(plan.TrialAttempted, "这一档必须真跑过试开（硬链接 + 引擎列目录）才算数");
            sample.AssertNothingRenamed();
            sample.AssertBytesIntact();

            // ③ 只有试开成立才真改名：执行计划 ⇒ 只有 `111` 这个名字变了，内容一个字节不差。
            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);

            sample.AssertRenamedToMissingVolume();
        }

        /// <summary>
        /// 直接拿那个「名字全丢的中间卷」（<c>111</c>）去问内容路 —— 它自己**没有 7z 起始头**
        /// （7z 的中间片是裸字节流），所以锚点去同目录找：<c>111.7z.001</c> 有魔数、包基名与它逐字相同
        /// （都是 <c>111</c>）、这一组名字自洽、而且它就是这组里名字丢了的那一片。
        /// ⇒ 出**同一份**计划：基名从**锚点**推（⛔ 不从"手上这一卷"推 —— 拿 <c>111</c> 推不出这一组叫什么）。
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_中间卷名字全丢_直接拿那一卷问_锚点去同目录找_得到同一份计划()
        {
            Sample sample = NewSample();

            VolumeNameRepairPlan byContent = await VolumeNameRepair.PlanByContentAsync(
                sample.Nameless,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(sample.Nameless),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111｜同目录候选", byContent);

            Assert.True(byContent.CanRepair, $"这一档现在应当能出计划：{byContent.Reason}");
            Assert.Equal("111.7z.002", byContent.SuggestedFileName);
            Assert.Equal(
                new[] { "111.7z.001→111.7z.001", "111→111.7z.002", "111.7z.003→111.7z.003" },
                byContent.Items.Select(i => i.CurrentFileName + "→" + i.SuggestedFileName));
            Assert.True(byContent.TrialAttempted);

            // 计划的入口指向**真要改的那一卷**（TryApply 拿它当入口；指到"名字本来就对"的卷上会整份白算）。
            Assert.Equal(sample.Nameless, byContent.CurrentPath, ignoreCase: true);

            // 放宽到邻近目录的那一次（管线在同一目录判不出来时会再问一遍）结论一样。
            VolumeNameRepairPlan widened = await VolumeNameRepair.PlanByContentWithNearbyCandidatesAsync(
                sample.Nameless,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(sample.Nameless),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111｜邻近目录候选", widened);

            Assert.True(widened.CanRepair);
            Assert.Equal(byContent.SuggestedFileName, widened.SuggestedFileName);
            Assert.Equal(
                byContent.Items.Select(i => i.CurrentFileName + "→" + i.SuggestedFileName),
                widened.Items.Select(i => i.CurrentFileName + "→" + i.SuggestedFileName));

            sample.AssertNothingRenamed();
            sample.AssertBytesIntact();
        }

        /// <summary>
        /// **试开不成立 ⇒ 一个名字都不改**（用户 2026-09-30 红线：证不出整组是齐的，宁可什么都不做）。
        ///
        /// <para>造法：把**末卷**（<c>111.7z.003</c>，7z 的 next header 就在它里面）的内容换成等长的垃圾 ——
        /// 尺寸一点没变（所以"起始头自述的字节数"这一条照旧成立、闸门照旧放行），
        /// 但引擎按这个顺序列不出里面的东西 ⇒ 结论必须是"不改名"，而不是"改一个试试"。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_中间卷名字全丢_试开不成立时_一个名字都不改()
        {
            Sample sample = NewSample();

            // 末卷换成等长垃圾：尺寸不变、内容不再是 7z 的 next header。
            var garbage = new byte[new FileInfo(sample.Third).Length];
            new Random(20261003).NextBytes(garbage);
            File.WriteAllBytes(sample.Third, garbage);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                sample.First,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(sample.First),
                NewEngine(),
                sample.WorkRoot);

            Dump("入口=111.7z.001｜末卷内容被换掉", plan);

            Assert.False(plan.CanRepair, "试开不成立时不许出计划");
            Assert.Empty(plan.Items);
            Assert.Empty(plan.SuggestedFileName);
            Assert.True(plan.TrialAttempted, "这一档是真试过之后才下的结论（⛔ 不是'没试'）");

            sample.AssertNothingRenamed();
        }

        /// <summary>
        /// **判不出那一档：两片都丢了名字、而且等大** —— 一组五卷里 <c>.002</c> 与 <c>.004</c> 的名字都丢了
        /// （<c>111</c> / <c>111.7z</c>，都正好是满片、都一样大），末卷 <c>.005</c> 还不在手上。
        ///
        /// <para>结论必须是**什么都不做**，并如实报出"还差多少字节"：起始头（明文、<c>-mhe</c> 也不加密）
        /// 自述整包 <c>4.5 MiB</c>，手上只有 <c>4 MiB</c> ⇒ 还差 <c>524288</c> 字节。
        /// ⛔ 缺的是第几卷在 7z 上本来就无从知道（中间片内容里没有卷号），所以那句话不许说得比事实更满。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真七z_两片都丢了名字且等大_缺一卷_什么都不做并如实报缺多少字节()
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var payload = new byte[FiveVolumePayloadBytes];
            new Random(20261004).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "111.7z", "data.bin");

            string first = Path.Combine(directory, "111.7z.001");
            string third = Path.Combine(directory, "111.7z.003");
            string fifth = Path.Combine(directory, "111.7z.005");

            Assert.True(File.Exists(Path.Combine(directory, "111.7z.004")), "样本不是五卷");
            Assert.True(File.Exists(fifth), "样本不是五卷");

            // 两片名字全丢：`.002` → `111`、`.004` → `111.7z`（都还在、都一样大）。
            File.Move(Path.Combine(directory, "111.7z.002"), Path.Combine(directory, "111"));
            File.Move(Path.Combine(directory, "111.7z.004"), Path.Combine(directory, "111.7z"));

            // 末卷不在手上 ⇒ 这一组凑不齐。
            // ⚠ 差多少字节不是"整数 MiB 减去手上那些"：7z 的 next header 也占字节，所以照实量末卷的长度。
            long missingLength = new FileInfo(fifth).Length;

            File.Delete(fifth);

            var before = Directory.GetFiles(directory)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                NewEngine(),
                Path.Combine(directory, VolumeContentInference.WorkDirectoryName));

            Dump("入口=111.7z.001｜两片丢名且等大 + 缺末卷", plan);

            Assert.False(plan.CanRepair, "这一档判不出 ⇒ 不许出计划");
            Assert.Empty(plan.Items);
            Assert.Empty(plan.SuggestedFileName);

            // 如实报：一次引擎都没调（证据是字节数，不是试开），而"还差多少字节"是算得出来的事实。
            Assert.False(plan.TrialAttempted, "这一档靠的是起始头的字节数，不该去烧引擎");
            Assert.True(plan.ByteBudgetMismatch, "结论应当是「字节数对不上」这一档");
            Assert.Contains("还差 " + missingLength.ToString(System.Globalization.CultureInfo.InvariantCulture), plan.Reason);

            foreach ((string path, byte[] expected) in before)
            {
                Assert.True(File.Exists(path), $"源文件被动过：{path}");
                Assert.Equal(expected, File.ReadAllBytes(path));
            }

            Assert.False(File.Exists(Path.Combine(directory, "111.7z.002")), "判不出时不许改名");
            Assert.False(File.Exists(Path.Combine(directory, "111.7z.004")), "判不出时不许改名");
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
            byte[] namelessBytes = File.ReadAllBytes(Path.Combine(directory, "111.7z.002"));
            File.Move(Path.Combine(directory, "111.7z.002"), nameless);

            long firstLength = new FileInfo(first).Length;

            // 形状必须与真机一致，否则这一组用例验的不是那个形状。
            Assert.Equal(1024 * 1024, firstLength);                                    // 第 1 卷 = 切分上限（满片）
            Assert.Equal(firstLength, new FileInfo(nameless).Length);                  // 中间那一卷也是满片
            Assert.True(new FileInfo(third).Length < firstLength, "末卷应当更短");

            // 名字这条路对这个形状的结论一个字没变（用户已经在真机上量过的那一条）：第一节是标准名 ⇒ 判不由它改。
            VolumeNameRepairPlan byName = VolumeNameRepair.Plan(
                first,
                VolumeNameRepair.EnumerateFileNamesInDirectory(first));

            _output.WriteLine($"[入口=111.7z.001｜名字路] CanRepair={byName.CanRepair} Reason=[{byName.Reason}]");

            Assert.False(byName.CanRepair);

            return new Sample(directory, first, nameless, third, namelessBytes);
        }

        private void Dump(string label, VolumeNameRepairPlan plan)
        {
            _output.WriteLine(string.Empty);
            _output.WriteLine($"[{label}] CanRepair={plan.CanRepair}");
            _output.WriteLine($"[{label}] SuggestedFileName=[{plan.SuggestedFileName}]");
            _output.WriteLine($"[{label}] Reason=[{plan.Reason}]");
            _output.WriteLine($"[{label}] TrialAttempted={plan.TrialAttempted}｜ByteBudgetMismatch={plan.ByteBudgetMismatch}");
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
            private readonly byte[] _namelessBytes;

            public Sample(string directory, string first, string nameless, string third, byte[] namelessBytes)
            {
                Directory = directory;
                First = first;
                Nameless = nameless;
                Third = third;
                _namelessBytes = namelessBytes;

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

            /// <summary>还没执行计划：盘上不许出现 <c>111.7z.002</c>（出计划这一步只读）。</summary>
            public void AssertNothingRenamed() =>
                Assert.False(File.Exists(Path.Combine(Directory, "111.7z.002")), "还没执行计划，名字就已经变了");

            /// <summary>三份源文件都还在、内容一个字节不差。</summary>
            public void AssertBytesIntact()
            {
                foreach ((string path, byte[] expected) in _before)
                {
                    Assert.True(File.Exists(path), $"源文件被动过：{path}");
                    Assert.Equal(expected, File.ReadAllBytes(path));
                }
            }

            /// <summary>执行计划之后：只有 <c>111</c> 改成了 <c>111.7z.002</c>（内容一个字节不差），另外两卷原地不动。</summary>
            public void AssertRenamedToMissingVolume()
            {
                string renamed = Path.Combine(Directory, "111.7z.002");

                Assert.True(File.Exists(renamed), "计划执行之后应当出现 111.7z.002");
                Assert.False(File.Exists(Nameless), "旧名字 111 应当已经不在了");
                Assert.Equal(_namelessBytes, File.ReadAllBytes(renamed));

                Assert.True(File.Exists(First));
                Assert.True(File.Exists(Third));
                Assert.Equal(_before[First], File.ReadAllBytes(First));
                Assert.Equal(_before[Third], File.ReadAllBytes(Third));

                Assert.Equal(4, System.IO.Directory.GetFiles(Directory).Length);   // data.bin + 三卷
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
